using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using QuantPairs.Core.Models;

namespace QuantPairs.Data.Writers;

public sealed class CsvWriterService
{
    /// <summary>
    /// Écrit un CSV "tidy": timestamp,series_id,value (UTC ISO 8601).
    /// </summary>
    public void WriteTidy(IEnumerable<TimeSeriesFrame> frames, string outPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
        using var sw = new StreamWriter(outPath);
        sw.WriteLine("timestamp,series_id,value");

        foreach (var f in frames)
        {
            foreach (var p in f.Points)
            {
                sw.WriteLine($"{p.Timestamp:O},{f.SeriesId},{p.Value.ToString(CultureInfo.InvariantCulture)}");
            }
        }
    }
}
