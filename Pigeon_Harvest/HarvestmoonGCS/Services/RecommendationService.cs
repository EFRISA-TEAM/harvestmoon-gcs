using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Watches recommendations.json written by recommendation_bridge.py and exposes
/// the latest agronomic recommendations to the UI via event notification.
///
/// IPC mechanism: Python writes JSON file → FileSystemWatcher detects change →
/// C# parses and raises RecommendationsUpdated event.
/// </summary>
public sealed class RecommendationService : IDisposable
{
    // ── Public model ────────────────────────────────────────────────────────

    public sealed class RecommendationResult
    {
        public string Timestamp       { get; init; } = string.Empty;
        public double Fhi             { get; init; }
        public string FieldStatus     { get; init; } = string.Empty;
        public string Urgency         { get; init; } = string.Empty;
        public string DominantCondition { get; init; } = string.Empty;
        public List<string> TopRecommendations { get; init; } = new();
        public string Engine          { get; init; } = string.Empty;
        public double HealthyPct      { get; init; }
        public double StressPct       { get; init; }
        public double DroughtPct      { get; init; }
        public double BareSoilPct     { get; init; }
    }

    // ── Events ───────────────────────────────────────────────────────────────

    public event EventHandler<RecommendationResult>? RecommendationsUpdated;

    // ── State ────────────────────────────────────────────────────────────────

    private readonly string _jsonPath;
    private readonly string _bridgeScriptPath;
    private readonly string? _pythonCommand;
    private FileSystemWatcher? _watcher;
    private RecommendationResult? _latest;
    private DateTime _lastRead = DateTime.MinValue;
    private readonly SemaphoreSlim _readLock = new(1, 1);

    public RecommendationResult? Latest => _latest;
    public bool IsAvailable => _latest != null;

    // ── Constructor ──────────────────────────────────────────────────────────

    public RecommendationService()
    {
        var docDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _jsonPath = Path.Combine(docDir, "HarvestmoonGCS", "recommendations.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_jsonPath)!);

        // Resolve bridge script path
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "recommendation_bridge.py"),
            Path.Combine(AppContext.BaseDirectory, "..", "recommendation_bridge.py"),
            "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/recommendation_bridge.py",
        };
        _bridgeScriptPath = Array.Find(candidates, File.Exists) ?? candidates[0];

        // Resolve python
        _pythonCommand = ResolvePython();

