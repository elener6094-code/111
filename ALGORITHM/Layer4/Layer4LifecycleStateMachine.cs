using QuantConnect.Algorithm.CSharp.Layer1;

namespace QuantConnect.Algorithm.CSharp.Layer4
{
    /// <summary>
    /// WARMUP -> ARMED -> CANDIDATE -> OPEN lifecycle.
    /// Candidate runs as an expanding in-place monitor (no replacement).
    /// </summary>
    public sealed class Layer4LifecycleStateMachine
    {
        public StrategyLifecycleState State { get; private set; } = StrategyLifecycleState.HardOff;

        public bool ShouldRunLayer1 =>
            State != StrategyLifecycleState.Open &&
            State != StrategyLifecycleState.Entering &&
            State != StrategyLifecycleState.HardOff;

        public bool OnRthSessionOpen()
        {
            State = StrategyLifecycleState.Warmup;
            return true;
        }

        public bool OnHardOffEntered()
        {
            if (State == StrategyLifecycleState.HardOff)
            {
                return false;
            }

            State = StrategyLifecycleState.HardOff;
            return true;
        }

        public bool OnBaselineReady()
        {
            if (State != StrategyLifecycleState.Warmup)
            {
                return false;
            }

            State = StrategyLifecycleState.Armed;
            return true;
        }

        public Layer4DispatchDirective OnPrimaryTriggerFired(PrimaryTriggerSignal trigger)
        {
            if (trigger == null)
            {
                return new Layer4DispatchDirective(false, null);
            }

            if (State == StrategyLifecycleState.Open || State == StrategyLifecycleState.Entering)
            {
                // No tradable trigger dispatch while entry/position lifecycle is active.
                return new Layer4DispatchDirective(false, null);
            }

            if (State == StrategyLifecycleState.HardOff)
            {
                return new Layer4DispatchDirective(false, null);
            }

            if (State == StrategyLifecycleState.Warmup)
            {
                // Warmup state is not allowed to dispatch triggers.
                return new Layer4DispatchDirective(false, null);
            }

            if (State == StrategyLifecycleState.Armed)
            {
                State = StrategyLifecycleState.Candidate;
                return new Layer4DispatchDirective(true, trigger);
            }

            // State == CANDIDATE:
            // Dynamic candidate is already active; no replacement dispatch.
            return new Layer4DispatchDirective(false, null);
        }

        public void OnLayer2Confirmed()
        {
            State = StrategyLifecycleState.Entering;
        }

        public bool OnEntryFilled()
        {
            if (State != StrategyLifecycleState.Entering)
            {
                return false;
            }

            State = StrategyLifecycleState.Open;
            return true;
        }

        public bool OnLayer2Invalidated()
        {
            if (State != StrategyLifecycleState.Candidate)
            {
                return false;
            }

            State = StrategyLifecycleState.Armed;
            return true;
        }

        public bool OnPositionFullyClosed()
        {
            if (State == StrategyLifecycleState.Warmup || State == StrategyLifecycleState.HardOff)
            {
                return false;
            }

            State = StrategyLifecycleState.Warmup;
            return true;
        }
    }
}
