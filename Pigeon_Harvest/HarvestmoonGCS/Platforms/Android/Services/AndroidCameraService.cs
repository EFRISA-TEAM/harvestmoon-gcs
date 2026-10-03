#if __ANDROID__
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Views;
using Android.Util;
using AndroidX.Core.Content;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AndroidImage = Android.Media.Image;
using IOPath = System.IO.Path;

namespace HarvestmoonGCS.Platforms.Android.Services;

/// <summary>
/// Android Camera2 implementation for live preview frames + snapshot capture.
/// </summary>
public class AndroidCameraService : ICameraService
{
    private const int CameraOpenTimeoutMs = 3000;
    private const int SessionOpenTimeoutMs = 3000;
    // Realme Pad Mini class hardware spends a lot of time JPEG encoding/decoding
    // large preview frames. 320x240 keeps preview latency low and leaves CPU for YOLO.
    private const int DefaultFrameWidth = 320;
    private const int DefaultFrameHeight = 240;

    private readonly Context _context;
    private readonly object _latestFrameSync = new();
    private readonly Handler _mainHandler = new(Looper.MainLooper);

    private CameraManager? _cameraManager;
    private HandlerThread? _cameraThread;
    private Handler? _cameraHandler;
    private CameraDevice? _cameraDevice;
    private CameraCaptureSession? _captureSession;
    private ImageReader? _imageReader;
    private CaptureRequest? _previewRequest;
    private MediaRecorder? _mediaRecorder;
    private byte[]? _latestJpegFrame;
    private readonly object _recordingSync = new();
    private StreamWriter? _recordingManifest;
    private string _recordingPath = string.Empty;
    private string _recordingFramesDirectory = string.Empty;
    private int _recordingFrameCount;
    private bool _isRecording;

    public bool IsStreaming { get; private set; }
    public bool IsRecording => _isRecording;
    public bool IsClassificationStream => false;

    // Stub — Android pakai AndroidDemoVideoDecoder untuk demo, bukan HSV stream
    public Task<bool> StartHsvStreamAsync(string source, string? modelPath = null,
        float maxFps = 15f, bool showOverlay = true, bool demo = true, float playbackRate = 1.0f,
        bool yoloEnabled = false)
        => Task.FromResult(false);

    public event EventHandler<byte[]>? FrameReceived;
    public event EventHandler<bool>? StreamingStatusChanged;
    public event EventHandler<bool>? RecordingStatusChanged;
    public event EventHandler<string>? ConnectionError;

    public AndroidCameraService(Context context)
    {
        _context = context;
        _cameraManager = _context.GetSystemService(Context.CameraService) as CameraManager;
    }

    public async Task InitializeAsync()
    {
        if (_cameraManager == null)
        {
            _cameraManager = _context.GetSystemService(Context.CameraService) as CameraManager;
        }

        await Task.CompletedTask;
    }

    public async Task<List<CameraSource>> GetAvailableSourcesAsync()
    {
        var sources = new List<CameraSource>();
        var cameras = await GetAvailableCamerasAsync();

        foreach (var camera in cameras)
        {
            sources.Add(new CameraSource
            {
                Id = camera.Id,
                Name = camera.Name,
                Description = camera.IsFrontFacing ? "Front camera" : "Back camera",
                Type = CameraSourceType.LocalCamera,
                IsAvailable = true
            });
        }

        return sources;
    }

