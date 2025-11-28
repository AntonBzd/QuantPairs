using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

using QuantPairs.Core.Models;
using QuantPairs.Data.Readers;
using QuantPairs.Data.Validators;
using QuantPairs.MarketData;            // SeriesAligner & ReturnMatrixBuilder
using QuantPairs.Research.Clustering;   // PcaClusterer
using QuantPairs.Research.Cointegration;// EngleGranger
using QuantPairs.Trading;               // KalmanHedge, Backtester
using QuantPairs.Research.Validation;   // Grid, Validator (grid search)

/* =============================
 *   PRESENTATION HELPERS ONLY
 *   (no logic changes below)
 * ============================= */

static string Rule(char ch = '─', int width = 80) => new string(ch, width);

static void H1(string title)
{
    Console.WriteLine();
    Console.WriteLine(Rule());
    Console.WriteLine(title);
    Console.WriteLine(Rule());
}

static void H2(string title)
{
    Console.WriteLine();
    Console.WriteLine($"— {title} —");
}

static void Info(string msg)  => Console.WriteLine($"[INFO]  {msg}");
static void Warn(string msg)  => Console.WriteLine($"[WARN]  {msg}");
static void Error(string msg) => Console.Error.WriteLine($"[ERROR] {msg}");

static void Step(int n, int total, string title)
{
    Console.WriteLine();
    Console.WriteLine($">▶ Step {n}/{total}: {title}");
}

static void Bullets(params string[] items)
{
    foreach (var s in items) Console.WriteLine($"   • {s}");
}

static string FormatElapsed(TimeSpan t)
{
    return (t.TotalSeconds < 1)
        ? $"{t.TotalMilliseconds:0} ms"
        : (t.TotalMinutes < 1 ? $"{t.TotalSeconds:0.00} s" : $"{t.TotalMinutes:0.00} min");
}



static T WithTimer<T>(string label, Func<T> f)
{
    var sw = Stopwatch.StartNew();
    var r  = f();
    sw.Stop();
    Info($"{label} done in {FormatElapsed(sw.Elapsed)}");
    return r;
}



static void PrintKeyValues((string k, string v)[] kvs)
{
    foreach (var (k, v) in kvs) Console.WriteLine($"   - {k}: {v}");
}

static void PrintTable(string[] headers, IEnumerable<string[]> rows)
{
    // compute widths
    var data = rows.ToList();
    int cols = headers.Length;
    var w = new int[cols];
    for (int c = 0; c < cols; c++)
    {
        w[c] = headers[c].Length;
        foreach (var r in data) if (c < r.Length) w[c] = Math.Max(w[c], r[c]?.Length ?? 0);
    }

    string Sep(string left, string mid, string right, string fill)
        => left + string.Join(mid, w.Select(W => new string(fill[0], W + 2))) + right;

    Console.WriteLine(Sep("+", "+", "+", "-"));
    // header row
    Console.Write("|");
    for (int c = 0; c < cols; c++)
        Console.Write(" " + headers[c].PadRight(w[c]) + " |");
    Console.WriteLine();
    Console.WriteLine(Sep("+", "+", "+", "-"));

    foreach (var r in data)
    {
        Console.Write("|");
        for (int c = 0; c < cols; c++)
        {
            var cell = (c < r.Length ? r[c] : "") ?? "";
            Console.Write(" " + cell.PadRight(w[c]) + " |");
        }
        Console.WriteLine();
    }
    Console.WriteLine(Sep("+", "+", "+", "-"));
}

// Simple progress bar that renders nicely in a WPF textbox
static void RenderProgress(double p)
{
    int width = 30;
    int filled = (int)(p * width);
    Console.Write("\r[");
    Console.Write(new string('#', filled));
    Console.Write(new string('.', Math.Max(0, width - filled)));
    Console.Write($"] {p * 100:0.0}% ");
    if (p >= 1.0) Console.WriteLine();
}

/* =============================
 *           ROUTER
 * ============================= */

H1("QuantPairs CLI");
Info($"Run ID: {DateTime.UtcNow:yyyyMMdd-HHmmss} (UTC)");
Info($"Args   : {string.Join(" ", args)}");

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";

bool hasCoint    = args.Any(a => string.Equals(a, "coint",    StringComparison.OrdinalIgnoreCase));
bool hasCluster  = args.Any(a => string.Equals(a, "cluster",  StringComparison.OrdinalIgnoreCase));
bool hasValidate = args.Any(a => string.Equals(a, "validate", StringComparison.OrdinalIgnoreCase));

if (mode == "cluster" || (!string.IsNullOrEmpty(mode) && hasCluster))
{
    H2("MODE: CLUSTER (PCA + KMeans)");
    Console.WriteLine("Purpose:");
    Bullets(
        "Reduce dimensionality with PCA (variance capture).",
        "Group similar assets with KMeans (optional auto K).",
        "Export mapping: data/processed/clusters_*.csv");
    RunCluster(args);
    return;
}
else if (mode == "coint" || (!string.IsNullOrEmpty(mode) && hasCoint))
{
    H2("MODE: COINTEGRATION (Engle–Granger)");
    Console.WriteLine("Purpose:");
    Bullets(
        "Check pair-wise cointegration on TRAIN (80%).",
        "Compute half-life from residuals.",
        "Export ranked pairs: data/processed/coint_pairs_*.csv");
    RunCoint(args);
    return;
}
else if (mode == "validate" || (!string.IsNullOrEmpty(mode) && hasValidate))
{
    H2("MODE: VALIDATION (Grid over configs)");
    Console.WriteLine("Purpose:");
    Bullets(
        "Evaluate candidate pairs on VALID slice.",
        "Grid over entry/exit/stop + sizing + (Q,R for Kalman).",
        "Export ALL configs: data/processed/validate_all_*.csv",
        "Print per-pair top configs and global Top 10.");
    RunValidate(args);
    return;
}
else if (mode == "oos" || (!string.IsNullOrEmpty(mode) && args.Any(a => string.Equals(a, "oos", StringComparison.OrdinalIgnoreCase))))
{
    H2("MODE: OOS FINAL TEST");
    Console.WriteLine("Purpose:");
    Bullets(
        "Replay best VALID configs on OOS period only.",
        "Rank by Sharpe, export equity curves CSV for top results.");
    RunOosFinal(args);
    return;
}
else
{
    H2("MODE: DATA SUMMARY");
    Console.WriteLine("Purpose:");
    Bullets("Quick schema validation + dataset summary.");
    RunDataSummary(args);
    return;
}

