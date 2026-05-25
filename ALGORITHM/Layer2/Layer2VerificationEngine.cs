using System;
using QuantConnect.Algorithm.CSharp.DataEligibility;
using QuantConnect.Algorithm.CSharp.Layer1;

namespace QuantConnect.Algorithm.CSharp.Layer2
{
    /// <summary>
    /// Layer 2 dynamic verification model.
    /// Locks dominant side at trigger time, then monitors an expanding window until
    /// confirmation or immediate invalidation.
    /// </summary>
    public sealed class Layer2VerificationEngine
    {
        private readonly decimal _dDominanceThresholdPct;
        private readonly int _nInstitutionalMinCount;
        private readonly decimal _zInstitutionalNotionalThreshold;
        private readonly decimal _pInstitutionalPurityThresholdPct;
        private ActiveCandidate _candidate;

        public Layer2VerificationEngine(
            decimal dDominanceThresholdPct,
            int nInstitutionalMinCount,
            decimal zInstitutionalNotionalThreshold,
            decimal pInstitutionalPurityThresholdPct)
        {
            _dDominanceThresholdPct = dDominanceThresholdPct;
            _nInstitutionalMinCount = nInstitutionalMinCount;
            _zInstitutionalNotionalThreshold = zInstitutionalNotionalThreshold;
            _pInstitutionalPurityThresholdPct = pInstitutionalPurityThresholdPct;
        }

        public bool HasActiveCandidate => _candidate != null;

        public void ResetForNewRthSession()
        {
            _candidate = null;
        }

        public Layer2VerificationDecision ForceInvalidateActiveCandidate(string reason)
        {
            if (_candidate == null)
            {
                return Monitoring("Candidate is not active.");
            }

            _candidate = null;
            return Invalidated(reason);
        }

        public Layer2VerificationDecision StartCandidate(PrimaryTriggerSignal trigger)
        {
            if (trigger == null || trigger.Window == null || trigger.Window.Count == 0)
            {
                return Invalidated("Empty trigger window.");
            }

            decimal totalVolume = 0m;
            decimal buyVolume = 0m;
            decimal sellVolume = 0m;
            int institutionalCount = 0;
            int institutionalAlignedCount = 0;

            for (var i = 0; i < trigger.Window.Count; i++)
            {
                var tx = trigger.Window[i];
                totalVolume += tx.Volume;

                if (tx.Side == TradeSide.Buy)
                {
                    buyVolume += tx.Volume;
                }
                else if (tx.Side == TradeSide.Sell)
                {
                    sellVolume += tx.Volume;
                }

                // PRD: institutional notional uses consolidated LastPrice * Volume.
                var isInstitutional = tx.LastPrice * tx.Volume > _zInstitutionalNotionalThreshold;
                if (!isInstitutional)
                {
                    continue;
                }

                institutionalCount++;
            }

            if (totalVolume <= 0m)
            {
                return Invalidated("Total trigger-window volume is zero.");
            }

            var buyDominance = (buyVolume / totalVolume) * 100m;
            var sellDominance = (sellVolume / totalVolume) * 100m;

            TradeSide dominantSide;
            decimal dominancePercent;
            if (buyDominance >= _dDominanceThresholdPct && buyDominance >= sellDominance)
            {
                dominantSide = TradeSide.Buy;
                dominancePercent = buyDominance;
            }
            else if (sellDominance >= _dDominanceThresholdPct && sellDominance > buyDominance)
            {
                dominantSide = TradeSide.Sell;
                dominancePercent = sellDominance;
            }
            else
            {
                return Invalidated(
                    $"Directional dominance failed: Buy={buyDominance:F4}% Sell={sellDominance:F4}% Threshold={_dDominanceThresholdPct:F4}%.");
            }

            for (var i = 0; i < trigger.Window.Count; i++)
            {
                var tx = trigger.Window[i];
                var isInstitutional = tx.LastPrice * tx.Volume > _zInstitutionalNotionalThreshold;
                if (isInstitutional && tx.Side == dominantSide)
                {
                    institutionalAlignedCount++;
                }
            }

            var purityPercent = ResolvePurityPercent(institutionalCount, institutionalAlignedCount);
            if (institutionalCount > 0 && purityPercent < _pInstitutionalPurityThresholdPct)
            {
                return Invalidated(
                    $"Institutional purity failed: Purity={purityPercent:F4}% Threshold={_pInstitutionalPurityThresholdPct:F4}%.");
            }

            var pRefTime = trigger.Window[0].ExchangeTime;
            _candidate = new ActiveCandidate(
                dominantSide,
                trigger.PRef,
                pRefTime,
                trigger.TriggerTime,
                trigger.ThresholdIatMicroseconds,
                totalVolume,
                buyVolume,
                sellVolume,
                institutionalCount,
                institutionalAlignedCount);

            if (institutionalCount >= _nInstitutionalMinCount)
            {
                return Confirmed(trigger.PRef, pRefTime);
            }

            return Monitoring("Awaiting institutional threshold.");
        }

