using System;
using System.Collections.Generic;
using QuantConnect.Algorithm.CSharp.DataEligibility;
using QuantConnect.Algorithm.CSharp.Layer3;

namespace QuantConnect.Algorithm.CSharp.Layer7
{
    public enum ExitActionType
    {
        None = 0,
        Panic = 1,
        HardStop = 2,
        TakeProfit = 3
    }

    public sealed record ExitAction(
        ExitActionType Type,
        int Quantity,
        string Reason
    );

    public sealed record ExitExecutionUpdate(
        bool PositionClosed,
        bool StopAdjusted,
        decimal CurrentStopPrice
    );

    public sealed record ExitEvaluationDiagnostics(
        int RemainingQuantity,
        decimal CurrentStopPrice,
        int TargetIndex,
        int CurrentTargetRemaining,
        decimal? NextTargetPrice,
        bool PendingExecution,
        ExitActionType PendingType,
        int PendingQuantity,
        decimal CumulativeCounterPct,
        int MicroFailureHits,
        decimal MicroCounterPct,
        bool HardStopTriggered,
        bool TakeProfitTouched,
        int TakeProfitQuantityIfTouched,
        bool PanicTriggered,
        string PanicReason
    );

    /// <summary>
    /// Milestone 7 exit controller.
    /// Enforces Panic > HardStop > TakeProfit precedence.
    /// </summary>
    public sealed class Layer7ExitEngine
    {
        private readonly PositionProtectionPlan _plan;
        private readonly decimal _fPanicCounterPressurePct;
        private readonly decimal _gMicroCounterPct;
        private readonly int _hMicroWindowMs;
        private readonly int _jMicroFailureCount;
        private readonly decimal _bStepPct;
        private readonly DateTime _pRefTime;

        private readonly Queue<WindowPoint> _microWindow = new();
        private decimal _microTotalVolume;
        private decimal _microCounterVolume;
        private int _microFailureHits;

        private decimal _expandingTotalVolume;
        private decimal _expandingCounterVolume;

        private int _remainingQuantity;
        private int _targetIndex;
        private int _currentTargetRemaining;
        private decimal _currentStopPrice;

        private bool _pendingExecution;
        private ExitActionType _pendingType;
        private int _pendingQuantity;

        public Layer7ExitEngine(
            PositionProtectionPlan plan,
            decimal fPanicCounterPressurePct,
            decimal gMicroCounterPct,
            int hMicroWindowMs,
            int jMicroFailureCount,
            decimal bStepPct,
            DateTime pRefTime)
        {
            _plan = plan;
            _fPanicCounterPressurePct = fPanicCounterPressurePct;
            _gMicroCounterPct = gMicroCounterPct;
            _hMicroWindowMs = hMicroWindowMs;
            _jMicroFailureCount = jMicroFailureCount;
            _bStepPct = bStepPct;
            _pRefTime = pRefTime;

            _remainingQuantity = plan.EntryQuantity;
            _currentStopPrice = plan.InitialHardStopPrice;
            if (plan.Targets.Count > 0)
            {
                _currentTargetRemaining = plan.Targets[0].Quantity;
            }
        }

        public bool IsClosed => _remainingQuantity <= 0;
        public decimal CurrentStopPrice => _currentStopPrice;
        public int RemainingQuantity => _remainingQuantity;

        public void SeedFromHistory(IReadOnlyList<EligibleConsolidatedTransaction> history)
        {
            if (history == null || history.Count == 0)
            {
                return;
            }

            for (var i = 0; i < history.Count; i++)
            {
                if (history[i].ExchangeTime >= _pRefTime)
                {
                    UpdatePanicTrackers(history[i]);
                }
            }
        }

