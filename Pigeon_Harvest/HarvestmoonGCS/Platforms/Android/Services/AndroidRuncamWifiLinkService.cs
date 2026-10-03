#if __ANDROID__
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.OS;
using Android.Views;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Core.Services.Connection;
using Serilog;

namespace HarvestmoonGCS.Platforms.Android.Services;

/// <summary>
/// Implementasi RunCam WiFi Link 2 untuk Android.
/// 
/// Menggunakan Android MediaExtractor + MediaCodec untuk decode stream RTSP
/// agar dapat memanfaatkan hardware decoder H.264/H.265 di perangkat Android,
/// sehingga lebih hemat baterai dibanding software decode.
///
/// Alur:
///  1. StartCameraStreamAsync → buka MediaExtractor ke RTSP URL RunCam
///  2. MediaCodec H.264/H.265 hardware decoder → output Surface atau byte[]
///  3. Frame di-convert ke JPEG → FrameReceived event
///  4. MAVLink UDP berjalan paralel di Thread terpisah
/// </summary>
public class AndroidRuncamWifiLinkService : IRuncamWifiLinkService
{
    private readonly RuncamWifiLinkDetector _detector = new();
    private readonly IMavLinkService? _mavLinkService;
    private readonly Context _context;

    private MediaExtractor? _extractor;
    private MediaCodec? _decoder;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private MavLinkUdpTransport? _mavUdpTransport;
    private Surface? _outputSurface;

    public bool IsCameraConnected { get; private set; }
    public bool IsMavLinkConnected { get; private set; }
    public RuncamWifiLinkConfig CurrentConfig { get; private set; } = new();
    public RuncamConnectionStatus Status { get; private set; } = RuncamConnectionStatus.Disconnected;

    public event EventHandler<byte[]>? FrameReceived;
    public event EventHandler<RuncamConnectionStatus>? StatusChanged;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<RuncamWifiLinkConfig>? DeviceDetected;

    public AndroidRuncamWifiLinkService(Context context, IMavLinkService? mavLinkService = null)
    {
        _context = context;
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
            Log.Error(ex, "[AndroidRuncamWifi] DetectDevices gagal");
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

        Log.Information("[AndroidRuncamWifi] Membuka stream: {Url}", config.ActiveStreamUrl);

        try
        {
            bool opened = await Task.Run(() => OpenMediaExtractor(config.ActiveStreamUrl), ct);

            if (!opened)
            {
                // Fallback: coba semua kandidat MJPEG (path RunCam dan Orange Pi /video_feed)
                foreach (var mjpegUrl in config.MjpegUrls)
                {
                    Log.Warning("[AndroidRuncamWifi] Gagal, fallback ke {Url}", mjpegUrl);
                    opened = await Task.Run(() => OpenMediaExtractor(mjpegUrl), ct);
                    if (opened) break;
                }

                if (!opened)
                {
                    SetStatus(RuncamConnectionStatus.Error);
                    ErrorOccurred?.Invoke(this, $"Tidak dapat membuka stream: {config.ActiveStreamUrl}");
                    return false;
                }
            }

            IsCameraConnected = true;
            UpdateCombinedStatus();

            _streamCts = new CancellationTokenSource();
            _streamTask = Task.Run(() => DecodeLoopAsync(_streamCts.Token), _streamCts.Token);

            Log.Information("[AndroidRuncamWifi] Stream aktif");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[AndroidRuncamWifi] StartCameraStream exception");
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

        try
        {
            _decoder?.Stop();
            _decoder?.Release();
            _decoder?.Dispose();
            _decoder = null;

            _extractor?.Release();
            _extractor?.Dispose();
            _extractor = null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AndroidRuncamWifi] Stop stream error (ignorable)");
        }

        IsCameraConnected = false;
        UpdateCombinedStatus();
    }

    // ── MAVLink over WiFi ──────────────────────────────────────────────────────

