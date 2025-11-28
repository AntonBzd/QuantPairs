using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantPairs.Research.Cointegration
{
    public enum AdfTrend { None, Const } 
    public enum AlphaLevel { Alpha5 = 5, Alpha10 = 10, Alpha15 = 15 }

    public sealed class EngleGrangerResult
    {
        public string SeriesY { get; init; } = "";
        public string SeriesX { get; init; } = "";
        public double Alpha { get; init; }      // intercept OLS
        public double Beta { get; init; }       // slope OLS
        public double AdfStat { get; init; }    // t-stat sur e_{t-1} dans ADF
        public int UsedLag { get; init; }       // lags retenus par BIC
        public int Nobs { get; init; }          // n effectif ADF
        public bool RejectAt5 { get; init; }
        public bool RejectAt10 { get; init; }
        public bool RejectAt15 { get; init; }
        public double? ApproxPvalue { get; init; } 
    }

    public static class EngleGranger
    {
        /// <summary>
        /// Test Engle-Granger pour (y,x) : y_t = a + b x_t + u_t ; ADF sur u_t (constante).
        /// maxLag: lags max pour ADF (BIC), typiquement 0..4 ou 0..8.
        /// </summary>
        public static EngleGrangerResult TestPair(double[] y, double[] x,
                                                  string nameY, string nameX,
                                                  int maxLag = 4)
        {
            if (y.Length != x.Length) throw new ArgumentException("Length mismatch");
            int n = y.Length;
            if (n < 50) throw new ArgumentException("Series too short for EG");

            // 1) OLS y = a + b x
            // [a, b] = (X'X)^{-1} X'y, X = [1, x]
            double sum1 = n;
            double sumx = x.Sum();
            double sumy = y.Sum();
            double sumxx = x.Select(v => v * v).Sum();
            double sumxy = 0; for (int i = 0; i < n; i++) sumxy += x[i] * y[i];

            double det = sum1 * sumxx - sumx * sumx;
            if (Math.Abs(det) < 1e-12) throw new InvalidOperationException("Singular");
            double a = (sumxx * sumy - sumx * sumxy) / det;
            double b = (sum1 * sumxy - sumx * sumy) / det;

            // Residuals u = y - (a + b x)
            var u = new double[n];
            for (int i = 0; i < n; i++) u[i] = y[i] - (a + b * x[i]);

            // 2) ADF(u) avec constante : Δu_t = c + ρ u_{t-1} + Σ γ_i Δu_{t-i} + ε_t
            // Lags 0..maxLag, sélection BIC
            int bestLag = 0;
            double bestBic = double.PositiveInfinity;
            double bestT = double.NaN;
            int bestNobs = 0;

            for (int lag = 0; lag <= maxLag; lag++)
            {
                var (tStat, bic, nobs) = Adf_tStat_and_BIC(u, lag);
                if (bic < bestBic)
                {
                    bestBic = bic; bestLag = lag; bestT = tStat; bestNobs = nobs;
                }
            }

            // 3) Décision via seuils asymptotiques ADF 
            // Valeurs critiques ~ MacKinnon (approx grandes tailles dataset)
            // 5% ≈ -2.86 ; 10% ≈ -2.57 ; 15% ≈ -2.43 (constante, ADF)
            double c5 = -2.86, c10 = -2.57, c15 = -2.43;

            bool rej5  = bestT < c5;
            bool rej10 = bestT < c10;
            bool rej15 = bestT < c15;

            // p-value approx (linéaire grossièrement entre -4.0->1%, -3.4->5%, -2.86->10%, -2.43->15%, -2.0->>20%)
            double? pApprox = ApproxPValue(bestT);

            return new EngleGrangerResult
            {
                SeriesY = nameY, SeriesX = nameX,
                Alpha = a, Beta = b,
                AdfStat = bestT, UsedLag = bestLag, Nobs = bestNobs,
                RejectAt5 = rej5, RejectAt10 = rej10, RejectAt15 = rej15,
                ApproxPvalue = pApprox
            };
        }

        private static (double tStat, double bic, int nobs) Adf_tStat_and_BIC(double[] u, int lag)
        {
            int n = u.Length;
            // Build Δu, u_{t-1}, Δu_{t-1..lag}
            var du = new double[n];
            for (int t = 1; t < n; t++) du[t] = u[t] - u[t - 1];

            int start = 1 + lag; // first t where all lags are defined
            int T = n - start;
            if (T < 10) return (double.NaN, double.PositiveInfinity, 0);

            int k = 2 + lag; // regressors: const + u_{t-1} + lag terms of Δu
            // Prepare matrices for OLS
            // y = Δu_t (t=start..n-1)
            // X = [1, u_{t-1}, Δu_{t-1}, ..., Δu_{t-lag}]
            var y = new double[T];
            var X = new double[T, k];

            for (int i = 0; i < T; i++)
            {
                int t = start + i;
                y[i] = du[t];
                int col = 0;
                X[i, col++] = 1.0;           // const
                X[i, col++] = u[t - 1];      // u_{t-1}
                for (int L = 1; L <= lag; L++)
                    X[i, col++] = du[t - L];
            }

            // OLS via normal equations: beta = (X'X)^{-1} X'y
            var XtX = new double[k, k];
            var Xty = new double[k];

            for (int i = 0; i < T; i++)
            {
                for (int a = 0; a < k; a++)
                {
                    Xty[a] += X[i, a] * y[i];
                    for (int b = 0; b < k; b++)
                        XtX[a, b] += X[i, a] * X[i, b];
                }
            }

            if (!TryInvertSymmetric(XtX, out var XtXinv))
                return (double.NaN, double.PositiveInfinity, 0);

            var beta = new double[k];
            for (int a = 0; a < k; a++)
                for (int b = 0; b < k; b++)
                    beta[a] += XtXinv[a, b] * Xty[b];

            // Residuals & sigma^2
            double rss = 0;
            for (int i = 0; i < T; i++)
            {
                double yh = 0;
                for (int a = 0; a < k; a++) yh += X[i, a] * beta[a];
                double e = y[i] - yh;
                rss += e * e;
            }
            double sigma2 = rss / (T - k);

            // Var(beta) = sigma2 * (X'X)^{-1}
            // t-stat for coefficient of u_{t-1} (col 1)
            double se_rho = Math.Sqrt(Math.Max(1e-18, sigma2 * XtXinv[1, 1]));
            double tStat = beta[1] / se_rho;

            // BIC = n*ln(rss/n) + k*ln(n)
            double bic = T * Math.Log(rss / T) + k * Math.Log(T);

            return (tStat, bic, T);
        }

        private static bool TryInvertSymmetric(double[,] A, out double[,] inv)
        {
            int n = A.GetLength(0);
            inv = new double[n, n];
            // Cholesky-like (simple Gauss-Jordan fallback)
            // Here use naive Gauss-Jordan for robustness
            var M = new double[n, 2 * n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) M[i, j] = A[i, j];
                M[i, n + i] = 1.0;
            }
            for (int i = 0; i < n; i++)
            {
                // pivot
                int p = i;
                double best = Math.Abs(M[i, i]);
                for (int r = i + 1; r < n; r++)
                {
                    double v = Math.Abs(M[r, i]);
                    if (v > best) { best = v; p = r; }
                }
                if (best < 1e-14) return false;
                if (p != i)
                {
                    for (int c = 0; c < 2 * n; c++)
                    { var tmp = M[i, c]; M[i, c] = M[p, c]; M[p, c] = tmp; }
                }
                // normalize
                double piv = M[i, i];
                for (int c = 0; c < 2 * n; c++) M[i, c] /= piv;
                // eliminate
                for (int r = 0; r < n; r++)
                {
                    if (r == i) continue;
                    double f = M[r, i];
                    for (int c = 0; c < 2 * n; c++) M[r, c] -= f * M[i, c];
                }
            }
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    inv[i, j] = M[i, n + j];
            return true;
        }

        private static double? ApproxPValue(double t)
        {
            // très grossière approx pour donner un ordre d’idée
            if (double.IsNaN(t)) return null;
            if (t <= -4.0) return 0.01;
            if (t <= -3.43) return 0.05;
            if (t <= -2.86) return 0.10;
            if (t <= -2.43) return 0.15;
            if (t <= -2.0) return 0.25;
            return 0.40;
        }
    }
}
