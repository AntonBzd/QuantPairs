using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;
using System.Collections.Generic;

namespace QuantPairs.App
{
    public partial class MainWindow : Window
    {
        private readonly string _cliProjRelative = @"src\QuantPairs.Cli\QuantPairs.Cli.csproj";
        private string _datasetPath;
        private string _lastValidateAllCsv;
        private string _lastEquityCsv;
        private readonly StringBuilder _buffer = new();

        public MainWindow()
        {
            InitializeComponent();
        }

        // ---------- UI helpers ----------
        private void AppendLog(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            Dispatcher.Invoke(() =>
            {
                _buffer.AppendLine(line);
                TxtLog.Text = _buffer.ToString();
                TxtLog.ScrollToEnd();
            });
        }

        private void SetProgress(int value, string label = "")
        {
            Dispatcher.Invoke(() =>
            {
                Bar.Value = value;
                TxtProgress.Text = label;
            });
        }

        // ---------- FS helpers ----------
        private static string RepoRootGuess()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir is not null; i++)
            {
                var sln = Path.Combine(dir.FullName, "QuantPairs.sln");
                var cli = Path.Combine(dir.FullName, @"src\QuantPairs.Cli\QuantPairs.Cli.csproj");
                if (File.Exists(sln) && File.Exists(cli))
                    return dir.FullName;
                dir = dir.Parent!;
            }
            return Directory.GetCurrentDirectory();
        }

        private static string? Latest(string folder, string pattern)
        {
            if (!Directory.Exists(folder)) return null;
            return new DirectoryInfo(folder).GetFiles(pattern)
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }

        // ---------- Process ----------
        private async Task<int> RunCliAsync(string args)
        {
            var root = RepoRootGuess();
            var cli = Path.Combine(root, _cliProjRelative);
            if (!File.Exists(cli))
            {
                AppendLog($"[ERROR] CLI introuvable: {cli}");
                return -1;
            }

            AppendLog($"> dotnet run --project {Path.GetFileName(cli)} -- {args}");
            var psi = new ProcessStartInfo("dotnet", $"run --project \"{cli}\" -- {args}")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) AppendLog(e.Data); };
            p.ErrorDataReceived +=  (_, e) => { if (e.Data != null) AppendLog(e.Data); };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            AppendLog($"[EXIT] code={p.ExitCode}\n");
            return p.ExitCode;
        }

        // ---------- Events ----------
        private void OnBrowse(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                InitialDirectory = Path.GetFullPath(Path.Combine(RepoRootGuess(), "data", "raw")),
                Filter = "CSV/Excel|*.csv;*.xlsx;*.xls|Tous les fichiers|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                _datasetPath = dlg.FileName;
                TxtDataset.Text = _datasetPath;
            }
        }

        private async void OnRun(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_datasetPath) || !File.Exists(_datasetPath))
            {
                MessageBox.Show("Sélectionne un dataset valide dans data/raw.", "Dataset manquant",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnRun.IsEnabled = false;
            BtnExport.IsEnabled = false;
            _buffer.Clear();
            TxtLog.Text = "";
            _lastValidateAllCsv = null;
            _lastEquityCsv = null;

            try
            {
                string processed = Path.Combine(RepoRootGuess(), "data", "processed");

                SetProgress(10, "Clustering…");
                int rc = await RunCliAsync($"cluster --file \"{_datasetPath}\"");
                if (rc != 0) { MessageBox.Show("Échec cluster."); return; }
                string? clusters = Latest(processed, "clusters_*.csv");

                SetProgress(35, "Cointégration…");
                rc = await RunCliAsync($"coint --file \"{_datasetPath}\"{(clusters != null ? $" --clusters \"{clusters}\"" : "")}");
                if (rc != 0) { MessageBox.Show("Échec coint."); return; }
                string? pairs = Latest(processed, "coint_pairs_*.csv");
                if (pairs == null) { MessageBox.Show("Aucun coint_pairs_*.csv trouvé."); return; }

                SetProgress(60, "Validation Test…");
                // valeurs par défaut câblées (pas de sélection sur l’UI) : alpha=10%, mode=both, top-n=50
                rc = await RunCliAsync($"validate --file \"{_datasetPath}\" --pairs \"{pairs}\" --alpha-level 10 --mode both --top-n 50");
                if (rc != 0) { MessageBox.Show("Échec validate."); return; }
                _lastValidateAllCsv = Latest(processed, "validate_all_*.csv");
                if (_lastValidateAllCsv == null) { MessageBox.Show("Aucun validate_all_*.csv trouvé."); return; }

                SetProgress(85, "Backtest Out Of Sample…");
                // valeurs par défaut câblées (pas de sélection sur l’UI) : top 4, ppy 1638
                rc = await RunCliAsync($"oos --file \"{_datasetPath}\" --validate-csv \"{_lastValidateAllCsv}\" --top 4 --ppy 1638");
                if (rc != 0) { MessageBox.Show("Échec oos."); return; }
                _lastEquityCsv = Latest(processed, "OOS_EQUITY_TOP*.csv");

                SetProgress(100, "Terminé ✓");
                BtnExport.IsEnabled = true;
                AppendLog("[DONE] Pipeline terminé avec succès.");
                if (_lastEquityCsv != null)
                    AppendLog($"[INFO] Equity OOS: {Path.GetFileName(_lastEquityCsv)}");
            }
            catch (Exception ex)
            {
                AppendLog($"[ERROR] {ex.Message}");
                MessageBox.Show($"Erreur: {ex.Message}", "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnRun.IsEnabled = true;
            }
        }

        private void OnExport(object sender, RoutedEventArgs e)
        {
            try
            {
                var processed = Path.Combine(RepoRootGuess(), "data", "processed");
                Directory.CreateDirectory(processed);

                var pdfPath = Path.Combine(processed, $"report_{DateTime.UtcNow:yyyyMMdd_HHmmss}.pdf");
                var datasetName = _datasetPath ?? "n/a";

                var produced = new List<string>();
                if (_lastValidateAllCsv != null) produced.Add(Path.GetFileName(_lastValidateAllCsv));
                if (_lastEquityCsv != null)      produced.Add(Path.GetFileName(_lastEquityCsv));

                QuestPDF.Settings.License = LicenseType.Community;

                Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Margin(30);
                        page.Size(PageSizes.A4);
                        page.DefaultTextStyle(x => x.FontSize(10));

                        page.Header().Row(row =>
                        {
                            row.RelativeItem().Text("QuantPairs – Rapport d’exécution")
                               .SemiBold().FontSize(16);
                            row.ConstantItem(120).Text(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                        });

                        page.Content().PaddingVertical(10).Column(col =>
                        {
                            col.Item().Text($"Dataset: {datasetName}");
                            if (produced.Count > 0)
                                col.Item().Text("Fichiers produits: " + string.Join(", ", produced));

                            col.Item().PaddingTop(10).Text("Log d'exécution (extrait)").SemiBold();
                            var logExcerpt = string.Join(Environment.NewLine, LastLines(TxtLog.Text, 800));
                            col.Item().Background(Colors.Grey.Lighten4)
                                     .Padding(6)
                                     .Text(logExcerpt)
                                     .FontFamily("Consolas")
                                     .FontSize(8);
                        });

                        page.Footer().AlignRight().Text(txt =>
                        {
                            txt.Span("Page ");
                            txt.CurrentPageNumber();
                            txt.Span(" / ");
                            txt.TotalPages();
                        });
                    });
                }).GeneratePdf(pdfPath);

                AppendLog($"[PDF] Exporté → {pdfPath}");
                MessageBox.Show($"PDF exporté:\n{pdfPath}", "Export PDF",
                                MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erreur export PDF: {ex.Message}", "Erreur",
                                MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static IEnumerable<string> LastLines(string text, int maxLines)
        {
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            return lines.Reverse().Take(maxLines).Reverse();
        }
    }
}










