using System;
using System.Globalization;

namespace QuantPairs.Core.Utils;

public static class DateTimeUtils
{
    /// <summary>
    /// Essaie de parser un timestamp (ISO 8601 recommandé). Si le fuseau est absent, on assume UTC.
    /// </summary>
    public static bool TryParseToUtc(string? input, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        if (DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
        {
            result = dto.ToUniversalTime();
            return true;
        }

        if (DateTime.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
        {
            result = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            return true;
        }

        return false;
    }
}
