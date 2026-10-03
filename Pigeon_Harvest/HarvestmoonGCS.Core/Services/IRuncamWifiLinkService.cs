using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Models;

namespace HarvestmoonGCS.Core.Services;

/// <summary>
/// Service khusus untuk RunCam WiFi Link 2.
/// Menangani:
///  1. Auto-detect hotspot WiFi RunCam di jaringan lokal
///  2. Koneksi RTSP / MJPEG stream kamera
///  3. Koneksi MAVLink via UDP melalui WiFi yang sama
/// </summary>
public interface IRuncamWifiLinkService : IDisposable
{
    // ── State ──────────────────────────────────────────────────────────────────
    bool IsCameraConnected { get; }
    bool IsMavLinkConnected { get; }
    RuncamWifiLinkConfig CurrentConfig { get; }
    RuncamConnectionStatus Status { get; }

    // ── Events ─────────────────────────────────────────────────────────────────
    /// <summary>Frame JPEG/RGB mentah dari stream kamera.</summary>
    event EventHandler<byte[]> FrameReceived;
    /// <summary>Status koneksi berubah (camera atau mavlink).</summary>
    event EventHandler<RuncamConnectionStatus> StatusChanged;
    /// <summary>Error yang terjadi.</summary>
    event EventHandler<string> ErrorOccurred;
    /// <summary>Perangkat RunCam baru terdeteksi di jaringan.</summary>
    event EventHandler<RuncamWifiLinkConfig> DeviceDetected;

    // ── Detection ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Scan jaringan lokal untuk RunCam WiFi Link.
    /// Cek port 554 (RTSP) dan 8080 (MJPEG) di subnet saat ini.
    /// </summary>
    Task<List<RuncamWifiLinkConfig>> DetectDevicesAsync(CancellationToken ct = default);

    /// <summary>
    /// Verifikasi apakah IP tertentu adalah RunCam WiFi Link.
    /// </summary>
    Task<bool> ProbeDeviceAsync(string ip, CancellationToken ct = default);

    // ── Camera Stream ──────────────────────────────────────────────────────────
    /// <summary>Mulai stream kamera dari konfigurasi.</summary>
    Task<bool> StartCameraStreamAsync(RuncamWifiLinkConfig config, CancellationToken ct = default);

    /// <summary>Stop stream kamera.</summary>
    Task StopCameraStreamAsync();

    // ── MAVLink over WiFi ──────────────────────────────────────────────────────
    /// <summary>
    /// Buat koneksi MAVLink UDP ke flight controller melalui WiFi Link.
    /// Drone biasanya mengirim MAVLink dari IP kamera ke port 14550 GCS.
    /// </summary>
    Task<bool> StartMavLinkAsync(RuncamWifiLinkConfig config, CancellationToken ct = default);

    /// <summary>Stop MAVLink connection.</summary>
    Task StopMavLinkAsync();

    // ── Combined ───────────────────────────────────────────────────────────────
    /// <summary>
    /// Connect kamera DAN MAVLink sekaligus dengan satu config.
    /// Ini adalah cara paling umum dipakai dari UI.
    /// </summary>
    Task<bool> ConnectAllAsync(RuncamWifiLinkConfig config, CancellationToken ct = default);

    /// <summary>Disconnect semua (kamera + MAVLink).</summary>
    Task DisconnectAllAsync();
}

/// <summary>Status koneksi RunCam WiFi Link.</summary>
public enum RuncamConnectionStatus
{
    Disconnected,
    Detecting,
    ConnectingCamera,
    ConnectingMavLink,
    CameraOnly,
    MavLinkOnly,
    FullyConnected,
    Error
}
