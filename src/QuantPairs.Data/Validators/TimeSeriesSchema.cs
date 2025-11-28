namespace QuantPairs.Data.Validators;

public sealed class TimeSeriesSchema
{
    /// <summary>Nom de la colonne de temps (par défaut "timestamp").</summary>
    public string TimestampColumn { get; init; } = "timestamp";

    /// <summary>Exiger des valeurs strictement > 0 (utile si log-transform prévue plus tard).</summary>
    public bool RequireStrictlyPositive { get; init; } = false;
}