        public ExitEvaluationDiagnostics GetEvaluationDiagnostics(EligibleConsolidatedTransaction tx)
        {
            if (tx == null)
            {
                return new ExitEvaluationDiagnostics(
                    RemainingQuantity: _remainingQuantity,
                    CurrentStopPrice: _currentStopPrice,
                    TargetIndex: _targetIndex,
                    CurrentTargetRemaining: _currentTargetRemaining,
                    NextTargetPrice: _targetIndex < _plan.Targets.Count
                        ? _plan.Targets[_targetIndex].TargetPrice
                        : null,
                    PendingExecution: _pendingExecution,
                    PendingType: _pendingType,
                    PendingQuantity: _pendingQuantity,
                    CumulativeCounterPct: 0m,
                    MicroFailureHits: _microFailureHits,
                    MicroCounterPct: 0m,
                    HardStopTriggered: false,
                    TakeProfitTouched: false,
                    TakeProfitQuantityIfTouched: 0,
                    PanicTriggered: false,
                    PanicReason: string.Empty);
            }

            var price = tx.LastPrice;
            var hardStopTriggered = !IsClosed && IsHardStopTriggered(price);
            var takeProfitTouched = TryGetTakeProfitQuantity(price, out var tpQty, out _);
            var panicTriggered = IsPanicTriggered(out var panicReason);

            decimal? nextTargetPrice = _targetIndex < _plan.Targets.Count
                ? _plan.Targets[_targetIndex].TargetPrice
                : null;

            var cumulativeCounterPct = _expandingTotalVolume > 0m
                ? (_expandingCounterVolume / _expandingTotalVolume) * 100m
                : 0m;

            var microCounterPct = _microTotalVolume > 0m
                ? (_microCounterVolume / _microTotalVolume) * 100m
                : 0m;

            return new ExitEvaluationDiagnostics(
                RemainingQuantity: _remainingQuantity,
                CurrentStopPrice: _currentStopPrice,
                TargetIndex: _targetIndex,
                CurrentTargetRemaining: _currentTargetRemaining,
                NextTargetPrice: nextTargetPrice,
                PendingExecution: _pendingExecution,
                PendingType: _pendingType,
                PendingQuantity: _pendingQuantity,
                CumulativeCounterPct: cumulativeCounterPct,
                MicroFailureHits: _microFailureHits,
                MicroCounterPct: microCounterPct,
                HardStopTriggered: hardStopTriggered,
                TakeProfitTouched: takeProfitTouched,
                TakeProfitQuantityIfTouched: tpQty,
                PanicTriggered: panicTriggered,
                PanicReason: panicReason);
        }

        public ExitAction Evaluate(EligibleConsolidatedTransaction tx)
        {
            if (IsClosed || _pendingExecution || tx == null)
            {
                return new ExitAction(ExitActionType.None, 0, string.Empty);
            }

            if (tx.ExchangeTime >= _pRefTime)
            {
                UpdatePanicTrackers(tx);
            }

            // Priority 1: Panic
            if (IsPanicTriggered(out var panicReason))
            {
                return BuildAction(ExitActionType.Panic, _remainingQuantity, panicReason);
            }

            // Priority 2: Hard stop
            if (IsHardStopTriggered(tx.LastPrice))
            {
                return BuildAction(
                    ExitActionType.HardStop,
                    _remainingQuantity,
                    $"Hard stop touched at Px={tx.LastPrice} Stop={_currentStopPrice}");
            }

            // Priority 3: Take profit
            if (TryGetTakeProfitQuantity(tx.LastPrice, out var tpQuantity, out var tpReason))
            {
                return BuildAction(ExitActionType.TakeProfit, tpQuantity, tpReason);
            }

            return new ExitAction(ExitActionType.None, 0, string.Empty);
        }

        public ExitExecutionUpdate OnExecutionFilled(int absoluteFillQuantity)
        {
            if (!_pendingExecution || absoluteFillQuantity <= 0)
            {
                return new ExitExecutionUpdate(
                    PositionClosed: IsClosed,
                    StopAdjusted: false,
                    CurrentStopPrice: _currentStopPrice);
            }

            if (_pendingType == ExitActionType.Panic || _pendingType == ExitActionType.HardStop)
            {
                _remainingQuantity = 0;
                _pendingExecution = false;
                _pendingType = ExitActionType.None;
                _pendingQuantity = 0;
                return new ExitExecutionUpdate(
                    PositionClosed: true,
                    StopAdjusted: false,
                    CurrentStopPrice: _currentStopPrice);
            }

            // Take-profit fill path.
            var applied = Math.Min(absoluteFillQuantity, _remainingQuantity);
            _remainingQuantity -= applied;
            var stopAdjusted = false;

            var tpApplied = applied;
            while (tpApplied > 0 && _targetIndex < _plan.Targets.Count)
            {
                if (tpApplied >= _currentTargetRemaining)
                {
                    tpApplied -= _currentTargetRemaining;
                    _targetIndex++;
                    if (_remainingQuantity > 0)
                    {
                        AdvanceStopByOneStep();
                        stopAdjusted = true;
                    }

                    _currentTargetRemaining = _targetIndex < _plan.Targets.Count
                        ? _plan.Targets[_targetIndex].Quantity
                        : 0;
                }
                else
                {
                    _currentTargetRemaining -= tpApplied;
                    tpApplied = 0;
                }
            }

            _pendingExecution = false;
            _pendingType = ExitActionType.None;
            _pendingQuantity = 0;
            return new ExitExecutionUpdate(
                PositionClosed: IsClosed,
                StopAdjusted: stopAdjusted,
                CurrentStopPrice: _currentStopPrice);
        }

