using System;
using System.Globalization;
using QuantConnect;
using QuantConnect.Data;
using QuantConnect.Data.Market;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Backtest-only SIP trade feed from daily CSV files on GitHub.
    /// </summary>
    public sealed class GitHubSipTradeData : BaseData
    {
        private const string GitHubRawBaseUrl = "https://raw.githubusercontent.com/elener6094-code/SIP/main/";

        public decimal TradePrice { get; set; }
        public decimal TradeSize { get; set; }
        public bool IsSuspicious { get; set; }

        public string ExchangeCode { get; set; } = string.Empty;
        public string SaleCondition { get; set; } = string.Empty;

        public override SubscriptionDataSource GetSource(
            SubscriptionDataConfig config,
            DateTime date,
            bool isLiveMode)
        {
            var fileName = $"{date:yyyyMMdd}.csv";
            var url = GitHubRawBaseUrl + fileName;
            return new SubscriptionDataSource(url, SubscriptionTransportMedium.RemoteFile, FileFormat.Csv);
        }

        public override BaseData Reader(
            SubscriptionDataConfig config,
            string line,
            DateTime date,
            bool isLiveMode)
        {
            if (string.IsNullOrWhiteSpace(line) ||
                line.StartsWith("ms_since_midnight", StringComparison.Ordinal))
            {
                return null;
            }

            var parts = line.Split(',');
            if (parts.Length < 6)
            {
                return null;
            }

            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var msSinceMidnight) ||
                !decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var price) ||
                !decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var size) ||
                !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var suspiciousFlag))
            {
                return null;
            }

            var exchangeTime = date.Date.AddMilliseconds(msSinceMidnight);

            return new GitHubSipTradeData
            {
                Symbol = config.Symbol,
                Time = exchangeTime,
                EndTime = exchangeTime,
                Value = price,
                TradePrice = price,
                TradeSize = size,
                ExchangeCode = parts[3],
                SaleCondition = parts[4],
                IsSuspicious = suspiciousFlag != 0
            };
        }

        public Tick ToTradeTick(Symbol equitySymbol)
        {
            var tick = new Tick(Time, equitySymbol, SaleCondition, ExchangeCode, TradeSize, TradePrice);
            tick.Suspicious = IsSuspicious;
            return tick;
        }
    }
}
