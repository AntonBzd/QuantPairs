using System;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;
using QuantPairs.Core.Errors;
using QuantPairs.Data.Validators;


namespace QuantPairs.Data.Readers
{
    public sealed class ExcelTimeSeriesReader
    {
        public (Dictionary<string, QuantPairs.Core.Models.TimeSeriesFrame> frames, ValidationReport report)
            ReadExcel(string path, string? sheetName = null)
        {
            var rows = ReadSheet(path, sheetName);
            var (frames, report) = TimeSeriesValidator.ValidateAndBuildFrames(rows, new TimeSeriesSchema());
            return (new Dictionary<string, QuantPairs.Core.Models.TimeSeriesFrame>(frames, StringComparer.OrdinalIgnoreCase), report);
        }

        // Utilitaire interne (lecture brute en dictionnaires)
        private static IList<IDictionary<string, object?>> ReadSheet(string path, string? sheetName)
        {
            using var wb = new XLWorkbook(path);

            var ws = sheetName is null
                ? wb.Worksheets.FirstOrDefault()
                : wb.Worksheets.Worksheet(sheetName);

            if (ws is null)
                throw new DataFormatException($"Worksheet not found (sheet='{sheetName ?? "<first>"}').");

            var used = ws.RangeUsed();
            if (used is null)
                throw new DataFormatException("Worksheet is empty (no used range).");

            int rowCount = used.RowCount();
            int colCount = used.ColumnCount();
            if (rowCount < 2 || colCount < 2)
                throw new DataFormatException("Expected at least 1 header row and 1 data row, and >= 2 columns.");

            var headerRow = used.FirstRow();
            var headers = headerRow.Cells(1, colCount)
                                  .Select(c => c.GetString().Trim())
                                  .ToList();

            var rows = new List<IDictionary<string, object?>>(rowCount - 1);
            for (int r = 2; r <= rowCount; r++)
            {
                var row = used.Row(r);
                var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int c = 1; c <= colCount; c++)
                {
                    var cell = row.Cell(c);
                    object? val = cell.Value;
                    dict[headers[c - 1]] = val;
                }
                rows.Add(dict);
            }
            return rows;
        }
    }
}
