using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HarvestmoonGCS.Services.Security;

public record MasterLockResult(bool IsAllowed, string Message, string HardwareId);

/// <summary>
/// Sistem Penguncian & Lisensi Terpusat MoonHarvest GCS (C# Side)
/// Sinkron 100% dengan engine Python (moonharvest_security.py).
/// Mendukung Remote Master Switch, Offline Lease Dead-Man's Timer, dan HWID Fingerprinting.
/// </summary>
public static class MasterLockService
{
    private static readonly byte[] InternalSalt = Encoding.UTF8.GetBytes("MoonHarvest-DefisitAir-Padi-2026-SecretSalt");
    private const int DefaultLeaseHours = 48;

    private static readonly string StorageDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".moonharvest");

    private static readonly string LeaseFile = Path.Combine(StorageDir, "lease.v1");

    /// <summary>
    /// Menghitung Hardware ID unik yang kompatibel dengan modul Python.
    /// </summary>
    public static string GetHardwareId()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
            System.Runtime.InteropServices.Architecture.Arm => "armv7l",
            _ => "x86_64"
        };

        var components = new List<string>
        {
            OperatingSystem.IsWindows() ? "Windows" : (OperatingSystem.IsMacOS() ? "Darwin" : "Linux"),
            arch
        };

        // Baca machine-id pada Linux/Raspberry Pi
        string[] machineIdPaths =
        {
            "/etc/machine-id",
            "/var/lib/dbus/machine-id",
            "/sys/class/dmi/id/product_uuid"
        };

        foreach (var p in machineIdPaths)
        {
            try
            {
                if (File.Exists(p))
                {
                    var text = File.ReadAllText(p).Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        components.Add(text);
                        break;
                    }
                }
            }
            catch { }
        }

        // MAC Address / Network Fallback
        try
        {
            var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            foreach (var nic in nics)
            {
                var addr = nic.GetPhysicalAddress().ToString();
                if (!string.IsNullOrEmpty(addr) && addr != "000000000000")
                {
                    components.Add(addr.ToLowerInvariant());
                    break;
                }
            }
        }
        catch { }

        components.Add(Environment.GetEnvironmentVariable("USER") ?? Environment.UserName);
        components.Add(Environment.MachineName);

        var rawId = string.Join("|", components);
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(rawId)));

        return $"HWID-{hash[..4]}-{hash.Substring(4, 4)}-{hash.Substring(8, 4)}-{hash.Substring(12, 4)}";
    }

    /// <summary>
    /// Verifikasi akses menyeluruh saat aplikasi startup.
    /// </summary>
    public static MasterLockResult VerifyAccess()
    {
        var hwid = GetHardwareId();
        var config = LoadConfig();

        var remoteUrl = Environment.GetEnvironmentVariable("MOONHARVEST_LOCK_URL") ??
                        config.RemoteSwitchUrl;
        var licenseKey = Environment.GetEnvironmentVariable("MOONHARVEST_LICENSE_KEY") ??
                         config.LicenseKey ?? "MH-MASTER-OWNER";
        var leaseHours = config.OfflineLeaseHours > 0 ? config.OfflineLeaseHours : DefaultLeaseHours;

        // 1. Coba verifikasi online
        var (onlineOk, onlineMsg, remoteData) = CheckRemoteSwitch(remoteUrl, hwid, licenseKey);

        if (onlineOk)
        {
            SaveOfflineLease(hwid, leaseHours, onlineMsg);
            return new MasterLockResult(true, $"Online OK: {onlineMsg}", hwid);
        }

        // Jika remote switch secara tegas menyatakan sistem DIKUNCI / DICABUT
        if (remoteData != null && (remoteData.GlobalLock || onlineMsg.Contains("TERKUNCI") || onlineMsg.Contains("DICABUT") || onlineMsg.Contains("HABIS") || onlineMsg.Contains("TIDAK VALID")))
        {
            // Hapus lease lokal seketika
            try
            {
                if (File.Exists(LeaseFile))
                {
                    File.Delete(LeaseFile);
                }
            }
            catch { }

            return new MasterLockResult(false, onlineMsg, hwid);
        }

        // 2. Jika offline / koneksi gagal -> cek sewa lokal
        if (config.AllowEmergencyOffline)
        {
            var (offlineOk, offlineMsg) = CheckOfflineLease(hwid);
            if (offlineOk)
            {
                return new MasterLockResult(true, $"Offline Fallback OK: {offlineMsg}", hwid);
            }
            return new MasterLockResult(false, $"Akses Ditolak ({onlineMsg}). {offlineMsg}", hwid);
        }

        return new MasterLockResult(false, $"Akses Ditolak: {onlineMsg}", hwid);
    }

    private static (bool Ok, string Message, RemoteSwitchPayload? Payload) CheckRemoteSwitch(string? url, string hwid, string licenseKey)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return (false, "URL remote switch belum disetel.", null);
        }

        try
        {
            string jsonBody;

            // Dukungan file lokal untuk testing
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                var localPath = url.Replace("file://", "");
                if (!File.Exists(localPath))
                {
                    // Coba cari relatif terhadap BaseDirectory
                    var candidate = Path.Combine(AppContext.BaseDirectory, localPath);
                    if (File.Exists(candidate))
                    {
                        localPath = candidate;
                    }
                    else
                    {
                        return (false, $"File switch lokal tidak ditemukan: {localPath}", null);
                    }
                }
                jsonBody = File.ReadAllText(localPath);
            }
            else
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                client.DefaultRequestHeaders.Add("User-Agent", $"MoonHarvestGCS-Security/1.0 ({hwid})");
                client.DefaultRequestHeaders.Add("X-Hardware-ID", hwid);
                client.DefaultRequestHeaders.Add("X-License-Key", licenseKey);

                // Bypass cache CDN/GitHub raw dengan timestamp query
                var liveUrl = url.Contains("?")
                    ? $"{url}&_t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"
                    : $"{url}?_t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

                var response = client.GetAsync(liveUrl).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"Server lisensi merespons HTTP {response.StatusCode}", null);
                }
                jsonBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var data = JsonSerializer.Deserialize<RemoteSwitchPayload>(jsonBody, options);
            if (data == null)
            {
                return (false, "Format switch bukan JSON yang valid.", null);
            }

            // 1. Cek Saklar Global Master
            if (data.GlobalLock)
            {
                var msg = string.IsNullOrWhiteSpace(data.LockMessage)
                    ? "Sistem MoonHarvest telah dikunci secara global oleh pemilik."
                    : data.LockMessage;
                return (false, $"[TERKUNCI] {msg}", data);
            }

            // 2. Cek Tanggal Kedaluwarsa
            if (!string.IsNullOrWhiteSpace(data.ValidUntil) &&
                DateTime.TryParse(data.ValidUntil, out var expDate) &&
                DateTime.UtcNow > expDate.ToUniversalTime())
            {
                return (false, $"[KEDALUWARSA] Lisensi sistem telah berakhir pada {data.ValidUntil}", data);
            }

            // 3. Cek Kunci Lisensi Paket Spesifik (Jika Ada)
            if (data.Licenses != null && data.Licenses.Count > 0 && !string.IsNullOrWhiteSpace(licenseKey))
            {
                if (data.Licenses.TryGetValue(licenseKey, out var lic))
                {
                    var licStatus = (lic.Status ?? "ACTIVE").ToUpperInvariant();
                    if (licStatus != "ACTIVE")
                    {
                        return (false, $"[LISENSI DICABUT] Kunci lisensi '{licenseKey}' ({lic.Holder}) berstatus {licStatus}. Akses dinonaktifkan oleh pemilik.", data);
                    }

                    if (!string.IsNullOrWhiteSpace(lic.ValidUntil) &&
                        DateTime.TryParse(lic.ValidUntil, out var licExp) &&
                        DateTime.UtcNow > licExp.ToUniversalTime())
                    {
                        return (false, $"[LISENSI HABIS] Kunci lisensi '{licenseKey}' ({lic.Holder}) telah kedaluwarsa pada {lic.ValidUntil}.", data);
                    }
                }
                else
                {
                    return (false, $"[LISENSI TIDAK VALID] Kunci lisensi '{licenseKey}' tidak terdaftar pada server.", data);
                }
            }

            // 4. Cek Tabel Multi-Device Granular (Per-Device Status & Expiry)
            if (data.Devices != null && data.Devices.Count > 0)
            {
                if (data.Devices.TryGetValue(hwid, out var dev))
                {
                    var devName = !string.IsNullOrWhiteSpace(dev.Name) ? dev.Name : hwid;
                    var status = (dev.Status ?? "ACTIVE").ToUpperInvariant();
                    if (status != "ACTIVE")
                    {
                        return (false, $"[LISENSI DICABUT] Perangkat '{devName}' ({hwid}) berstatus {status}. Akses dinonaktifkan oleh pemilik.", data);
                    }

                    if (!string.IsNullOrWhiteSpace(dev.ValidUntil) &&
                        DateTime.TryParse(dev.ValidUntil, out var devExp) &&
                        DateTime.UtcNow > devExp.ToUniversalTime())
                    {
                        return (false, $"[LISENSI HABIS] Lisensi perangkat '{devName}' telah kedaluwarsa pada {dev.ValidUntil}.", data);
                    }

                    return (true, $"Perangkat '{devName}' terverifikasi aktif.", data);
                }
                else
                {
                    // Tidak ada di tabel devices
                    if (data.AuthorizedHardware == null || !data.AuthorizedHardware.Contains("*"))
                    {
                        return (false, $"[PERANGKAT BELUM TERDAFTAR] Hardware ID ({hwid}) belum diaktivasi oleh pemilik sistem. Salin ID ini dan kirimkan ke admin.", data);
                    }
                }
            }

            // 5. Cek Hardware Whitelist Global (Fallback)
            if (data.AuthorizedHardware != null &&
                !data.AuthorizedHardware.Contains("*") &&
                !data.AuthorizedHardware.Contains(hwid))
            {
                return (false, $"[TIDAK DIIZINKAN] Perangkat ({hwid}) belum terdaftar.", data);
            }

            return (true, "Lisensi terverifikasi valid secara online.", data);
        }
        catch (Exception ex)
        {
            return (false, $"Koneksi ke switch lisensi gagal: {ex.Message}", null);
        }
    }

    private static (bool Ok, string Message) CheckOfflineLease(string hwid)
    {
        if (!File.Exists(LeaseFile))
        {
            return (false, "Tidak ada lisensi offline. Perangkat wajib terhubung ke internet.");
        }

        try
        {
            var raw = File.ReadAllBytes(LeaseFile);
            if (raw.Length < 32)
            {
                return (false, "Data lisensi offline rusak.");
            }

            var sig = raw.AsSpan(0, 32);
            var jsonBytes = raw.AsSpan(32);

            var key = DeriveLeaseKey(hwid);
            using var hmac = new HMACSHA256(key);
            var expectedSig = hmac.ComputeHash(jsonBytes.ToArray());

            if (!CryptographicOperations.FixedTimeEquals(sig, expectedSig))
            {
                return (false, "Integritas lisensi offline tidak valid (deteksi duplikasi/manipulasi).");
            }

            var jsonStr = Encoding.UTF8.GetString(jsonBytes);
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            if (root.TryGetProperty("hwid", out var fileHwid) && fileHwid.GetString() != hwid)
            {
                return (false, "Lisensi offline tidak cocok dengan perangkat ini.");
            }

            var expiresAt = root.GetProperty("expires_at").GetDouble();
            var remainingSec = expiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (remainingSec <= 0)
            {
                return (false, "Masa sewa offline telah kedaluwarsa. Sambungkan ke internet.");
            }

            var remainingHours = (int)(remainingSec / 3600);
            return (true, $"Mode offline aktif (Tersisa {remainingHours} jam sewa)");
        }
        catch (Exception ex)
        {
            return (false, $"Gagal membaca lisensi offline: {ex.Message}");
        }
    }

    private static void SaveOfflineLease(string hwid, int leaseHours, string statusMsg)
    {
        try
        {
            Directory.CreateDirectory(StorageDir);
            var expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (leaseHours * 3600);
            var payload = new
            {
                hwid = hwid,
                expires_at = (double)expiresAt,
                created_at = (double)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                status = "ACTIVE",
                message = statusMsg
            };

            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            var key = DeriveLeaseKey(hwid);
            using var hmac = new HMACSHA256(key);
            var sig = hmac.ComputeHash(jsonBytes);

            using var fs = new FileStream(LeaseFile, FileMode.Create, FileAccess.Write);
            fs.Write(sig);
            fs.Write(jsonBytes);
        }
        catch { }
    }

    private static byte[] DeriveLeaseKey(string hwid)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(hwid),
            InternalSalt,
            10000,
            HashAlgorithmName.SHA256,
            32);
    }

    private static SecurityConfig LoadConfig()
    {
        string[] searchPaths =
        {
            Path.Combine(AppContext.BaseDirectory, "moonharvest_security.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "moonharvest_security.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "moonharvest_security.json"),
            Path.Combine(StorageDir, "moonharvest_security.json")
        };

        SecurityConfig? cfg = null;
        foreach (var p in searchPaths)
        {
            try
            {
                if (File.Exists(p))
                {
                    var text = File.ReadAllText(p);
                    cfg = JsonSerializer.Deserialize<SecurityConfig>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (cfg != null) break;
                }
            }
            catch { }
        }

        cfg ??= new SecurityConfig();

        string[] licSearchPaths =
        {
            Path.Combine(AppContext.BaseDirectory, "moonharvest.lic"),
            Path.Combine(Directory.GetCurrentDirectory(), "moonharvest.lic")
        };
        foreach (var lp in licSearchPaths)
        {
            try
            {
                if (File.Exists(lp))
                {
                    var keyText = File.ReadAllText(lp).Trim();
                    if (!string.IsNullOrEmpty(keyText))
                    {
                        cfg.LicenseKey = keyText;
                        break;
                    }
                }
            }
            catch { }
        }

        return cfg;
    }

    private class SecurityConfig
    {
        [JsonPropertyName("remote_switch_url")]
        public string RemoteSwitchUrl { get; set; } = "https://gist.githubusercontent.com/FawwazFA/fc07275e43ed0c1730f63807e163ae1b/raw/moonharvest_lock.json";

        [JsonPropertyName("offline_lease_hours")]
        public int OfflineLeaseHours { get; set; } = 48;

        [JsonPropertyName("license_key")]
        public string LicenseKey { get; set; } = "MH-MASTER-OWNER";

        [JsonPropertyName("allow_emergency_offline")]
        public bool AllowEmergencyOffline { get; set; } = true;
    }

    private class RemoteSwitchPayload
    {
        [JsonPropertyName("global_lock")]
        public bool GlobalLock { get; set; }

        [JsonPropertyName("lock_message")]
        public string? LockMessage { get; set; }

        [JsonPropertyName("valid_until")]
        public string? ValidUntil { get; set; }

        [JsonPropertyName("authorized_hardware")]
        public List<string>? AuthorizedHardware { get; set; }

        [JsonPropertyName("devices")]
        public Dictionary<string, DeviceEntry>? Devices { get; set; }

        [JsonPropertyName("licenses")]
        public Dictionary<string, LicenseEntry>? Licenses { get; set; }
    }

    private class LicenseEntry
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("holder")]
        public string? Holder { get; set; }

        [JsonPropertyName("valid_until")]
        public string? ValidUntil { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }

    private class DeviceEntry
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("valid_until")]
        public string? ValidUntil { get; set; }

        [JsonPropertyName("note")]
        public string? Note { get; set; }
    }
}
