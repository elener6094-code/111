using System;
using System.Collections.Generic;
using QuantConnect.Algorithm.CSharp.DataEligibility;

namespace QuantConnect.Algorithm.CSharp.Layer3
{
    /// <summary>
    /// Milestone 6 core planning for entry anchors and initial protection structure.
    /// </summary>
    public sealed class Layer3ProtectionPlanner
    {
        private readonly int _tpParts;
        private readonly decimal _bPercentStep;
        private readonly decimal _stopLossPercentFromPRef;
        private readonly decimal _vMaxDistancePercentFromEntry;

        public Layer3ProtectionPlanner(
            int tpParts,
            decimal bPercentStep,
            decimal stopLossPercentFromPRef,
            decimal vMaxDistancePercentFromEntry)
        {
            _tpParts = tpParts;
            _bPercentStep = bPercentStep;
            _stopLossPercentFromPRef = stopLossPercentFromPRef;
            _vMaxDistancePercentFromEntry = vMaxDistancePercentFromEntry;
        }

        public int ResolveEntryQuantity(TradeSide side, int configuredSize)
        {
            if (side == TradeSide.Buy)
            {
                return configuredSize;
            }

            if (side == TradeSide.Sell)
            {
                return -configuredSize;
            }

            throw new InvalidOperationException("Cannot resolve entry quantity for unknown side.");
        }

        public PositionProtectionPlan BuildPlan(
            TradeSide side,
            int absoluteEntryQuantity,
            decimal pRef,
            decimal entryFillPrice)
        {
            if (side == TradeSide.Unknown)
            {
                throw new InvalidOperationException("Cannot build protection plan for unknown side.");
            }

            if (absoluteEntryQuantity <= 0)
            {
                throw new InvalidOperationException("Entry quantity must be positive.");
            }

            var initialHardStop = ComputeInitialHardStop(side, pRef, entryFillPrice);
            var targets = BuildTargets(side, absoluteEntryQuantity, entryFillPrice);

            return new PositionProtectionPlan(
                Direction: side,
                EntryQuantity: absoluteEntryQuantity,
                PRefAnchor: pRef,
                EntryFillPriceAnchor: entryFillPrice,
                InitialHardStopPrice: initialHardStop,
                Targets: targets);
        }

        private decimal ComputeInitialHardStop(TradeSide side, decimal pRef, decimal entryFillPrice)
        {
            var stopDistanceFromPRef = pRef * (_stopLossPercentFromPRef / 100m);
            var rawStop = side == TradeSide.Buy
                ? pRef - stopDistanceFromPRef
                : pRef + stopDistanceFromPRef;

            // Risk-limitation clamp: stop cannot be farther than V% from EntryFillPrice.
            var maxDistance = entryFillPrice * (_vMaxDistancePercentFromEntry / 100m);
            if (side == TradeSide.Buy)
            {
                var minimumAllowed = entryFillPrice - maxDistance;
                return Math.Max(rawStop, minimumAllowed);
            }

            var maximumAllowed = entryFillPrice + maxDistance;
            return Math.Min(rawStop, maximumAllowed);
        }

        private IReadOnlyList<ProtectionTarget> BuildTargets(
            TradeSide side,
            int absoluteEntryQuantity,
            decimal entryFillPrice)
        {
            var targets = new List<ProtectionTarget>(_tpParts);
            var basePartQty = absoluteEntryQuantity / _tpParts;
            var remainder = absoluteEntryQuantity - (basePartQty * _tpParts);

            var directionMultiplier = side == TradeSide.Buy ? 1m : -1m;
            for (var i = 1; i <= _tpParts; i++)
            {
                var stepDistance = entryFillPrice * (_bPercentStep / 100m) * i;
                var targetPrice = entryFillPrice + (directionMultiplier * stepDistance);
                var quantity = basePartQty;
                if (i == _tpParts)
                {
                    quantity += remainder;
                }

                targets.Add(new ProtectionTarget(
                    Index: i,
                    TargetPrice: targetPrice,
                    Quantity: quantity));
            }

            return targets;
        }
    }
}