/* =============================
 *          DATA SUMMARY
 * ============================= */
static void RunDataSummary(string[] args)
{
    string? file = null;
    string format = "auto";
    string? sheet = null;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--file" && i + 1 < args.Length) file = args[++i];
        else if (args[i] == "--format" && i + 1 < args.Length) format = args[++i];
        else if (args[i] == "--sheet" && i + 1 < args.Length) sheet = args[++i];
    }

    if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
    {
        Error("Usage: summary --file <path> [--format csv|excel] [--sheet <n>]");
        return;
    }

    try
    {
        Dictionary<string, TimeSeriesFrame> frames;
        ValidationReport report;

        (frames, report) = WithTimer<(Dictionary<string, TimeSeriesFrame>, ValidationReport)>(
            "Loading & validating",
            () =>
            {
                if (format == "excel" || (format == "auto" && (file!.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || file!.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))))
                {
                    var reader = new ExcelTimeSeriesReader();
                    var (f, rep) = reader.ReadExcel(file!, sheet);
                    return (new Dictionary<string, TimeSeriesFrame>(f, StringComparer.OrdinalIgnoreCase), rep);
                }
                else
                {
                    var rows = CsvTimeSeriesReader.ReadCsv(file!);
                    (IReadOnlyDictionary<string, TimeSeriesFrame> f, ValidationReport rep) = TimeSeriesValidator.ValidateAndBuildFrames(rows, new TimeSeriesSchema());
                    return (new Dictionary<string, TimeSeriesFrame>(f, StringComparer.OrdinalIgnoreCase), rep);
                }
            });

        H1("DATA SUMMARY");
        PrintKeyValues(new[]
        {
            ("File", file!),
            ("Series", frames.Count.ToString(CultureInfo.InvariantCulture))
        });
        Console.WriteLine();
        Console.WriteLine(report.ToString());
        Info("Summary complete.");
    }
    catch (Exception ex)
    {
        Error(ex.Message);
        Environment.ExitCode = 1;
    }
}

/* =============================
 *   CLUSTER (PCA + KMeans)
 * ============================= */
static void RunCluster(string[] args)
{
    string? cfile = null, cformat = "auto", csheet = null;
    DateTimeOffset? trainStart = null, trainEnd = null;
    int pcs = 0;   // 0 => AUTO
    int? k  = null; // null => AUTO

    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--file"        && i + 1 < args.Length) cfile = args[++i];
        else if (args[i] == "--format" && i + 1 < args.Length) cformat = args[++i];
        else if (args[i] == "--sheet"  && i + 1 < args.Length) csheet = args[++i];
        else if (args[i] == "--train-start" && i + 1 < args.Length && DateTimeOffset.TryParse(args[++i], out var ts)) trainStart = ts;
        else if (args[i] == "--train-end"   && i + 1 < args.Length && DateTimeOffset.TryParse(args[++i], out var te)) trainEnd = te;
        else if (args[i] == "--pcs"    && i + 1 < args.Length && int.TryParse(args[++i], out var pX)) pcs = pX;
        else if (args[i] == "--k"      && i + 1 < args.Length && int.TryParse(args[++i], out var kX)) k = kX;
    }

    if (string.IsNullOrWhiteSpace(cfile) || !File.Exists(cfile))
    {
        Error("Usage: cluster --file <path> [--format csv|excel] [--sheet <n>] [--train-start ISO] [--train-end ISO] [--pcs N] [--k K]");
        Environment.ExitCode = 2;
        return;
    }

    Step(1, 3, "Load & split");
    IReadOnlyDictionary<string, TimeSeriesFrame> frames = WithTimer("Reading dataset", () =>
    {
        if (cformat == "excel" || (cformat == "auto" && (cfile!.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || cfile!.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))))
        {
            var reader = new ExcelTimeSeriesReader();
            var (f, _) = reader.ReadExcel(cfile!, csheet);
            return f;
        }
        else
        {
            var rows = CsvTimeSeriesReader.ReadCsv(cfile!);
            var (f, _) = TimeSeriesValidator.ValidateAndBuildFrames(rows, new TimeSeriesSchema());
            return f;
        }
    });

    if (trainStart is null && trainEnd is null)
    {
        var tmp = ReturnMatrixBuilder.BuildLogReturnMatrix(frames, null, null);
        var fullIdx = tmp.index;
        if (fullIdx.Count < 20)
        {
            Warn("Dataset too short for 80/20 split; using all data as TRAIN.");
            trainStart = fullIdx.First();
            trainEnd = fullIdx.Last();
        }
        else
        {
            int cut = (int)Math.Floor(fullIdx.Count * 0.8);
            trainStart = fullIdx.First();
            trainEnd = fullIdx[cut - 1];
            Info($"[Auto split 80/20] TRAIN {trainStart:yyyy-MM-dd} → {trainEnd:yyyy-MM-dd}");
            Info($"Test slice kept for later steps");
        }
    }

    Step(2, 3, "PCA + KMeans");
    var builder = ReturnMatrixBuilder.BuildLogReturnMatrix(frames, trainStart, trainEnd);
    var ret    = builder.returns;
    var series = builder.series;

    Console.WriteLine();
    PrintKeyValues(new[]
    {
        ("Series", series.Length.ToString(CultureInfo.InvariantCulture)),
        ("Train periods", ret.GetLength(0).ToString(CultureInfo.InvariantCulture))
    });

    var clusterer = new PcaClusterer();
    PcaClusterer.Result res;
    if (pcs <= 0 && k is null)
    {
        Info("AUTO mode: choosing PCs and K…");
        res = WithTimer("PCA+KMeans (auto)", () => clusterer.FitAuto(ret, series));
    }
    else
    {
        Info($"MANUAL mode: PCs={(pcs>0?pcs:6)}, K={(k?.ToString() ?? "auto search")}");
        var progress = new Progress<double>(RenderProgress);
        res = WithTimer("PCA+KMeans", () => clusterer.Fit(
            ret, series,
            pcs: (pcs > 0 ? pcs : 6),
            kClusters: k,
            kMin: 3, kMax: 10,
            minSize: 5,
            maxSize: Math.Max(20, series.Length),
            kmeansMaxIter: 100,
            kmeansRuns: 3,
            progress: progress));
    }

    Step(3, 3, "Report + export");
    double totalExplained = res.ExplainedVar.Sum();
    var evRows = new List<string[]>();
    double cum = 0;
    for (int i = 0; i < Math.Min(5, res.ExplainedVar.Length); i++)
    {
        cum += res.ExplainedVar[i];
        evRows.Add(new[]
        {
            $"PC{i+1}",
            $"{res.ExplainedVar[i]/totalExplained*100:0.00}%",
            $"{cum/totalExplained*100:0.00}%"
        });
    }
    PrintTable(new[] { "PC", "Explained", "Cumulative" }, evRows);

    var groups = series.Zip(res.ClusterLabels, (s, c) => (s, c))
                       .GroupBy(x => x.c)
                       .OrderBy(g => g.Key)
                       .ToList();

    var groupRows = groups.Select(g => new[] { $"Cluster {g.Key}", g.Count().ToString(), string.Join(", ", g.Select(x => x.s)) });
    PrintTable(new[] { "Group", "Count", "Members" }, groupRows);

    var outDir = Path.Combine("data", "processed");
    Directory.CreateDirectory(outDir);
    var outPath = Path.Combine(outDir, $"clusters_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
    using (var sw = new StreamWriter(outPath))
    {
        sw.WriteLine("series,cluster");
        foreach (var g in groups)
            foreach (var (s, _) in g)
                sw.WriteLine($"{s},{g.Key}");
    }
    Info($"Saved: {outPath}");
    Console.WriteLine("Next: run 'coint' with --clusters <that file> to restrict pair tests per group.");
}

