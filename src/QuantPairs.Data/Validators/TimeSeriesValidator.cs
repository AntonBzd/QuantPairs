using System;
using System.Collections.Generic;
using System.Linq;
using QuantPairs.Core.Errors;
using QuantPairs.Core.Models;
using QuantPairs.Core.Utils;

namespace QuantPairs.Data.Validators
{
    public sealed class TimeSeriesValidator
    {
        public TimeSeriesSchema Schema { get; init; } = new TimeSeriesSchema();

        public (TimeSeriesFrame normalized, ValidationReport report) Validate(TimeSeriesFrame frame)
        {
            var report = new ValidationReport();
            var pts = frame.Points?.ToList() ?? new List<TimeSeriesPoint>();

            // 1) Nettoyage valeurs invalides
            var before = pts.Count;
            pts = pts.Where(p => !double.IsNaN(p.Value) && !double.IsInfinity(p.Value)).ToList();
            report.NaNOrInvalidCount = before - pts.Count;

            // 2) Déduplication timestamps (garder la dernière occurrence)
            var grouped = pts.GroupBy(p => p.Timestamp).ToDictionary(g => g.Key, g => g.Last());
            report.DuplicateTimestamps = pts.Count - grouped.Count;
            var unique = grouped.Values.ToList();

            // 3) Tri
            var sorted = unique.OrderBy(p => p.Timestamp).ToList();

            // 4) Contraintes > 0 si demandé
            if (Schema.RequireStrictlyPositive)
                sorted = sorted.Where(p => p.Value > 0).ToList();

            // 5) Remplir le report
            report.RowCount = sorted.Count;
            report.MinTimestampUtc = sorted.FirstOrDefault()?.Timestamp;
            report.MaxTimestampUtc = sorted.LastOrDefault()?.Timestamp;

            var (median, label) = InferFrequency(sorted.Select(x => x.Timestamp).ToList());
            report.DeducedFrequency = median;
            report.InferredFrequency = label;
            report.SeriesIds = new List<string> { frame.SeriesId };

            var normalized = new TimeSeriesFrame(frame.SeriesId, sorted);
            return (normalized, report);
        }

        // === Utilitaire multi-séries (utilisé par la CLI)
        public static (IReadOnlyDictionary<string, TimeSeriesFrame> Frames, ValidationReport Report)
            ValidateAndBuildFrames(IList<IDictionary<string, object?>> rows, TimeSeriesSchema schema)
        {
            if (rows.Count == 0) throw new DataFormatException("No data rows found.");

            var cols = rows[0].Keys.ToList();
            var tsCol = cols.FirstOrDefault(c => string.Equals(c, schema.TimestampColumn, StringComparison.OrdinalIgnoreCase))
                        ?? throw new DataFormatException($"Timestamp column '{schema.TimestampColumn}' not found.");
            var seriesCols = cols.Where(c => !string.Equals(c, tsCol, StringComparison.OrdinalIgnoreCase)).ToList();
            if (seriesCols.Count == 0) throw new DataFormatException("No series columns found.");

            // Parse toutes les lignes
            var parsed = new List<(DateTimeOffset ts, Dictionary<string,double?> vals)>(rows.Count);
            foreach (var r in rows)
            {
                var tsOk = DateTimeUtils.TryParseToUtc(Convert.ToString(r[tsCol]), out var ts);
                if (!tsOk) continue;

                var vals = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
                foreach (var sc in seriesCols)
                {
                    double? v = null;
                    var obj = r[sc];
                    if (obj is double d) v = d;
                    else if (obj is float f) v = f;
                    else if (obj is decimal m) v = (double)m;
                    else if (obj is int i) v = i;
                    else if (obj is long l) v = l;
                    else if (obj is string s && double.TryParse(s, System.Globalization.NumberStyles.Any,
                             System.Globalization.CultureInfo.InvariantCulture, out var dv)) v = dv;
                    vals[sc] = v;
                }
                parsed.Add((ts, vals));
            }

            // Dédup timestamps (garder dernière ligne)
            var ordered = parsed
                .GroupBy(p => p.ts)
                .Select(g => (ts: g.Key, vals: g.Last().vals))
                .OrderBy(p => p.ts)
                .ToList();

            int duplicates = parsed.Count - ordered.Count;

            // Construire frames par série
            var frameBuild = new Dictionary<string, List<QuantPairs.Core.Models.TimeSeriesPoint>>(StringComparer.OrdinalIgnoreCase);
            foreach (var sc in seriesCols) frameBuild[sc] = new List<QuantPairs.Core.Models.TimeSeriesPoint>(ordered.Count);

            int invalid = 0;
            foreach (var row in ordered)
            {
                foreach (var sc in seriesCols)
                {
                    var v = row.vals[sc];
                    if (v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) { invalid++; continue; }
                    if (schema.RequireStrictlyPositive && v.Value <= 0) { invalid++; continue; }
                    frameBuild[sc].Add(new QuantPairs.Core.Models.TimeSeriesPoint(row.ts, v.Value, sc));
                }
            }

            var frames = frameBuild.ToDictionary(kv => kv.Key, kv => new TimeSeriesFrame(kv.Key, kv.Value));

            // Fréquence globale (pour affichage CLI)
            var allTs = ordered.Select(o => o.ts).ToList();
            var (median, label2) = InferFrequency(allTs);

            var report = new ValidationReport
            {
                RowCount = ordered.Count,
                MinTimestampUtc = ordered.FirstOrDefault().ts,
                MaxTimestampUtc = ordered.LastOrDefault().ts,
                DeducedFrequency = median,
                InferredFrequency = label2,
                DuplicateTimestamps = duplicates,
                NaNOrInvalidCount = invalid,
                SeriesIds = frames.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList()
            };

            return (frames, report);
        }

        private static (TimeSpan? median, string label) InferFrequency(IReadOnlyList<DateTimeOffset> ts)
        {
            if (ts.Count < 3) return (null, "unknown");
            var deltas = new List<TimeSpan>(ts.Count - 1);
            for (int i = 1; i < ts.Count; i++) deltas.Add(ts[i] - ts[i - 1]);

            var median = deltas.OrderBy(d => d).ElementAt(deltas.Count / 2);

            static bool approx(TimeSpan x, TimeSpan target, double tolMinutes)
                => Math.Abs((x - target).TotalMinutes) <= tolMinutes;

            if (approx(median, TimeSpan.FromHours(1), 5)) return (median, "hourly");
            if (approx(median, TimeSpan.FromDays(1), 60)) return (median, "daily");
            return (median, "irregular");
        }
    }
}
