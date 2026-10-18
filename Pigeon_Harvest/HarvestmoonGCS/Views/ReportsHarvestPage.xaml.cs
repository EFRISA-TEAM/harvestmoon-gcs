using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Extensions.DependencyInjection;
using HarvestmoonGCS.Services;

namespace HarvestmoonGCS.Views;

public sealed partial class ReportsHarvestPage : Page
{
    private sealed class ReportEntry
    {
        public required string Id { get; init; }
        public required string DateTime { get; init; }
        public required string Area { get; init; }
        public required string Duration { get; init; }
        public required int Detections { get; init; }
        public required string Priority { get; init; }
        public bool IsDemo { get; init; }
        public string TlogPath { get; init; } = string.Empty;
        public string GeofenceAlertsJson { get; init; } = "[]";
        public string YoloBenchmarkJson { get; init; } = string.Empty;
        public string EvidenceBundlePath { get; init; } = string.Empty;
        public string VideoRecordingPath { get; init; } = string.Empty;
        public string IncidentTimelineJson { get; init; } = string.Empty;
    }

    private static readonly IReadOnlyList<ReportEntry> SeedReports = new List<ReportEntry>
    {
        new() { Id = "MS-60S-SUBANG-01", DateTime = "2026-09-10 08:30", Area = "Subang Rice Estate · Sawah Subang", Duration = "00:01:00", Detections = 1842, Priority = "Medium", IsDemo = true, YoloBenchmarkJson = "{\"FramesPerSecond\":15.1,\"AverageLatencyMs\":6.2,\"AverageDetections\":18.4}" },
        new() { Id = "HST15-RAWAMERTA-02", DateTime = "2026-09-09 14:15", Area = "Sukamerta Karawang 15 HST · Karawang", Duration = "00:00:16", Detections = 1638, Priority = "Low", IsDemo = true, YoloBenchmarkJson = "{\"FramesPerSecond\":15.2,\"AverageLatencyMs\":6.3,\"AverageDetections\":14.8}" },
        new() { Id = "IRR-MAJALENGKA-03", DateTime = "2026-09-07 10:45", Area = "Rentang Majalengka Irrigation · Majalengka", Duration = "00:00:45", Detections = 2140, Priority = "Low", IsDemo = true, YoloBenchmarkJson = "{\"FramesPerSecond\":15.0,\"AverageLatencyMs\":6.4,\"AverageDetections\":16.2}" },
        new() { Id = "DRY-SINDUR-04", DateTime = "2026-09-05 13:20", Area = "Gunung Sindur Dryland Sector · Bogor", Duration = "00:00:30", Detections = 1420, Priority = "High", IsDemo = true, YoloBenchmarkJson = "{\"FramesPerSecond\":14.8,\"AverageLatencyMs\":6.5,\"AverageDetections\":12.1}" },
    };

    private readonly DispatcherTimer _feedbackTimer;
    private readonly HarvestFunctionalService? _harvestFunctionalService;
    private readonly List<ReportEntry> _reports = new();
    private string? _selectedId;
    private string _operatorNote = "Area Petak Sektor B3 perlu penyesuaian debit irigasi tersier dan pemantauan defisit air kanopi.";

    public ReportsHarvestPage()
    {
        this.InitializeComponent();

        _harvestFunctionalService = App.Current.Services.GetService<HarvestFunctionalService>();
        _reports.AddRange(EnrichSeedReports(SeedReports));
        _selectedId = _reports.FirstOrDefault()?.Id;
        OperatorNoteTextBox.Text = _operatorNote;

        _feedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer.Stop();
            ActionFeedbackBorder.Visibility = Visibility.Collapsed;
        };

        Loaded += OnPageLoaded;

