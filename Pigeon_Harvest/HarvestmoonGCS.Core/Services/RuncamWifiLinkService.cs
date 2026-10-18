using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services.Connection;
using OpenCvSharp;
using Serilog;

namespace HarvestmoonGCS.Core.Services;

/// <summary>
/// Implementasi RuncamWifiLinkService untuk Desktop (Linux/Windows/macOS).
/// Menggunakan OpenCvSharp untuk decode stream RTSP/MJPEG dari RunCam WiFi Link 2.
///
/// Alur koneksi:
///  1. ConnectAllAsync(config) dipanggil dari UI
///  2. StartCameraStreamAsync → buka OpenCvSharp VideoCapture ke RTSP URL
///  3. StartMavLinkAsync → buat MavLinkUdpTransport dan register ke IMavLinkService
///  4. Frame loop decode frame → emit FrameReceived event → VideoStreamControl render
/// </summary>
public class RuncamWifiLinkService : IRuncamWifiLinkService
{
    private readonly RuncamWifiLinkDetector _detector = new();
    private readonly IMavLinkService? _mavLinkService;

    private VideoCapture? _capture;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private MavLinkUdpTransport? _mavUdpTransport;

    public bool IsCameraConnected { get; private set; }
    public bool IsMavLinkConnected { get; private set; }
    public RuncamWifiLinkConfig CurrentConfig { get; private set; } = new();
    public RuncamConnectionStatus Status { get; private set; } = RuncamConnectionStatus.Disconnected;

    public event EventHandler<byte[]>? FrameReceived;
    public event EventHandler<RuncamConnectionStatus>? StatusChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<RuncamWifiLinkConfig>? DeviceDetected;

    public RuncamWifiLinkService(IMavLinkService? mavLinkService = null)
    {
        _mavLinkService = mavLinkService;
    }

    // ── Detection ──────────────────────────────────────────────────────────────

