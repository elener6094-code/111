namespace QuantConnect.Algorithm.CSharp.Layer4
{
    public enum StrategyLifecycleState
    {
        HardOff = 0,
        Warmup = 1,
        Armed = 2,
        Candidate = 3,
        Entering = 4,
        Open = 5
    }
}
