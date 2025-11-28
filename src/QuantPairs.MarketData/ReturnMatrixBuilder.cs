using System;
using System.Collections.Generic;
using System.Linq;
using QuantPairs.Core.Models;

namespace QuantPairs.MarketData;

public static class ReturnMatrixBuilder
{
    /// <summary>
    /// Construit une matrice de log-returns (rows = temps, cols = séries) en alignant sur l'intersection des timestamps.
    /// Renvoie (timestamps alignés, matrix NxM, noms des colonnes).
    /// </summary>
    public static (List<DateTimeOffset> index, double[,] returns, string[] series)
        BuildLogReturnMatrix(IReadOnlyDictionary<string, TimeSeriesFrame> frames, DateTimeOffset? start, DateTimeOffset? end)
    {
        // 1) restreindre aux dates [start, end] si données
        var trimmed = frames.ToDictionary(
            kv => kv.Key,
            kv => new TimeSeriesFrame(kv.Key,
                kv.Value.Points.Where(p =>
                    (!start.HasValue || p.Timestamp >= start.Value) &&
                    (!end.HasValue   || p.Timestamp <= end.Value)))
        );

        // 2) timestamps communs (intersection)
        IEnumerable<DateTimeOffset> common = trimmed.First().Value.Points.Select(p => p.Timestamp);
        foreach (var kv in trimmed.Skip(1))
            common = common.Intersect(kv.Value.Points.Select(p => p.Timestamp));
        var idx = common.OrderBy(t => t).ToList();
        if (idx.Count < 3) throw new InvalidOperationException("Not enough aligned timestamps to compute returns.");

        // 3) construire matrice prix alignés
        var series = trimmed.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
        int n = idx.Count;
        int m = series.Length;
        var prices = new double[n, m];

        for (int j = 0; j < m; j++)
        {
            var s = series[j];
            var map = trimmed[s].Points.ToDictionary(p => p.Timestamp, p => p.Value);
            for (int i = 0; i < n; i++)
                prices[i, j] = map[idx[i]];
        }

        // 4) log-returns: r_t = log(P_t) - log(P_{t-1})
        var rets = new double[n - 1, m];
        for (int j = 0; j < m; j++)
        {
            for (int i = 1; i < n; i++)
            {
                var p0 = prices[i - 1, j];
                var p1 = prices[i, j];
                if (p0 <= 0 || p1 <= 0) throw new InvalidOperationException("Prices must be > 0 for log returns.");
                rets[i - 1, j] = Math.Log(p1) - Math.Log(p0);
            }
        }
        // timestamps pour returns = à partir du 2e point
        var idxRet = idx.Skip(1).ToList();
        return (idxRet, rets, series);
    }
}
