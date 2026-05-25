using QuantConnect.Algorithm.CSharp.Layer1;

namespace QuantConnect.Algorithm.CSharp.Layer4
{
    public sealed record Layer4DispatchDirective(
        bool ShouldDispatch,
        PrimaryTriggerSignal Trigger
    );
}
