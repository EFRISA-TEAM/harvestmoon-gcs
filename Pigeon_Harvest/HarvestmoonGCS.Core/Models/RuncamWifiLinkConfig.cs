using System.Collections.Generic;

namespace HarvestmoonGCS.Core.Models;

/// <summary>
/// Konfigurasi koneksi RunCam WiFi Link 2.
/// RunCam WiFi Link 2 membuat hotspot WiFi sendiri dengan SSID "RUNCAM_WIFILINK_XXXXXX".
/// Default IP gateway: 192.168.0.1
/// Stream RTSP: rtsp://192.168.0.1:554/live
/// Stream MJPEG: http://192.168.0.1:8080/?action=stream
/// MAVLink UDP masuk ke port 14550 di GCS, drone send dari 192.168.0.1:14551
/// </summary>
public class RuncamWifiLinkConfig
{
    // ── Network ────────────────────────────────────────────────────────────────
    /// <summary>IP default RunCam WiFi Link 2 saat menjadi hotspot.</summary>
    public string CameraIp { get; set; } = "192.168.0.1";

    /// <summary>Port RTSP stream utama.</summary>
    public int RtspPort { get; set; } = 554;

    /// <summary>Port MJPEG fallback stream.</summary>
    public int MjpegPort { get; set; } = 8080;

    /// <summary>Path RTSP stream (setelah IP:Port).</summary>
    public string RtspPath { get; set; } = "/live";

    /// <summary>Path MJPEG stream query string.</summary>
    public string MjpegPath { get; set; } = "/?action=stream";

    // ── MAVLink ────────────────────────────────────────────────────────────────
    /// <summary>Port UDP lokal yang digunakan GCS untuk terima MAVLink dari drone.</summary>
    public int MavLinkLocalPort { get; set; } = 14550;

    /// <summary>IP tujuan kirim MAVLink ke drone (biasanya IP camera/FC).</summary>
    public string MavLinkRemoteIp { get; set; } = "192.168.0.1";

    /// <summary>Port tujuan kirim MAVLink ke drone.</summary>
    public int MavLinkRemotePort { get; set; } = 14551;

    // ── Stream preference ──────────────────────────────────────────────────────
    /// <summary>Protokol stream yang digunakan.</summary>
    public RuncamStreamProtocol StreamProtocol { get; set; } = RuncamStreamProtocol.Rtsp;

    /// <summary>Resolusi preferred.</summary>
    public RuncamResolution Resolution { get; set; } = RuncamResolution.HD_1080p;

    /// <summary>Aktifkan auto-detect WiFi hotspot RunCam.</summary>
    public bool AutoDetect { get; set; } = true;

    // ── Computed helpers ───────────────────────────────────────────────────────

    /// <summary>URL RTSP lengkap atau custom URL jika diawali rtsp://.</summary>
    public string RtspUrl => CameraIp.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
        ? CameraIp
        : $"rtsp://{CameraIp}:{RtspPort}{RtspPath}";

    /// <summary>URL MJPEG lengkap (path default RunCam).</summary>
    public string MjpegUrl => $"http://{CameraIp}:{MjpegPort}{MjpegPath}";

    /// <summary>
    /// URL MJPEG alternatif untuk Orange Pi / MoonHarvest UAV yang memakai
    /// endpoint /video_feed (bukan /?action=stream milik RunCam).
    /// </summary>
    public string MjpegUrlAlt => $"http://{CameraIp}:{MjpegPort}/video_feed";

    /// <summary>
    /// Semua kandidat URL MJPEG (didahulukan sesuai protokol yang dipilih),
    /// dicoba berurutan sampai ada yang bisa dibuka.
    /// </summary>
    public List<string> MjpegUrls
    {
        get
        {
            var urls = new List<string>();
            if (StreamProtocol == RuncamStreamProtocol.Mjpeg)
            {
                urls.Add(MjpegUrl);
                urls.Add(MjpegUrlAlt);
            }
            else
            {
                urls.Add(MjpegUrlAlt);
                urls.Add(MjpegUrl);
            }
            return urls;
        }
    }

    /// <summary>URL stream aktif sesuai protocol preference.</summary>
    public string ActiveStreamUrl => StreamProtocol == RuncamStreamProtocol.Mjpeg ? MjpegUrl : RtspUrl;

    /// <summary>Nama WiFi SSID default RunCam WiFi Link 2.</summary>
    public static readonly string DefaultSsidPrefix = "RUNCAM_WIFILINK";

    /// <summary>Subnet default hotspot RunCam WiFi Link 2.</summary>
    public static readonly string DefaultSubnet = "192.168.0.";

    /// <summary>Semua IP yang biasanya digunakan RunCam / SIYI HM30 sebagai gateway.</summary>
    public static readonly List<string> KnownGatewayIps = new()
    {
        "192.168.0.1",
        "192.168.1.1",
        "192.168.144.11",
        "192.168.144.25",
        "10.0.0.1"
    };

    /// <summary>Port-port yang di-probe untuk memverifikasi kehadiran RunCam stream.</summary>
    public static readonly List<int> ProbePorts = new() { 554, 8080, 80 };
}

/// <summary>Pilihan protokol stream kamera.</summary>
public enum RuncamStreamProtocol
{
    /// <summary>RTSP – latensi rendah, direkomendasikan untuk desktop dan Android 5+.</summary>
    Rtsp,
    /// <summary>MJPEG HTTP – fallback jika RTSP tidak bisa dibuka.</summary>
    Mjpeg,
    /// <summary>Otomatis: coba RTSP dulu, fallback ke MJPEG.</summary>
    Auto
}

/// <summary>Resolusi video yang di-request ke kamera.</summary>
public enum RuncamResolution
{
    SD_480p,
    HD_720p,
    HD_1080p,
    UHD_4K
}
