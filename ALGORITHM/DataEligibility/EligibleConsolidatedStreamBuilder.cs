using System;
using System.Collections.Generic;
using QuantConnect.Data.Market;

namespace QuantConnect.Algorithm.CSharp.DataEligibility
{
    /// <summary>
    /// Milestone 2:
    /// - Excludes suspicious transactions.
    /// - Consolidates by exchange microsecond timestamp.
    /// - Classifies side with tick test after consolidation.
    /// </summary>
    public sealed class EligibleConsolidatedStreamBuilder
    {
        private bool _hasPreviousConsolidated;
        private decimal _previousConsolidatedPrice;
        private TradeSide _previousSide = TradeSide.Unknown;

        public IReadOnlyList<EligibleConsolidatedTransaction> Build(IReadOnlyList<Tick> inputTicks)
        {
            if (inputTicks == null || inputTicks.Count == 0)
            {
                return Array.Empty<EligibleConsolidatedTransaction>();
            }

            var blocks = new Dictionary<DateTime, ConsolidationBlock>();

            for (var i = 0; i < inputTicks.Count; i++)
            {
                var tick = inputTicks[i];
                if (tick.TickType != TickType.Trade)
                {
                    continue;
                }

                if (tick.Suspicious)
                {
                    continue;
                }

                var exchangeMicrosecond = NormalizeToMicrosecond(tick.Time);
                if (!blocks.TryGetValue(exchangeMicrosecond, out var block))
                {
                    block = new ConsolidationBlock();
                    blocks.Add(exchangeMicrosecond, block);
                }

                block.LastPrice = tick.Price;
                block.Volume += tick.Quantity;
            }

            if (blocks.Count == 0)
            {
                return Array.Empty<EligibleConsolidatedTransaction>();
            }

            var orderedTimestamps = new List<DateTime>(blocks.Keys);
            orderedTimestamps.Sort();

            var result = new List<EligibleConsolidatedTransaction>(orderedTimestamps.Count);
            for (var i = 0; i < orderedTimestamps.Count; i++)
            {
                var ts = orderedTimestamps[i];
                var block = blocks[ts];
                var side = Classify(block.LastPrice);

                result.Add(new EligibleConsolidatedTransaction(
                    ExchangeTime: ts,
                    LastPrice: block.LastPrice,
                    Volume: block.Volume,
                    Side: side));
            }

            return result;
        }

        private TradeSide Classify(decimal currentPrice)
        {
            // Plan clarification: first consolidated transaction is not side-classified.
            if (!_hasPreviousConsolidated)
            {
                _hasPreviousConsolidated = true;
                _previousConsolidatedPrice = currentPrice;
                _previousSide = TradeSide.Unknown;
                return TradeSide.Unknown;
            }

            TradeSide side;
            if (currentPrice > _previousConsolidatedPrice)
            {
                side = TradeSide.Buy;
            }
            else if (currentPrice < _previousConsolidatedPrice)
            {
                side = TradeSide.Sell;
            }
            else
            {
                // Mandatory recursive flat inheritance.
                side = _previousSide;
            }

            _previousConsolidatedPrice = currentPrice;
            _previousSide = side;
            return side;
        }

        private static DateTime NormalizeToMicrosecond(DateTime timestamp)
        {
            const long ticksPerMicrosecond = 10L;
            var normalizedTicks = timestamp.Ticks - (timestamp.Ticks % ticksPerMicrosecond);
            return new DateTime(normalizedTicks, timestamp.Kind);
        }

        private sealed class ConsolidationBlock
        {
            public decimal LastPrice { get; set; }
            public decimal Volume { get; set; }
        }
    }
}
