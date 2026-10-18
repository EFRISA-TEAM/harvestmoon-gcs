using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using HarvestmoonGCS.Core.Services;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Layanan integrasi VIRA AI di dalam Harvestmoon GCS.
/// Berjalan otomatis di latar belakang aplikasi (baik di Windows, Linux, maupun Android).
/// Menghubungkan Moonharvest ke VIRA Hub untuk pemantauan telemetri, kontrol suara, dan kendali jarak jauh.
/// </summary>
public class ViraIntegrationService : IAsyncDisposable
{
    private readonly ILogger<ViraIntegrationService>? _logger;
    private readonly IMavLinkService? _mavLink;
    private HubConnection? _connection;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;
    private string _deviceId;
    private string _hubUrl;
    private string _deviceToken;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public ViraIntegrationService(
        IMavLinkService? mavLink = null,
        ILogger<ViraIntegrationService>? logger = null)
    {
        _mavLink = mavLink;
        _logger = logger;

        // Ambil URL Hub dari Environment Variable atau default localhost:5153
        _hubUrl = Environment.GetEnvironmentVariable("VIRA_HUB_URL") ?? "http://localhost:5153";
        _deviceToken = Environment.GetEnvironmentVariable("VIRA_DEVICE_TOKEN") ?? "VIRA-SECRET-CHANGE-ME";

        // Generate ID perangkat unik atau persisten
        _deviceId = Environment.GetEnvironmentVariable("VIRA_DEVICE_ID") 
            ?? ("moonharvest-" + Environment.MachineName.ToLowerInvariant().Replace(" ", "-"));
    }

    /// <summary>
    /// Memulai koneksi background ke VIRA Hub dan memastikan VIRA Lite daemon tetap aktif di OS.
    /// </summary>
    public void Start()
    {
        // 1. Pastikan VIRA Lite daemon terpasang dan berjalan secara independen di OS
        //    sehingga tetap berjalan 100% full meskipun Moonharvest ditutup!
        _ = Task.Run(() =>
        {
            try
            {
                EnsureViraLiteDaemonRunning();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[VIRA] Gagal memastikan Vira Lite daemon");
            }
        });

        // 2. Jalankan koneksi SignalR in-app untuk pertukaran telemetri langsung
        _ = Task.Run(async () =>
        {
            try
            {
                await InitializeAndConnectAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[VIRA] Gagal menginisialisasi ViraIntegrationService");
            }
        });
    }