/* =============================
 * COINTEGRATION (Engle–Granger)
 * ============================= */
static void RunCoint(string[] args)
{
    string? file = null, format = "auto", sheet = null, clustersPath = null;
    DateTimeOffset? trainStart = null, trainEnd = null;
    double alphaLevel = 0.05; // display only; we export both 5% & 10%
    int maxLag = 4;

    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--file" && i + 1 < args.Length) file = args[++i];
        else if (args[i] == "--format" && i + 1 < args.Length) format = args[++i];
        else if (args[i] == "--sheet" && i + 1 < args.Length) sheet = args[++i];
        else if (args[i] == "--clusters" && i + 1 < args.Length) clustersPath = args[++i];
        else if (args[i] == "--train-start" && i + 1 < args.Length && DateTimeOffset.TryParse(args[++i], out var ts)) trainStart = ts;
        else if (args[i] == "--train-end" && i + 1 < args.Length && DateTimeOffset.TryParse(args[++i], out var te)) trainEnd = te;
        else if (args[i] == "--alpha" && i + 1 < args.Length && double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var a)) alphaLevel = a;
        else if (args[i] == "--max-lag" && i + 1 < args.Length && int.TryParse(args[++i], out var ml)) maxLag = ml;
    }

    if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
    {
        Error("Usage: coint --file <path> [--format csv|excel] [--sheet <n>] [--clusters path] [--train-start ISO] [--train-end ISO] [--alpha 0.05|0.10|0.15] [--max-lag N]");
        Environment.ExitCode = 2;
        return;
    }

    Step(1, 4, "Load & TRAIN window");
    IReadOnlyDictionary<string, TimeSeriesFrame> frames = WithTimer("Reading dataset", () =>
    {
        if (format == "excel" || (format == "auto" && (file!.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || file!.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))))
        {
            var reader = new ExcelTimeSeriesReader();
            var (f, _) = reader.ReadExcel(file!, sheet);
            return f;
        }
        else
        {
            var rows = CsvTimeSeriesReader.ReadCsv(file!);
            var (f, _) = TimeSeriesValidator.ValidateAndBuildFrames(rows, new TimeSeriesSchema());
            return f;
        }
    });

    if (trainStart is null && trainEnd is null)
    {
        var tmp = ReturnMatrixBuilder.BuildLogReturnMatrix(frames, null, null);
        var fullIdx = tmp.index;
        if (fullIdx.Count < 20)
        {
            Warn("Dataset too short; using all as TRAIN.");
            trainStart = fullIdx.First();
            trainEnd = fullIdx.Last();
        }
        else
        {
            int cut = (int)Math.Floor(fullIdx.Count * 0.8);
            trainStart = fullIdx.First();
            trainEnd   = fullIdx[cut - 1];
            Info($"[Auto split 80/20] TRAIN {trainStart:yyyy-MM-dd} → {trainEnd:yyyy-MM-dd}");
        }
    }

    Step(2, 4, "Align log-prices (TRAIN)");
    var (idx, logs) = WithTimer("(Align)", () => SeriesAligner.BuildAlignedLogPrices(frames, trainStart, trainEnd));
    if (logs.Count < 2 || idx.Length < 50)
    {
        Warn("Not enough aligned data to run cointegration (need >= 50 obs).");
        return;
    }
    PrintKeyValues(new[] { ("Series", logs.Count.ToString()), ("Obs", idx.Length.ToString()) });

    Step(3, 4, "Prepare clusters");
    Dictionary<int, List<string>> clusters = new();
    if (!string.IsNullOrWhiteSpace(clustersPath) && File.Exists(clustersPath))
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(clustersPath).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (parts.Length >= 2 && int.TryParse(parts[1], out int cl))
                map[parts[0].Trim()] = cl;
        }
        foreach (var s in logs.Keys)
        {
            if (!map.TryGetValue(s, out int cl)) cl = 0;
            if (!clusters.ContainsKey(cl)) clusters[cl] = new List<string>();
            clusters[cl].Add(s);
        }
        Info($"Clusters loaded: {clusters.Count}");
    }
    else
    {
        clusters[0] = logs.Keys.ToList();
        Info("No clusters file → single group.");
    }

    Step(4, 4, "Engle–Granger tests");
    double critical5 = -3.58;
    double critical10 = -3.29;
    Console.WriteLine("Using MacKinnon criticals (const, 2 vars):");
    PrintKeyValues(new[] { ("5%", critical5.ToString("F2")), ("10%", critical10.ToString("F2")) });

    var outDir = Path.Combine("data", "processed");
    Directory.CreateDirectory(outDir);
    var outPath = Path.Combine(outDir, $"coint_pairs_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
    using var sw = new StreamWriter(outPath);
    sw.WriteLine("cluster,series_y,series_x,n,adf_stat,beta,alpha,used_lag,half_life,pass_5,pass_10,approx_pvalue");

    int totalPairs = 0, totalPass5 = 0, totalPass10 = 0;

    foreach (var kv in clusters.OrderBy(k => k.Key))
    {
        var clId = kv.Key;
        var names = kv.Value.Distinct().Where(logs.ContainsKey).ToList();
        if (names.Count < 2)
        {
            Warn($"Cluster {clId}: only {names.Count} series → skip");
            continue;
        }

        var results = new List<(string y, string x, EngleGrangerResult eg, double? hl)>();

        for (int i = 0; i < names.Count; i++)
        for (int j = i + 1; j < names.Count; j++)
        {
            var s1 = names[i];
            var s2 = names[j];
            var yArr = logs[s2];
            var xArr = logs[s1];
            if (yArr.Length != xArr.Length || yArr.Length < 50) continue;

            var eg1 = EngleGranger.TestPair(yArr, xArr, s2, s1, maxLag);
            var hl1 = EstimateHalfLifeFromAlphaBetaResiduals(yArr, xArr, eg1.Alpha, eg1.Beta);
            results.Add((s2, s1, eg1, hl1));

            var eg2 = EngleGranger.TestPair(xArr, yArr, s1, s2, maxLag);
            var hl2 = EstimateHalfLifeFromAlphaBetaResiduals(xArr, yArr, eg2.Alpha, eg2.Beta);
            results.Add((s1, s2, eg2, hl2));
        }

        if (results.Count == 0)
        {
            Warn($"Cluster {clId}: no valid pairs.");
            continue;
        }

        totalPairs += results.Count;
        var ordered = results.OrderBy(r => r.eg.AdfStat).ToList();
        int pass5 = ordered.Count(r => r.eg.AdfStat <= critical5);
        int pass10 = ordered.Count(r => r.eg.AdfStat <= critical10);
        totalPass5 += pass5;
        totalPass10 += pass10;

        Console.WriteLine();
        PrintKeyValues(new[]
        {
            ($"Cluster {clId}", $"{names.Count} series"),
            ("Directional pairs", results.Count.ToString()),
            ("@5%", pass5.ToString()),
            ("@10%", pass10.ToString())
        });

        Console.WriteLine("Top 10 by ADF (most negative strongest):");
        var topRows = ordered.Take(10).Select(r =>
        {
            string flag = r.eg.AdfStat <= critical5 ? "5%" : (r.eg.AdfStat <= critical10 ? "10%" : "Fail");
            string hlStr = r.hl.HasValue ? r.hl.Value.ToString("F1", CultureInfo.InvariantCulture) : "NA";
            return new[]
            {
                $"{r.y} ~ {r.x}",
                r.eg.Nobs.ToString(),
                r.eg.AdfStat.ToString("F3", CultureInfo.InvariantCulture),
                r.eg.Beta.ToString("F3", CultureInfo.InvariantCulture),
                r.eg.UsedLag.ToString(),
                hlStr,
                flag
            };
        });
        PrintTable(new[] { "Pair", "n", "ADF", "beta", "lag", "HL", "α-level" }, topRows);

        foreach (var (y, x, eg, hl) in ordered)
        {
            bool p5 = eg.AdfStat <= critical5;
            bool p10 = eg.AdfStat <= critical10;
            sw.WriteLine(string.Join(",", new[]
            {
                clId.ToString(CultureInfo.InvariantCulture), y, x,
                eg.Nobs.ToString(CultureInfo.InvariantCulture),
                eg.AdfStat.ToString(CultureInfo.InvariantCulture),
                eg.Beta.ToString(CultureInfo.InvariantCulture),
                eg.Alpha.ToString(CultureInfo.InvariantCulture),
                eg.UsedLag.ToString(CultureInfo.InvariantCulture),
                (hl.HasValue ? hl.Value.ToString(CultureInfo.InvariantCulture) : ""),
                p5.ToString(), p10.ToString(),
                (eg.ApproxPvalue?.ToString(CultureInfo.InvariantCulture) ?? "")
            }));
        }
    }

    H2("Overall Summary");
    PrintKeyValues(new[]
    {
        ("Directional pairs tested", totalPairs.ToString()),
        ("Cointegrated @5%", totalPass5.ToString()),
        ("Cointegrated @10%", totalPass10.ToString()),
        ("Output CSV", outPath)
    });
    Console.WriteLine("Tip: feed this file to 'validate' to search trading parameters.");
}