        public Layer2VerificationDecision EvaluateNext(EligibleConsolidatedTransaction tx)
        {
            if (_candidate == null || tx == null)
            {
                return Invalidated("Candidate is not active.");
            }

            var iatMicroseconds = (decimal)(tx.ExchangeTime - _candidate.LastExchangeTime).Ticks / 10m;
            if (iatMicroseconds <= 0m || iatMicroseconds > _candidate.ThresholdIatMicroseconds)
            {
                return InvalidateActive(
                    $"Fast-sequence invalidated: IATus={iatMicroseconds:F4} ThresholdIATus={_candidate.ThresholdIatMicroseconds:F4}.");
            }

            _candidate.LastExchangeTime = tx.ExchangeTime;
            _candidate.TotalVolume += tx.Volume;
            if (tx.Side == TradeSide.Buy)
            {
                _candidate.BuyVolume += tx.Volume;
            }
            else if (tx.Side == TradeSide.Sell)
            {
                _candidate.SellVolume += tx.Volume;
            }

            var isInstitutional = tx.LastPrice * tx.Volume > _zInstitutionalNotionalThreshold;
            if (isInstitutional)
            {
                _candidate.InstitutionalCount++;
                if (tx.Side == _candidate.LockedDominantSide)
                {
                    _candidate.InstitutionalAlignedCount++;
                }
            }

            var lockedVolume = _candidate.LockedDominantSide == TradeSide.Buy
                ? _candidate.BuyVolume
                : _candidate.SellVolume;
            var dominancePercent = _candidate.TotalVolume > 0m
                ? (lockedVolume / _candidate.TotalVolume) * 100m
                : 0m;

            if (dominancePercent < _dDominanceThresholdPct)
            {
                return InvalidateActive(
                    $"Directional dominance failed: LockedSide={_candidate.LockedDominantSide} Dominance={dominancePercent:F4}% Threshold={_dDominanceThresholdPct:F4}%.");
            }

            var purityPercent = ResolvePurityPercent(_candidate.InstitutionalCount, _candidate.InstitutionalAlignedCount);
            if (_candidate.InstitutionalCount > 0 && purityPercent < _pInstitutionalPurityThresholdPct)
            {
                return InvalidateActive(
                    $"Institutional purity failed: Purity={purityPercent:F4}% Threshold={_pInstitutionalPurityThresholdPct:F4}%.");
            }

            if (_candidate.InstitutionalCount >= _nInstitutionalMinCount)
            {
                return Confirmed(_candidate.PRef, _candidate.PRefTime);
            }

            return new Layer2VerificationDecision(
                Status: Layer2DecisionStatus.Monitoring,
                DominantSide: _candidate.LockedDominantSide,
                DominancePercent: dominancePercent,
                InstitutionalCount: _candidate.InstitutionalCount,
                InstitutionalPurityPercent: purityPercent,
                PRef: _candidate.PRef,
                PRefTime: _candidate.PRefTime,
                RejectionReason: "Awaiting institutional threshold.");
        }