    public async Task<bool> StartCameraAsync(string source)
    {
        if (_cameraManager == null)
        {
            ConnectionError?.Invoke(this, "Camera manager belum tersedia.");
            return false;
        }

        if (!HasCameraPermission())
        {
            ConnectionError?.Invoke(this, "Izin kamera belum diberikan (CAMERA).");
            return false;
        }

        try
        {
            await StopCameraAsync();

            var cameraId = source;
            if (string.IsNullOrWhiteSpace(cameraId))
            {
                cameraId = _cameraManager.GetCameraIdList()?.FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(cameraId))
            {
                ConnectionError?.Invoke(this, "Tidak ada kamera yang tersedia.");
                return false;
            }

            EnsureCameraThread();
            ConfigureImageReader(cameraId);
            _cameraDevice = await OpenCameraInternalAsync(cameraId);
            if (_cameraDevice == null)
            {
                await StopCameraAsync();
                return false;
            }

            var sessionReady = await StartPreviewSessionAsync();
            if (!sessionReady)
            {
                await StopCameraAsync();
                ConnectionError?.Invoke(this, "Gagal memulai sesi kamera.");
                return false;
            }

            IsStreaming = true;
            StreamingStatusChanged?.Invoke(this, true);
            return true;
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(this, $"Gagal memulai kamera: {ex.Message}");
            await StopCameraAsync();
            return false;
        }
    }

    public async Task StopCameraAsync()
    {
        try
        {
            try
            {
                _captureSession?.StopRepeating();
                _captureSession?.AbortCaptures();
            }
            catch
            {
                // Best effort while session is tearing down.
            }

            _captureSession?.Close();
            _captureSession?.Dispose();
            _captureSession = null;

            _imageReader?.SetOnImageAvailableListener(null, null);
            _imageReader?.Close();
            _imageReader?.Dispose();
            _imageReader = null;

            _cameraDevice?.Close();
            _cameraDevice?.Dispose();
            _cameraDevice = null;
            _previewRequest = null;

            if (_mediaRecorder != null)
            {
                try
                {
                    if (_isRecording)
                    {
                        _mediaRecorder.Stop();
                    }
                }
                catch
                {
                    // Ignore stop failure from partially initialized recorder.
                }

                _mediaRecorder.Reset();
                _mediaRecorder.Release();
                _mediaRecorder.Dispose();
                _mediaRecorder = null;
            }

            if (_isRecording)
            {
                _isRecording = false;
                RecordingStatusChanged?.Invoke(this, false);
            }

            lock (_latestFrameSync)
            {
                _latestJpegFrame = null;
            }

            if (IsStreaming)
            {
                IsStreaming = false;
                StreamingStatusChanged?.Invoke(this, false);
            }

            StopCameraThread();
        }
        finally
        {
            await Task.CompletedTask;
        }
    }

