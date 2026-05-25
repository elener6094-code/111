using System.Collections.Generic;
using QuantConnect.Algorithm.CSharp.DataEligibility;

namespace QuantConnect.Algorithm.CSharp.Layer3
{
    /// <summary>
    /// Milestone 6 protection governance plan.
    /// P_ref remains immutable for hard-stop/panic anchors.
    /// EntryFillPrice anchors staged take-profit distances.
    /// </summary>
    public sealed record PositionProtectionPlan(
        TradeSide Direction,
        int EntryQuantity,
        decimal PRefAnchor,
        decimal EntryFillPriceAnchor,
        decimal InitialHardStopPrice,
        IReadOnlyList<ProtectionTarget> Targets
    );
}
