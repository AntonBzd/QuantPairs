using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace QuantPairs.Data.Readers;

public static class CsvTimeSeriesReader
{
    /// <summary>
    /// Lit un CSV simple (séparateur ',') avec entêtes sur la 1ère ligne.
    /// Retourne une liste de lignes "header -> object".
    /// </summary>
    public static IList<IDictionary<string, object?>> ReadCsv(string path, char delimiter = ',')
    {
        if (!File.Exists(path)) throw new FileNotFoundException(path);

        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return new List<IDictionary<string, object?>>();

        var headers = lines[0].Split(delimiter).Select(h => h.Trim()).ToList();
        var rows = new List<IDictionary<string, object?>>(lines.Length - 1);

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(delimiter);
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            for (int c = 0; c < headers.Count && c < parts.Length; c++)
            {
                var s = parts[c].Trim();

                // Essaye double Invariant, sinon garde string
                if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var dv))
                    dict[headers[c]] = dv;
                else
                    dict[headers[c]] = s;
            }
            rows.Add(dict);
        }

        return rows;
    }
}
