using System;
using System.Linq;
using QuantConnect.Algorithm;
using QuantConnect.Algorithm.CSharp.DataEligibility;
using QuantConnect.Algorithm.CSharp.Layer2;
using QuantConnect.Algorithm.CSharp.Layer3;
using QuantConnect.Algorithm.CSharp.Layer4;
using QuantConnect.Algorithm.CSharp.Layer7;
using QuantConnect.Orders;

namespace QuantConnect.Algorithm.CSharp.Debug
{
    internal sealed class NewsShockPostLayer2DebugLogger
    {
        private readonly QCAlgorithm _algorithm;
        private readonly Func<DateTime> _exchangeTimestamp;
        private readonly Func<decimal> _portfolioQuantity;

        private bool _enabled;
        private int _exitEvalLogs;
        private int? _pendingExitSkipOrderId;
        private int _openWithoutLayer7Logs;

        public NewsShockPostLayer2DebugLogger(
            QCAlgorithm algorithm,
            Func<DateTime> exchangeTimestamp,
            Func<decimal> portfolioQuantity)
        {
            _algorithm = algorithm;
            _exchangeTimestamp = exchangeTimestamp;
            _portfolioQuantity = portfolioQuantity;
        }

        public void OnLayer2Confirmed(DateTime exchangeTime, Layer2VerificationDecision decision, StrategyLifecycleState state)
        {
            _enabled = true;
            Log(
                $"Event=Layer2Confirmed ExchangeTs={exchangeTime:O} State={state} " +
                $"Side={decision.DominantSide} PRef={decision.PRef} Dominance={decision.DominancePercent:F4}% " +
                $"InstitutionalCount={decision.InstitutionalCount} Purity={decision.InstitutionalPurityPercent:F4}%");
        }

        public void OnEntrySubmitted(
            DateTime exchangeTime,
            int orderId,
            int quantity,
            TradeSide side,
            decimal pRef,
            StrategyLifecycleState state)
        {
            Log(
                $"Event=EntrySubmitted ExchangeTs={exchangeTime:O} OrderId={orderId} " +
                $"Qty={quantity} Side={side} PRef={pRef} State={state}");
        }

        public void LogEvent(string message)
        {
            Log(message);
        }

        public void OnEntryFilled(
            DateTime exchangeTime,
            PositionProtectionPlan plan,
            Layer7ExitEngine layer7Engine,
            StrategyLifecycleState state,
            OrderEvent orderEvent = null,
            decimal? fillPrice = null,
            string source = null)
        {
            _exitEvalLogs = 0;
            _openWithoutLayer7Logs = 0;

            if (orderEvent != null)
            {
                Log(
                    $"Event=EntryFilled ExchangeTs={exchangeTime:O} FillPx={orderEvent.FillPrice} " +
                    $"FillQty={orderEvent.FillQuantity} PortfolioQty={_portfolioQuantity()} State={state} " +
                    $"PlanDir={plan.Direction} PlanQty={plan.EntryQuantity} PRef={plan.PRefAnchor} " +
                    $"EntryFill={plan.EntryFillPriceAnchor} HardStop={plan.InitialHardStopPrice} " +
                    $"Targets=[{string.Join(", ", plan.Targets.Select(t => $"#{t.Index}@{t.TargetPrice}x{t.Quantity}"))}] " +
                    $"L7Remaining={layer7Engine.RemainingQuantity} L7Stop={layer7Engine.CurrentStopPrice}");
                return;
            }

            Log(
                $"Event=EntryFilledBootstrap ExchangeTs={exchangeTime:O} Source={source} FillPx={fillPrice} " +
                $"PortfolioQty={_portfolioQuantity()} State={state} PlanDir={plan.Direction} PlanQty={plan.EntryQuantity} " +
                $"PRef={plan.PRefAnchor} EntryFill={plan.EntryFillPriceAnchor} HardStop={plan.InitialHardStopPrice} " +
                $"Targets=[{string.Join(", ", plan.Targets.Select(t => $"#{t.Index}@{t.TargetPrice}x{t.Quantity}"))}] " +
                $"L7Remaining={layer7Engine.RemainingQuantity} L7Stop={layer7Engine.CurrentStopPrice}");
        }

        public void OnOpenWithoutLayer7(
            EligibleConsolidatedTransaction tx,
            int? activeEntryOrderId,
            int? activeExitOrderId,
            bool pendingEntry)
        {
            if (_portfolioQuantity() == 0 || _openWithoutLayer7Logs >= 5)
            {
                return;
            }

            _openWithoutLayer7Logs++;
            Log(
                $"Event=OpenWithoutLayer7#{_openWithoutLayer7Logs} ExchangeTs={tx.ExchangeTime:O} Px={tx.LastPrice} " +
                $"PortfolioQty={_portfolioQuantity()} ActiveEntryOrderId={activeEntryOrderId} " +
                $"ActiveExitOrderId={activeExitOrderId} PendingEntry={pendingEntry}");
        }

        public void OnExitEvalSkippedLayer7Null(StrategyLifecycleState state)
        {
            Log(
                $"Event=ExitEvalSkipped ExchangeTs={_exchangeTimestamp():O} Reason=Layer7Null " +
                $"PortfolioQty={_portfolioQuantity()} State={state}");
        }