    public async Task<bool> TakePictureAsync(string? filename = null)
    {
        byte[]? snapshot;
        lock (_latestFrameSync)
        {
            snapshot = _latestJpegFrame?.ToArray();
        }

        if (snapshot == null || snapshot.Length == 0)
        {
            ConnectionError?.Invoke(this, "Belum ada frame kamera untuk disimpan.");
            return false;
        }

        try
        {
            var targetFile = ResolveOutputPath(filename);
            var targetDirectory = IOPath.GetDirectoryName(targetFile);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            await File.WriteAllBytesAsync(targetFile, snapshot);
            return true;
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(this, $"Gagal menyimpan gambar: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> StartRecordingAsync(string? filename = null)
    {
        if (!IsStreaming)
        {
            ConnectionError?.Invoke(this, "Mulai kamera dulu sebelum recording.");
            return false;
        }

        if (_isRecording)
        {
            return true;
        }

        try
        {
            var outputPath = ResolveRecordingPath(filename);
            var outputDir = IOPath.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            lock (_recordingSync)
            {
                _recordingPath = outputPath;
                _recordingFramesDirectory = IOPath.Combine(
                    outputDir ?? IOPath.Combine(AndroidCompatibility.GetAppStoragePath(), "Camera"),
                    $"{IOPath.GetFileNameWithoutExtension(outputPath)}_frames");
                Directory.CreateDirectory(_recordingFramesDirectory);

                _recordingManifest?.Dispose();
                _recordingManifest = new StreamWriter(new FileStream(outputPath + ".frames.txt", FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true
                };
                _recordingManifest.WriteLine("# HarvestmoonGCS Android camera recording");
                _recordingManifest.WriteLine($"started_utc={DateTime.UtcNow:O}");
                _recordingManifest.WriteLine($"frames_directory={_recordingFramesDirectory}");
                _recordingFrameCount = 0;
                _isRecording = true;
            }

            RecordingStatusChanged?.Invoke(this, true);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(this, $"Gagal memulai recording: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> StopRecordingAsync()
    {
        if (!_isRecording)
        {
            return true;
        }

        try
        {
            lock (_recordingSync)
            {
                _isRecording = false;
                _recordingManifest?.WriteLine($"stopped_utc={DateTime.UtcNow:O}");
                _recordingManifest?.Flush();
                _recordingManifest?.Dispose();
                _recordingManifest = null;
            }

            RecordingStatusChanged?.Invoke(this, false);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(this, $"Gagal menghentikan recording: {ex.Message}");
            return false;
        }
    }

    public async Task SendCameraControlAsync(CameraControlCommand command, float value)
    {
        await Task.CompletedTask;
    }

    private async Task<List<CameraInfo>> GetAvailableCamerasAsync()
    {
        var cameras = new List<CameraInfo>();
        if (_cameraManager == null)
        {
            return cameras;
        }

        try
        {
            var cameraIds = _cameraManager.GetCameraIdList() ?? Array.Empty<string>();
            foreach (var cameraId in cameraIds)
            {
                var characteristics = _cameraManager.GetCameraCharacteristics(cameraId);
                var facingObject = characteristics?.Get(CameraCharacteristics.LensFacing);
                var facingValue = facingObject is Java.Lang.Integer facingInteger
                    ? facingInteger.IntValue()
                    : -1;

                var isFront = facingValue == (int)LensFacing.Front;
                cameras.Add(new CameraInfo
                {
                    Id = cameraId,
                    Name = isFront ? "Front Camera" : "Back Camera",
                    IsFrontFacing = isFront
                });
            }
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(this, $"Gagal membaca daftar kamera: {ex.Message}");
        }

        await Task.CompletedTask;
        return cameras;
    }

    private void ConfigureImageReader(string cameraId)
    {
        var size = new Size(DefaultFrameWidth, DefaultFrameHeight);

        try
        {
            var characteristics = _cameraManager?.GetCameraCharacteristics(cameraId);
            var streamMap = characteristics?.Get(CameraCharacteristics.ScalerStreamConfigurationMap) as StreamConfigurationMap;
            var availableSizes = streamMap?.GetOutputSizes((int)ImageFormatType.Jpeg);
            if (availableSizes != null && availableSizes.Length > 0)
            {
                size = availableSizes
                    .OrderBy(s => Math.Abs(s.Width - DefaultFrameWidth) + Math.Abs(s.Height - DefaultFrameHeight))
                    .First();
            }
        }
        catch
        {
            // Fall back to defaults when camera metadata is unavailable.
        }

        _imageReader?.SetOnImageAvailableListener(null, null);
        _imageReader?.Close();
        _imageReader?.Dispose();

        _imageReader = ImageReader.NewInstance(size.Width, size.Height, ImageFormatType.Jpeg, 2);
        _imageReader.SetOnImageAvailableListener(new ImageAvailableListener(this), _cameraHandler);
    }

    private async Task<CameraDevice?> OpenCameraInternalAsync(string cameraId)
    {
        if (_cameraManager == null)
        {
            return null;
        }

        var tcs = new TaskCompletionSource<CameraDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _cameraManager.OpenCamera(cameraId, new CameraStateCallback(this, tcs), _cameraHandler);
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(this, $"Gagal membuka kamera: {ex.Message}");
            return null;
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(CameraOpenTimeoutMs));
        if (completed != tcs.Task)
        {
            ConnectionError?.Invoke(this, "Timeout saat membuka kamera.");
            return null;
        }

        try
        {
            return await tcs.Task;
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(this, $"Kamera gagal dibuka: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> StartPreviewSessionAsync()
    {
        if (_cameraDevice == null || _imageReader == null)
        {
            return false;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var outputs = new List<Surface> { _imageReader.Surface };
            _cameraDevice.CreateCaptureSession(outputs, new SessionStateCallback(this, tcs), _cameraHandler);
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(this, $"Gagal membuat sesi kamera: {ex.Message}");
            return false;
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(SessionOpenTimeoutMs));
        if (completed != tcs.Task)
        {
            ConnectionError?.Invoke(this, "Timeout saat memulai sesi kamera.");
            return false;
        }

        return await tcs.Task;
    }

    private bool HasCameraPermission()
    {
        return ContextCompat.CheckSelfPermission(_context, global::Android.Manifest.Permission.Camera) == Permission.Granted;
    }

    private void EnsureCameraThread()
    {
        if (_cameraThread?.IsAlive == true && _cameraHandler != null)
        {
            return;
        }

        _cameraThread = new HandlerThread("HarvestmoonCameraFrames", -4);
        _cameraThread.Start();
        _cameraHandler = new Handler(_cameraThread.Looper!);
    }

    private void StopCameraThread()
    {
        if (_cameraThread == null)
        {
            _cameraHandler = null;
            return;
        }

        try
        {
            _cameraThread.QuitSafely();
            _cameraThread.Join(500);
        }
        catch
        {
            // Best effort cleanup; Android will reclaim the thread with the process.
        }
        finally
        {
            _cameraThread.Dispose();
            _cameraThread = null;
            _cameraHandler?.Dispose();
            _cameraHandler = null;
        }
    }

    private static string ResolveOutputPath(string? filename)
    {
        var baseDirectory = IOPath.Combine(AndroidCompatibility.GetAppStoragePath(), "Camera");
        if (string.IsNullOrWhiteSpace(filename))
        {
            return IOPath.Combine(baseDirectory, $"IMG_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
        }

        if (IOPath.IsPathRooted(filename))
        {
            return filename;
        }

        return IOPath.Combine(baseDirectory, filename);
    }

    private static string ResolveRecordingPath(string? filename)
    {
        var baseDirectory = IOPath.Combine(AndroidCompatibility.GetAppStoragePath(), "Camera");
        if (string.IsNullOrWhiteSpace(filename))
        {
            return IOPath.Combine(baseDirectory, $"VID_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        }

        return IOPath.IsPathRooted(filename)
            ? filename
            : IOPath.Combine(baseDirectory, filename);
    }

    private void OnFrameAvailable(byte[] jpegBytes)
    {
        lock (_latestFrameSync)
        {
            _latestJpegFrame = jpegBytes;
        }

        WriteRecordingFrame(jpegBytes);
        FrameReceived?.Invoke(this, jpegBytes);
    }

    private void WriteRecordingFrame(byte[] jpegBytes)
    {
        if (!_isRecording || jpegBytes.Length == 0)
        {
            return;
        }

        try
        {
            lock (_recordingSync)
            {
                if (!_isRecording)
                {
                    return;
                }

                var frameNumber = ++_recordingFrameCount;
                var frameName = $"frame_{frameNumber:D06}.jpg";
                var framePath = IOPath.Combine(_recordingFramesDirectory, frameName);
                File.WriteAllBytes(framePath, jpegBytes);
                _recordingManifest?.WriteLine($"{frameNumber},{DateTime.UtcNow:O},{frameName},{jpegBytes.Length}");
            }
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(this, $"Gagal menulis frame recording: {ex.Message}");
        }
    }

    private sealed class CameraStateCallback : CameraDevice.StateCallback
    {
        private readonly AndroidCameraService _service;
        private readonly TaskCompletionSource<CameraDevice> _tcs;

        public CameraStateCallback(AndroidCameraService service, TaskCompletionSource<CameraDevice> tcs)
        {
            _service = service;
            _tcs = tcs;
        }

        public override void OnOpened(CameraDevice camera)
        {
            _service._cameraDevice = camera;
            _tcs.TrySetResult(camera);
        }

        public override void OnDisconnected(CameraDevice camera)
        {
            camera.Close();
            if (ReferenceEquals(_service._cameraDevice, camera))
            {
                _service._cameraDevice = null;
            }

            _tcs.TrySetException(new InvalidOperationException("Kamera terputus."));
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            camera.Close();
            if (ReferenceEquals(_service._cameraDevice, camera))
            {
                _service._cameraDevice = null;
            }

            _tcs.TrySetException(new InvalidOperationException($"Camera error: {error}"));
        }
    }

    private sealed class SessionStateCallback : CameraCaptureSession.StateCallback
    {
        private readonly AndroidCameraService _service;
        private readonly TaskCompletionSource<bool> _tcs;

        public SessionStateCallback(AndroidCameraService service, TaskCompletionSource<bool> tcs)
        {
            _service = service;
            _tcs = tcs;
        }

        public override void OnConfigured(CameraCaptureSession session)
        {
            try
            {
                _service._captureSession = session;
                if (_service._cameraDevice == null || _service._imageReader == null)
                {
                    _tcs.TrySetResult(false);
                    return;
                }

                using var requestBuilder = _service._cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
                requestBuilder.AddTarget(_service._imageReader.Surface);
                requestBuilder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.ContinuousPicture);
                requestBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.On);
                _service._previewRequest = requestBuilder.Build();

                session.SetRepeatingRequest(_service._previewRequest, null, _service._cameraHandler);
                _tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                _service.ConnectionError?.Invoke(_service, $"Gagal memulai preview: {ex.Message}");
                _tcs.TrySetResult(false);
            }
        }

        public override void OnConfigureFailed(CameraCaptureSession session)
        {
            _tcs.TrySetResult(false);
        }
    }

    private sealed class ImageAvailableListener : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        private readonly AndroidCameraService _service;

        public ImageAvailableListener(AndroidCameraService service)
        {
            _service = service;
        }

        public void OnImageAvailable(ImageReader? reader)
        {
            if (reader == null)
            {
                return;
            }

            AndroidImage? image = null;
            try
            {
                image = reader.AcquireLatestImage();
                if (image == null)
                {
                    return;
                }

                var planes = image.GetPlanes();
                if (planes == null || planes.Length == 0)
                {
                    return;
                }

                var buffer = planes[0].Buffer;
                if (buffer == null)
                {
                    return;
                }

                var bytes = new byte[buffer.Remaining()];
                buffer.Get(bytes);

                if (bytes.Length > 0)
                {
                    _service.OnFrameAvailable(bytes);
                }
            }
            catch (Java.Lang.IllegalStateException)
            {
                // Transient queue pressure: drain stale images and continue streaming.
                DrainReader(reader);
            }
            catch (Exception ex)
            {
                _service.ConnectionError?.Invoke(_service, $"Frame kamera gagal dibaca: {ex.Message}");
            }
            finally
            {
                if (image != null)
                {
                    try
                    {
                        image.Close();
                    }
                    catch
                    {
                    }

                    image.Dispose();
                }
            }
        }

        private static void DrainReader(ImageReader reader)
        {
            for (var i = 0; i < 8; i++)
            {
                AndroidImage? stale = null;
                try
                {
                    stale = reader.AcquireLatestImage();
                    if (stale == null)
                    {
                        return;
                    }
                }
                catch
                {
                    return;
                }
                finally
                {
                    if (stale != null)
                    {
                        try
                        {
                            stale.Close();
                        }
                        catch
                        {
                        }

                        stale.Dispose();
                    }
                }
            }
        }
    }
}

public class CameraInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsFrontFacing { get; set; }
}
#endif