    public async Task<bool> StartMavLinkAsync(RuncamWifiLinkConfig config, CancellationToken ct = default)
    {
        await StopMavLinkAsync();
        SetStatus(RuncamConnectionStatus.ConnectingMavLink);
        CurrentConfig = config;

        Log.Information("[AndroidRuncamWifi] MAVLink UDP local:{Local} → {Remote}:{RPort}",
            config.MavLinkLocalPort, config.MavLinkRemoteIp, config.MavLinkRemotePort);

        try
        {
            _mavUdpTransport = new MavLinkUdpTransport(
                localPort: config.MavLinkLocalPort,
                remoteIp: config.MavLinkRemoteIp,
                remotePort: config.MavLinkRemotePort);

            _mavUdpTransport.Connect();

            if (_mavLinkService != null)
                await _mavLinkService.ConnectWithTransportAsync(_mavUdpTransport);

            IsMavLinkConnected = true;
            UpdateCombinedStatus();
            Log.Information("[AndroidRuncamWifi] MAVLink UDP terhubung");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[AndroidRuncamWifi] StartMavLink exception");
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
        bool mavOk    = await StartMavLinkAsync(config, ct);
        return cameraOk || mavOk;
    }

    public async Task DisconnectAllAsync()
    {
        await StopCameraStreamAsync();
        await StopMavLinkAsync();
        SetStatus(RuncamConnectionStatus.Disconnected);
    }

    // ── MediaExtractor + MediaCodec ────────────────────────────────────────────

    private bool OpenMediaExtractor(string url)
    {
        try
        {
            _extractor = new MediaExtractor();
            _extractor.SetDataSource(url);

            // Cari video track H.264 atau H.265
            int videoTrack = -1;
            MediaFormat? format = null;
            for (int i = 0; i < _extractor.TrackCount; i++)
            {
                var tf = _extractor.GetTrackFormat(i);
                string? mime = tf.GetString(MediaFormat.KeyMime);
                if (mime != null && mime.StartsWith("video/"))
                {
                    videoTrack = i;
                    format = tf;
                    break;
                }
            }

            if (videoTrack < 0 || format == null)
            {
                Log.Warning("[AndroidRuncamWifi] Tidak menemukan video track di {Url}", url);
                return false;
            }

            _extractor.SelectTrack(videoTrack);

            string codecMime = format.GetString(MediaFormat.KeyMime) ?? "video/avc";
            _decoder = MediaCodec.CreateDecoderByType(codecMime);

            // Konfigurasi tanpa Surface → output ke ByteBuffer agar bisa di-convert ke JPEG
            _decoder.Configure(format, null, null, 0);
            _decoder.Start();

            Log.Information("[AndroidRuncamWifi] MediaCodec {Mime} berhasil dikonfigurasi", codecMime);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[AndroidRuncamWifi] OpenMediaExtractor gagal untuk {Url}", url);
            _decoder?.Release();
            _decoder = null;
            _extractor?.Release();
            _extractor = null;
            return false;
        }
    }

    private async Task DecodeLoopAsync(CancellationToken ct)
    {
        if (_extractor == null || _decoder == null) return;

        var bufferInfo = new MediaCodec.BufferInfo();
        const int TimeoutUs = 10_000; // 10ms

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Feed data ke decoder input
                int inputIdx = _decoder.DequeueInputBuffer(TimeoutUs);
                if (inputIdx >= 0)
                {
                    var inputBuf = _decoder.GetInputBuffer(inputIdx);
                    if (inputBuf != null)
                    {
                        inputBuf.Clear();
                        int sampleSize = _extractor.ReadSampleData(inputBuf, 0);
                        if (sampleSize < 0)
                        {
                            // End of stream - untuk live stream ini tidak seharusnya terjadi
                            _decoder.QueueInputBuffer(inputIdx, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                            Log.Warning("[AndroidRuncamWifi] End-of-stream dari extractor");
                            await Task.Delay(500, ct);
                            continue;
                        }

                        long pts = _extractor.SampleTime;
                        _decoder.QueueInputBuffer(inputIdx, 0, sampleSize, pts, 0);
                        _extractor.Advance();
                    }
                }

                // Ambil output dari decoder
                int outputIdx = _decoder.DequeueOutputBuffer(bufferInfo, TimeoutUs);
                if (outputIdx >= 0)
                {
                    var outputBuf = _decoder.GetOutputBuffer(outputIdx);
                    if (outputBuf != null && bufferInfo.Size > 0)
                    {
                        byte[] rawData = new byte[bufferInfo.Size];
                        outputBuf.Position(bufferInfo.Offset);
                        outputBuf.Get(rawData);

                        // Ambil dimensi untuk convert ke Bitmap
                        var outFormat = _decoder.GetOutputFormat(outputIdx);
                        int width  = outFormat.GetInteger(MediaFormat.KeyWidth);
                        int height = outFormat.GetInteger(MediaFormat.KeyHeight);

                        // Convert YUV → JPEG via Android Bitmap
                        var jpegBytes = ConvertYuvToJpeg(rawData, width, height);
                        if (jpegBytes != null)
                            FrameReceived?.Invoke(this, jpegBytes);
                    }

                    _decoder.ReleaseOutputBuffer(outputIdx, false);
                }
                else if (outputIdx == (int)MediaCodecInfoState.TryAgainLater)
                {
                    // Tidak ada output tersedia, tunggu sebentar
                    await Task.Delay(5, ct);
                }
            }
            catch (System.OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    Log.Warning(ex, "[AndroidRuncamWifi] DecodeLoop error");
                    await Task.Delay(200, ct);
                }
            }
        }
    }

    /// <summary>Convert raw YUV420 bytes ke JPEG menggunakan Android Bitmap.</summary>
    private static byte[]? ConvertYuvToJpeg(byte[] yuv, int width, int height)
    {
        try
        {
            // Gunakan YuvImage untuk konversi cepat di Android
            var yuvImage = new YuvImage(yuv, ImageFormatType.Nv21, width, height, null);
            using var stream = new System.IO.MemoryStream();
            yuvImage.CompressToJpeg(new Rect(0, 0, width, height), 85, stream);
            return stream.ToArray();
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "[AndroidRuncamWifi] ConvertYuvToJpeg gagal");
            return null;
        }
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
            (true, true)  => RuncamConnectionStatus.FullyConnected,
            (true, false) => RuncamConnectionStatus.CameraOnly,
            (false, true) => RuncamConnectionStatus.MavLinkOnly,
            _             => RuncamConnectionStatus.Disconnected
        };
        SetStatus(s);
    }

    public void Dispose()
    {
        // C-1 fix: jangan GetAwaiter().GetResult() karena bisa deadlock.
        _streamCts?.Cancel();
        try { _streamTask?.Wait(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
        _streamCts?.Dispose();
        _streamCts = null;

        try
        {
            _decoder?.Stop();
            _decoder?.Release();
            _decoder?.Dispose();
            _extractor?.Release();
            _extractor?.Dispose();
        }
        catch { /* ignore */ }
        _decoder = null;
        _extractor = null;

        _mavUdpTransport?.Disconnect();
        _mavUdpTransport?.Dispose();
        _mavUdpTransport = null;

        IsCameraConnected = false;
        IsMavLinkConnected = false;
    }
}
#endif
