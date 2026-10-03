using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services;
using OpenCvSharp;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Desktop implementation of ICameraService using OpenCvSharp for camera access.
/// Supports local USB cameras and network streams (RTSP, HTTP, etc).
/// </summary>
public class DesktopCameraService : ICameraService
{
    private VideoCapture? _capture;
    private VideoWriter? _videoWriter;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private string? _currentSource;
    private readonly string _outputDirectory;
    
    public bool IsStreaming { get; private set; }
    public bool IsRecording { get; private set; }
    public bool IsClassificationStream => false;

    // Stub — DesktopCameraService tidak support HSV stream (pakai PythonCameraService di production)
    public Task<bool> StartHsvStreamAsync(string source, string? modelPath = null,
        float maxFps = 15f, bool showOverlay = true, bool demo = true, float playbackRate = 1.0f,
        bool yoloEnabled = false)
        => StartCameraAsync(source);
    
    public event EventHandler<byte[]>? FrameReceived;
    public event EventHandler<bool>? StreamingStatusChanged;
    public event EventHandler<bool>? RecordingStatusChanged;
    public event EventHandler<string>? ConnectionError;

    public DesktopCameraService()
    {
        // Create output directory for screenshots and videos
        _outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "HarvestmoonGCS",
            "Camera"
        );
        Directory.CreateDirectory(_outputDirectory);
        
