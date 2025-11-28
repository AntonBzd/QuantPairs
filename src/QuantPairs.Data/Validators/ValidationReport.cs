using System;
using System.Collections.Generic;
using System.Text;

namespace QuantPairs.Data.Validators;

public sealed class ValidationReport
{
    public int RowCount { get; set; }
    public int DuplicateTimestamps { get; set; }
    public int NaNOrInvalidCount { get; set; }

    public DateTimeOffset? MinTimestampUtc { get; set; }
    public DateTimeOffset? MaxTimestampUtc { get; set; }

    /// <summary>
    /// Fréquence déduite au format TimeSpan? pour compatibilité avec les tests (ex: 1:00:00 pour horaire).
    /// </summary>
    public TimeSpan? DeducedFrequency { get; set; }

    /// <summary>
    /// Libellé lisible par la CLI : "hourly" | "daily" | "irregular" | "unknown".
    /// </summary>
    public string? InferredFrequency { get; set; }

    /// <summary>Identifiants de séries (noms de colonnes après 'timestamp').</summary>
    public IList<string> SeriesIds { get; set; } = new List<string>();

    // Alias 
    public int CountNa
    {
        get => NaNOrInvalidCount;
        set => NaNOrInvalidCount = value;
    }

    public int CountDuplicates
    {
        get => DuplicateTimestamps;
        set => DuplicateTimestamps = value;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Rows         : {RowCount}");
        sb.AppendLine($"From (UTC)   : {MinTimestampUtc:O}");
        sb.AppendLine($"To   (UTC)   : {MaxTimestampUtc:O}");
        sb.AppendLine($"Frequency    : {InferredFrequency ?? (DeducedFrequency?.ToString() ?? "unknown")}");
        sb.AppendLine($"Duplicates   : {DuplicateTimestamps}");
        sb.AppendLine($"Invalid vals : {NaNOrInvalidCount}");
        sb.AppendLine($"Series       : {string.Join(", ", SeriesIds)}");
        return sb.ToString();
    }
}
