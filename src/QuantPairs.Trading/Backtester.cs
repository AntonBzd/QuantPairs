using System;
using System.Linq;

namespace QuantPairs.Trading
{
    public enum SizingMode
    {
        Fixed,
        HalfLifeScaled,
        VolScaled
    }

    public sealed class BacktestReport
    {
        public double Sharpe { get; init; }
        public double Calmar { get; init; }
        public double MaxDrawdown { get; init; }
        public double WinRate { get; init; }
        public double ProfitFactor { get; init; }
        public double AnnualTurnover { get; init; }
        public int Trades { get; init; }
    }

    public static class Backtester
    {
        /// <summary>
        /// Simple spread backtest on residuals ε_t; position is in "spread units".
        /// PnL_t = pos_{t-1} * (ε_t - ε_{t-1})
        /// Entry when |z| ≥ zEntry (go long spread if z<0, short if z>0),
        /// Exit when |z| ≤ zExit, Stop when |z| ≥ zStop.
        /// </summary>
        public static BacktestReport Run(
            double[] residual, // ε_t on the evaluated period (VALID or OOS)
            double mu, // mean from TRAIN (static)
            double sigma, // std from TRAIN (static, >0)
            double zEntry,
            double zExit,
            double? zStop,
            SizingMode sizing,
            double? halfLife, // if sizing=HalfLifeScaled
            double periodsPerYear = 252.0 * 6.5 // for hourly US equities ~1638
        )
        {
            int n = residual.Length;
            if (n < 3 || sigma <= 0) return new BacktestReport();

            double size = sizing switch
            {
                SizingMode.Fixed => 1.0,
                SizingMode.HalfLifeScaled => 1.0 / Math.Sqrt(Math.Max(1.0, halfLife ?? 1.0)),
                SizingMode.VolScaled => 1.0 / sigma,
                _ => 1.0
            };

            int pos = 0; // -1, 0, +1 (spread units)
            double[] pnl = new double[n];
            int trades = 0;
            int wins = 0, losses = 0;
            double grossWin = 0, grossLoss = 0;
            double turnover = 0;
            double tradePnl = 0; // accumulator for current trade PnL

            for (int t = 1; t < n; t++)
            {
                double z = (residual[t - 1] - mu) / sigma;

                // Exit / Stop (BEFORE entry to avoid open/close in same period)
                if (pos != 0)
                {
                    bool shouldClose = false;
                    if (zStop.HasValue && Math.Abs(z) >= zStop.Value)
                    {
                        shouldClose = true; // stop loss
                    }
                    else if (Math.Abs(z) <= zExit)
                    {
                        shouldClose = true; // take profit
                    }

                    if (shouldClose)
                    {
                        // Close trade
                        if (tradePnl > 0) { wins++; grossWin += tradePnl; }
                        else if (tradePnl < 0) { losses++; grossLoss += -tradePnl; }
                        turnover += Math.Abs(pos);
                        trades++;
                        pos = 0;
                        tradePnl = 0;
                    }
                }

                // Entry (only if flat)
                if (pos == 0 && Math.Abs(z) >= zEntry)
                {
                    pos = (z > 0 ? -1 : +1); // short spread when z>0; long when z<0
                    turnover += Math.Abs(pos);
                }

                // PnL calculation
                double dE = residual[t] - residual[t - 1];
                double thisPnl = size * pos * dE;
                pnl[t] = thisPnl;
                if (pos != 0) tradePnl += thisPnl;
            }

            // If position still open at end of backtest
            if (pos != 0)
            {
                if (tradePnl > 0) { wins++; grossWin += tradePnl; }
                else if (tradePnl < 0) { losses++; grossLoss += -tradePnl; }
            }

            // Statistics
            double mean = pnl.Average();
            double std = Math.Sqrt(Math.Max(1e-16, pnl.Select(x => (x - mean) * (x - mean)).Average()));
            double sharpe = (std > 0 ? mean / std * Math.Sqrt(periodsPerYear) : 0.0);

            // Equity & drawdown
            double eq = 0, peak = 0, maxdd = 0;
            foreach (var x in pnl)
            {
                eq += x;
                if (eq > peak) peak = eq;
                double dd = peak - eq;
                if (dd > maxdd) maxdd = dd;
            }

            // Simple Calmar: annualized return / maxDD
            double annualRet = mean * periodsPerYear;
            double calmar = (maxdd > 0 ? annualRet / maxdd : 0.0);
            double profitFactor = (grossLoss > 0 ? grossWin / grossLoss : (grossWin > 0 ? double.PositiveInfinity : 0.0));
            double winRate = (wins + losses > 0 ? (double)wins / (wins + losses) : 0.0);

            return new BacktestReport
            {
                Sharpe = sharpe,
                Calmar = calmar,
                MaxDrawdown = maxdd,
                WinRate = winRate,
                ProfitFactor = profitFactor,
                Trades = trades,
                AnnualTurnover = turnover / n * periodsPerYear
            };
        }
    }
}