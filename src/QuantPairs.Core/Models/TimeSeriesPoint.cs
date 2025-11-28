using System;

namespace QuantPairs.Core.Models
{
    public sealed class TimeSeriesPoint
    {
        public DateTimeOffset Timestamp { get; init; }
        public double Value { get; init; }
        public string SeriesId { get; init; } = string.Empty;

        public TimeSeriesPoint(DateTimeOffset timestamp, double value, string seriesId)
        {
            Timestamp = timestamp;
            Value = value;
            SeriesId = seriesId ?? throw new ArgumentNullException(nameof(seriesId));
        }
    }
}