        System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] Output directory: {_outputDirectory}");
        CameraDebug($"Output directory: {_outputDirectory}");
    }

    public async Task InitializeAsync()
    {
        System.Diagnostics.Debug.WriteLine("[DesktopCameraService] Initialized");
        await Task.CompletedTask;
    }

    public async Task<List<CameraSource>> GetAvailableSourcesAsync()
    {
        var sources = new List<CameraSource>();
        
        try
        {
            Serilog.Log.Information("[DesktopCameraService] ========== DETECTING CAMERAS ==========");
            CameraDebug("DETECTING CAMERAS");
            System.Diagnostics.Debug.WriteLine("[DesktopCameraService] ========== DETECTING CAMERAS ==========");
            
            // Try to detect local cameras using OpenCvSharp
            // Check up to 10 camera indices
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    Serilog.Log.Information($"[DesktopCameraService] Testing camera index {i}...");
                    System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] Testing camera index {i}...");
                    
                    using var testCapture = new VideoCapture(i, VideoCaptureAPIs.ANY);
                    
                    // Give it a moment to initialize
                    await Task.Delay(100);
                    
                    if (testCapture.IsOpened())
                    {
                        Serilog.Log.Information($"[DesktopCameraService] Camera {i} opened, testing frame read...");
                        
                        // Try to read a frame to verify it's actually working
                        using var testFrame = new Mat();
                        bool canRead = testCapture.Read(testFrame);
                        
                        if (canRead && !testFrame.Empty())
                        {
                            var width = (int)testCapture.FrameWidth;
                            var height = (int)testCapture.FrameHeight;
                            var fps = testCapture.Fps;
                            
                            sources.Add(new CameraSource
                            {
                                Id = i.ToString(),
                                Name = $"Camera {i}",
                                Description = $"Local Camera {i} ({width}x{height} @ {fps:F0}fps)",
                                Type = CameraSourceType.LocalCamera,
                                IsAvailable = true
                            });
                            
                            Serilog.Log.Information($"[DesktopCameraService] ✓ Found working camera {i}: {width}x{height} @ {fps:F0}fps");
                            CameraDebug($"FOUND camera {i}: {width}x{height} @ {fps:F0}fps");
                            System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] ✓ Found working camera {i}: {width}x{height} @ {fps:F0}fps");
                        }
                        else
                        {
                            Serilog.Log.Warning($"[DesktopCameraService] ✗ Camera {i} opened but cannot read frames");
                            System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] ✗ Camera {i} opened but cannot read frames");
                        }
                    }
                    else
                    {
                        Serilog.Log.Information($"[DesktopCameraService] ✗ Camera {i} not available");
                        System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] ✗ Camera {i} not available");
                        // If we can't open this index, likely no more cameras
                        if (i > 0 && sources.Count == 0)
                        {
                            Serilog.Log.Information("[DesktopCameraService] No cameras found, stopping search");
                            break; // No cameras found at all, stop searching
                        }
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, $"[DesktopCameraService] ✗ Error testing camera {i}");
                    System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] ✗ Error testing camera {i}: {ex.Message}");
                    // If we get an error on index 0, continue trying a few more
                    if (i > 2)
                    {
                        Serilog.Log.Information("[DesktopCameraService] Too many errors, stopping search");
                        break; // Stop after a few failures
                    }
                }
            }
            
            foreach (var index in GetLinuxVideoDeviceIndices())
            {
                if (sources.Any(source => source.Id == index.ToString()))
                {
                    continue;
                }

                sources.Add(new CameraSource
                {
                    Id = index.ToString(),
                    Name = $"Camera {index}",
                    Description = $"Local Camera {index} (/dev/video{index})",
                    Type = CameraSourceType.LocalCamera,
                    IsAvailable = true
                });
                CameraDebug($"ADDED linux video device /dev/video{index} without OpenCV frame probe");
            }

            // Add option for network stream
            sources.Add(new CameraSource
            {
                Id = "network",
                Name = "Network Stream (RTSP/HTTP)",
                Description = "Enter custom network stream URL",
                Type = CameraSourceType.NetworkStream,
                IsAvailable = true
            });
            
            Serilog.Log.Information($"[DesktopCameraService] ========== FOUND {sources.Count} CAMERA SOURCES ==========");
            CameraDebug($"FOUND {sources.Count} CAMERA SOURCES");
            System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] ========== FOUND {sources.Count} CAMERA SOURCES ==========");
            
            if (sources.Count == 1) // Only network option
            {
                Serilog.Log.Warning("[DesktopCameraService] ⚠️  WARNING: No local cameras detected!");
                Serilog.Log.Warning("[DesktopCameraService] Possible reasons:");
                Serilog.Log.Warning("[DesktopCameraService] 1. No camera connected");
                Serilog.Log.Warning("[DesktopCameraService] 2. Camera in use by another application");
                Serilog.Log.Warning("[DesktopCameraService] 3. Camera permissions not granted");
                Serilog.Log.Warning("[DesktopCameraService] 4. OpenCV not properly installed");
                
                System.Diagnostics.Debug.WriteLine("[DesktopCameraService] ⚠️  WARNING: No local cameras detected!");
                System.Diagnostics.Debug.WriteLine("[DesktopCameraService] Possible reasons:");
                System.Diagnostics.Debug.WriteLine("[DesktopCameraService] 1. No camera connected");
                System.Diagnostics.Debug.WriteLine("[DesktopCameraService] 2. Camera in use by another application");
                System.Diagnostics.Debug.WriteLine("[DesktopCameraService] 3. Camera permissions not granted");
                System.Diagnostics.Debug.WriteLine("[DesktopCameraService] 4. OpenCV not properly installed");
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[DesktopCameraService] ========== ERROR DETECTING CAMERAS ==========");
            System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] ========== ERROR DETECTING CAMERAS ==========");
            System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] {ex.StackTrace}");
            ConnectionError?.Invoke(this, $"Camera detection error: {ex.Message}");
        }
        
        return sources;
    }

    public async Task<bool> StartCameraAsync(string source)
    {
        return await Task.Run(() => StartCameraBlocking(source));
    }

    private bool StartCameraBlocking(string source)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] ========== STARTING CAMERA ==========");
            System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] Source: {source}");
            CameraDebug($"START source={source}");
            
            // C-1 fix: jangan GetAwaiter().GetResult() di dalam Task.Run – bisa deadlock.
            // Panggil StopCameraBlocking() synchronously sebagai gantinya.
            StopCameraBlocking();
            
            _currentSource = source;
            
            // Create VideoCapture from source
            if (int.TryParse(source, out int cameraIndex))
            {
                System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] Opening local camera index: {cameraIndex}");
                _capture = OpenLocalCapture(cameraIndex);
            }
            else if (source == "network")
            {
                System.Diagnostics.Debug.WriteLine("[DesktopCameraService] Network stream selected - user needs to provide URL");
                ConnectionError?.Invoke(this, "Please enter network stream URL in SettingsPage camera settings");
                return false;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] Opening network stream: {source}");
                _capture = new VideoCapture(source); // Network stream (RTSP, HTTP, etc)
            }
            
            if (_capture == null || !_capture.IsOpened())
            {
                System.Diagnostics.Debug.WriteLine("[DesktopCameraService] ✗ Failed to open camera");
                ConnectionError?.Invoke(this, "Failed to open camera. Check if camera is available and not in use.");
                return false;
            }
            
            // Try to read a test frame
            using var testFrame = new Mat();
            if (!TryReadFrameWithRetry(_capture, testFrame))
            {
                System.Diagnostics.Debug.WriteLine("[DesktopCameraService] ✗ Camera opened but cannot read frames");
                _capture.Release();
                _capture.Dispose();
                _capture = null;
                ConnectionError?.Invoke(this, "Camera opened but cannot read frames. Try another camera.");
                return false;
            }
            
            var width = (int)_capture.FrameWidth;
            var height = (int)_capture.FrameHeight;
            var fps = _capture.Fps;
            
            System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] ✓ Camera opened successfully");
            System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] Resolution: {width}x{height}");
            System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] FPS: {fps:F0}");
            
            IsStreaming = true;
            StreamingStatusChanged?.Invoke(this, true);
            
            // Start streaming
            _streamCts = new CancellationTokenSource();
            _streamTask = Task.Run(() => StreamLoop(_streamCts.Token));
            
            System.Diagnostics.Debug.WriteLine("[DesktopCameraService] ✓ Camera streaming started");
            CameraDebug($"STREAMING STARTED source={source} {width}x{height} fps={fps:F0}");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] ✗ Error starting camera: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] {ex.StackTrace}");
            CameraDebug($"START ERROR source={source}: {ex.Message}");
            ConnectionError?.Invoke(this, $"Failed to start camera: {ex.Message}");
            return false;
        }
    }

    public async Task StopCameraAsync()
    {
        System.Diagnostics.Debug.WriteLine("[DesktopCameraService] Stopping camera");
        
        _streamCts?.Cancel();
        
        if (_streamTask != null)
        {
            await _streamTask;
        }
        
        // Stop recording if active
        if (IsRecording)
        {
            await StopRecordingAsync();
        }
        
        // Release capture
        if (_capture != null)
        {
            _capture.Release();
            _capture.Dispose();
            _capture = null;
        }
        
        IsStreaming = false;
        StreamingStatusChanged?.Invoke(this, false);
        
        System.Diagnostics.Debug.WriteLine("[DesktopCameraService] Camera stopped");
    }

    /// <summary>
    /// Streaming loop for reading frames from camera.
    /// For file sources (video files), the video is looped automatically.
    /// </summary>
    private void StreamLoop(CancellationToken ct)
    {
        System.Diagnostics.Debug.WriteLine("[DesktopCameraService] Stream loop started");

        // Detect if source is a file (video loop) vs a live camera/stream
        bool isFileSource = !string.IsNullOrEmpty(_currentSource)
            && !int.TryParse(_currentSource, out _)
            && _currentSource != "network"
            && File.Exists(_currentSource);

        int frameCount = 0;
        using var frame = new Mat();
        
        while (!ct.IsCancellationRequested && _capture != null && _capture.IsOpened())
        {
            try
            {
                if (!_capture.Read(frame) || frame.Empty())
                {
                    if (isFileSource && !ct.IsCancellationRequested)
                    {
                        // End of video file — seek back to frame 0 and loop
                        System.Diagnostics.Debug.WriteLine("[DesktopCameraService] Video ended, looping back to start");
                        // Try PosMsec first; if the capture ignores it, fall back to PosFrames
                        bool seeked = _capture.Set(VideoCaptureProperties.PosMsec, 0);
                        if (!seeked)
                            _capture.Set(VideoCaptureProperties.PosFrames, 0);
                        // Brief pause so the codec can rewind before the next Read()
                        Thread.Sleep(50);
                        continue;
                    }
                    System.Diagnostics.Debug.WriteLine("[DesktopCameraService] Failed to read frame");
                    break;
                }
                
                // Encode frame to JPEG
                var encoded = frame.ImEncode(".jpg");
                
                // Invoke event with frame data
                FrameReceived?.Invoke(this, encoded);
                
                // Write to video if recording
                if (IsRecording && _videoWriter != null)
                {
                    _videoWriter.Write(frame);
                }
                
                frameCount++;
                
                if (frameCount % 30 == 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] Streamed {frameCount} frames");
                    CameraDebug($"STREAMED {frameCount} frames");
                }
                
                Thread.Sleep(33); // ~30 FPS
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] Stream error: {ex.Message}");
                break;
            }
        }
        
        System.Diagnostics.Debug.WriteLine("[DesktopCameraService] Stream loop ended");
    }

    private static VideoCapture? OpenLocalCapture(int cameraIndex)
    {
        var backends = new[] { VideoCaptureAPIs.V4L2, VideoCaptureAPIs.ANY };
        foreach (var backend in backends)
        {
            var capture = new VideoCapture(cameraIndex, backend);
            if (capture.IsOpened())
            {
                capture.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
                capture.Set(VideoCaptureProperties.FrameWidth, 1280);
                capture.Set(VideoCaptureProperties.FrameHeight, 720);
                return capture;
            }

            capture.Dispose();
        }

        return null;
    }

    private static bool TryReadFrameWithRetry(VideoCapture capture, Mat frame)
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            if (capture.Read(frame) && !frame.Empty())
            {
                return true;
            }

            Thread.Sleep(80);
        }

        return false;
    }

    private static IEnumerable<int> GetLinuxVideoDeviceIndices()
    {
        if (!Directory.Exists("/dev"))
        {
            return Enumerable.Empty<int>();
        }

        return Directory.GetFiles("/dev", "video*")
            .Select(path => Path.GetFileName(path).Replace("video", string.Empty))
            .Where(value => int.TryParse(value, out _))
            .Select(int.Parse)
            .OrderBy(index => index)
            .Take(10);
    }

    public async Task<bool> TakePictureAsync(string? filename = null)
    {
        if (!IsStreaming || _capture == null)
        {
            System.Diagnostics.Debug.WriteLine("[DesktopCameraService] Cannot take picture - not streaming");
            return false;
        }
        
        try
        {
            filename ??= Path.Combine(_outputDirectory, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
            
            using var frame = new Mat();
            if (_capture.Read(frame) && !frame.Empty())
            {
                frame.SaveImage(filename);
                System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] Picture saved: {filename}");
                return true;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] Error taking picture: {ex.Message}");
            ConnectionError?.Invoke(this, $"Failed to take picture: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> StartRecordingAsync(string? filename = null)
    {
        if (!IsStreaming || _capture == null)
        {
            System.Diagnostics.Debug.WriteLine("[DesktopCameraService] Cannot start recording - not streaming");
            return false;
        }
        
        if (IsRecording)
        {
            System.Diagnostics.Debug.WriteLine("[DesktopCameraService] Already recording");
            return false;
        }
        
        try
        {
            filename ??= Path.Combine(_outputDirectory, $"video_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            
            var fps = _capture.Fps;
            var width = (int)_capture.FrameWidth;
            var height = (int)_capture.FrameHeight;
            
            _videoWriter = new VideoWriter(filename, FourCC.H264, fps > 0 ? fps : 30, new OpenCvSharp.Size(width, height));
            
            if (!_videoWriter.IsOpened())
            {
                System.Diagnostics.Debug.WriteLine("[DesktopCameraService] Failed to open video writer");
                _videoWriter?.Dispose();
                _videoWriter = null;
                return false;
            }
            
            IsRecording = true;
            RecordingStatusChanged?.Invoke(this, true);
            
            System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] Recording started: {filename}");
            return await Task.FromResult(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] Error starting recording: {ex.Message}");
            ConnectionError?.Invoke(this, $"Failed to start recording: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> StopRecordingAsync()
    {
        if (!IsRecording)
        {
            return false;
        }
        
        try
        {
            _videoWriter?.Release();
            _videoWriter?.Dispose();
            _videoWriter = null;
            
            IsRecording = false;
            RecordingStatusChanged?.Invoke(this, false);
            
            System.Diagnostics.Debug.WriteLine("[DesktopCameraService] Recording stopped");
            return await Task.FromResult(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] Error stopping recording: {ex.Message}");
            return false;
        }
    }

    public async Task SendCameraControlAsync(CameraControlCommand command, float value)
    {
        System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] Camera control: {command} = {value}");
        
        // TODO: Implement camera controls (zoom, focus, exposure, etc)
        // This would typically involve:
        // 1. Setting VideoCapture properties (CAP_PROP_ZOOM, CAP_PROP_FOCUS, etc)
        // 2. Or sending commands to network cameras via their API
        
        await Task.CompletedTask;
    }

    private static void CameraDebug(string message)
    {
        try
        {
            File.AppendAllText("/tmp/pigeon_camera.log", $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    /// <summary>
    /// C-1 fix: Synchronous stop yang aman dipanggil dari blocking context (Task.Run).
    /// StopCameraAsync tidak boleh dipanggil via GetAwaiter().GetResult() karena
    /// bisa deadlock jika ada SynchronizationContext. Gunakan ini sebagai gantinya.
    /// </summary>
    private void StopCameraBlocking()
    {
        System.Diagnostics.Debug.WriteLine("[DesktopCameraService] StopCameraBlocking called");

        // Signal stream loop untuk berhenti
        var cts = Interlocked.Exchange(ref _streamCts!, null);
        cts?.Cancel();

        // Tunggu stream task dengan timeout (jangan block selamanya)
        var task = _streamTask;
        if (task != null)
        {
            try { task.Wait(TimeSpan.FromSeconds(3)); }
            catch { /* ignore */ }
            _streamTask = null;
        }

        cts?.Dispose();

        // Stop recording synchronously jika aktif
        if (IsRecording)
        {
            try
            {
                _videoWriter?.Release();
                _videoWriter?.Dispose();
                _videoWriter = null;
                IsRecording = false;
                RecordingStatusChanged?.Invoke(this, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] StopCameraBlocking recording stop error: {ex.Message}");
            }
        }

        // Release capture
        if (_capture != null)
        {
            try
            {
                _capture.Release();
                _capture.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] StopCameraBlocking capture release error: {ex.Message}");
            }
            _capture = null;
        }

        if (IsStreaming)
        {
            IsStreaming = false;
            StreamingStatusChanged?.Invoke(this, false);
        }

        System.Diagnostics.Debug.WriteLine("[DesktopCameraService] StopCameraBlocking completed");
    }
}
