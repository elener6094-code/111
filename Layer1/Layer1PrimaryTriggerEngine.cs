using System;
using System.Collections.Generic;
using QuantConnect.Algorithm.CSharp.DataEligibility;

namespace QuantConnect.Algorithm.CSharp.Layer1
{
    /// <summary>
    /// Milestone 3 Layer 1 engine:
    /// - Baseline warmup/rebuild lifecycle.
    /// - Minute-boundary BaseIAT recalculation.
    /// - Freeze/unfreeze anomaly protection.
    /// - Strict X-consecutive fast-sequence trigger signaling.
    /// </summary>
    public sealed class Layer1PrimaryTriggerEngine
    {
        private readonly int _wMinutes;
        private readonly int _xConsecutive;
        private readonly decimal _mSpeedMultiplier;

        private DateTime? _warmupStart;
        private DateTime? _lastBaselineRecalcMinute;

        private EligibleConsolidatedTransaction _previousTransaction;
        private bool _hasPreviousTransaction;

        private bool _isFrozen;
        private decimal _frozenBaseIatMicroseconds;
        private decimal? _baseIatMicroseconds;
        private bool _signalEmittedInCurrentFrozenSequence;

        private readonly List<IatSample> _iatSamples = new();
        private readonly List<EligibleConsolidatedTransaction> _currentFastSequence = new();

        public bool IsBaselineReady => _baseIatMicroseconds.HasValue && _baseIatMicroseconds.Value > 0m;

        public Layer1PrimaryTriggerEngine(int wMinutes, int xConsecutive, decimal mSpeedMultiplier)
        {
            _wMinutes = wMinutes;
            _xConsecutive = xConsecutive;
            _mSpeedMultiplier = mSpeedMultiplier;
        }

        public bool HasInProgressFastSequence => _isFrozen || _currentFastSequence.Count > 0;

        public IReadOnlyList<PrimaryTriggerSignal> ProcessBatch(IReadOnlyList<EligibleConsolidatedTransaction> consolidated)
        {
            if (consolidated == null || consolidated.Count == 0)
            {
                return Array.Empty<PrimaryTriggerSignal>();
            }

            var emitted = new List<PrimaryTriggerSignal>();
            for (var i = 0; i < consolidated.Count; i++)
            {
                var signal = ProcessOne(consolidated[i], allowSignalEmission: true);
                if (signal != null)
                {
                    emitted.Add(signal);
                }
            }

            return emitted;
        }

        public void ResetForOpenLifecycle()
        {
            _isFrozen = false;
            _frozenBaseIatMicroseconds = 0m;
            _signalEmittedInCurrentFrozenSequence = false;
            _currentFastSequence.Clear();
        }

        public void ThawBaseIatFromBufferedSamples(DateTime now)
        {
            _isFrozen = false;
            _frozenBaseIatMicroseconds = 0m;
            _signalEmittedInCurrentFrozenSequence = false;
            _currentFastSequence.Clear();
            RecalculateBaseIatImmediate(now);
        }

        public void ClearFastSequenceOnPauseStart(DateTime now)
        {
            if (!HasInProgressFastSequence)
            {
                return;
            }

            ThawBaseIatFromBufferedSamples(now);
        }

        public void ResetForNewRthSession()
        {
            _warmupStart = null;
            _lastBaselineRecalcMinute = null;
            _previousTransaction = null;
            _hasPreviousTransaction = false;
            _isFrozen = false;
            _frozenBaseIatMicroseconds = 0m;
            _baseIatMicroseconds = null;
            _signalEmittedInCurrentFrozenSequence = false;
            _iatSamples.Clear();
            _currentFastSequence.Clear();
        }

        public void ObserveBatchDuringOpen(IReadOnlyList<EligibleConsolidatedTransaction> consolidated)
        {
            if (consolidated == null || consolidated.Count == 0)
            {
                return;
            }

            for (var i = 0; i < consolidated.Count; i++)
            {
                ProcessOne(consolidated[i], allowSignalEmission: false);
            }
        }

