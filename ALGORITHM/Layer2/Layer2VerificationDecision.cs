using System;
using QuantConnect.Algorithm.CSharp.DataEligibility;

namespace QuantConnect.Algorithm.CSharp.Layer2
{
    public enum Layer2DecisionStatus
    {
        Monitoring = 0,
        Confirmed = 1,
        Invalidated = 2
    }

    public sealed record Layer2VerificationDecision(
        Layer2DecisionStatus Status,
        TradeSide DominantSide,
        decimal DominancePercent,
        int InstitutionalCount,
        decimal InstitutionalPurityPercent,
        decimal PRef,
        DateTime PRefTime,
        string RejectionReason
    );
}
