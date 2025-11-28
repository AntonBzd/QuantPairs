using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;

namespace QuantPairs.Research.Clustering;

public sealed class PcaClusterer
{
    public sealed record Result(
        string[] Series,
        int[] ClusterLabels,
        double[,] Loadings,     // M x pcs (stocks x PCs)
        double[] ExplainedVar,  // pcs eigenvalues (variance of kept PCs)
        int K
    );

    /// <summary>
    /// AUTO mode: determine pcs from explained-variance threshold (tau) and K from targetPerCluster heuristic.
    /// </summary>
    public Result FitAuto(
        double[,] returns,
        string[] series,
        double tau = 0.85,
        int pcsCap = 8,
        int targetPerCluster = 10,
        int? seed = 42)
    {
        int n = returns.GetLength(0);
        int m = returns.GetLength(1);

        // === 1) Standardize columns ===
        var R = Matrix<double>.Build.DenseOfArray(returns);
        for (int j = 0; j < m; j++)
        {
            var col = R.Column(j);
            double mean = col.Average();
            double varc = col.Select(x => (x - mean) * (x - mean)).Average();
            double std = Math.Sqrt(Math.Max(varc, 1e-12));
            R.SetColumn(j, col.Select(x => (x - mean) / std).ToArray());
        }

        // === 2) PCA: Covariance + EVD ===
        var cov = R.TransposeThisAndMultiply(R) / Math.Max(1.0, n - 1);
        var evd = cov.Evd(Symmetricity.Symmetric);

        var eigenVals = evd.EigenValues.Select(z => Math.Max(0, z.Real)).ToArray();
        var order = Enumerable.Range(0, m).OrderByDescending(i => eigenVals[i]).ToArray();

        // === 3) Choose pcs via explained variance threshold ===
        double totalVar = eigenVals.Sum();
        if (totalVar < 1e-12) totalVar = 1e-12; // safety
        
        double cum = 0;
        int pcs = 1;
        for (int i = 0; i < m; i++)
        {
            cum += eigenVals[order[i]];
            if (cum / totalVar >= tau) { pcs = i + 1; break; }
        }
        
        pcs = Math.Clamp(pcs, 2, Math.Min(pcsCap, m)); // Cannot have more PCs than variables!

        // === 4) Build loadings matrix (M x pcs) ===
        var L = Matrix<double>.Build.Dense(m, pcs);
        var topVals = new double[pcs];
        for (int k = 0; k < pcs; k++)
        {
            int idx = order[k];
            topVals[k] = eigenVals[idx];
            L.SetColumn(k, evd.EigenVectors.Column(idx));
        }

        // === 5) Determine K from heuristic ===
        int kFixed = Math.Clamp((int)Math.Round((double)m / targetPerCluster), 2, Math.Min(12, m));

        // === 6) K-means  ===
        var rnd = seed.HasValue ? new Random(seed.Value) : new Random();
        var labels = Kmeans(L, kFixed, 100, rnd, out _);

        return new Result(series, labels, L.ToArray(), topVals, kFixed);
    }

    /// <summary>
    /// MANUAL mode: PCA on returns (N x M) -> loadings (M x pcs), then K-means with optional K search.
    /// Use this when you want full control over pcs and K parameters.
    /// </summary>
    public Result Fit(
        double[,] returns,
        string[] series,
        int pcs,
        int? kClusters = null,
        int kMin = 3, int kMax = 12,
        int minSize = 5, int maxSize = 20,
        int kmeansMaxIter = 200,
        int kmeansRuns = 10,
        int? seed = 42,
        IProgress<double>? progress = null)
    {
        int n = returns.GetLength(0);
        int m = returns.GetLength(1);
        if (pcs < 1 || pcs > Math.Min(m, 20)) pcs = Math.Min(10, m);

        // --- 1) Standardize columns ---
        var R = Matrix<double>.Build.DenseOfArray(returns);
        for (int j = 0; j < m; j++)
        {
            var col = R.Column(j);
            double mean = col.Average();
            double varc = col.Select(x => (x - mean) * (x - mean)).Average();
            double std = Math.Sqrt(Math.Max(varc, 1e-12));
            R.SetColumn(j, col.Select(x => (x - mean) / std).ToArray());
        }

        // --- 2) Covariance + EVD ---
        var Rt = R.Transpose();
        var C = (Rt * R) / Math.Max(1.0, (n - 1.0));
        var evd = C.Evd(Symmetricity.Symmetric);

        var eigenVals = evd.EigenValues.Select(z => Math.Max(0, z.Real)).ToArray();
        var eigenVecs = evd.EigenVectors;
        var order = Enumerable.Range(0, m).OrderByDescending(i => eigenVals[i]).ToArray();

        var topVals = new double[pcs];
        for (int k = 0; k < pcs; k++) topVals[k] = eigenVals[order[k]];

        var L = Matrix<double>.Build.Dense(m, pcs); // loadings M x pcs
        for (int k = 0; k < pcs; k++)
        {
            int idx = order[k];
            L.SetColumn(k, eigenVecs.Column(idx));
        }

        // --- 3) K-means on loadings (rows = stocks) ---
        var rnd = seed.HasValue ? new Random(seed.Value) : new Random();

        double bestInertia = double.PositiveInfinity;
        int[] bestLabels = new int[m];
        int bestK = kClusters ?? -1;

        var Kcandidates = kClusters.HasValue
            ? new[] { kClusters.Value }
            : Enumerable.Range(kMin, Math.Max(0, kMax - kMin + 1));

        int totalSteps = (kClusters.HasValue ? 1 : Kcandidates.Count()) * kmeansRuns;
        int step = 0;
        double prevBestAcrossK = double.PositiveInfinity;

        foreach (var K in Kcandidates)
        {
            double bestThisK = double.PositiveInfinity;
            int validRunsThisK = 0;

            for (int run = 0; run < kmeansRuns; run++)
            {
                var labels = Kmeans(L, K, kmeansMaxIter, rnd, out double inertia);

                // cluster size constraints
                var sizes = labels.GroupBy(x => x).Select(g => g.Count()).ToArray();
                bool sizeOk = sizes.All(s => s >= minSize) && sizes.All(s => s <= Math.Max(maxSize, m));
                
                step++;
                progress?.Report(step / (double)totalSteps);
                
                if (!sizeOk) continue;

                validRunsThisK++;

                if (inertia < bestInertia)
                {
                    bestInertia = inertia;
                    bestLabels = labels;
                    bestK = K;
                }

                if (inertia < bestThisK) bestThisK = inertia;

                // within-K early stop: if we found 3 valid runs and improvement is minimal
                if (validRunsThisK >= 3)
                {
                    double gain = (bestThisK - inertia) / Math.Max(1e-12, Math.Abs(bestThisK));
                    if (gain < 0.001) break; // <0.1% improvement
                }
            }

            // across-K early stop: if next K unlikely to help (gain <1% vs previous best)
            if (prevBestAcrossK < double.PositiveInfinity && bestThisK < double.PositiveInfinity)
            {
                double gainK = (prevBestAcrossK - bestThisK) / Math.Max(1e-12, prevBestAcrossK);
                if (gainK < 0.01) break;
            }
            if (bestThisK < double.PositiveInfinity)
                prevBestAcrossK = Math.Min(prevBestAcrossK, bestThisK);
        }

        // Fallback if no valid clustering found
        if (bestK <= 0)
        {
            int K = Math.Max(2, kMin);
            bestLabels = Kmeans(L, K, kmeansMaxIter, rnd, out bestInertia);
            bestK = K;
            progress?.Report(1.0);
        }

        return new Result(series, bestLabels, L.ToArray(), topVals, bestK);
    }

