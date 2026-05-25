namespace QuantConnect.Algorithm.CSharp.Layer3
{
    public sealed record ProtectionTarget(
        int Index,
        decimal TargetPrice,
        int Quantity
    );
}