/* =============================
 *      VALIDATION (GRID)
 * ============================= */
static void RunValidate(string[] args)
{
    string? file = null, format = "auto", sheet = null;
    string? pairsCsv = null;
    string alphaLevel = "5";
    double? hlMin = null, hlMax = null;
    string? zEntryCsv = null, zExitCsv = null, zStopCsv = null, qCsv = null, rCsv = null;
    double periodsPerYear = 252.0 * 6.5;
    string modeOpt = "both";
    int topN = 50;

    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--file" && i + 1 < args.Length) file = args[++i];
        else if (args[i] == "--format" && i + 1 < args.Length) format = args[++i];
        else if (args[i] == "--sheet" && i + 1 < args.Length) sheet = args[++i];
        else if (args[i] == "--pairs" && i + 1 < args.Length) pairsCsv = args[++i];
        else if (args[i] == "--alpha-level" && i + 1 < args.Length) alphaLevel = args[++i];
        else if (args[i] == "--hl-min" && i + 1 < args.Length && double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var h1)) hlMin = h1;
        else if (args[i] == "--hl-max" && i + 1 < args.Length && double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var h2)) hlMax = h2;
        else if (args[i] == "--z-entry" && i + 1 < args.Length) zEntryCsv = args[++i];
        else if (args[i] == "--z-exit" && i + 1 < args.Length) zExitCsv = args[++i];
        else if (args[i] == "--z-stop" && i + 1 < args.Length) zStopCsv = args[++i];
        else if (args[i] == "--q" && i + 1 < args.Length) qCsv = args[++i];
        else if (args[i] == "--r" && i + 1 < args.Length) rCsv = args[++i];
        else if (args[i] == "--ppy" && i + 1 < args.Length && double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var ppy)) periodsPerYear = ppy;
        else if (args[i] == "--mode" && i + 1 < args.Length) modeOpt = args[++i].ToLowerInvariant();
        else if (args[i] == "--top-n" && i + 1 < args.Length && int.TryParse(args[++i], out var tn)) topN = tn;
    }

    if (string.IsNullOrWhiteSpace(file) || !File.Exists(file) || string.IsNullOrWhiteSpace(pairsCsv) || !File.Exists(pairsCsv))
    {
        Error("Usage: validate --file <prices.csv> --pairs <coint_pairs.csv> [--mode both|kalman|static] [--alpha-level 5|10] [--hl-min Hmin] [--hl-max Hmax] [--z-entry \"1,1.5,2\"] [--z-exit \"0.5,1\"] [--z-stop \"3,4\"] [--q \"1e-7,1e-6\"] [--r \"1e-4,1e-3\"] [--ppy N] [--top-n 50]");
        Environment.ExitCode = 2;
        return;
    }

    Step(1, 6, "Load dataset");
    IReadOnlyDictionary<string, TimeSeriesFrame> frames = WithTimer("Reading dataset", () =>
    {
        if (format == "excel" || (format == "auto" && (file!.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || file!.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))))
        {
            var reader = new ExcelTimeSeriesReader();
            var (f, _) = reader.ReadExcel(file!, sheet);
            return f;
        }
        else
        {
            var rows = CsvTimeSeriesReader.ReadCsv(file!);
            var (f, _) = TimeSeriesValidator.ValidateAndBuildFrames(rows, new TimeSeriesSchema());
            return f;
        }
    });

    Step(2, 6, "Build aligned log prices");
    var (fullIdx, fullLogs) = WithTimer("(Align)", () => SeriesAligner.BuildAlignedLogPrices(frames, null, null));
    int T = fullIdx.Length;
    if (T < 200) { Warn("Dataset too short for Train/Valid/OOS (need >=200)."); return; }

    int cut80 = (int)Math.Floor(T * 0.8);
    int holdout = T - cut80;
    int validLen = holdout / 2;
    int oosLen = holdout - validLen;

    Console.WriteLine();
    PrintKeyValues(new[]
    {
        ("TRAIN", $"{cut80} obs"),
        ("VALID", $"{validLen} obs"),
        ("OOS",   $"{oosLen} obs")
    });

    Step(3, 6, "Grid definition");
    double[] qArr = string.IsNullOrWhiteSpace(qCsv) ? Array.Empty<double>() : Grid.ParseDoubles(qCsv, Array.Empty<double>());
    double[] rArr = string.IsNullOrWhiteSpace(rCsv) ? Array.Empty<double>() : Grid.ParseDoubles(rCsv, Array.Empty<double>());
    if (qArr.Length == 0 && rArr.Length == 0) Info("Q/R = AUTO (derived from HL & sigma)");
    else
    {
        PrintKeyValues(new[]
        {
            ("Q", string.Join(",", qArr.Select(q => q.ToString("E2")))),
            ("R", string.Join(",", rArr.Select(r => r.ToString("E2"))))
        });
    }

    var grid = new Grid
    {
        ZEntry = Grid.ParseDoubles(zEntryCsv, new[] { 1.0, 1.5, 2.0 }),
        ZExit  = Grid.ParseDoubles(zExitCsv,  new[] { 0.5, 1.0 }),
        ZStop  = Grid.ParseDoubles(zStopCsv,  new[] { 3.0, 4.0 }),
        Q = qArr, R = rArr,
        Sizing = new[] { SizingMode.Fixed, SizingMode.HalfLifeScaled }
    };

    Step(4, 6, "Load cointegrated candidates");
    var candidates = Validator.LoadCointCandidates(pairsCsv, alphaLevel, hlMin, hlMax);
    if (candidates.Count == 0) { Warn("No candidates after filtering."); return; }
    Info($"Loaded {candidates.Count} pairs");

    Step(5, 6, "Evaluate configs on VALID");
    var outDir = Path.Combine("data", "processed");
    Directory.CreateDirectory(outDir);
    var outPath = Path.Combine(outDir, $"validate_all_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
    using var sw = new StreamWriter(outPath);
    sw.WriteLine("pair_y,pair_x,mode,z_entry,z_exit,z_stop,sizing,sharpe,calmar,max_dd,win_rate,profit_factor,turnover,alpha,beta,half_life,q,r");

    var allResults = new List<ValidationResult>();

    foreach (var (y, x, alpha, beta, halfLife) in candidates)
    {
        if (!fullLogs.ContainsKey(y) || !fullLogs.ContainsKey(x)) continue;
        Console.WriteLine();
        Console.WriteLine($"Pair {y}/{x} (HL={halfLife:F1})");

        var yAll = fullLogs[y];
        var xAll = fullLogs[x];

        // TRAIN slice
        double[] yTrain = SubArray(yAll, 0, cut80);
        double[] xTrain = SubArray(xAll, 0, cut80);

        var residTrain = new double[yTrain.Length];
        for (int i = 0; i < yTrain.Length; i++)
            residTrain[i] = yTrain[i] - (alpha + beta * xTrain[i]);

        double muTrain = Mean(residTrain);
        double sigmaTrain = Std(residTrain, muTrain);
        if (sigmaTrain <= 0) continue;

        // VALID slice
        double[] yValid = SubArray(yAll, cut80, validLen);
        double[] xValid = SubArray(xAll, cut80, validLen);

        var configs = Validator.EvaluatePairAllConfigs(
            y, x, alpha, beta, muTrain, sigmaTrain, halfLife,
            yValid, xValid, grid, periodsPerYear);

        if (configs.Count == 0) continue;

        foreach (var cfg in configs)
        {
            sw.WriteLine(string.Join(",", new[]
            {
                cfg.PairY, cfg.PairX, cfg.Mode,
                cfg.ZEntry.ToString(CultureInfo.InvariantCulture),
                cfg.ZExit.ToString(CultureInfo.InvariantCulture),
                cfg.ZStop.ToString(CultureInfo.InvariantCulture),
                cfg.Sizing.ToString(),
                cfg.Sharpe.ToString(CultureInfo.InvariantCulture),
                cfg.Calmar.ToString(CultureInfo.InvariantCulture),
                cfg.MaxDD.ToString(CultureInfo.InvariantCulture),
                cfg.WinRate.ToString(CultureInfo.InvariantCulture),
                cfg.ProfitFactor.ToString(CultureInfo.InvariantCulture),
                cfg.Turnover.ToString(CultureInfo.InvariantCulture),
                cfg.Alpha.ToString(CultureInfo.InvariantCulture),
                cfg.Beta.ToString(CultureInfo.InvariantCulture),
                (cfg.HalfLifeTrain?.ToString(CultureInfo.InvariantCulture) ?? ""),
                (cfg.Q?.ToString(CultureInfo.InvariantCulture) ?? ""),
                (cfg.R?.ToString(CultureInfo.InvariantCulture) ?? "")
            }));
        }

        allResults.AddRange(configs);

        // human-readable top N
        Console.WriteLine($"Top {Math.Min(topN, configs.Count)} configs:");
        PrintTable(
            new[] { "#", "Mode", "Zin", "Zout", "Zstop", "Sizing", "Sharpe", "Q", "R" },
            configs.Take(topN).Select((cfg, i) => new[]
            {
                (i+1).ToString(),
                cfg.Mode,
                cfg.ZEntry.ToString("0.#"),
                cfg.ZExit.ToString("0.#"),
                cfg.ZStop.ToString("0.#"),
                cfg.Sizing.ToString(),
                cfg.Sharpe.ToString("0.00"),
                (cfg.Q?.ToString("E1", CultureInfo.InvariantCulture) ?? "-"),
                (cfg.R?.ToString("E1", CultureInfo.InvariantCulture) ?? "-")
            })
        );
    }

    Info($"Saved ALL {allResults.Count} configs → {outPath}");

    Step(6, 6, "Global top 10");
    var globalTop = allResults.OrderByDescending(r => r.Sharpe).Take(10).ToList();
    PrintTable(
        new[] { "Pair", "Mode", "Zin", "Zout", "Zstop", "Sizing", "Sharpe", "Calmar", "Q", "R" },
        globalTop.Select(cfg => new[]
        {
            $"{cfg.PairY}/{cfg.PairX}", cfg.Mode,
            cfg.ZEntry.ToString("0.#"), cfg.ZExit.ToString("0.#"), cfg.ZStop.ToString("0.#"),
            cfg.Sizing.ToString(), cfg.Sharpe.ToString("0.00"), cfg.Calmar.ToString("0.00"),
            (cfg.Q?.ToString("E1", CultureInfo.InvariantCulture) ?? "-"),
            (cfg.R?.ToString("E1", CultureInfo.InvariantCulture) ?? "-")
        })
    );

    Console.WriteLine("Next: run 'oos' with --validate-csv <that file> to replay best configs on OOS.");
}

