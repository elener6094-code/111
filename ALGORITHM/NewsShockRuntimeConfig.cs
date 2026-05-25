using System;
using System.Collections.Generic;
using System.Globalization;
using QuantConnect.Algorithm;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Milestone 1 runtime contract:
    /// strict parameter boundary for all PRD-mandated inputs.
    /// </summary>
    public sealed record NewsShockRuntimeConfig(
        string Symbol,
        int PositionSize,
        int WMinutes,
        int XConsecutiveFastTrades,
        decimal MSpeedMultiplier,
        decimal DDirectionalDominancePct,
        int NInstitutionalCountMin,
        decimal ZInstitutionalNotionalThreshold,
        decimal PInstitutionalPurityPct,
        int TPParts,
        decimal BTargetSpacingPct,
        decimal StopLossPercent,
        decimal VMaxStopDistancePct,
        decimal FPanicCounterPressurePct,
        decimal GPanicMicroTestCounterPct,
        int HPanicMicroWindowMs,
        int JPanicMicroFailureCount,
        string TradingPauseWindow,
        int OperationalTimingMs,
        DateTime StartDate,
        DateTime EndDate
    )
    {
        public static NewsShockRuntimeConfig Load(QCAlgorithm algorithm)
        {
            var missing = new List<string>();
            var now = algorithm.UtcTime == default ? DateTime.UtcNow : algorithm.UtcTime;

            string symbol = GetRequiredString(algorithm, "Symbol", missing);
            int positionSize = GetRequiredInt(algorithm, "PositionSize", missing);
            int w = GetRequiredInt(algorithm, "W", missing);
            int x = GetRequiredInt(algorithm, "X", missing);
            decimal m = GetRequiredDecimal(algorithm, "M", missing);
            decimal d = GetRequiredDecimal(algorithm, "D", missing);
            int n = GetRequiredInt(algorithm, "N", missing);
            decimal z = GetRequiredDecimal(algorithm, "Z", missing);
            decimal p = GetRequiredDecimal(algorithm, "P", missing);
            int tpParts = GetRequiredInt(algorithm, "TPParts", missing);
            decimal b = GetRequiredDecimal(algorithm, "B", missing);
            decimal stopLossPercent = GetRequiredDecimal(algorithm, "StopLossPercent", missing);
            decimal v = GetRequiredDecimal(algorithm, "V", missing);
            decimal f = GetRequiredDecimal(algorithm, "F", missing);
            decimal g = GetRequiredDecimal(algorithm, "G", missing);
            int h = GetRequiredInt(algorithm, "H", missing);
            int j = GetRequiredInt(algorithm, "J", missing);
            string tradingPauseWindow = GetRequiredString(algorithm, "TradingPauseWindow", missing);
            int operationalTimingMs = GetRequiredInt(algorithm, "OperationalTimingMs", missing);
            DateTime? configuredStartDate = GetOptionalDate(algorithm, "StartDate", missing);
            DateTime? configuredEndDate = GetOptionalDate(algorithm, "EndDate", missing);
            DateTime startDate = ResolveDateForMode(
                configuredStartDate,
                configuredEndDate,
                "StartDate",
                algorithm.LiveMode,
                now,
                missing);
            DateTime endDate = ResolveDateForMode(
                configuredEndDate,
                configuredStartDate,
                "EndDate",
                algorithm.LiveMode,
                now,
                missing);

            if (missing.Count > 0)
            {
                throw new ArgumentException(
                    $"Missing or invalid required parameters: {string.Join(", ", missing)}");
            }

            ValidateBounds(
                positionSize,
                w,
                x,
                m,
                d,
                n,
                z,
                p,
                tpParts,
                b,
                stopLossPercent,
                v,
                f,
                g,
                h,
                j,
                tradingPauseWindow,
                operationalTimingMs,
                startDate,
                endDate);

            return new NewsShockRuntimeConfig(
                Symbol: symbol,
                PositionSize: positionSize,
                WMinutes: w,
                XConsecutiveFastTrades: x,
                MSpeedMultiplier: m,
                DDirectionalDominancePct: d,
                NInstitutionalCountMin: n,
                ZInstitutionalNotionalThreshold: z,
                PInstitutionalPurityPct: p,
                TPParts: tpParts,
                BTargetSpacingPct: b,
                StopLossPercent: stopLossPercent,
                VMaxStopDistancePct: v,
                FPanicCounterPressurePct: f,
                GPanicMicroTestCounterPct: g,
                HPanicMicroWindowMs: h,
                JPanicMicroFailureCount: j,
                TradingPauseWindow: tradingPauseWindow,
                OperationalTimingMs: operationalTimingMs,
                StartDate: startDate,
                EndDate: endDate
            );
        }

        private static void ValidateBounds(
            int positionSize,
            int w,
            int x,
            decimal m,
            decimal d,
            int n,
            decimal z,
            decimal p,
            int tpParts,
            decimal b,
            decimal stopLossPercent,
            decimal v,
            decimal f,
            decimal g,
            int h,
            int j,
            string tradingPauseWindow,
            int operationalTimingMs,
            DateTime startDate,
            DateTime endDate)
        {
            if (positionSize <= 0) throw new ArgumentOutOfRangeException(nameof(positionSize), "PositionSize must be > 0.");
            if (w <= 0) throw new ArgumentOutOfRangeException(nameof(w), "W must be > 0.");
            if (x <= 0) throw new ArgumentOutOfRangeException(nameof(x), "X must be > 0.");
            if (m <= 0) throw new ArgumentOutOfRangeException(nameof(m), "M must be > 0.");
            if (d <= 0 || d > 100) throw new ArgumentOutOfRangeException(nameof(d), "D must be in (0, 100].");
            if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), "N must be >= 0.");
            if (z <= 0) throw new ArgumentOutOfRangeException(nameof(z), "Z must be > 0.");
            if (p <= 0 || p > 100) throw new ArgumentOutOfRangeException(nameof(p), "P must be in (0, 100].");
            if (tpParts <= 0) throw new ArgumentOutOfRangeException(nameof(tpParts), "TPParts must be > 0.");
            if (b <= 0) throw new ArgumentOutOfRangeException(nameof(b), "B must be > 0.");
            if (stopLossPercent <= 0 || stopLossPercent > 100) throw new ArgumentOutOfRangeException(nameof(stopLossPercent), "StopLossPercent must be in (0, 100].");
            if (v <= 0 || v > 100) throw new ArgumentOutOfRangeException(nameof(v), "V must be in (0, 100].");
            if (f <= 0 || f > 100) throw new ArgumentOutOfRangeException(nameof(f), "F must be in (0, 100].");
            if (g <= 0 || g > 100) throw new ArgumentOutOfRangeException(nameof(g), "G must be in (0, 100].");
            if (h <= 0) throw new ArgumentOutOfRangeException(nameof(h), "H must be > 0.");
            if (j <= 0) throw new ArgumentOutOfRangeException(nameof(j), "J must be > 0.");
            ValidateTradingPauseWindow(tradingPauseWindow);
            if (operationalTimingMs <= 0) throw new ArgumentOutOfRangeException(nameof(operationalTimingMs), "OperationalTimingMs must be > 0.");
            if (endDate < startDate) throw new ArgumentOutOfRangeException(nameof(endDate), "EndDate must be greater than or equal to StartDate.");
        }

        private static void ValidateTradingPauseWindow(string tradingPauseWindow)
        {
            if (string.IsNullOrWhiteSpace(tradingPauseWindow))
            {
                throw new ArgumentOutOfRangeException(nameof(tradingPauseWindow), "TradingPauseWindow is required.");
            }

            var parts = tradingPauseWindow.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !TimeSpan.TryParseExact(parts[0], @"hh\:mm", CultureInfo.InvariantCulture, out var start) ||
                !TimeSpan.TryParseExact(parts[1], @"hh\:mm", CultureInfo.InvariantCulture, out var end) ||
                start < TimeSpan.Zero ||
                end < TimeSpan.Zero ||
                start >= TimeSpan.FromDays(1) ||
                end > TimeSpan.FromDays(1))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(tradingPauseWindow),
                    "TradingPauseWindow must be in HH:MM-HH:MM format.");
            }
        }

        private static string GetRequiredString(QCAlgorithm algorithm, string key, List<string> missing)
        {
            var value = algorithm.GetParameter(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add(key);
                return string.Empty;
            }
            return value.Trim();
        }

        private static int GetRequiredInt(QCAlgorithm algorithm, string key, List<string> missing)
        {
            var raw = algorithm.GetParameter(key);
            if (!int.TryParse(raw, out var value))
            {
                missing.Add(key);
                return 0;
            }
            return value;
        }

        private static decimal GetRequiredDecimal(QCAlgorithm algorithm, string key, List<string> missing)
        {
            var raw = algorithm.GetParameter(key);
            if (!decimal.TryParse(raw, out var value))
            {
                missing.Add(key);
                return 0m;
            }
            return value;
        }

        private static DateTime? GetOptionalDate(QCAlgorithm algorithm, string key, List<string> missing)
        {
            var raw = algorithm.GetParameter(key);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            {
                missing.Add(key);
                return null;
            }

            return value;
        }

        private static DateTime ResolveDateForMode(
            DateTime? configuredDate,
            DateTime? companionConfiguredDate,
            string key,
            bool isLiveMode,
            DateTime now,
            List<string> missing)
        {
            if (configuredDate.HasValue)
            {
                return configuredDate.Value;
            }

            if (!isLiveMode)
            {
                if (!missing.Contains(key))
                {
                    missing.Add(key);
                }
                return default;
            }

            return companionConfiguredDate ?? now;
        }
    }
}
