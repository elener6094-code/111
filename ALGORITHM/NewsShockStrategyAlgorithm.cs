using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Algorithm.CSharp.DataEligibility;
using QuantConnect.Algorithm.CSharp.Layer1;
using QuantConnect.Algorithm.CSharp.Layer2;
using QuantConnect.Algorithm.CSharp.Layer3;
using QuantConnect.Algorithm.CSharp.Layer4;
using QuantConnect.Algorithm.CSharp.Layer7;
using QuantConnect.Algorithm.CSharp.Debug;
using QuantConnect.Securities;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// News Shock Strategy Engine - Milestone 1-8 implementation:
    /// - Defines and validates the full runtime contract.
    /// - Enforces trade-tick-only feed boundary for live and backtest.
    /// - Includes event-schema observability and acceptance closure checks.
    /// </summary>
    public sealed class NewsShockStrategyAlgorithm : QCAlgorithm
    {
        private NewsShockRuntimeConfig _config;
        private Security _security;
        private Symbol _symbol;
        private Symbol _githubSipSymbol;
        private EligibleConsolidatedStreamBuilder _eligibleStreamBuilder;
        private Layer1PrimaryTriggerEngine _layer1Engine;
        private Layer2VerificationEngine _layer2Engine;
        private Layer3ProtectionPlanner _layer3Planner;
        private Layer4LifecycleStateMachine _layer4StateMachine;
        private Layer7ExitEngine _layer7Engine;
        private int? _activeEntryOrderId;
        private int? _activeExitOrderId;
        private PendingEntryContext _pendingEntryContext;
        private PositionProtectionPlan _activeProtectionPlan;
        private readonly List<EligibleConsolidatedTransaction> _consolidatedHistory = new();
        private DateTime? _lastExchangeTimestamp;
        private DailyTimeWindow _tradingPauseWindow;
        private bool _isPauseWindowActive;
        private bool _pendingHardOffAfterForcedClose;
        private NewsShockPostLayer2DebugLogger _postLayer2Debug;

        private const string PauseStartInvalidationReason = "Trading pause window started.";
        private const string RthCloseInvalidationReason = "RTH close hard-off transition.";

        public override void Initialize()
        {
            _config = NewsShockRuntimeConfig.Load(this);
            SetStartDate(_config.StartDate);
            SetEndDate(_config.EndDate);

            // Live: QuantConnect U.S. equity tick stream. Backtest: GitHub SIP CSV.
            var security = LiveMode
                ? AddEquity(
                    _config.Symbol,
                    Resolution.Tick,
                    fillForward: false,
                    extendedMarketHours: true)
                : AddEquity(
                    _config.Symbol,
                    Resolution.Minute,
                    fillForward: false,
                    extendedMarketHours: true);

            if (!LiveMode)
            {
                _githubSipSymbol = AddData<GitHubSipTradeData>(security.Symbol, Resolution.Tick).Symbol;
            }

            // Preserve raw trade prices; avoid adjusted/bar-derived behavior.
            SubscriptionManager.Subscriptions
                .Where(config => config.Symbol == security.Symbol)
                .SetDataNormalizationMode(DataNormalizationMode.Raw);
            _security = security;
            _symbol = security.Symbol;
            _tradingPauseWindow = DailyTimeWindow.Parse(_config.TradingPauseWindow);
            _eligibleStreamBuilder = new EligibleConsolidatedStreamBuilder();
            _layer1Engine = new Layer1PrimaryTriggerEngine(
                _config.WMinutes,
                _config.XConsecutiveFastTrades,
                _config.MSpeedMultiplier);
            _layer2Engine = new Layer2VerificationEngine(
                _config.DDirectionalDominancePct,
                _config.NInstitutionalCountMin,
                _config.ZInstitutionalNotionalThreshold,
                _config.PInstitutionalPurityPct);
            _layer3Planner = new Layer3ProtectionPlanner(
                _config.TPParts,
                _config.BTargetSpacingPct,
                _config.StopLossPercent,
                _config.VMaxStopDistancePct);
            _layer4StateMachine = new Layer4LifecycleStateMachine();
            _postLayer2Debug = new NewsShockPostLayer2DebugLogger(
                this,
                ResolveExchangeTimestamp,
                () => Portfolio[_symbol].Quantity);

            ValidateAcceptanceClosure();
        }

        public override void OnData(Slice slice)
        {
            EnforceInputBoundary(slice);

            if (!TryGetInputTradeTicks(slice, out var ticks) || ticks.Count == 0)
            {
                return;
            }

            var inRthTicks = new List<Tick>(ticks.Count);
            for (var i = 0; i < ticks.Count; i++)
            {
                var tick = ticks[i];
                if (tick.TickType != TickType.Trade)
                {
                    continue;
                }

                if (IsInsideRegularTradingHours(tick.Time))
                {
                    inRthTicks.Add(tick);
                    continue;
                }

                HandleOutsideRthTick(tick.Time);
            }

            if (inRthTicks.Count == 0)
            {
                return;
            }

            var consolidated = _eligibleStreamBuilder.Build(inRthTicks);
            if (consolidated.Count == 0)
            {
                return;
            }

            for (var i = 0; i < consolidated.Count; i++)
            {
                ProcessConsolidatedTransaction(consolidated[i]);
            }
        }

        private void ProcessConsolidatedTransaction(EligibleConsolidatedTransaction tx)
        {
            if (!IsInsideRegularTradingHours(tx.ExchangeTime))
            {
                HandleOutsideRthTick(tx.ExchangeTime);
                return;
            }

            if (_layer4StateMachine.State == StrategyLifecycleState.HardOff)
            {
                StartRthSession();
            }

            EvaluateTradingPauseWindow(tx.ExchangeTime);
            AppendHistory(tx);

            if (_layer4StateMachine.State == StrategyLifecycleState.Entering ||
                _layer4StateMachine.State == StrategyLifecycleState.Open)
            {
                ProcessHeldPositionLifecycle(new[] { tx });
                return;
            }

            if (_isPauseWindowActive)
            {
                _layer1Engine.ObserveBatchDuringOpen(new[] { tx });
                _ = _layer1Engine.IsBaselineReady && _layer4StateMachine.OnBaselineReady();

                return;
            }

            _ = _layer1Engine.IsBaselineReady && _layer4StateMachine.OnBaselineReady();

            if (!_layer4StateMachine.ShouldRunLayer1)
            {
                return;
            }

            DateTime? triggerTime = null;
            var signals = _layer1Engine.ProcessBatch(new[] { tx });
            for (var i = 0; i < signals.Count; i++)
            {
                var signal = signals[i];
                triggerTime = signal.TriggerTime;

                var directive = _layer4StateMachine.OnPrimaryTriggerFired(signal);
                if (!directive.ShouldDispatch)
                {
                    continue;
                }

                var startDecision = _layer2Engine.StartCandidate(directive.Trigger);
                if (startDecision.Status == Layer2DecisionStatus.Confirmed)
                {
                    EmitLayer2Confirmed(signal.TriggerTime, startDecision);
                    SubmitEntryOnLayer2Confirmation(startDecision, tx.LastPrice);
                    return;
                }

                if (startDecision.Status == Layer2DecisionStatus.Invalidated)
                {
                    InvalidateCandidate(startDecision.RejectionReason, signal.TriggerTime);
                    return;
                }
            }

            if (_layer4StateMachine.State != StrategyLifecycleState.Candidate || !_layer2Engine.HasActiveCandidate)
            {
                return;
            }

            if (triggerTime.HasValue && tx.ExchangeTime <= triggerTime.Value)
            {
                return;
            }

            var decision = _layer2Engine.EvaluateNext(tx);
            if (decision.Status == Layer2DecisionStatus.Monitoring)
            {
                return;
            }

            if (decision.Status == Layer2DecisionStatus.Confirmed)
            {
                EmitLayer2Confirmed(tx.ExchangeTime, decision);
                SubmitEntryOnLayer2Confirmation(decision, tx.LastPrice);
                return;
            }

            InvalidateCandidate(decision.RejectionReason, tx.ExchangeTime);
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (IsEntryOrderEvent(orderEvent))
            {
                _postLayer2Debug.LogEvent(
                    $"Event=EntryOrderEvent ExchangeTs={ResolveExchangeTimestamp():O} OrderId={orderEvent.OrderId} " +
                    $"Status={orderEvent.Status} FillPx={orderEvent.FillPrice} FillQty={orderEvent.FillQuantity} " +
                    $"PortfolioQty={Portfolio[_symbol].Quantity}");

                if (orderEvent.Status != OrderStatus.Filled)
                {
                    return;
                }

                if (_pendingEntryContext == null)
                {
                    throw new InvalidOperationException("Entry fill received without pending entry context.");
                }

                CompleteEntryFill(orderEvent.FillPrice, orderEvent, "OnOrderEvent");
                return;
            }

            if (!_activeExitOrderId.HasValue || orderEvent.OrderId != _activeExitOrderId.Value)
            {
                return;
            }

            var exitType = Transactions.GetOrderById(orderEvent.OrderId)?.Tag ?? "Unknown";
            _postLayer2Debug.OnExitOrderEvent(orderEvent, exitType, _layer7Engine);

            if (orderEvent.Status != OrderStatus.PartiallyFilled && orderEvent.Status != OrderStatus.Filled)
            {
                return;
            }
            if (string.Equals(exitType, "RTH_FORCED_CLOSE", StringComparison.Ordinal))
            {
                if (orderEvent.Status == OrderStatus.Filled)
                {
                    _activeExitOrderId = null;
                }

                if (_pendingHardOffAfterForcedClose && Portfolio[_symbol].Quantity == 0)
                {
                    var closeTime = ResolveExchangeTimestamp();
                    EmitPositionFullyClosed(closeTime, "RTH Forced Flat");
                    EnterHardOff(closeTime);
                }

                return;
            }

            if (_layer7Engine == null)
            {
                throw new InvalidOperationException("Exit fill received without active Layer 7 engine.");
            }

            var absoluteFill = Math.Abs((int)orderEvent.FillQuantity);
            var executionUpdate = _layer7Engine.OnExecutionFilled(absoluteFill);
            _postLayer2Debug.OnExitFillApplied(exitType, absoluteFill, _layer7Engine, executionUpdate);

            if (orderEvent.Status == OrderStatus.Filled)
            {
                _activeExitOrderId = null;
            }

            if (_layer7Engine.IsClosed && Portfolio[_symbol].Quantity == 0)
            {
                _activeProtectionPlan = null;
                _layer7Engine = null;
                if (_layer4StateMachine.OnPositionFullyClosed())
                {
                    var closeTime = ResolveExchangeTimestamp();
                    EmitPositionFullyClosed(closeTime, ResolvePositionExitReason(exitType));
                }
            }
            else if (_layer7Engine.IsClosed && Portfolio[_symbol].Quantity != 0)
            {
                _postLayer2Debug.OnExitMismatch(
                    $"L7Closed=true but PortfolioQty={Portfolio[_symbol].Quantity} " +
                    $"ExchangeTs={ResolveExchangeTimestamp():O} Tag={exitType}");
            }
            else if (!_layer7Engine.IsClosed && Portfolio[_symbol].Quantity == 0)
            {
                _postLayer2Debug.OnExitMismatch(
                    $"L7Closed=false L7Remaining={_layer7Engine.RemainingQuantity} " +
                    $"but PortfolioQty=0 ExchangeTs={ResolveExchangeTimestamp():O} Tag={exitType}");
            }

        }

        private void EnforceInputBoundary(Slice slice)
        {
            // PRD prohibits aggregated bars for core decision inputs (live tick feed only).
            if (LiveMode && (slice.Bars.Count > 0 || slice.QuoteBars.Count > 0))
            {
                throw new InvalidOperationException(
                    "Aggregated bars are prohibited by PRD input rules.");
            }

            // Deterministic, single-symbol contract for this strategy instance.
            if (LiveMode)
            {
                if (slice.Ticks.Count > 0 && !slice.Ticks.ContainsKey(_symbol))
                {
                    throw new InvalidOperationException(
                        "Received ticks for a non-configured symbol; violates runtime contract.");
                }

                return;
            }

            var githubSipRows = slice.Get<GitHubSipTradeData>();
            if (githubSipRows.Count > 0 && !githubSipRows.ContainsKey(_githubSipSymbol))
            {
                throw new InvalidOperationException(
                    "Received GitHub SIP rows for a non-configured symbol; violates runtime contract.");
            }
        }

        private bool TryGetInputTradeTicks(Slice slice, out IReadOnlyList<Tick> ticks)
        {
            if (LiveMode)
            {
                if (slice.Ticks.TryGetValue(_symbol, out var liveTicks) && liveTicks.Count > 0)
                {
                    ticks = liveTicks;
                    return true;
                }

                ticks = Array.Empty<Tick>();
                return false;
            }

            if (!slice.Get<GitHubSipTradeData>().TryGetValue(_githubSipSymbol, out var sipRow))
            {
                ticks = Array.Empty<Tick>();
                return false;
            }

            ticks = new[] { sipRow.ToTradeTick(_symbol) };
            return true;
        }

        private bool IsInsideRegularTradingHours(DateTime exchangeTime)
        {
            return _security.Exchange.Hours.IsOpen(exchangeTime, extendedMarketHours: false);
        }

        private void StartRthSession()
        {
            ResetSessionState();
            _layer4StateMachine.OnRthSessionOpen();
        }

        private void HandleOutsideRthTick(DateTime exchangeTime)
        {
            if (_layer4StateMachine.State == StrategyLifecycleState.HardOff && !_pendingHardOffAfterForcedClose)
            {
                return;
            }

            if (_pendingHardOffAfterForcedClose)
            {
                if (Portfolio[_symbol].Quantity == 0)
                {
                    EnterHardOff(exchangeTime);
                }

                return;
            }

            if (_layer4StateMachine.State == StrategyLifecycleState.Candidate && _layer2Engine.HasActiveCandidate)
            {
                var decision = _layer2Engine.ForceInvalidateActiveCandidate(RthCloseInvalidationReason);
                if (decision.Status == Layer2DecisionStatus.Invalidated)
                {
                    InvalidateCandidate(decision.RejectionReason, exchangeTime);
                }
            }

            if ((_layer4StateMachine.State == StrategyLifecycleState.Open ||
                 _layer4StateMachine.State == StrategyLifecycleState.Entering) &&
                Portfolio[_symbol].Quantity != 0)
            {
                SubmitRthForcedClose(exchangeTime);
                return;
            }

            EnterHardOff(exchangeTime);
        }

        private void SubmitRthForcedClose(DateTime exchangeTime)
        {
            if (_activeExitOrderId.HasValue)
            {
                _pendingHardOffAfterForcedClose = true;
                return;
            }

            var currentQuantity = Portfolio[_symbol].Quantity;
            if (currentQuantity == 0)
            {
                EnterHardOff(exchangeTime);
                return;
            }

            var closeTicket = MarketOrder(
                _symbol,
                -currentQuantity,
                asynchronous: false,
                tag: "RTH_FORCED_CLOSE");
            _activeExitOrderId = closeTicket.OrderId;
            _pendingHardOffAfterForcedClose = true;
            _postLayer2Debug.OnRthForcedCloseSubmit(exchangeTime, closeTicket.OrderId, -currentQuantity);
        }

        private void EnterHardOff(DateTime exchangeTime)
        {
            _ = _layer4StateMachine.OnHardOffEntered();
            ResetSessionState();
        }

        private void ResetSessionState()
        {
            _pendingHardOffAfterForcedClose = false;
            _isPauseWindowActive = false;
            _activeEntryOrderId = null;
            _activeExitOrderId = null;
            _pendingEntryContext = null;
            _activeProtectionPlan = null;
            _layer7Engine = null;
            _lastExchangeTimestamp = null;
            _consolidatedHistory.Clear();

            _eligibleStreamBuilder = new EligibleConsolidatedStreamBuilder();
            _layer1Engine.ResetForNewRthSession();
            _layer2Engine.ResetForNewRthSession();
        }

        private void EvaluateTradingPauseWindow(DateTime exchangeTime)
        {
            var isInsidePause = _tradingPauseWindow.Contains(exchangeTime.TimeOfDay);
            if (!_isPauseWindowActive && isInsidePause)
            {
                _isPauseWindowActive = true;
                OnTradingPauseWindowStarted(exchangeTime);
                return;
            }

            if (_isPauseWindowActive && !isInsidePause)
            {
                _isPauseWindowActive = false;
            }
        }

        private void OnTradingPauseWindowStarted(DateTime exchangeTime)
        {
            if (_layer4StateMachine.State == StrategyLifecycleState.Candidate && _layer2Engine.HasActiveCandidate)
            {
                var decision = _layer2Engine.ForceInvalidateActiveCandidate(PauseStartInvalidationReason);
                if (decision.Status == Layer2DecisionStatus.Invalidated)
                {
                    InvalidateCandidate(decision.RejectionReason, exchangeTime);
                }
                return;
            }

            if (_layer4StateMachine.State == StrategyLifecycleState.Armed)
            {
                _layer1Engine.ClearFastSequenceOnPauseStart(exchangeTime);
            }
        }

        private void ProcessHeldPositionLifecycle(IReadOnlyList<EligibleConsolidatedTransaction> consolidated)
        {
            TryBootstrapLayer7FromHeldPosition(consolidated[consolidated.Count - 1].LastPrice);

            if (_layer4StateMachine.State != StrategyLifecycleState.Open)
            {
                if (_layer7Engine == null && HasExpectedEntryHoldings())
                {
                    _postLayer2Debug.OnOpenWithoutLayer7(
                        consolidated[consolidated.Count - 1],
                        _activeEntryOrderId,
                        _activeExitOrderId,
                        _pendingEntryContext != null);
                }

                return;
            }

            _layer1Engine.ObserveBatchDuringOpen(consolidated);
            ProcessOpenPositionExits(consolidated);
        }

        private void ProcessOpenPositionExits(IReadOnlyList<EligibleConsolidatedTransaction> consolidated)
        {
            if (_layer7Engine == null)
            {
                if (Portfolio[_symbol].Quantity != 0)
                {
                    _postLayer2Debug.OnExitEvalSkippedLayer7Null(_layer4StateMachine.State);
                }

                return;
            }

            if (_activeExitOrderId.HasValue)
            {
                _postLayer2Debug.OnExitEvalSkippedPendingOrder(
                    _activeExitOrderId.Value,
                    _layer7Engine.RemainingQuantity);
                return;
            }

            _postLayer2Debug.OnExitEvalClearedPendingOrder();

            for (var i = 0; i < consolidated.Count; i++)
            {
                var tx = consolidated[i];
                var action = _layer7Engine.Evaluate(tx);
                _postLayer2Debug.OnExitEvaluated(tx, _layer7Engine, action);

                if (action.Type == ExitActionType.None || action.Quantity <= 0)
                {
                    continue;
                }

                int closeQuantity = _activeProtectionPlan.Direction == TradeSide.Buy
                    ? -action.Quantity
                    : action.Quantity;

                _postLayer2Debug.OnExitOrderSubmit(
                    tx,
                    action,
                    closeQuantity,
                    _layer7Engine.RemainingQuantity,
                    _layer7Engine.CurrentStopPrice);

                var exitTicket = MarketOrder(
                    _symbol,
                    closeQuantity,
                    asynchronous: true,
                    tag: action.Type.ToString());
                _activeExitOrderId = exitTicket.OrderId;
                _postLayer2Debug.OnExitOrderSubmitted(tx.ExchangeTime, exitTicket.OrderId, action.Type, closeQuantity);
                break;
            }
        }

        private void AppendHistory(EligibleConsolidatedTransaction tx)
        {
            SyncSecurityMarketPrice(tx.LastPrice, tx.ExchangeTime);

            _consolidatedHistory.Add(tx);
            _lastExchangeTimestamp = tx.ExchangeTime;

            const int maxHistory = 200000;
            if (_consolidatedHistory.Count > maxHistory)
            {
                _consolidatedHistory.RemoveRange(0, _consolidatedHistory.Count - maxHistory);
            }
        }

        private sealed record PendingEntryContext(
            TradeSide Direction,
            decimal PRef,
            DateTime PRefTime,
            int AbsoluteQuantity
        );

        private readonly record struct DailyTimeWindow(TimeSpan Start, TimeSpan End)
        {
            public static DailyTimeWindow Parse(string value)
            {
                var parts = value.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 2 ||
                    !TimeSpan.TryParseExact(parts[0], @"hh\:mm", CultureInfo.InvariantCulture, out var start) ||
                    !TimeSpan.TryParseExact(parts[1], @"hh\:mm", CultureInfo.InvariantCulture, out var end))
                {
                    throw new ArgumentException("TradingPauseWindow must be in HH:MM-HH:MM format.", nameof(value));
                }

                return new DailyTimeWindow(start, end);
            }

            public bool Contains(TimeSpan time)
            {
                if (Start == End)
                {
                    return false;
                }

                if (Start < End)
                {
                    return time >= Start && time < End;
                }

                return time >= Start || time < End;
            }
        }

        private void ValidateAcceptanceClosure()
        {
            if (_eligibleStreamBuilder == null ||
                _layer1Engine == null ||
                _layer2Engine == null ||
                _layer3Planner == null ||
                _layer4StateMachine == null)
            {
                throw new InvalidOperationException("Acceptance closure failed: required strategy modules are not initialized.");
            }
        }

        private DateTime ResolveExchangeTimestamp()
        {
            return _lastExchangeTimestamp ?? Time;
        }

        private void SubmitEntryOnLayer2Confirmation(Layer2VerificationDecision decision, decimal referencePrice)
        {
            _layer4StateMachine.OnLayer2Confirmed();
            _layer1Engine.ResetForOpenLifecycle();
            _pendingEntryContext = new PendingEntryContext(
                Direction: decision.DominantSide,
                PRef: decision.PRef,
                PRefTime: decision.PRefTime,
                AbsoluteQuantity: Math.Abs(_layer3Planner.ResolveEntryQuantity(
                    decision.DominantSide,
                    _config.PositionSize)));

            var entryQuantity = _layer3Planner.ResolveEntryQuantity(
                decision.DominantSide,
                _config.PositionSize);

            if (!LiveMode)
            {
                SyncSecurityMarketPrice(referencePrice, ResolveExchangeTimestamp());
            }

            var entryTicket = MarketOrder(
                _symbol,
                entryQuantity,
                asynchronous: LiveMode,
                tag: $"ENTRY Layer2Confirmed Side={decision.DominantSide} PRef={decision.PRef}");
            _activeEntryOrderId = entryTicket.OrderId;
            _postLayer2Debug.OnEntrySubmitted(
                ResolveExchangeTimestamp(),
                entryTicket.OrderId,
                entryQuantity,
                decision.DominantSide,
                decision.PRef,
                _layer4StateMachine.State);

            if (!LiveMode && entryTicket.Status == OrderStatus.Filled)
            {
                CompleteEntryFill(entryTicket.AverageFillPrice, null, "SyncBacktestFill");
            }
        }

        private bool IsEntryOrderEvent(OrderEvent orderEvent)
        {
            if (_pendingEntryContext == null)
            {
                return false;
            }

            if (_activeEntryOrderId.HasValue && orderEvent.OrderId != _activeEntryOrderId.Value)
            {
                return false;
            }

            var tag = Transactions.GetOrderById(orderEvent.OrderId)?.Tag;
            return tag != null &&
                   tag.StartsWith("ENTRY Layer2Confirmed", StringComparison.Ordinal);
        }

        private bool HasExpectedEntryHoldings()
        {
            if (_pendingEntryContext == null)
            {
                return false;
            }

            var quantity = Portfolio[_symbol].Quantity;
            return _pendingEntryContext.Direction == TradeSide.Buy
                ? quantity > 0m
                : quantity < 0m;
        }

        private void TryBootstrapLayer7FromHeldPosition(decimal referencePrice)
        {
            if (_layer7Engine != null || _pendingEntryContext == null || !HasExpectedEntryHoldings())
            {
                return;
            }

            CompleteEntryFill(referencePrice, null, "HeldPositionBootstrap");
        }

        private void CompleteEntryFill(decimal fillPrice, OrderEvent orderEvent, string source)
        {
            if (_layer7Engine != null || _pendingEntryContext == null)
            {
                return;
            }

            var plan = _layer3Planner.BuildPlan(
                _pendingEntryContext.Direction,
                _pendingEntryContext.AbsoluteQuantity,
                _pendingEntryContext.PRef,
                fillPrice);
            _activeProtectionPlan = plan;
            _layer7Engine = new Layer7ExitEngine(
                plan,
                _config.FPanicCounterPressurePct,
                _config.GPanicMicroTestCounterPct,
                _config.HPanicMicroWindowMs,
                _config.JPanicMicroFailureCount,
                _config.BTargetSpacingPct,
                _pendingEntryContext.PRefTime);
            _layer7Engine.SeedFromHistory(_consolidatedHistory);
            _ = _layer4StateMachine.OnEntryFilled();

            _activeEntryOrderId = null;
            _pendingEntryContext = null;

            if (orderEvent != null)
            {
                _postLayer2Debug.OnEntryFilled(
                    ResolveExchangeTimestamp(),
                    plan,
                    _layer7Engine,
                    _layer4StateMachine.State,
                    orderEvent);
                return;
            }

            _postLayer2Debug.OnEntryFilled(
                ResolveExchangeTimestamp(),
                plan,
                _layer7Engine,
                _layer4StateMachine.State,
                fillPrice: fillPrice,
                source: source);
        }

        private void SyncSecurityMarketPrice(decimal price, DateTime exchangeTime)
        {
            if (LiveMode)
            {
                return;
            }

            _security.SetMarketPrice(new Tick(exchangeTime, _symbol, price));
        }

        private void InvalidateCandidate(string reason, DateTime eventTime)
        {
            EmitLayer2RejectedNoTrade(eventTime, reason);

            _layer1Engine.ThawBaseIatFromBufferedSamples(eventTime);
            _ = _layer4StateMachine.OnLayer2Invalidated();
        }

        private void EmitLayer2Confirmed(DateTime exchangeTime, Layer2VerificationDecision decision)
        {
            _postLayer2Debug.OnLayer2Confirmed(exchangeTime, decision, _layer4StateMachine.State);
        }

        private void EmitLayer2RejectedNoTrade(DateTime exchangeTime, string reason)
        {
            Debug($"Event=Layer2RejectedNoTrade ExchangeTs={exchangeTime:O} State={_layer4StateMachine.State} Reason={reason}");
        }

        private void EmitPositionFullyClosed(DateTime exchangeTime, string reason)
        {
            _postLayer2Debug.OnPositionFullyClosed(exchangeTime, _layer4StateMachine.State, reason);
        }

        private static string ResolvePositionExitReason(string exitTag)
        {
            if (string.Equals(exitTag, "TakeProfit", StringComparison.Ordinal))
            {
                return "Take Profit";
            }

            if (string.Equals(exitTag, "HardStop", StringComparison.Ordinal))
            {
                return "Hard Stop";
            }

            if (string.Equals(exitTag, "PanicExit", StringComparison.Ordinal))
            {
                return "Panic Exit";
            }

            if (string.Equals(exitTag, "RTH_FORCED_CLOSE", StringComparison.Ordinal))
            {
                return "RTH Forced Flat";
            }

            return $"Unknown({exitTag})";
        }
    }
}
