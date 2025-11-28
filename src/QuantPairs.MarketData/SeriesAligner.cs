using System;
using System.Collections.Generic;
using System.Linq;
using QuantPairs.Core.Models;

namespace QuantPairs.MarketData
{
    public static class SeriesAligner
    {
        /// <summary>
        /// Aligne les séries sur l’intersection des timestamps, coupe sur [start,end],
        /// et renvoie (index UTC trié, log-prix alignés). Retourne des collections vides si rien à aligner.
        /// </summary>
        public static (DateTimeOffset[] index, Dictionary<string, double[]> logs)
            BuildAlignedLogPrices(
                IReadOnlyDictionary<string, TimeSeriesFrame> frames,
                DateTimeOffset? start,
                DateTimeOffset? end)
        {
            if (frames is null || frames.Count == 0)
                return (Array.Empty<DateTimeOffset>(), new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase));

            // 1) Filtrer chaque série sur la fenêtre [start, end]
            var perSeries = new Dictionary<string, List<(DateTimeOffset ts, double v)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in frames)
            {
                var id = kv.Key;
                var pts = kv.Value.Points ?? Array.Empty<TimeSeriesPoint>();

                var filtered = pts
                    .Where(p =>
                        (!start.HasValue || p.Timestamp >= start.Value) &&
                        (!end.HasValue   || p.Timestamp <= end.Value))
                    .OrderBy(p => p.Timestamp)
                    .Select(p => (p.Timestamp, p.Value))
                    .ToList();

                // garde seulement prix strictly positifs pour log
                filtered = filtered.Where(x => x.Value > 0 && !double.IsNaN(x.Value) && !double.IsInfinity(x.Value)).ToList();

                if (filtered.Count > 0)
                    perSeries[id] = filtered;
            }

            if (perSeries.Count < 2)
                return (Array.Empty<DateTimeOffset>(), new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase));

            // 2) Construire l’intersection des timestamps
            HashSet<DateTimeOffset>? inter = null;
            foreach (var list in perSeries.Values)
            {
                var set = new HashSet<DateTimeOffset>(list.Select(t => t.ts));
                inter = inter is null ? set : new HashSet<DateTimeOffset>(inter.Intersect(set));
                if (inter.Count == 0) break;
            }

            if (inter is null || inter.Count == 0)
                return (Array.Empty<DateTimeOffset>(), new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase));

            var index = inter.ToList();
            index.Sort();

            // 3) Map rapide timestamp -> valeur pour chaque série et sortir log-prix alignés
            var result = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (sid, list) in perSeries)
            {
                var map = new Dictionary<DateTimeOffset, double>(list.Count);
                foreach (var t in list) map[t.ts] = t.v;

                // si une série n’a pas toutes les dates de l’intersection, on la jette
                bool hasAll = index.All(ts => map.ContainsKey(ts));
                if (!hasAll) continue;

                var arr = new double[index.Count];
                for (int i = 0; i < index.Count; i++)
                    arr[i] = Math.Log(map[index[i]]);

                result[sid] = arr;
            }

            if (result.Count < 2)
                return (Array.Empty<DateTimeOffset>(), new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase));

            return (index.ToArray(), result);
        }
    }
}