        StartWatcher();
        // Read existing file if present
        _ = TryReadJsonAsync();
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Run the Python recommendation bridge with current detection results.
    /// Non-blocking — result arrives via RecommendationsUpdated event when bridge finishes.
    /// </summary>
    public void RequestRecommendations(
        double healthyPct,
        double stressPct,
        double droughtPct,
        double bareSoilPct,
        double fhi,
        double areaHa = 1.0,
        int daysAfterTransplant = 0)
    {
        if (string.IsNullOrEmpty(_pythonCommand) || !File.Exists(_bridgeScriptPath))
        {
            Log.Warning("[RecommendationService] Python or bridge script not found — using fallback");
            GenerateFallback(healthyPct, stressPct, droughtPct, bareSoilPct, fhi);
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var args = $"\"{_bridgeScriptPath}\" " +
                           $"--healthy {healthyPct:F2} --stress {stressPct:F2} " +
                           $"--drought {droughtPct:F2} --bare_soil {bareSoilPct:F2} " +
                           $"--fhi {fhi:F2} --area_ha {areaHa:F2} " +
                           $"--days_after_transplant {daysAfterTransplant} " +
                           $"--output_path \"{_jsonPath}\"";

                var psi = new ProcessStartInfo(_pythonCommand, args)
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(30_000); // 30 s timeout
                Log.Information("[RecommendationService] Bridge exited: {Code}", proc?.ExitCode);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[RecommendationService] Failed to run bridge");
                GenerateFallback(healthyPct, stressPct, droughtPct, bareSoilPct, fhi);
            }
        });
    }

    // ── FileSystemWatcher ────────────────────────────────────────────────────

    private void StartWatcher()
    {
        var dir = Path.GetDirectoryName(_jsonPath)!;
        var file = Path.GetFileName(_jsonPath);
        try
        {
            _watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => _ = TryReadJsonAsync();
            _watcher.Created += (_, _) => _ = TryReadJsonAsync();
            Log.Information("[RecommendationService] Watching {Path}", _jsonPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RecommendationService] Could not start FileSystemWatcher");
        }
    }

    private async Task TryReadJsonAsync()
    {
        // Debounce: ignore if read within last 500 ms
        if ((DateTime.UtcNow - _lastRead).TotalMilliseconds < 500) return;
        if (!await _readLock.WaitAsync(0)) return;
        try
        {
            _lastRead = DateTime.UtcNow;
            await Task.Delay(200); // wait for Python to finish writing
            if (!File.Exists(_jsonPath)) return;

            var json = await File.ReadAllTextAsync(_jsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string Get(string key) => root.TryGetProperty(key, out var p) ? p.GetString() ?? "" : "";
            double GetD(string key) => root.TryGetProperty(key, out var p) ? p.GetDouble() : 0;

            var recs = new List<string>();
            if (root.TryGetProperty("top_recommendations", out var recsEl) &&
                recsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in recsEl.EnumerateArray())
                    recs.Add(item.GetString() ?? "");
            }

            double hp = 0, sp = 0, dp = 0, bp = 0;
            if (root.TryGetProperty("crop_distribution", out var dist))
            {
                hp = dist.TryGetProperty("healthy",   out var h) ? h.GetDouble() : 0;
                sp = dist.TryGetProperty("stress",    out var s) ? s.GetDouble() : 0;
                dp = dist.TryGetProperty("drought",   out var d) ? d.GetDouble() : 0;
                bp = dist.TryGetProperty("bare_soil", out var b) ? b.GetDouble() : 0;
            }

            var result = new RecommendationResult
            {
                Timestamp          = Get("timestamp"),
                Fhi                = GetD("fhi"),
                FieldStatus        = Get("field_status"),
                Urgency            = Get("urgency"),
                DominantCondition  = Get("dominant_condition"),
                TopRecommendations = recs,
                Engine             = Get("engine"),
                HealthyPct         = hp,
                StressPct          = sp,
                DroughtPct         = dp,
                BareSoilPct        = bp,
            };

            _latest = result;
            RecommendationsUpdated?.Invoke(this, result);
            Log.Information("[RecommendationService] Loaded recommendations FHI={Fhi:F1} urgency={U}", result.Fhi, result.Urgency);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RecommendationService] Failed to read recommendations.json");
        }
        finally
        {
            _readLock.Release();
        }
    }

    // ── Fallback ─────────────────────────────────────────────────────────────

    private void GenerateFallback(double hp, double sp, double dp, double bp, double fhi)
    {
        string status, urgency;
        List<string> recs;

        if (fhi >= 75)
        {
            status  = "healthy"; urgency = "low";
            recs = new List<string>
            {
                $"Field Health Index {fhi:F1} — kondisi baik. Lanjutkan monitoring rutin.",
                $"Zona sehat {hp:F1}% — pertumbuhan optimal. Pertahankan irigasi saat ini.",
                "Jadwalkan inspeksi darat pada siklus berikutnya untuk konfirmasi.",
            };
        }
        else if (fhi >= 55)
        {
            status  = "moderate_stress"; urgency = "moderate";
            recs = new List<string>
            {
                $"Stress zone {sp:F1}% terdeteksi — periksa sistem irigasi dalam 24–48 jam.",
                $"Drought area {dp:F1}% — tambahkan irigasi di area prioritas.",
                $"Bare soil {bp:F1}% — inspeksi fisik untuk deteksi hama atau kerusakan.",
            };
        }
        else
        {
            status  = "high_stress"; urgency = "high";
            recs = new List<string>
            {
                $"FHI rendah ({fhi:F1}) — tindakan segera. Periksa irigasi dan nutrisi tanaman.",
                $"Stress {sp:F1}% + Drought {dp:F1}% — prioritaskan zona terburuk dulu.",
                "Lakukan sampling tanah untuk analisis kekurangan hara.",
            };
        }

        _latest = new RecommendationResult
        {
            Timestamp          = DateTime.Now.ToString("s"),
            Fhi                = fhi,
            FieldStatus        = status,
            Urgency            = urgency,
            DominantCondition  = hp > sp && hp > dp ? "healthy_crop" : sp >= dp ? "stressed_crop" : "drought_stress",
            TopRecommendations = recs,
            Engine             = "Fallback (no Python)",
            HealthyPct         = hp, StressPct = sp, DroughtPct = dp, BareSoilPct = bp,
        };
        RecommendationsUpdated?.Invoke(this, _latest);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string? ResolvePython()
    {
        // Check venv first, then system
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "python", "python.exe"),
            Path.Combine(AppContext.BaseDirectory, ".venv-camera", "Scripts", "python.exe"),
            "/home/fawwazfa/Program/Harvestmoon/.venv/bin/python3",
            "/home/fawwazfa/Program/Harvestmoon/.venv/bin/python",
            "/usr/bin/python3",
            "/usr/local/bin/python3",
            "python3",
            "python",
        };
        foreach (var c in candidates)
        {
            try
            {
                if (c.StartsWith('/') && !File.Exists(c)) continue;
                using var p = Process.Start(new ProcessStartInfo(c, "--version")
                    { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
                p?.WaitForExit(3000);
                if (p?.ExitCode == 0) return c;
            }
            catch { }
        }
        return null;
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        _watcher?.Dispose();
        _readLock.Dispose();
    }
}