    public async Task<List<RuncamWifiLinkConfig>> DetectDevicesAsync(CancellationToken ct = default)
    {
        SetStatus(RuncamConnectionStatus.Detecting);
        try
        {
            var devices = await _detector.DetectAsync(ct);
            foreach (var d in devices)
                DeviceDetected?.Invoke(this, d);

            if (devices.Count == 0)
                SetStatus(RuncamConnectionStatus.Disconnected);

            return devices;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RuncamWifiLinkService] DetectDevices gagal");
            SetStatus(RuncamConnectionStatus.Error);
            ErrorOccurred?.Invoke(this, $"Deteksi gagal: {ex.Message}");
            return new List<RuncamWifiLinkConfig>();
        }
    }

    public Task<bool> ProbeDeviceAsync(string ip, CancellationToken ct = default)
        => _detector.ProbeIpAsync(ip, ct).ContinueWith(
            t => !t.IsFaulted && !t.IsCanceled && t.Result != null,
            TaskContinuationOptions.None);

    // ── Camera Stream ──────────────────────────────────────────────────────────

    public async Task<bool> StartCameraStreamAsync(RuncamWifiLinkConfig config, CancellationToken ct = default)
    {
        await StopCameraStreamAsync();
        CurrentConfig = config;
        SetStatus(RuncamConnectionStatus.ConnectingCamera);

        Log.Information("[RuncamWifiLinkService] Membuka stream: {Url}", config.ActiveStreamUrl);

        try
        {
            _capture = await Task.Run(() =>
            {
                // Kandidat URL: RTSP → MJPEG (path RunCam) → MJPEG (path Orange Pi /video_feed).
                // Setup UAV lewat router (TP-Link) memakai Orange Pi, jadi /video_feed harus dicoba.
                var candidates = new List<string>();
                if (config.StreamProtocol == RuncamStreamProtocol.Rtsp)
                {
                    candidates.Add(config.RtspUrl);
                    if (config.CameraIp.Contains("192.168.144."))
                    {
                        candidates.Add("rtsp://192.168.144.25:8554/main.264");
                        candidates.Add("rtsp://192.168.144.25:8554/live/ch0");
                        candidates.Add("rtsp://192.168.144.11:8554/main.264");
                    }
                    candidates.AddRange(config.MjpegUrls);
                }
                else
                {
                    candidates.AddRange(config.MjpegUrls);
                    candidates.Add(config.RtspUrl);
                    if (config.CameraIp.Contains("192.168.144."))
                    {
                        candidates.Add("rtsp://192.168.144.25:8554/main.264");
                        candidates.Add("rtsp://192.168.144.25:8554/live/ch0");
                    }
                }

                VideoCapture? cap = null;
                foreach (var url in candidates)
                {
                    try
                    {
                        var candidate = new VideoCapture();
                        candidate.Set(VideoCaptureProperties.BufferSize, 3);
                        candidate.Open(url);
                        if (candidate.IsOpened())
                        {
                            Log.Information("[RuncamWifiLinkService] Stream terbuka: {Url}", url);
                            cap = candidate;
                            break;
                        }
                        candidate.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[RuncamWifiLinkService] Gagal membuka {Url}", url);
                    }
                }

                return cap;
            }, ct);

            if (_capture == null || !_capture.IsOpened())
            {
                Log.Error("[RuncamWifiLinkService] Gagal membuka stream {Url}", config.ActiveStreamUrl);
                SetStatus(RuncamConnectionStatus.Error);
                ErrorOccurred?.Invoke(this, $"Tidak dapat membuka stream: {config.ActiveStreamUrl}");
                return false;
            }

            IsCameraConnected = true;
            UpdateCombinedStatus();

            _streamCts = new CancellationTokenSource();
            _streamTask = Task.Run(() => StreamLoop(_streamCts.Token), _streamCts.Token);

            Log.Information("[RuncamWifiLinkService] Stream aktif: {Url}", config.ActiveStreamUrl);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RuncamWifiLinkService] StartCameraStream exception");
            SetStatus(RuncamConnectionStatus.Error);
            ErrorOccurred?.Invoke(this, $"Stream error: {ex.Message}");
            return false;
        }
    }

    public async Task StopCameraStreamAsync()
    {
        if (_streamCts != null)
        {
            _streamCts.Cancel();
            if (_streamTask != null)
            {
                try { await _streamTask.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch { /* ignore */ }
            }
            _streamCts.Dispose();
            _streamCts = null;
        }

        _capture?.Release();
        _capture?.Dispose();
        _capture = null;

        IsCameraConnected = false;
        UpdateCombinedStatus();
        Log.Information("[RuncamWifiLinkService] Stream dihentikan");
    }

    // ── MAVLink over WiFi ──────────────────────────────────────────────────────

    public async Task<bool> StartMavLinkAsync(RuncamWifiLinkConfig config, CancellationToken ct = default)
    {
        await StopMavLinkAsync();
        SetStatus(RuncamConnectionStatus.ConnectingMavLink);
        CurrentConfig = config;

        Log.Information("[RuncamWifiLinkService] Membuka MAVLink UDP local:{Local} remote:{Remote}:{RPort}",
            config.MavLinkLocalPort, config.MavLinkRemoteIp, config.MavLinkRemotePort);

        try
        {
            _mavUdpTransport = new MavLinkUdpTransport(
                localPort: config.MavLinkLocalPort,
                remoteIp: config.MavLinkRemoteIp,
                remotePort: config.MavLinkRemotePort);

            _mavUdpTransport.Connect();

            if (_mavLinkService != null)
            {
                // Hubungkan transport ke MavLinkService yang sudah ada
                await _mavLinkService.ConnectWithTransportAsync(_mavUdpTransport);
            }

            IsMavLinkConnected = true;
            UpdateCombinedStatus();
            Log.Information("[RuncamWifiLinkService] MAVLink UDP terhubung");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RuncamWifiLinkService] StartMavLink exception");
            SetStatus(RuncamConnectionStatus.Error);
            ErrorOccurred?.Invoke(this, $"MAVLink error: {ex.Message}");
            return false;
        }
    }

    public async Task StopMavLinkAsync()
    {
        if (_mavUdpTransport != null)
        {
            _mavUdpTransport.Disconnect();
            _mavUdpTransport.Dispose();
            _mavUdpTransport = null;
        }

        IsMavLinkConnected = false;
        UpdateCombinedStatus();
        await Task.CompletedTask;
    }

    // ── Combined ───────────────────────────────────────────────────────────────

    public async Task<bool> ConnectAllAsync(RuncamWifiLinkConfig config, CancellationToken ct = default)
    {
        CurrentConfig = config;
        bool cameraOk = await StartCameraStreamAsync(config, ct);
        bool mavOk = await StartMavLinkAsync(config, ct);

        Log.Information("[RuncamWifiLinkService] ConnectAll selesai: camera={Cam} mav={Mav}", cameraOk, mavOk);
        return cameraOk || mavOk; // berhasil selama salah satu berhasil
    }

    public async Task DisconnectAllAsync()
    {
        await StopCameraStreamAsync();
        await StopMavLinkAsync();
        SetStatus(RuncamConnectionStatus.Disconnected);
    }

    // ── Stream loop ────────────────────────────────────────────────────────────

    private void StreamLoop(CancellationToken ct)
    {
        using var mat = new Mat();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_capture == null || !_capture.IsOpened())
                {
                    Thread.Sleep(500);
                    continue;
                }

                if (!_capture.Read(mat) || mat.Empty())
                {
                    Log.Warning("[RuncamWifiLinkService] Frame kosong, mencoba kembali...");
                    Thread.Sleep(100);
                    continue;
                }

                // Encode ke JPEG untuk dikirim ke VideoStreamControl
                var jpegBytes = mat.ToBytes(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, 85));
                if (jpegBytes != null && jpegBytes.Length > 0)
                    FrameReceived?.Invoke(this, jpegBytes);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    Log.Warning(ex, "[RuncamWifiLinkService] Frame error");
                    Thread.Sleep(200);
                }
            }
        }

        Log.Debug("[RuncamWifiLinkService] StreamLoop selesai");
    }

    // ── Status helpers ─────────────────────────────────────────────────────────

    private void SetStatus(RuncamConnectionStatus s)
    {
        Status = s;
        StatusChanged?.Invoke(this, s);
    }

    private void UpdateCombinedStatus()
    {
        var s = (IsCameraConnected, IsMavLinkConnected) switch
        {
            (true, true)   => RuncamConnectionStatus.FullyConnected,
            (true, false)  => RuncamConnectionStatus.CameraOnly,
            (false, true)  => RuncamConnectionStatus.MavLinkOnly,
            _              => RuncamConnectionStatus.Disconnected
        };
        SetStatus(s);
    }

    public void Dispose()
    {
        // C-1 fix: jangan GetAwaiter().GetResult() karena bisa deadlock.
        // Stop stream dan MAVLink secara synchronous.
        _streamCts?.Cancel();
        try { _streamTask?.Wait(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
        _streamCts?.Dispose();
        _streamCts = null;
        _capture?.Release();
        _capture?.Dispose();
        _capture = null;

        _mavUdpTransport?.Disconnect();
        _mavUdpTransport?.Dispose();
        _mavUdpTransport = null;

        IsCameraConnected = false;
        IsMavLinkConnected = false;
    }
}