        public void OnExitEvalSkippedPendingOrder(int orderId, int layer7Remaining)
        {
            if (_pendingExitSkipOrderId == orderId)
            {
                return;
            }

            _pendingExitSkipOrderId = orderId;
            Log(
                $"Event=ExitEvalSkipped ExchangeTs={_exchangeTimestamp():O} Reason=PendingExitOrder " +
                $"ActiveExitOrderId={orderId} L7Remaining={layer7Remaining} PortfolioQty={_portfolioQuantity()}");
        }

        public void OnExitEvalClearedPendingOrder()
        {
            _pendingExitSkipOrderId = null;
        }

        public void OnExitEvaluated(
            EligibleConsolidatedTransaction tx,
            Layer7ExitEngine layer7Engine,
            ExitAction action)
        {
            var diagnostics = layer7Engine.GetEvaluationDiagnostics(tx);
            var shouldLog = action.Type != ExitActionType.None
                || diagnostics.HardStopTriggered
                || diagnostics.PanicTriggered
                || diagnostics.TakeProfitTouched
                || diagnostics.PendingExecution
                || _exitEvalLogs < 3;

            if (!shouldLog)
            {
                return;
            }

            if (_exitEvalLogs < 3)
            {
                _exitEvalLogs++;
            }

            Log(
                $"Event=ExitEval ExchangeTs={tx.ExchangeTime:O} Px={tx.LastPrice} Side={tx.Side} " +
                $"Action={action.Type} ActionQty={action.Quantity} ActionReason={action.Reason} " +
                $"L7Remaining={diagnostics.RemainingQuantity} L7Stop={diagnostics.CurrentStopPrice} " +
                $"TargetIdx={diagnostics.TargetIndex} TargetRemaining={diagnostics.CurrentTargetRemaining} " +
                $"NextTarget={diagnostics.NextTargetPrice} HardStopHit={diagnostics.HardStopTriggered} " +
                $"TpTouched={diagnostics.TakeProfitTouched} TpQty={diagnostics.TakeProfitQuantityIfTouched} " +
                $"PanicHit={diagnostics.PanicTriggered} PanicReason={diagnostics.PanicReason} " +
                $"CumCounterPct={diagnostics.CumulativeCounterPct:F4} MicroCounterPct={diagnostics.MicroCounterPct:F4} " +
                $"MicroFailures={diagnostics.MicroFailureHits} PendingExec={diagnostics.PendingExecution} " +
                $"PendingType={diagnostics.PendingType} PendingQty={diagnostics.PendingQuantity} " +
                $"PortfolioQty={_portfolioQuantity()}");
        }

        public void OnExitOrderSubmit(
            EligibleConsolidatedTransaction tx,
            ExitAction action,
            int orderQuantity,
            int layer7Remaining,
            decimal layer7Stop)
        {
            Log(
                $"Event=ExitOrderSubmit ExchangeTs={tx.ExchangeTime:O} Type={action.Type} " +
                $"ActionQty={action.Quantity} OrderQty={orderQuantity} Px={tx.LastPrice} " +
                $"Reason={action.Reason} PortfolioQty={_portfolioQuantity()} " +
                $"L7Remaining={layer7Remaining} L7Stop={layer7Stop}");
        }

        public void OnExitOrderSubmitted(DateTime exchangeTime, int orderId, ExitActionType type, int orderQuantity)
        {
            Log(
                $"Event=ExitOrderSubmitted ExchangeTs={exchangeTime:O} OrderId={orderId} " +
                $"Tag={type} Qty={orderQuantity}");
        }

        public void OnExitOrderEvent(
            OrderEvent orderEvent,
            string exitType,
            Layer7ExitEngine layer7Engine)
        {
            Log(
                $"Event=ExitOrderEvent ExchangeTs={_exchangeTimestamp():O} OrderId={orderEvent.OrderId} " +
                $"Tag={exitType} Status={orderEvent.Status} FillPx={orderEvent.FillPrice} " +
                $"FillQty={orderEvent.FillQuantity} PortfolioQty={_portfolioQuantity()} " +
                $"L7Null={layer7Engine == null} L7Closed={layer7Engine?.IsClosed} " +
                $"L7Remaining={layer7Engine?.RemainingQuantity}");
        }

        public void OnExitFillApplied(
            string exitType,
            int appliedQty,
            Layer7ExitEngine layer7Engine,
            ExitExecutionUpdate executionUpdate)
        {
            Log(
                $"Event=ExitFillApplied ExchangeTs={_exchangeTimestamp():O} Tag={exitType} " +
                $"AppliedQty={appliedQty} L7Remaining={layer7Engine.RemainingQuantity} " +
                $"L7Closed={layer7Engine.IsClosed} L7Stop={layer7Engine.CurrentStopPrice} " +
                $"StopAdjusted={executionUpdate.StopAdjusted} PortfolioQty={_portfolioQuantity()}");
        }

        public void OnExitMismatch(string message)
        {
            Log($"Event=ExitMismatch {message}");
        }

        public void OnRthForcedCloseSubmit(DateTime exchangeTime, int orderId, decimal orderQuantity)
        {
            Log(
                $"Event=RthForcedCloseSubmit ExchangeTs={exchangeTime:O} OrderId={orderId} " +
                $"Qty={orderQuantity} PortfolioQty={_portfolioQuantity()}");
        }

        public void OnPositionFullyClosed(DateTime exchangeTime, StrategyLifecycleState state, string reason)
        {
            Log($"Event=PositionFullyClosed ExchangeTs={exchangeTime:O} State={state} Reason={reason}");
        }

        private void Log(string message)
        {
            if (!_enabled)
            {
                return;
            }

            _algorithm.Debug(message);
        }
    }
}