    /// <summary>
    /// K-means with k-means++ initialization, cached rows & early stopping.
    /// </summary>
    private static int[] Kmeans(Matrix<double> data, int K, int maxIter, Random rnd, out double inertia)
    {
        int M = data.RowCount, d = data.ColumnCount;

        // cache rows to avoid repeated allocations
        var X = new double[M][];
        for (int i = 0; i < M; i++) X[i] = data.Row(i).ToArray();

        // k-means++ init
        var centroids = new double[K][];
        var labels = new int[M];

        centroids[0] = (double[])X[rnd.Next(M)].Clone();

        var dist2 = new double[M]; // squared distance to nearest chosen centroid
        for (int c = 1; c < K; c++)
        {
            for (int i = 0; i < M; i++)
            {
                double best = double.PositiveInfinity;
                var xi = X[i];
                for (int j = 0; j < c; j++)
                {
                    double s = 0; var cj = centroids[j];
                    for (int p = 0; p < d; p++) { double diff = xi[p] - cj[p]; s += diff * diff; }
                    if (s < best) best = s;
                }
                dist2[i] = best;
            }
            double sum = dist2.Sum();
            if (sum <= 0) { centroids[c] = (double[])X[rnd.Next(M)].Clone(); continue; }
            double r = rnd.NextDouble() * sum, acc = 0;
            int pick = 0; 
            for (int i = 0; i < M; i++) { acc += dist2[i]; if (acc >= r) { pick = i; break; } }
            centroids[c] = (double[])X[pick].Clone();
        }

        double prevInertia = double.PositiveInfinity;

        for (int iter = 0; iter < maxIter; iter++)
        {
            bool changed = false;

            // assign
            for (int i = 0; i < M; i++)
            {
                int best = 0; double bestDist = double.PositiveInfinity;
                var xi = X[i];
                for (int k = 0; k < K; k++)
                {
                    double s = 0; var ck = centroids[k];
                    for (int p = 0; p < d; p++) { double diff = xi[p] - ck[p]; s += diff * diff; }
                    if (s < bestDist) { bestDist = s; best = k; }
                }
                if (labels[i] != best) { labels[i] = best; changed = true; }
            }

            // recompute centroids
            var counts = new int[K];
            var sums = new double[K][];
            for (int k = 0; k < K; k++) sums[k] = new double[d];

            for (int i = 0; i < M; i++)
            {
                int c = labels[i]; counts[c]++;
                var xi = X[i]; var sc = sums[c];
                for (int p = 0; p < d; p++) sc[p] += xi[p];
            }

            for (int k = 0; k < K; k++)
            {
                if (counts[k] == 0) { centroids[k] = (double[])X[rnd.Next(M)].Clone(); continue; }
                for (int p = 0; p < d; p++) centroids[k][p] = sums[k][p] / counts[k];
            }

            // inertia + early stop
            double curr = 0;
            for (int i = 0; i < M; i++)
            {
                var xi = X[i]; var ck = centroids[labels[i]];
                double s = 0; for (int p = 0; p < d; p++) { double diff = xi[p] - ck[p]; s += diff * diff; }
                curr += s;
            }
            
            if (!changed || Math.Abs(prevInertia - curr) < 1e-8) { prevInertia = curr; break; }
            prevInertia = curr;
        }

        inertia = prevInertia;
        return labels;
    }
}