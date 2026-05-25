using System;
using System.Collections.Generic;
using QuantConnect.Algorithm.CSharp.DataEligibility;

namespace QuantConnect.Algorithm.CSharp.Layer1
{
    /// <summary>
    /// Layer 1 output payload for downstream verification.
    /// </summary>
    public sealed record PrimaryTriggerSignal(
        DateTime TriggerTime,
        decimal BaseIatMicroseconds,
        decimal ThresholdIatMicroseconds,
        decimal PRef,
        IReadOnlyList<EligibleConsolidatedTransaction> Window
    );
}