/* =============================
 *     OUT-OF-SAMPLE FINAL
 * ============================= */
static void RunOosFinal(string[] args)
{
    string? file = null, format = "auto", sheet = null;
    string? validateCsv = null;
    int topPerPair = 1;
    bool plotEquity = true;
    double periodsPerYear = 252.0 * 6.5;

    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--file" && i + 1 < args.Length) file = args[++i];
        else if (args[i] == "--format" && i + 1 < args.Length) format = args[++i];
        else if (args[i] == "--sheet" && i + 1 < args.Length) sheet = args[++i];
        else if (args[i] == "--validate-csv" && i + 1 < args.Length) validateCsv = args[++i];
        else if (args[i] == "--top" && i + 1 < args.Length) int.TryParse(args[++i], out topPerPair);
        else if (args[i] == "--no-plot") plotEquity = false;
        else if (args[i] == "--ppy" && i + 1 < args.Length) double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out periodsPerYear);
    }

    if (string.IsNullOrWhiteSpace(file) || !File.Exists(file) ||
        string.IsNullOrWhiteSpace(validateCsv) || !File.Exists(validateCsv))
    {
        Error("Usage: oos --file <prices.csv> --validate-csv <validate_all_*.csv> [--top N] [--no-plot] [--ppy N]");
        Environment.ExitCode = 2;
        return;
    }

    Step(1, 5, "Load dataset & OOS period");
    IReadOnlyDictionary<string, TimeSeriesFrame> frames = WithTimer("Reading dataset", () =>
    {
        if (format == "excel" || (format == "auto" && (file!.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || file!.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))))
        {
            var reader = new ExcelTimeSeriesReader();
            var (f, _) = reader.ReadExcel(file!, sheet);
            return f;
        }
        else
        {
            var rows = CsvTimeSeriesReader.ReadCsv(file!);
            var (f, _) = TimeSeriesValidator.ValidateAndBuildFrames(rows, new TimeSeriesSchema());
            return f;
        }
    });

    var (fullIdx, fullLogs) = SeriesAligner.BuildAlignedLogPrices(frames, null, null);
    int T = fullIdx.Length;
    if (T < 300) { Warn("Not enough data for proper OOS (need >=300)."); return; }

    int cut80 = (int)Math.Floor(T * 0.8);
    int holdout = T - cut80;
    int validLen = holdout / 2;
    int oosStartIdx = cut80 + validLen;
    var oosDates = fullIdx.Skip(oosStartIdx).ToArray();

    PrintKeyValues(new[] { ("OOS Period", $"{oosDates.First():yyyy-MM-dd} → {oosDates.Last():yyyy-MM-dd} ({oosDates.Length} obs)") });

    Step(2, 5, "Load best VALID configs");
    var bestConfigs = new List<(ValidationResult cfg, string pairKey)>();
    var pairGroups = new Dictionary<string, List<ValidationResult>>();

    using (var sr = new StreamReader(validateCsv))
    {
        sr.ReadLine(); // header
        while (!sr.EndOfStream)
        {
            var line = sr.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var p = line.Split(',');
            if (p.Length < 18) continue;

            try
            {
                double zstop = string.IsNullOrWhiteSpace(p[5]) || p[5] == "NA" ? 0 : double.Parse(p[5], CultureInfo.InvariantCulture);
                double? q = string.IsNullOrWhiteSpace(p[16]) ? (double?)null : double.Parse(p[16], CultureInfo.InvariantCulture);
                double? r = string.IsNullOrWhiteSpace(p[17]) ? (double?)null : double.Parse(p[17], CultureInfo.InvariantCulture);
                double? hl = string.IsNullOrWhiteSpace(p[15]) ? (double?)null : double.Parse(p[15], CultureInfo.InvariantCulture);

                var cfg = new ValidationResult(
                    PairY: p[0], PairX: p[1],
                    Mode: p[2],
                    ZEntry: double.Parse(p[3], CultureInfo.InvariantCulture),
                    ZExit:  double.Parse(p[4], CultureInfo.InvariantCulture),
                    ZStop:  zstop,
                    Sizing: Enum.Parse<SizingMode>(p[6]),
                    Sharpe: double.Parse(p[7], CultureInfo.InvariantCulture),
                    Calmar: double.Parse(p[8], CultureInfo.InvariantCulture),
                    MaxDD:  double.Parse(p[9], CultureInfo.InvariantCulture),
                    WinRate: double.Parse(p[10], CultureInfo.InvariantCulture),
                    ProfitFactor: double.Parse(p[11], CultureInfo.InvariantCulture),
                    Turnover: double.Parse(p[12], CultureInfo.InvariantCulture),
                    Alpha: double.Parse(p[13], CultureInfo.InvariantCulture),
                    Beta:  double.Parse(p[14], CultureInfo.InvariantCulture),
                    HalfLifeTrain: hl, Q: q, R: r
                );

                string key = $"{cfg.PairY}/{cfg.PairX}";
                if (!pairGroups.ContainsKey(key)) pairGroups[key] = new List<ValidationResult>();
                pairGroups[key].Add(cfg);
            }
            catch { continue; }
        }
    }

    foreach (var kv in pairGroups)
        foreach (var cfg in kv.Value.OrderByDescending(c => c.Sharpe).Take(topPerPair))
            bestConfigs.Add((cfg, kv.Key));

    Info($"Selected {bestConfigs.Count} config(s) (Top {topPerPair} per pair across {pairGroups.Count} pairs)");

    Step(3, 5, "Backtest OOS");
    var oosResults = new List<(string pair, ValidationResult cfg, BacktestReport rep, double[] equity)>();

    foreach (var (cfg, pair) in bestConfigs)
    {
        if (!fullLogs.TryGetValue(cfg.PairY, out var yAll) || !fullLogs.TryGetValue(cfg.PairX, out var xAll)) continue;

        var yOos = SubArray(yAll, oosStartIdx, yAll.Length - oosStartIdx);
        var xOos = SubArray(xAll, oosStartIdx, xAll.Length - oosStartIdx);

        double[] residualsOos;
        if (cfg.Mode == "static")
        {
            residualsOos = new double[yOos.Length];
            for (int i = 0; i < yOos.Length; i++)
                residualsOos[i] = yOos[i] - (cfg.Alpha + cfg.Beta * xOos[i]);
        }
        else
        {
            if (!cfg.Q.HasValue || !cfg.R.HasValue) continue;
            var kf = KalmanHedge.Run(yOos, xOos, cfg.Alpha, cfg.Beta, pInit: 1.0, q: cfg.Q.Value, r: cfg.R.Value);
            residualsOos = kf.Residuals;
        }

        // mu/sigma from TRAIN
        var yTrain = SubArray(yAll, 0, cut80);
        var xTrain = SubArray(xAll, 0, cut80);
        var residTrain = new double[yTrain.Length];
        for (int i = 0; i < yTrain.Length; i++)
            residTrain[i] = yTrain[i] - (cfg.Alpha + cfg.Beta * xTrain[i]);

        double muTrain = Mean(residTrain);
        double sigmaTrain = Std(residTrain, muTrain);
        if (sigmaTrain <= 0) continue;

        var report = Backtester.Run(
            residual: residualsOos,
            mu: muTrain, sigma: sigmaTrain,
            zEntry: cfg.ZEntry, zExit: cfg.ZExit,
            zStop: cfg.ZStop > 0 ? cfg.ZStop : (double?)null,
            sizing: cfg.Sizing, halfLife: cfg.HalfLifeTrain,
            periodsPerYear: periodsPerYear);

        // equity curve (simple)
        var equityCurve = new double[residualsOos.Length];
        double equity = 0.0;
        int pos = 0;
        double size = cfg.Sizing switch
        {
            SizingMode.Fixed => 1.0,
            SizingMode.HalfLifeScaled => cfg.HalfLifeTrain.HasValue ? 1.0 / Math.Sqrt(cfg.HalfLifeTrain.Value) : 1.0,
            SizingMode.VolScaled => 1.0 / sigmaTrain,
            _ => 1.0
        };

        for (int t = 1; t < residualsOos.Length; t++)
        {
            double z = (residualsOos[t - 1] - muTrain) / sigmaTrain;
            if (pos != 0)
            {
                bool closeByExit = Math.Abs(z) <= cfg.ZExit;
                bool closeByStop = cfg.ZStop > 0 && Math.Abs(z) >= cfg.ZStop;
                if (closeByExit || closeByStop) pos = 0;
            }
            if (pos == 0 && Math.Abs(z) >= cfg.ZEntry) pos = z > 0 ? -1 : 1;

            double dE = residualsOos[t] - residualsOos[t - 1];
            equity += (size * pos * dE);
            equityCurve[t] = equity;
        }

        oosResults.Add((pair, cfg, report, equityCurve));
    }

    Step(4, 5, "Ranking");
    Console.WriteLine($"{"Rank",-4} {"Pair",-20} {"Mode",-7} {"Zin",5} {"Zout",5} {"Zstop",7} {"Size",-12} {"Sharpe",7} {"Calmar",7} {"MaxDD",7} {"Trades",6} {"Win%",6} {"Status"}");
    Console.WriteLine(new string('-', 120));
    var ranked = oosResults.OrderByDescending(x => x.rep.Sharpe).ToList();
    for (int i = 0; i < ranked.Count; i++)
    {
        var (pair, cfg, rep, _) = ranked[i];
        string zstopStr = cfg.ZStop > 0 ? cfg.ZStop.ToString("0.0") : "-";
        string status = rep.Sharpe > 1.8 ? "ELITE" :
                        rep.Sharpe > 1.3 ? "STRONG" :
                        rep.Sharpe > 0.8 ? "DECENT" :
                        rep.Sharpe > 0.3 ? "WEAK" : "DEAD";
        Console.WriteLine(
            $"{i + 1,-4} {pair,-20} {cfg.Mode,-7} " +
            $"{cfg.ZEntry,5:0.0} {cfg.ZExit,5:0.0} {zstopStr,7} {cfg.Sizing,-12} " +
            $"{rep.Sharpe,7:0.00} {rep.Calmar,7:0.00} {rep.MaxDrawdown,7:P1} {rep.Trades,6} {rep.WinRate,6:P0} {status}");
    }

    Step(5, 5, "Export equity curves (Top 10)");
    if (plotEquity && ranked.Count > 0)
    {
        var outDir = Path.Combine("data", "processed");
        Directory.CreateDirectory(outDir);
        var equityPath = Path.Combine(outDir, $"OOS_EQUITY_TOP{Math.Min(10, ranked.Count)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
        using var sw = new StreamWriter(equityPath);
        sw.Write("Date");
        foreach (var r in ranked.Take(10))
            sw.Write($",{r.pair.Replace("/", "_")}_S{ r.rep.Sharpe:F2}");
        sw.WriteLine();

        for (int t = 0; t < oosDates.Length; t++)
        {
            sw.Write(oosDates[t].ToString("yyyy-MM-dd"));
            foreach (var r in ranked.Take(10))
            {
                var eq = t < r.equity.Length ? r.equity[t] : r.equity[^1];
                sw.Write($",{eq:F6}");
            }
            sw.WriteLine();
        }
        Info($"Saved equity CSV → {equityPath}");
    }
    Info($"OOS test finished – {ranked.Count} strategies.");
}