        RenderMissionList();
        RenderDetail();
        _ = SafeLoadPersistedReportsAsync();
    }

    /// <summary>
    /// Fire-and-forget wrapper so a disk/IO failure while reloading history cannot surface as an
    /// unobserved exception and tear down the process.
    /// </summary>
    private async Task SafeLoadPersistedReportsAsync()
    {
        try
        {
            await LoadPersistedReportsAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[ReportsHarvestPage] LoadPersistedReports failed");
        }
    }

    /// <summary>
    /// Reloads mission history from disk. Pages are preloaded/cached at app startup, so without
    /// re-fetching on each activation this page kept showing whatever reports existed when the
    /// app launched — a mission started later (e.g. a live Dashboard session) never appeared
    /// until the app was restarted.
    /// </summary>
    public void OnPageActivated()
    {
        _ = SafeLoadPersistedReportsAsync();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
#if __ANDROID__
        // Compact layout for Android tablet: smaller padding, font sizes, and button spacing
        // so the export section fits comfortably on a 8" tablet screen.

        // Export card: tighter padding
        ExportCard.Padding = new Thickness(8);
        ExportCard.CornerRadius = new CornerRadius(8);

        // Export title: smaller font
        ExportTitleText.FontSize = 13;
        ExportTitleText.Margin = new Thickness(0, 0, 0, 6);

        // Export buttons panel: less spacing, wrap if needed
        ExportButtonsPanel.Spacing = 4;
        ExportButtonsPanel.Orientation = Orientation.Horizontal;

        // All export buttons: compact padding and font
        var compactPadding = new Thickness(8, 5, 8, 5);
        foreach (var btn in new[] { ExportPdfButton, ExportCsvButton, ExportJsonButton, ExportShareButton, ExportCoopButton })
        {
            btn.Padding = compactPadding;
            btn.FontSize = 12;
        }

        // "Send to Cooperative" label is long — shorten it on Android
        ExportCoopButton.Content = "Cooperative";

        // DetailCard: tighter padding
        DetailCard.Padding = new Thickness(8);
        DetailCard.CornerRadius = new CornerRadius(8);
        DetailTitleText.FontSize = 13;

        // Operator notes: reduce height slightly
        OperatorNoteTextBox.Height = 70;
#endif
    }

    private static IEnumerable<ReportEntry> EnrichSeedReports(IReadOnlyList<ReportEntry> seeds)
    {
        // Prefer app-bundled assets so demo evidence works on any machine/OS, then fall back
        // to the developer's local checkout for convenience. Absolute /home/... paths alone
        // meant Windows and Android always resolved to nothing.
        static string Asset(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "demo_videos", name);

        var scenarioVideos = new[]
        {
            Asset("stream_multisector_60s.mp4"),
            Asset("stream_v7c_final.mp4"),
            "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/stream_multisector_60s.mp4",
            "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/stream_v7c_final.mp4",
            "/home/fawwazfa/Program/Harvestmoon/demo_videos/out/gabung_fused_only.mp4",
            "/home/fawwazfa/Program/Harvestmoon/vidio/YDX_burned.mp4",
        };

        var fallbackCandidates = new[]
        {
            Asset("YDXJ_fused_only_detected.mp4"),
            Asset("stream_multisector_survey.mp4"),
            "/home/fawwazfa/Program/Harvestmoon/vidio/15d.mp4",
            "/home/fawwazfa/Program/Harvestmoon/vidio/YDXJ0012_demo(1).mp4",
            "/home/fawwazfa/Program/Harvestmoon/vid/15d.mp4",
            "/home/fawwazfa/Program/Harvestmoon/demo_videos/fusion_gabung/gabung_fused_only.mp4",
            "/home/fawwazfa/Program/Harvestmoon/demo_videos/fusion_gabung/gabung_fused.mp4",
        };
        var fallbackVideo = System.Array.Find(
            fallbackCandidates,
            p => System.IO.File.Exists(p) && new System.IO.FileInfo(p).Length > 10_000) ?? string.Empty;

        // TLogs live under a writable root; MyDocuments is empty on Android.
        var docsRoot = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(docsRoot))
        {
            docsRoot = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        }
        if (string.IsNullOrWhiteSpace(docsRoot))
        {
            docsRoot = AppContext.BaseDirectory;
        }

        var tlogDir = System.IO.Path.Combine(docsRoot, "HarvestmoonGCS", "TLogs");

        foreach (var (seed, idx) in seeds.Select((s, i) => (s, i)))
        {
            var vid = (idx < scenarioVideos.Length && System.IO.File.Exists(scenarioVideos[idx]))
                ? scenarioVideos[idx]
                : fallbackVideo;

            var tlogName = $"demo_{seed.Id.Replace("DEMO-", "").Replace("-", "_")}.tlog";
            yield return new ReportEntry
            {
                Id                  = seed.Id,
                DateTime            = seed.DateTime,
                Area                = seed.Area,
                Duration            = seed.Duration,
                Detections          = seed.Detections,
                Priority            = seed.Priority,
                IsDemo              = seed.IsDemo,
                VideoRecordingPath  = vid,
                TlogPath            = System.IO.Path.Combine(tlogDir, tlogName),
                GeofenceAlertsJson  = "[]",
                YoloBenchmarkJson   = seed.YoloBenchmarkJson,
                EvidenceBundlePath  = seed.EvidenceBundlePath,
                IncidentTimelineJson = seed.IncidentTimelineJson,
            };
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RenderMissionList();
    }

    private void OperatorNoteTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _operatorNote = OperatorNoteTextBox.Text;
    }

    private async void ExportAction_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button button || button.Content is not string label)
            {
                return;
            }

            var selected = _reports.FirstOrDefault(r => r.Id == _selectedId);
            if (selected == null)
            {
                ShowActionFeedback("Please select a report first");
                return;
            }

            if (_harvestFunctionalService != null && label is "PDF" or "CSV" or "JSON")
            {
                var record = ToHarvestReportRecord(selected);
                var baseName = selected.Id.Replace(" ", "_").Replace("/", "-");
                var path = label switch
                {
                    "PDF" => await _harvestFunctionalService.ExportReportPdfAsync(record, baseName),
                    "CSV" => await _harvestFunctionalService.ExportReportCsvAsync(record, baseName),
                    "JSON" => await _harvestFunctionalService.ExportReportJsonAsync(record, baseName),
                    _ => string.Empty
                };

                ShowActionFeedback(DescribeExport(label, path));
                return;
            }

            if (_harvestFunctionalService != null && label == "Share")
            {
                var record = ToHarvestReportRecord(selected);
                var baseName = selected.Id.Replace(" ", "_").Replace("/", "-");
                var path = await _harvestFunctionalService.ExportReportPdfAsync(record, baseName);
                ShowActionFeedback(string.IsNullOrWhiteSpace(path) || IsExportFailure(path)
                    ? $"Failed to prepare report for sharing: {path}"
                    : $"Report ready to share: {path}");
                return;
            }

            if (_harvestFunctionalService != null && (label == "Send to Cooperative" || label == "Cooperative" || button == ExportCoopButton))
            {
                var record = ToHarvestReportRecord(selected);
                var zipPath = await _harvestFunctionalService.QueueReportForCooperativeAsync(record);
                ShowActionFeedback(string.IsNullOrWhiteSpace(zipPath) || IsExportFailure(zipPath)
                    ? $"Failed to prepare report bundle: {zipPath}"
                    : $"Report bundle queued in Outbox — send manually to the cooperative: {zipPath}");
                return;
            }

            ShowActionFeedback("Action executed");
        }
        catch (Exception ex)
        {
            // Bundling zips files and writes to disk; an escaping exception here would
            // terminate the process.
            Serilog.Log.Error(ex, "[ReportsHarvestPage] Export action failed");
            ShowActionFeedback($"Export failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The file/evidence layer signals failure by returning an "EXPORT_FAILED: ..." string
    /// rather than throwing, so callers must not present it as a successful save.
    /// </summary>
    private static bool IsExportFailure(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.StartsWith("EXPORT_FAILED", StringComparison.OrdinalIgnoreCase);

    private static string DescribeExport(string label, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return $"{label} export failed: no output path returned";
        }

        return IsExportFailure(path)
            ? $"{label} export failed: {path}"
            : $"{label} report saved: {path}";
    }

    private void MissionRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string id)
        {
            _selectedId = id;
            RenderMissionList();
            RenderDetail();
        }
    }

    private void RenderMissionList()
    {
        MissionItemsPanel.Children.Clear();

        var query = SearchBox.Text?.Trim().ToLowerInvariant();
        var filtered = _reports
            .Where(r => string.IsNullOrWhiteSpace(query)
                || r.Id.ToLowerInvariant().Contains(query)
                || r.Area.ToLowerInvariant().Contains(query)
                || r.Priority.ToLowerInvariant().Contains(query))
            .ToList();

        if (filtered.Count == 0)
        {
            MissionItemsPanel.Children.Add(new TextBlock
            {
                Text = "No matching missions.",
                Foreground = GetThemeBrush("MutedForegroundBrush"),
                Margin = new Thickness(2, 6, 2, 6)
            });
            return;
        }

        foreach (var report in filtered)
        {
            MissionItemsPanel.Children.Add(CreateMissionRow(report, report.Id == _selectedId));
        }
    }

    private Border CreateMissionRow(ReportEntry report, bool selected)
    {
        var (pillBg, pillBorder, pillFg) = GetPriorityBrushes(report.Priority);

        var rowBorder = new Border
        {
            Background = selected ? Brush("#F1FAF1") : GetThemeBrush("CardBrush"),
            BorderBrush = selected ? Brush("#C8E6C9") : GetThemeBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 6),
            Tag = report.Id
        };
        rowBorder.Tapped += MissionRow_Tapped;

        var stack = new StackPanel();

        var headGrid = new Grid();
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var idText = new TextBlock
        {
            Text = report.Id,
            FontSize = 12,
            Foreground = GetThemeBrush("ForegroundBrush")
        };

        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        if (report.IsDemo)
        {
            badges.Children.Add(new Border
            {
                Padding = new Thickness(8, 2, 8, 2),
                CornerRadius = new CornerRadius(5),
                Background = Brush("#332E7D32"),
                BorderBrush = Brush("#664CAF50"),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = "DEMO",
                    FontSize = 11,
                    Foreground = Brush("#2E7D32")
                }
            });
        }

        var priorityBadge = new Border
        {
            Padding = new Thickness(8, 2, 8, 2),
            CornerRadius = new CornerRadius(5),
            Background = pillBg,
            BorderBrush = pillBorder,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = report.Priority,
                FontSize = 11,
                Foreground = pillFg
            }
        };
        badges.Children.Add(priorityBadge);

        Grid.SetColumn(idText, 0);
        Grid.SetColumn(badges, 1);
        headGrid.Children.Add(idText);
        headGrid.Children.Add(badges);

        stack.Children.Add(headGrid);
        stack.Children.Add(new TextBlock
        {
            Text = report.Area,
            Foreground = GetThemeBrush("MutedForegroundBrush"),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0)
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{report.DateTime} · {report.Duration} · {report.Detections} detections",
            Foreground = GetThemeBrush("MutedForegroundBrush"),
            FontSize = 11
        });

        rowBorder.Child = stack;
        return rowBorder;
    }

    private void RenderDetail()
    {
        var selected = _reports.FirstOrDefault(r => r.Id == _selectedId);
        if (selected == null)
        {
            DetailContentPanel.Visibility = Visibility.Collapsed;
            EmptyDetailPanel.Visibility = Visibility.Visible;
            return;
        }

        DetailContentPanel.Visibility = Visibility.Visible;
        EmptyDetailPanel.Visibility = Visibility.Collapsed;

        DetailTitleText.Text = $"Detail · {selected.Id}";
        DetailDateText.Text = selected.DateTime.Split(' ').FirstOrDefault() ?? selected.DateTime;
        DetailDurationText.Text = selected.Duration;
        DetailDetectionText.Text = selected.Detections.ToString();

        var (pillBg, pillBorder, pillFg) = GetPriorityBrushes(selected.Priority);
        DetailPriorityBadge.Background = pillBg;
        DetailPriorityBadge.BorderBrush = pillBorder;
        DetailPriorityText.Foreground = pillFg;
        DetailPriorityText.Text = selected.IsDemo ? $"DEMO · {selected.Priority}" : selected.Priority;

        DetailLocationText.Text = $"Location: {selected.Area}";
        DetailTlogText.Text = string.IsNullOrWhiteSpace(selected.TlogPath)
            ? "No TLOG connected yet"
            : selected.TlogPath;
        DetailGeofenceAlertsText.Text = FormatGeofenceAlerts(selected.GeofenceAlertsJson);
        DetailEvidenceText.Text = string.IsNullOrWhiteSpace(selected.EvidenceBundlePath)
            ? "No evidence bundle yet"
            : selected.EvidenceBundlePath;
        DetailBenchmarkText.Text = $"YOLO Benchmark: {FormatBenchmark(selected.YoloBenchmarkJson)}";
        DetailVideoText.Text = string.IsNullOrWhiteSpace(selected.VideoRecordingPath)
            ? "Video: flight recording saved"
            : $"Video: {System.IO.Path.GetFileName(selected.VideoRecordingPath)}";
        MapPreviewText.Text = $"Map preview · {selected.Area}";
    }

    private void ShowActionFeedback(string message)
    {
        ActionFeedbackText.Text = message;
        ActionFeedbackBorder.Visibility = Visibility.Visible;
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    private async System.Threading.Tasks.Task LoadPersistedReportsAsync()
    {
        if (_harvestFunctionalService == null)
        {
            return;
        }

        var records = await _harvestFunctionalService.GetReportsAsync();
        if (records.Count == 0)
        {
            return;
        }

        _reports.Clear();
        _reports.AddRange(records.Select(ToReportEntry));
        _selectedId = _reports.FirstOrDefault()?.Id;
        RenderMissionList();
        RenderDetail();
    }

    private HarvestFunctionalService.HarvestReportRecord ToHarvestReportRecord(ReportEntry report)
    {
        return new HarvestFunctionalService.HarvestReportRecord
        {
            Id = report.Id,
            DateTime = report.DateTime,
            Area = report.Area,
            Duration = report.Duration,
            Detections = report.Detections,
            Priority = report.Priority,
            OperatorNote = _operatorNote,
            TlogPath = report.TlogPath,
            GeofenceAlertsJson = report.GeofenceAlertsJson,
            YoloBenchmarkJson = report.YoloBenchmarkJson,
            EvidenceBundlePath = report.EvidenceBundlePath,
            VideoRecordingPath = report.VideoRecordingPath,
            IncidentTimelineJson = report.IncidentTimelineJson
        };
    }

    private static ReportEntry ToReportEntry(HarvestFunctionalService.HarvestReportRecord record)
    {
        return new ReportEntry
        {
            Id = record.Id,
            DateTime = record.DateTime,
            Area = record.Area,
            Duration = record.Duration,
            Detections = record.Detections,
            Priority = record.Priority,
            IsDemo = false,
            TlogPath = record.TlogPath,
            GeofenceAlertsJson = record.GeofenceAlertsJson,
            YoloBenchmarkJson = record.YoloBenchmarkJson,
            EvidenceBundlePath = record.EvidenceBundlePath,
            VideoRecordingPath = record.VideoRecordingPath,
            IncidentTimelineJson = record.IncidentTimelineJson
        };
    }

    private static string FormatBenchmark(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "not available";
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var fps = root.TryGetProperty("FramesPerSecond", out var fpsProp) ? fpsProp.GetDouble() : 0;
            var latency = root.TryGetProperty("AverageLatencyMs", out var latencyProp) ? latencyProp.GetDouble() : 0;
            var detections = root.TryGetProperty("AverageDetections", out var detectionProp) ? detectionProp.GetDouble() : 0;
            return $"{fps:F1} FPS · {latency:F1} ms · avg {detections:F1} det";
        }
        catch
        {
            return json.Length > 120 ? json[..120] + "..." : json;
        }
    }

    private static string FormatGeofenceAlerts(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
        {
            return "No geofence alerts";
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                var lines = new List<string>();
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        lines.Add(item.GetString() ?? "");
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        var type     = item.TryGetProperty("type",     out var tp) ? tp.GetString() : null;
                        var severity = item.TryGetProperty("severity", out var sv) ? sv.GetString() : null;
                        var dist     = item.TryGetProperty("distance", out var di) ? $"{di.GetDouble():F0} m" : null;
                        var parts    = new[] { type, severity, dist }.Where(s => !string.IsNullOrEmpty(s));
                        lines.Add(string.Join(" · ", parts));
                    }
                }

                var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Take(5).ToList();
                return nonEmpty.Count == 0 ? "No geofence alerts" : string.Join(Environment.NewLine, nonEmpty);
            }

            return json.Length > 120 ? json[..120] + "..." : json;
        }
        catch
        {
            return json.Length > 120 ? json[..120] + "..." : json;
        }
    }

    private static (Brush background, Brush border, Brush foreground) GetPriorityBrushes(string priority)
    {
        return priority switch
        {
            "High" => (Brush("#33D32F2F"), Brush("#66D32F2F"), Brush("#D32F2F")),
            "Medium" => (Brush("#33FBC02D"), Brush("#66FBC02D"), Brush("#9E7000")),
            _ => (Brush("#332E7D32"), Brush("#664CAF50"), Brush("#2E7D32"))
        };
    }

    private static SolidColorBrush Brush(string hex)
    {
        var normalized = hex.TrimStart('#');
        if (normalized.Length == 6)
        {
            normalized = "FF" + normalized;
        }

        byte a = Convert.ToByte(normalized.Substring(0, 2), 16);
        byte r = Convert.ToByte(normalized.Substring(2, 2), 16);
        byte g = Convert.ToByte(normalized.Substring(4, 2), 16);
        byte b = Convert.ToByte(normalized.Substring(6, 2), 16);
        return new SolidColorBrush(ColorHelper.FromArgb(a, r, g, b));
    }

    private static Brush GetThemeBrush(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Colors.Transparent);
    }
}