        private PrimaryTriggerSignal ProcessOne(EligibleConsolidatedTransaction current, bool allowSignalEmission)
        {
            if (!_hasPreviousTransaction)
            {
                _previousTransaction = current;
                _hasPreviousTransaction = true;
                _warmupStart = current.ExchangeTime;
                return null;
            }

            var iatMicroseconds = (decimal)(current.ExchangeTime - _previousTransaction.ExchangeTime).Ticks / 10m;
            if (iatMicroseconds <= 0m)
            {
                _previousTransaction = current;
                return null;
            }

            var activeBase = _isFrozen ? _frozenBaseIatMicroseconds : _baseIatMicroseconds;
            var hasComparableBase = activeBase.HasValue && activeBase.Value > 0m;
            var threshold = hasComparableBase ? activeBase.Value / _mSpeedMultiplier : 0m;
            var isQualifyingFast = hasComparableBase && iatMicroseconds <= threshold;

            // Universal sampling: all transactions remain baseline-eligible, regardless of freeze/fast status.
            _iatSamples.Add(new IatSample(
                Timestamp: current.ExchangeTime,
                IatMicroseconds: iatMicroseconds,
                EligibleForBaseline: true));
            PruneOldSamples(current.ExchangeTime);

            if (IsWarmupComplete(current.ExchangeTime))
            {
                RecalculateBaseIatAtMinuteBoundary(current.ExchangeTime);
            }

            if (!hasComparableBase)
            {
                _previousTransaction = current;
                return null;
            }

            if (_isFrozen)
            {
                if (!isQualifyingFast)
                {
                    _isFrozen = false;
                    _signalEmittedInCurrentFrozenSequence = false;
                    _currentFastSequence.Clear();
                    RecalculateBaseIatImmediate(current.ExchangeTime);
                    _previousTransaction = current;
                    return null;
                }

                _currentFastSequence.Add(current);
                _previousTransaction = current;
                if (!allowSignalEmission)
                {
                    return null;
                }

                return TryEmitSignal(current, activeBase.Value, threshold);
            }

            if (!allowSignalEmission)
            {
                _previousTransaction = current;
                return null;
            }

            if (isQualifyingFast)
            {
                _isFrozen = true;
                _frozenBaseIatMicroseconds = _baseIatMicroseconds.Value;
                _signalEmittedInCurrentFrozenSequence = false;
                _currentFastSequence.Clear();
                _currentFastSequence.Add(current);

                _previousTransaction = current;
                return TryEmitSignal(current, _frozenBaseIatMicroseconds, _frozenBaseIatMicroseconds / _mSpeedMultiplier);
            }

            _currentFastSequence.Clear();
            _previousTransaction = current;
            return null;
        }

        private PrimaryTriggerSignal TryEmitSignal(
            EligibleConsolidatedTransaction current,
            decimal baseIatMicroseconds,
            decimal thresholdIatMicroseconds)
        {
            if (_signalEmittedInCurrentFrozenSequence || _currentFastSequence.Count < _xConsecutive)
            {
                return null;
            }

            var window = new List<EligibleConsolidatedTransaction>(_currentFastSequence);
            _signalEmittedInCurrentFrozenSequence = true;
            return new PrimaryTriggerSignal(
                TriggerTime: current.ExchangeTime,
                BaseIatMicroseconds: baseIatMicroseconds,
                ThresholdIatMicroseconds: thresholdIatMicroseconds,
                PRef: window[0].LastPrice,
                Window: window);
        }

        private bool IsWarmupComplete(DateTime now)
        {
            if (!_warmupStart.HasValue)
            {
                return false;
            }

            return now - _warmupStart.Value >= TimeSpan.FromMinutes(_wMinutes);
        }

        private void RecalculateBaseIatAtMinuteBoundary(DateTime now)
        {
            var minute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind);
            if (_lastBaselineRecalcMinute.HasValue && _lastBaselineRecalcMinute.Value == minute)
            {
                return;
            }

            _lastBaselineRecalcMinute = minute;
            RecalculateBaseIat(now);
        }

        private void RecalculateBaseIatImmediate(DateTime now)
        {
            RecalculateBaseIat(now);
        }

        private void RecalculateBaseIat(DateTime now)
        {
            PruneOldSamples(now);

            decimal sum = 0m;
            int count = 0;
            for (var i = 0; i < _iatSamples.Count; i++)
            {
                var sample = _iatSamples[i];
                if (!sample.EligibleForBaseline)
                {
                    continue;
                }

                sum += sample.IatMicroseconds;
                count++;
            }

            if (count > 0)
            {
                _baseIatMicroseconds = sum / count;
            }
        }

        private void PruneOldSamples(DateTime now)
        {
            var cutoff = now - TimeSpan.FromMinutes(_wMinutes);
            _iatSamples.RemoveAll(sample => sample.Timestamp < cutoff);
        }

        private sealed record IatSample(
            DateTime Timestamp,
            decimal IatMicroseconds,
            bool EligibleForBaseline
        );
    }
}
