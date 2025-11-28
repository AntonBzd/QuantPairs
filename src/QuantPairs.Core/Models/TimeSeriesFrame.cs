using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantPairs.Core.Models;

public sealed class TimeSeriesFrame
{
    public string SeriesId { get; }
    public IReadOnlyList<TimeSeriesPoint> Points { get; }

    public TimeSeriesFrame(string seriesId, IEnumerable<TimeSeriesPoint> points)
    {
        SeriesId = seriesId;
        Points = points.OrderBy(p => p.Timestamp).ToList();
    }

    public int Count => Points.Count;
    public DateTimeOffset? FirstTimestamp => Points.Count == 0 ? null : Points[0].Timestamp;
    public DateTimeOffset? LastTimestamp  => Points.Count == 0 ? null : Points[^1].Timestamp;
}
