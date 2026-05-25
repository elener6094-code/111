using System;

namespace QuantConnect.Algorithm.CSharp.DataEligibility
{
    /// <summary>
    /// Canonical post-filter, post-consolidation transaction unit.
    /// This is the only upstream source for later layers.
    /// </summary>
    public sealed record EligibleConsolidatedTransaction(
        DateTime ExchangeTime,
        decimal LastPrice,
        decimal Volume,
        TradeSide Side
    );
}