/* =============================
 *            HELPERS
 * ============================= */
static T[] SubArray<T>(T[] arr, int start, int len)
{
    var r = new T[Math.Max(0, Math.Min(len, arr.Length - start))];
    if (r.Length > 0) Array.Copy(arr, start, r, 0, r.Length);
    return r;
}

static double Mean(double[] a) => a.Length == 0 ? 0 : a.Average();

static double Std(double[] a, double mean)
{
    if (a.Length == 0) return 0;
    double s = 0;
    for (int i = 0; i < a.Length; i++)
    {
        double d = a[i] - mean;
        s += d * d;
    }
    return Math.Sqrt(Math.Max(1e-16, s / a.Length));
}

static double? EstimateHalfLifeFromAlphaBetaResiduals(double[] y, double[] x, double alpha, double beta)
{
    int n = Math.Min(y.Length, x.Length);
    if (n < 20) return null;

    var e = new double[n];
    for (int i = 0; i < n; i++) e[i] = y[i] - (alpha + beta * x[i]);

    int T = n - 1;
    if (T < 10) return null;

    double sumY = 0, sumX = 0, sumXX = 0, sumXY = 0, sum1 = T;

    for (int t = 1; t < n; t++)
    {
        double Yt = e[t];
        double Xt = e[t - 1];
        sumY += Yt;
        sumX += Xt;
        sumXX += Xt * Xt;
        sumXY += Xt * Yt;
    }

    double det = sum1 * sumXX - sumX * sumX;
    if (Math.Abs(det) < 1e-12) return null;

    double c = (sumY * sumXX - sumX * sumXY) / det;
    double phi = (sum1 * sumXY - sumX * sumY) / det;

    if (phi <= 0 || phi >= 1) return null;

    double hl = -Math.Log(2.0) / Math.Log(phi);
    if (double.IsFinite(hl) && hl > 0 && hl < 1e6) return hl;

    return null;
}