        private ExitAction BuildAction(ExitActionType type, int quantity, string reason)
        {
            if (quantity <= 0)
            {
                return new ExitAction(ExitActionType.None, 0, string.Empty);
            }

            _pendingExecution = true;
            _pendingType = type;
            _pendingQuantity = quantity;
            return new ExitAction(type, quantity, reason);
        }

        private bool IsPanicTriggered(out string reason)
        {
            var cumulativeCounterPct = _expandingTotalVolume > 0m
                ? (_expandingCounterVolume / _expandingTotalVolume) * 100m
                : 0m;

            if (cumulativeCounterPct >= _fPanicCounterPressurePct)
            {
                reason = $"Panic cumulative counter-pressure F reached: {cumulativeCounterPct:F4}% >= {_fPanicCounterPressurePct:F4}%";
                return true;
            }

            if (_microFailureHits >= _jMicroFailureCount)
            {
                reason = $"Panic micro-test failures J reached: {_microFailureHits} >= {_jMicroFailureCount}";
                return true;
            }

            reason = string.Empty;
            return false;
        }

        private bool IsHardStopTriggered(decimal currentPrice)
        {
            if (_plan.Direction == TradeSide.Buy)
            {
                return currentPrice <= _currentStopPrice;
            }

            return currentPrice >= _currentStopPrice;
        }

        private bool TryGetTakeProfitQuantity(decimal currentPrice, out int quantity, out string reason)
        {
            if (_targetIndex >= _plan.Targets.Count || _remainingQuantity <= 0)
            {
                quantity = 0;
                reason = string.Empty;
                return false;
            }

            var target = _plan.Targets[_targetIndex];
            bool touched = _plan.Direction == TradeSide.Buy
                ? currentPrice >= target.TargetPrice
                : currentPrice <= target.TargetPrice;

            if (!touched)
            {
                quantity = 0;
                reason = string.Empty;
                return false;
            }

            quantity = Math.Min(_currentTargetRemaining, _remainingQuantity);
            reason = $"Take-profit target {target.Index} touched at Px={currentPrice} Target={target.TargetPrice}";
            return quantity > 0;
        }

        private void AdvanceStopByOneStep()
        {
            var step = _plan.EntryFillPriceAnchor * (_bStepPct / 100m);
            if (_plan.Direction == TradeSide.Buy)
            {
                _currentStopPrice += step;
            }
            else
            {
                _currentStopPrice -= step;
            }
        }

        private void UpdatePanicTrackers(EligibleConsolidatedTransaction tx)
        {
            var isCounter = IsCounterDirection(tx.Side);
            _expandingTotalVolume += tx.Volume;
            if (isCounter)
            {
                _expandingCounterVolume += tx.Volume;
            }

            _microWindow.Enqueue(new WindowPoint(tx.ExchangeTime, tx.Volume, isCounter));
            _microTotalVolume += tx.Volume;
            if (isCounter)
            {
                _microCounterVolume += tx.Volume;
            }

            var cutoff = tx.ExchangeTime - TimeSpan.FromMilliseconds(_hMicroWindowMs);
            while (_microWindow.Count > 0 && _microWindow.Peek().Timestamp < cutoff)
            {
                var old = _microWindow.Dequeue();
                _microTotalVolume -= old.Volume;
                if (old.IsCounter)
                {
                    _microCounterVolume -= old.Volume;
                }
            }

            var microCounterPct = _microTotalVolume > 0m
                ? (_microCounterVolume / _microTotalVolume) * 100m
                : 0m;
            if (microCounterPct >= _gMicroCounterPct)
            {
                _microFailureHits++;
            }
        }

        private bool IsCounterDirection(TradeSide side)
        {
            if (_plan.Direction == TradeSide.Buy)
            {
                return side == TradeSide.Sell;
            }

            return side == TradeSide.Buy;
        }

        private sealed record WindowPoint(
            DateTime Timestamp,
            decimal Volume,
            bool IsCounter
        );
    }
}