        private Layer2VerificationDecision Confirmed(decimal pRef, DateTime pRefTime)
        {
            var dominancePercent = ResolveLockedDominancePercent(_candidate);
            var purityPercent = ResolvePurityPercent(_candidate.InstitutionalCount, _candidate.InstitutionalAlignedCount);
            var decision = new Layer2VerificationDecision(
                Status: Layer2DecisionStatus.Confirmed,
                DominantSide: _candidate.LockedDominantSide,
                DominancePercent: dominancePercent,
                InstitutionalCount: _candidate.InstitutionalCount,
                InstitutionalPurityPercent: purityPercent,
                PRef: pRef,
                PRefTime: pRefTime,
                RejectionReason: string.Empty);
            _candidate = null;
            return decision;
        }

        private static Layer2VerificationDecision Monitoring(string reason)
        {
            return new Layer2VerificationDecision(
                Status: Layer2DecisionStatus.Monitoring,
                DominantSide: TradeSide.Unknown,
                DominancePercent: 0m,
                InstitutionalCount: 0,
                InstitutionalPurityPercent: 100m,
                PRef: 0m,
                PRefTime: default,
                RejectionReason: reason);
        }

        private Layer2VerificationDecision InvalidateActive(string reason)
        {
            _candidate = null;
            return Invalidated(reason);
        }

        private static Layer2VerificationDecision Invalidated(string reason)
        {
            return new Layer2VerificationDecision(
                Status: Layer2DecisionStatus.Invalidated,
                DominantSide: TradeSide.Unknown,
                DominancePercent: 0m,
                InstitutionalCount: 0,
                InstitutionalPurityPercent: 0m,
                PRef: 0m,
                PRefTime: default,
                RejectionReason: reason);
        }

        private static decimal ResolvePurityPercent(int institutionalCount, int institutionalAlignedCount)
        {
            if (institutionalCount <= 0)
            {
                return 100m;
            }

            return ((decimal)institutionalAlignedCount / institutionalCount) * 100m;
        }

        private static decimal ResolveLockedDominancePercent(ActiveCandidate candidate)
        {
            if (candidate == null || candidate.TotalVolume <= 0m)
            {
                return 0m;
            }

            var lockedVolume = candidate.LockedDominantSide == TradeSide.Buy
                ? candidate.BuyVolume
                : candidate.SellVolume;
            return (lockedVolume / candidate.TotalVolume) * 100m;
        }

        private sealed class ActiveCandidate
        {
            public ActiveCandidate(
                TradeSide lockedDominantSide,
                decimal pRef,
                DateTime pRefTime,
                DateTime lastExchangeTime,
                decimal thresholdIatMicroseconds,
                decimal totalVolume,
                decimal buyVolume,
                decimal sellVolume,
                int institutionalCount,
                int institutionalAlignedCount)
            {
                LockedDominantSide = lockedDominantSide;
                PRef = pRef;
                PRefTime = pRefTime;
                LastExchangeTime = lastExchangeTime;
                ThresholdIatMicroseconds = thresholdIatMicroseconds;
                TotalVolume = totalVolume;
                BuyVolume = buyVolume;
                SellVolume = sellVolume;
                InstitutionalCount = institutionalCount;
                InstitutionalAlignedCount = institutionalAlignedCount;
            }

            public TradeSide LockedDominantSide { get; }
            public decimal PRef { get; }
            public DateTime PRefTime { get; }
            public DateTime LastExchangeTime { get; set; }
            public decimal ThresholdIatMicroseconds { get; }
            public decimal TotalVolume { get; set; }
            public decimal BuyVolume { get; set; }
            public decimal SellVolume { get; set; }
            public int InstitutionalCount { get; set; }
            public int InstitutionalAlignedCount { get; set; }
        }
    }
}