    private void EnsureViraLiteDaemonRunning()
    {
        try
        {
#if __WASM__
            return;
#endif
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

            if (!isWindows && !isLinux) return;

            string binaryName = isWindows ? "vira-lite.exe" : "vira-lite";

            // 1. Cek apakah vira-lite sudah aktif berjalan
            bool isRunning = Process.GetProcesses().Any(p =>
            {
                try
                {
                    var n = p.ProcessName.ToLowerInvariant();
                    return n.Contains("vira-lite") || n.Contains("viralite");
                }
                catch { return false; }
            });

            if (isRunning)
            {
                _logger?.LogInformation("[VIRA] VIRA Lite daemon sudah berjalan di background.");
                return;
            }

            // 2. Cari lokasi binary vira-lite
            string baseDir = AppContext.BaseDirectory;
            string? targetBin = null;

            var candidates = new List<string>
            {
                Path.Combine(baseDir, binaryName),
                Path.Combine(baseDir, "vira", binaryName),
                Path.Combine(baseDir, "tools", binaryName),
                Path.Combine(baseDir, "VIRA.Lite", binaryName)
            };

            if (isWindows)
            {
                candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIRA", binaryName));
                candidates.Add(@"C:\VIRA\vira-lite.exe");
            }
            else
            {
                candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "vira", binaryName));
                candidates.Add("/opt/vira/vira-lite");
                candidates.Add("/usr/local/bin/vira-lite");
            }

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    targetBin = path;
                    break;
                }
            }

            if (targetBin == null)
            {
                _logger?.LogWarning("[VIRA] Binary VIRA Lite ({Bin}) belum ditemukan di bundle.", binaryName);
                return;
            }

            // 3. Pasang persistensi ke folder instalasi permanen pengguna
            string installDir = isWindows
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIRA")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "vira");

            Directory.CreateDirectory(installDir);
            string permanentBin = Path.Combine(installDir, binaryName);

            try
            {
                if (!File.Exists(permanentBin) || new FileInfo(targetBin).Length != new FileInfo(permanentBin).Length)
                {
                    File.Copy(targetBin, permanentBin, true);

                    // Salin juga appsettings.json jika ada
                    var srcDir = Path.GetDirectoryName(targetBin);
                    if (!string.IsNullOrEmpty(srcDir))
                    {
                        var srcCfg = Path.Combine(srcDir, "appsettings.json");
                        if (File.Exists(srcCfg))
                        {
                            File.Copy(srcCfg, Path.Combine(installDir, "appsettings.json"), true);
                        }
                    }
                }
            }
            catch { }

            // 4. Daftarkan autorun sistem supaya aktif otomatis saat reboot
            if (isWindows)
            {
                try
                {
                    var regPsi = new ProcessStartInfo
                    {
                        FileName = "reg.exe",
                        Arguments = $"add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\" /v \"ViraLiteAgent\" /t REG_SZ /d \"\\\"{permanentBin}\\\"\" /f",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    Process.Start(regPsi)?.WaitForExit(3000);
                }
                catch { }
            }
            else if (isLinux)
            {
                try
                {
                    Process.Start("chmod", $"+x \"{permanentBin}\"")?.WaitForExit(2000);

                    string autostartDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart");
                    Directory.CreateDirectory(autostartDir);
                    string desktopEntry = Path.Combine(autostartDir, "vira-lite.desktop");
                    File.WriteAllText(desktopEntry, $"[Desktop Entry]\nType=Application\nName=VIRA Lite Agent\nExec=\"{permanentBin}\"\nHidden=false\nNoDisplay=true\nX-GNOME-Autostart-enabled=true\n");
                }
                catch { }
            }

            // 5. Jalankan VIRA Lite sebagai proses terpisah (Detached Process)
            //    sehingga tetap berjalan secara penuh meski Moonharvest ditutup!
            var psi = new ProcessStartInfo
            {
                FileName = permanentBin,
                WorkingDirectory = installDir,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VIRA_HUB_URL")))
                psi.EnvironmentVariables["VIRA_HUB_URL"] = _hubUrl;
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VIRA_DEVICE_TOKEN")))
                psi.EnvironmentVariables["VIRA_DEVICE_TOKEN"] = _deviceToken;

            var spawned = Process.Start(psi);
            if (spawned != null)
            {
                _logger?.LogInformation("[VIRA] VIRA Lite daemon berhasil dijalankan di background (PID: {Pid}) dan akan terus aktif walau aplikasi ditutup.", spawned.Id);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[VIRA] Gagal meluncurkan VIRA Lite daemon");
        }
    }

    private async Task InitializeAndConnectAsync(CancellationToken ct)
    {
        var targetUrl = $"{_hubUrl.TrimEnd('/')}/vira-lite";

        _connection = new HubConnectionBuilder()
            .WithUrl(targetUrl, opts =>
            {
                opts.Headers.Add("X-Device-Token", _deviceToken);
                opts.Headers.Add("X-Device-Id", _deviceId);
            })
            .WithAutomaticReconnect(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) })
            .Build();

        // Handler perintah masuk dari VIRA
        _connection.On<string>("ExecuteCommand", async (cmdJson) =>
        {
            await HandleIncomingCommandAsync(cmdJson);
        });

        _connection.Reconnected += async (id) =>
        {
            _logger?.LogInformation("[VIRA] Reconnected to Hub. Re-registering device...");
            await RegisterDeviceAsync();
        };

        // Mulai koneksi berulang hingga berhasil
        while (!ct.IsCancellationRequested && _connection != null)
        {
            try
            {
                await _connection.StartAsync(ct);
                _logger?.LogInformation("[VIRA] Terhubung ke VIRA Hub di {Url}", _hubUrl);
                await RegisterDeviceAsync();
                StartHeartbeatLoop(ct);
                break;
            }
            catch
            {
                await Task.Delay(5000, ct);
            }
        }
    }

    private async Task RegisterDeviceAsync()
    {
        if (!IsConnected || _connection == null) return;

        string osName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" :
                        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" :
                        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" : "Android/Embedded";

        var profile = new
        {
            DeviceId = _deviceId,
            Alias = $"Moonharvest GCS ({osName})",
            OS = osName,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeVersion = Environment.Version.ToString(),
            SupportsScreen = false,
            SupportsShell = true,
            SupportsInput = false,
            RegisteredAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(profile);
        await _connection.InvokeAsync("RegisterDevice", json);
    }

    private void StartHeartbeatLoop(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                try
                {
                    bool isMavConnected = _mavLink?.IsConnected ?? false;
                    var heartbeat = new
                    {
                        DeviceId = _deviceId,
                        Timestamp = DateTime.UtcNow,
                        CpuUsage = 0f,
                        MemoryUsageMb = (int)(GC.GetTotalMemory(false) / (1024 * 1024)),
                        BatteryPercent = -1,
                        IsCharging = false,
                        NetworkType = "WiFi",
                        StatusMessage = isMavConnected ? "MAVLink Online" : "MAVLink Standby"
                    };

                    if (_connection != null)
                    {
                        await _connection.InvokeAsync("DeviceHeartbeat", JsonSerializer.Serialize(heartbeat), ct);
                    }
                }
                catch
                {
                    // Abaikan kesalahan periodik heartbeat
                }

                await Task.Delay(10000, ct);
            }
        }, ct);
    }

    private async Task HandleIncomingCommandAsync(string commandJson)
    {
        if (string.IsNullOrWhiteSpace(commandJson)) return;

        string commandId = Guid.NewGuid().ToString();
        string type = "";
        string payload = "";

        try
        {
            using var doc = JsonDocument.Parse(commandJson);
            if (doc.RootElement.TryGetProperty("CommandId", out var idEl))
                commandId = idEl.GetString() ?? commandId;
            if (doc.RootElement.TryGetProperty("Type", out var typeEl))
                type = typeEl.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("Payload", out var pEl))
                payload = pEl.GetString() ?? "";
        }
        catch
        {
            type = commandJson.Trim();
        }

        string resultOutput = "";
        bool isSuccess = true;

        switch (type.ToLowerInvariant())
        {
            case "ping":
                resultOutput = "pong";
                break;

            case "moonharvest_status":
            case "status":
                bool isMav = _mavLink?.IsConnected ?? false;
                resultOutput = isMav 
                    ? "Moonharvest GCS aktif | Telemetri MAVLink terhubung." 
                    : "Moonharvest GCS aktif | Siap menerima koneksi drone.";
                break;

            case "arm":
                if (_mavLink != null)
                {
                    bool ok = await _mavLink.ArmDisarmAsync(true);
                    resultOutput = ok ? "Drone berhasil di-Arm." : "Gagal meng-Arm drone.";
                }
                else resultOutput = "Modul MAVLink belum siap.";
                break;

            case "disarm":
                if (_mavLink != null)
                {
                    bool ok = await _mavLink.ArmDisarmAsync(false);
                    resultOutput = ok ? "Drone berhasil di-Disarm." : "Gagal men-Disarm drone.";
                }
                else resultOutput = "Modul MAVLink belum siap.";
                break;

            case "takeoff":
                if (_mavLink != null)
                {
                    bool ok = await _mavLink.TakeoffAsync(10f);
                    resultOutput = ok ? "Takeoff berhasil dimulai." : "Gagal mengeksekusi Takeoff.";
                }
                else resultOutput = "Modul MAVLink belum siap.";
                break;

            case "rtl":
                if (_mavLink != null)
                {
                    bool ok = await _mavLink.SetFlightModeAsync("RTL");
                    resultOutput = ok ? "Mode RTL diaktifkan." : "Gagal mengaktifkan RTL.";
                }
                else resultOutput = "Modul MAVLink belum siap.";
                break;

            case "land":
                if (_mavLink != null)
                {
                    bool ok = await _mavLink.LandAsync();
                    resultOutput = ok ? "Mode Land diaktifkan." : "Gagal mengaktifkan Land.";
                }
                else resultOutput = "Modul MAVLink belum siap.";
                break;

            default:
                resultOutput = $"Perintah '{type}' diterima oleh Moonharvest.";
                break;
        }

        // Kirim respon balik ke VIRA Hub
        if (IsConnected && _connection != null)
        {
            var response = new
            {
                CommandId = commandId,
                DeviceId = _deviceId,
                Success = isSuccess,
                Output = resultOutput,
                Error = (string?)null
            };
            await _connection.InvokeAsync("DeviceResponse", JsonSerializer.Serialize(response));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _cts.Cancel();
            if (_connection != null)
            {
                await _connection.DisposeAsync();
            }
            _cts.Dispose();
        }
    }
}
