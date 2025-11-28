using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using QuantPairs.Trading;

namespace QuantPairs.Research.Validation
{
    public sealed class Grid
    {
        public double[] ZEntry { get; init; } = new[] { 1.0, 1.5, 2.0 };
        public double[] ZExit { get; init; } = new[] { 0.5, 1.0 };
        public double[] ZStop { get; init; } = new[] { 3.0, 4.0 };
        public double[] Q { get; init; } = Array.Empty<double>();
        public double[] R { get; init; } = Array.Empty<double>();
        public SizingMode[] Sizing { get; init; } = new[] { SizingMode.Fixed, SizingMode.HalfLifeScaled };

        public static double[] ParseDoubles(string? csv, double[] def)
        {
            if (string.IsNullOrWhiteSpace(csv)) return def;
            return csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture))
                      .ToArray();
        }
    }

    public sealed record ValidationResult(
        string PairY, string PairX,
        string Mode,
        double ZEntry, double ZExit, double ZStop,
        SizingMode Sizing,
        double Sharpe, double Calmar, double MaxDD, double WinRate, double ProfitFactor, double Turnover,
        double Alpha, double Beta,
        double? HalfLifeTrain,
        double? Q, double? R
    );

    public static class Validator
    {
        /// <summary>
        /// Evaluate ALL configs for a pair. Returns results sorted by Sharpe descending.
        /// </summary>
        public static List<ValidationResult> EvaluatePairAllConfigs(
            string yName, string xName,
            double alphaTrain, double betaTrain,
            double muTrain, double sigmaTrain, double? halfLifeTrain,
            double[] yValid, double[] xValid,
            Grid grid,
            double periodsPerYear = 252.0 * 6.5
        )
        {
            var results = new List<ValidationResult>();

            if (yValid.Length < 50 || xValid.Length < 50 || sigmaTrain <= 0)
                return results;

            // Static residuals
            double[] residStatic = new double[yValid.Length];
            for (int i = 0; i < yValid.Length; i++)
                residStatic[i] = yValid[i] - (alphaTrain + betaTrain * xValid[i]);

            var sizingModes = grid.Sizing ?? new[] { SizingMode.Fixed };

            // --- STATIC β loop ---
            foreach (var zIn in grid.ZEntry)
            foreach (var zOut in grid.ZExit)
            foreach (var zStop in grid.ZStop)
            foreach (var sz in sizingModes)
            {
                var rep = Backtester.Run(residStatic, muTrain, sigmaTrain, zIn, zOut, zStop, sz, halfLifeTrain, periodsPerYear);

                results.Add(new ValidationResult(
                    yName, xName, "static",
                    zIn, zOut, zStop, sz,
                    rep.Sharpe, rep.Calmar, rep.MaxDrawdown, rep.WinRate, rep.ProfitFactor, rep.AnnualTurnover,
                    alphaTrain, betaTrain, halfLifeTrain,
                    null, null
                ));
            }

            // --- KALMAN β loop (ADAPTIVE & OPTIMIZED) ---
            var qArray = grid.Q;
            var rArray = grid.R;

            if (qArray.Length == 0 || rArray.Length == 0)
            {
                // ===== GRILLE ADAPTATIVE OPTIMISÉE =====
                var qList = new List<double>();
                var rList = new List<double>();
                double sigma2 = sigmaTrain * sigmaTrain;

                if (halfLifeTrain.HasValue && halfLifeTrain.Value > 0.5)
                {
                    double hl = halfLifeTrain.Value;
                    double qOpt = 1.0 / (hl * hl);

                    if (hl < 5)
                    {
                        qList.AddRange(new[] { 1e-9, 5e-9, 1e-8, 5e-8, 1e-7, 5e-7, 1e-6, 5e-6, 1e-5, 5e-5, 1e-4 });
                        rList.AddRange(new[] { sigma2 * 1e-4, sigma2 * 1e-3, sigma2 * 1e-2, sigma2 * 0.05, sigma2 * 0.1, sigma2 * 0.25, sigma2 * 0.5, sigma2, sigma2 * 2, sigma2 * 5, sigma2 * 10, sigma2 * 20, sigma2 * 50, sigma2 * 100 });
                    }
                    else if (hl < 20)
                    {
                        qList.AddRange(new[] { qOpt * 1e-5, qOpt * 1e-4, qOpt * 1e-3, qOpt * 1e-2, qOpt * 0.1, qOpt * 0.5, qOpt, qOpt * 2, qOpt * 5, qOpt * 10, qOpt * 50, qOpt * 100 });
                        rList.AddRange(new[] { sigma2 * 1e-3, sigma2 * 1e-2, sigma2 * 0.05, sigma2 * 0.1, sigma2 * 0.25, sigma2 * 0.5, sigma2, sigma2 * 2, sigma2 * 5, sigma2 * 10, sigma2 * 20, sigma2 * 50 });
                    }
                    else if (hl < 100)
                    {
                        qList.AddRange(new[] { qOpt * 1e-4, qOpt * 1e-3, qOpt * 1e-2, qOpt * 0.1, qOpt * 0.25, qOpt * 0.5, qOpt, qOpt * 2, qOpt * 4, qOpt * 10, qOpt * 25, qOpt * 50, qOpt * 100 });
                        rList.AddRange(new[] { sigma2 * 1e-4, sigma2 * 1e-3, sigma2 * 1e-2, sigma2 * 0.05, sigma2 * 0.1, sigma2 * 0.25, sigma2 * 0.5, sigma2, sigma2 * 2, sigma2 * 5, sigma2 * 10, sigma2 * 20, sigma2 * 50, sigma2 * 100 });
                    }
                    else
                    {
                        qList.AddRange(new[] { qOpt * 0.01, qOpt * 0.05, qOpt * 0.1, qOpt * 0.5, qOpt, qOpt * 2, qOpt * 5, qOpt * 10, qOpt * 20, qOpt * 50, qOpt * 100, qOpt * 200 });
                        rList.AddRange(new[] { sigma2 * 1e-3, sigma2 * 1e-2, sigma2 * 0.05, sigma2 * 0.1, sigma2 * 0.25, sigma2 * 0.5, sigma2, sigma2 * 2, sigma2 * 5, sigma2 * 10, sigma2 * 20, sigma2 * 50 });
                    }
                }
                else
                {
                    qList.AddRange(new[] { 1e-9, 1e-8, 1e-7, 1e-6, 1e-5, 1e-4, 1e-3, 1e-2, 1e-1, 1.0 });
                    rList.AddRange(new[] { sigma2 * 1e-3, sigma2 * 1e-2, sigma2 * 0.1, sigma2, sigma2 * 10, sigma2 * 100 });
                }

                qArray = qList.Where(q => q > 0 && q < 1e3).Distinct().OrderBy(x => x).ToArray();
                rArray = rList.Where(r => r > 0 && r < 1e3).Distinct().OrderBy(x => x).ToArray();

                Console.WriteLine($"[Auto Q/R] {yName}/{xName} HL={halfLifeTrain:F1} σ={sigmaTrain:F4}");
                Console.WriteLine($" Q range: {qArray.First():E2} to {qArray.Last():E2} ({qArray.Length} values)");
                Console.WriteLine($" R range: {rArray.First():E2} to {rArray.Last():E2} ({rArray.Length} values)");
                Console.WriteLine($" Total Kalman configs: {qArray.Length * rArray.Length * grid.ZEntry.Length * grid.ZExit.Length * grid.ZStop.Length * sizingModes.Length}");
            }

            // Kalman grid search avec filtre Q/R ratio
            if (qArray.Length > 0 && rArray.Length > 0)
            {
                int kalmanTested = 0;
                int kalmanValid = 0;

                foreach (var q in qArray)
                foreach (var r in rArray)
                {
                    kalmanTested++;

                    double qrRatio = q / r;
                    if (qrRatio > 1e4 || qrRatio < 1e-7)
                        continue;

                    try
                    {
                        var kf = KalmanHedge.Run(
                            yValid, xValid,
                            alphaTrain, betaTrain,
                            pInit: 1.0, q: q, r: r
                        );

                        double residMean = kf.Residuals.Average();
                        double residStd = Math.Sqrt(kf.Residuals.Select(e => (e - residMean) * (e - residMean)).Average());

                        if (double.IsNaN(residStd) || double.IsInfinity(residStd) || residStd > sigmaTrain * 10)
                            continue;

                        kalmanValid++;

                        foreach (var zIn in grid.ZEntry)
                        foreach (var zOut in grid.ZExit)
                        foreach (var zStop in grid.ZStop)
                        foreach (var sz in sizingModes)
                        {
                            var rep = Backtester.Run(kf.Residuals, muTrain, sigmaTrain, zIn, zOut, zStop, sz, halfLifeTrain, periodsPerYear);

                            results.Add(new ValidationResult(
                                yName, xName, "kalman",
                                zIn, zOut, zStop, sz,
                                rep.Sharpe, rep.Calmar, rep.MaxDrawdown, rep.WinRate, rep.ProfitFactor, rep.AnnualTurnover,
                                alphaTrain, betaTrain, halfLifeTrain,
                                q, r
                            ));
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }

                Console.WriteLine($" Kalman: {kalmanValid}/{kalmanTested} valid Q/R pairs");
            }

            return results.OrderByDescending(r => r.Sharpe).ToList();
        }

        public static ValidationResult? EvaluatePair(
            string yName, string xName,
            double alphaTrain, double betaTrain,
            double muTrain, double sigmaTrain, double? halfLifeTrain,
            double[] yValid, double[] xValid,
            Grid grid,
            double periodsPerYear = 252.0 * 6.5
        )
        {
            var all = EvaluatePairAllConfigs(yName, xName, alphaTrain, betaTrain,
                                            muTrain, sigmaTrain, halfLifeTrain,
                                            yValid, xValid, grid, periodsPerYear);
            return all.FirstOrDefault();
        }

        public static List<(string y, string x, double alpha, double beta, double? halfLife)> LoadCointCandidates(
            string cointCsvPath,
            string alphaLevel,
            double? hlMin, double? hlMax
        )
        {
            var rows = new List<(string y, string x, double alpha, double beta, double? hl)>();
            if (!File.Exists(cointCsvPath)) return rows;

            bool use5 = alphaLevel.Trim() == "5";

            using var sr = new StreamReader(cointCsvPath);
            string? header = sr.ReadLine();

            while (!sr.EndOfStream)
            {
                var line = sr.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;
                var p = line.Split(',');

                if (p.Length < 12) continue;

                string y = p[1].Trim();
                string x = p[2].Trim();

                if (!double.TryParse(p[6], NumberStyles.Any, CultureInfo.InvariantCulture, out double alpha)) continue;
                if (!double.TryParse(p[5], NumberStyles.Any, CultureInfo.InvariantCulture, out double beta)) continue;

                double? hl = null;
                if (!string.IsNullOrWhiteSpace(p[8]) &&
                    double.TryParse(p[8], NumberStyles.Any, CultureInfo.InvariantCulture, out var hltmp))
                    hl = hltmp;

                bool pass5 = p[9].Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ||
                            p[9].Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
                bool pass10 = p[10].Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ||
                             p[10].Trim().Equals("True", StringComparison.OrdinalIgnoreCase);

                bool pass = use5 ? pass5 : (pass5 || pass10);
                if (!pass) continue;
                if (hlMin.HasValue && (!hl.HasValue || hl.Value < hlMin.Value)) continue;
                if (hlMax.HasValue && (!hl.HasValue || hl.Value > hlMax.Value)) continue;

                rows.Add((y, x, alpha, beta, hl));
            }

            return rows;
        }
    }
}