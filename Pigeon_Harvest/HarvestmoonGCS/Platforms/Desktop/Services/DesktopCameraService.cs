#if !__ANDROID__
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Core.Models;
using OpenCvSharp;

namespace HarvestmoonGCS.Platforms.Desktop.Services;

/// <summary>
/// Desktop implementation of ICameraService using OpenCV.
/// Provides video streaming, recording, and capture functionality for desktop platforms.
/// </summary>
public class DesktopCameraService : ICameraService
{
    private VideoCapture _videoCapture;
    private VideoWriter _videoWriter;
    private Mat _currentFrame;
    private Thread _captureThread;
    private CancellationTokenSource _captureCts;
    
    private bool _isStreaming;
    private bool _isRecording;
    private string _currentSource;
    private List<CameraSource> _availableSources;
    private CameraSettings _currentSettings;
    private string _recordingFilename;

    public bool IsStreaming => _isStreaming;
    public bool IsRecording => _isRecording;
    public bool IsClassificationStream => false;

    // Stub — Platforms/Desktop pakai PythonCameraService di production
    public Task<bool> StartHsvStreamAsync(string source, string? modelPath = null,
        float maxFps = 15f, bool showOverlay = true, bool demo = true, float playbackRate = 1.0f,
        bool yoloEnabled = false)
        => StartCameraAsync(source);
    public string CurrentSource => _currentSource;
    public IReadOnlyList<CameraSource> AvailableSources => _availableSources?.AsReadOnly();

    public event EventHandler<byte[]> FrameReceived;
    public event EventHandler<bool> StreamingStatusChanged;
    public event EventHandler<bool> RecordingStatusChanged;
    public event EventHandler<string> ConnectionError;

    public DesktopCameraService()
    {
        _availableSources = new List<CameraSource>();
        _currentSettings = new CameraSettings();
        _currentFrame = new Mat();
    }

    public async Task InitializeAsync()
    {
        try
        {
            await GetAvailableSourcesAsync();
        }
        catch (Exception ex)
        {
            OnConnectionError($"Failed to initialize camera service: {ex.Message}");
        }
    }

    public async Task<List<CameraSource>> GetAvailableSourcesAsync()
    {
        return await Task.Run(() =>
        {
            _availableSources.Clear();

            try
            {
                // Try to detect USB cameras (typically 0-3) with timeout
                for (int i = 0; i < 4; i++)
                {
                    try
                    {
                        // Use Task with timeout to prevent hanging
                        var detectionTask = Task.Run(() =>
                        {
                            using var testCapture = new VideoCapture(i);
                            if (testCapture.IsOpened())
                            {
                                // Verify camera is actually working by trying to read a frame
                                using var testFrame = new Mat();
                                bool canRead = testCapture.Read(testFrame);
                                testCapture.Release();
                                return canRead;
                            }
                            return false;
                        });

                        // Wait max 500ms per camera
                        if (detectionTask.Wait(500) && detectionTask.Result)
                        {
                            _availableSources.Add(new CameraSource
                            {
                                Id = i.ToString(),
                                Name = $"Camera {i}",
                                Description = $"USB Camera {i}",
                                Type = CameraSourceType.USB
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        // Skip this camera and continue
                        System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] Camera {i} detection failed: {ex.Message}");
                    }
                }

                // Add Orange Pi Drone Camera presets
                var orangePiUrls = new[] {
                    "http://10.235.141.127:8080/video_feed",
                    "http://192.168.1.100:8080/video_feed",
                    "http://192.168.0.100:8080/video_feed",
                    "http://10.0.0.100:8080/video_feed",
                    "http://192.168.1.10:8080/video_feed",
                    "http://192.168.0.10:8080/video_feed",
                };
                foreach (var url in orangePiUrls)
                {
                    try
                    {
                        using var testCap = new VideoCapture(url);
                        if (testCap.IsOpened())
                        {
                            using var testFrame = new Mat();
                            if (testCap.Read(testFrame) && testFrame != null)
                            {
                                _availableSources.Add(new CameraSource
                                {
                                    Id = url,
                                    Name = "🚁 Drone Camera (Orange Pi)",
                                    Description = $"Live MJPEG Stream dari Orange Pi — {url}",
                                    Type = CameraSourceType.NetworkStream
                                });
                                break;
                            }
                        }
                        testCap.Release();
                    }
                    catch { }
                }
                if (!_availableSources.Any(s => s.Type == CameraSourceType.NetworkStream && s.Name.Contains("Orange Pi")))
                {
                    _availableSources.Add(new CameraSource
                    {
                        Id = "http://192.168.1.100:8080/video_feed",
                        Name = "🚁 Drone Camera (Orange Pi - Try)",
                        Description = "Live MJPEG Stream dari Orange Pi. Common IP: 192.168.1.100, 192.168.0.100",
                        Type = CameraSourceType.NetworkStream
                    });
                }

                // Add generic network stream option
                _availableSources.Add(new CameraSource
                {
                    Id = "network",
                    Name = "Network Stream",
                    Description = "RTSP/HTTP video stream (e.g. http://192.168.1.100:8080/video_feed)",
                    Type = CameraSourceType.NetworkStream
                });

                // Add file option
                _availableSources.Add(new CameraSource
                {
                    Id = "file",
                    Name = "Video File",
                    Description = "Play video from file",
                    Type = CameraSourceType.File
                });
            }
            catch (Exception ex)
            {
                OnConnectionError($"Failed to enumerate cameras: {ex.Message}");
            }

            return _availableSources.ToList();
        });
    }

    public async Task<bool> StartCameraAsync(string source)
    {
        if (_isStreaming)
        {
            await StopCameraAsync();
        }

        return await Task.Run(() =>
        {
            try
            {
                _currentSource = source;

                // Determine if source is a camera index, URL, or file path
                bool isNetwork = source.StartsWith("http://") || source.StartsWith("https://") || 
                                 source.StartsWith("rtsp://");
                
                // Retry logic for network streams
                int maxRetries = isNetwork ? 3 : 1;
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    if (int.TryParse(source, out int cameraIndex))
                    {
                        _videoCapture = new VideoCapture(cameraIndex);
                    }
                    else if (isNetwork)
                    {
                        _videoCapture = new VideoCapture(source);
                    }
                    else if (File.Exists(source))
                    {
                        _videoCapture = new VideoCapture(source);
                    }
                    else
                    {
                        OnConnectionError($"Invalid camera source: {source}");
                        return false;
                    }

                    if (_videoCapture != null && _videoCapture.IsOpened())
                    {
                        break; // Success
                    }
                    
                    _videoCapture?.Release();
                    _videoCapture = null;
                    
                    if (attempt < maxRetries - 1 && isNetwork)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DesktopCameraService] Network stream attempt {attempt + 1} failed, retrying...");
                        Thread.Sleep(1000);
                    }
                }

                if (_videoCapture == null || !_videoCapture.IsOpened())
                {
                    OnConnectionError("Failed to open camera. Check network connection to Orange Pi. Try different IP: 192.168.1.100, 10.235.141.127, or 192.168.0.100.");
                    _videoCapture?.Release();
                    _videoCapture = null;
                    return false;
                }

                // Set camera properties
                _videoCapture.Set(VideoCaptureProperties.FrameWidth, _currentSettings.Width);
                _videoCapture.Set(VideoCaptureProperties.FrameHeight, _currentSettings.Height);
                _videoCapture.Set(VideoCaptureProperties.Fps, _currentSettings.FrameRate);

                // Start capture thread
                _captureCts = new CancellationTokenSource();
                _captureThread = new Thread(() => CaptureLoop(_captureCts.Token))
                {
                    IsBackground = true,
                    Name = "CameraCapture"
                };
                _captureThread.Start();

                _isStreaming = true;
                OnStreamingStatusChanged(true);

                return true;
            }
            catch (Exception ex)
            {
                OnConnectionError($"Failed to start camera: {ex.Message}");
                return false;
            }
        });
    }

    public async Task StopCameraAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                _captureCts?.Cancel();
                _captureThread?.Join(1000);

                if (_isRecording)
                {
                    StopRecordingAsync().Wait();
                }

                _videoCapture?.Release();
                _videoCapture = null;

                _isStreaming = false;
                _currentSource = null;
                OnStreamingStatusChanged(false);
            }
            catch (Exception ex)
            {
                OnConnectionError($"Error stopping camera: {ex.Message}");
            }
        });
    }

    public async Task<bool> TakePictureAsync(string filename = null)
    {
        if (!_isStreaming || _currentFrame == null || _currentFrame.Empty())
        {
            return false;
        }

        return await Task.Run(() =>
        {
            try
            {
                filename ??= GenerateFilename("IMG", ".jpg");

                lock (_currentFrame)
                {
                    if (!_currentFrame.Empty())
                    {
                        Cv2.ImWrite(filename, _currentFrame);
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                OnConnectionError($"Failed to capture image: {ex.Message}");
                return false;
            }
        });
    }

    public async Task<bool> StartRecordingAsync(string filename = null)
    {
        if (!_isStreaming || _isRecording)
        {
            return false;
        }

        return await Task.Run(() =>
        {
            try
            {
                _recordingFilename = filename ?? GenerateFilename("VID", ".mp4");

                // Determine codec based on format string
                VideoCodec codec;
                if (!Enum.TryParse<VideoCodec>(_currentSettings.Format, true, out codec))
                {
                    codec = VideoCodec.MJPEG; // Default fallback
                }
                var fourcc = GetFourCC(codec);

                _videoWriter = new VideoWriter(
                    _recordingFilename,
                    fourcc,
                    _currentSettings.FrameRate,
                    new OpenCvSharp.Size(_currentSettings.Width, _currentSettings.Height));

                if (!_videoWriter.IsOpened())
                {
                    OnConnectionError("Failed to open video writer");
                    _videoWriter?.Release();
                    _videoWriter = null;
                    return false;
                }

                _isRecording = true;
                OnRecordingStatusChanged(true);

                return true;
            }
            catch (Exception ex)
            {
                OnConnectionError($"Failed to start recording: {ex.Message}");
                _videoWriter?.Release();
                _videoWriter = null;
                return false;
            }
        });
    }

    public async Task<bool> StopRecordingAsync()
    {
        if (!_isRecording)
        {
            return false;
        }

        return await Task.Run(() =>
        {
            try
            {
                _videoWriter?.Release();
                _videoWriter = null;

                _isRecording = false;
                OnRecordingStatusChanged(false);

                return true;
            }
            catch (Exception ex)
            {
                OnConnectionError($"Failed to stop recording: {ex.Message}");
                return false;
            }
        });
    }

    public async Task SendCameraControlAsync(CameraControlCommand command, float value)
    {
        if (_videoCapture == null || !_videoCapture.IsOpened())
        {
            return;
        }

        await Task.Run(() =>
        {
            try
            {
                switch (command)
                {
                    case CameraControlCommand.Zoom:
                        _videoCapture.Set(VideoCaptureProperties.Zoom, value);
                        break;
                    case CameraControlCommand.Focus:
                        _videoCapture.Set(VideoCaptureProperties.Focus, value);
                        break;
                    case CameraControlCommand.Brightness:
                        _videoCapture.Set(VideoCaptureProperties.Brightness, value);
                        break;
                    case CameraControlCommand.Contrast:
                        _videoCapture.Set(VideoCaptureProperties.Contrast, value);
                        break;
                    case CameraControlCommand.Saturation:
                        _videoCapture.Set(VideoCaptureProperties.Saturation, value);
                        break;
                    case CameraControlCommand.Exposure:
                        _videoCapture.Set(VideoCaptureProperties.Exposure, value);
                        break;
                }
            }
            catch (Exception ex)
            {
                OnConnectionError($"Failed to send camera control: {ex.Message}");
            }
        });
    }

    public async Task<CameraSettings> GetSettingsAsync()
    {
        return await Task.FromResult(_currentSettings);
    }

    public async Task<bool> UpdateSettingsAsync(CameraSettings settings)
    {
        _currentSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        
        // If streaming, restart with new settings
        if (_isStreaming)
        {
            var source = _currentSource;
            await StopCameraAsync();
            return await StartCameraAsync(source);
        }

        return true;
    }

    private void CaptureLoop(CancellationToken cancellationToken)
    {
        var frame = new Mat();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_videoCapture == null || !_videoCapture.IsOpened())
                {
                    break;
                }

                if (_videoCapture.Read(frame) && !frame.Empty())
                {
                    // Update current frame
                    lock (_currentFrame)
                    {
                        frame.CopyTo(_currentFrame);
                    }

                    // Write to video file if recording
                    if (_isRecording && _videoWriter != null && _videoWriter.IsOpened())
                    {
                        _videoWriter.Write(frame);
                    }

                    // Encode frame to JPEG and raise event
                    var encoded = frame.ToBytes(".jpg");
                    OnFrameReceived(encoded);

                    // Control frame rate
                    var delay = 1000 / _currentSettings.FrameRate;
                    Thread.Sleep(delay);
                }
                else
                {
                    // End of stream or error
                    if (_currentSource != null && File.Exists(_currentSource))
                    {
                        // Loop video file
                        _videoCapture.Set(VideoCaptureProperties.PosFrames, 0);
                    }
                    else
                    {
                        OnConnectionError("Failed to read frame from camera");
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            OnConnectionError($"Error in capture loop: {ex.Message}");
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private int GetFourCC(VideoCodec format)
    {
        return format switch
        {
            VideoCodec.H264 => VideoWriter.FourCC('H', '2', '6', '4'),
            VideoCodec.H265 => VideoWriter.FourCC('H', '2', '6', '5'),
            VideoCodec.MJPEG => VideoWriter.FourCC('M', 'J', 'P', 'G'),
            VideoCodec.VP8 => VideoWriter.FourCC('V', 'P', '8', '0'),
            VideoCodec.VP9 => VideoWriter.FourCC('V', 'P', '9', '0'),
            _ => VideoWriter.FourCC('M', 'J', 'P', 'G')
        };
    }

    private string GenerateFilename(string prefix, string extension)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return Path.Combine(directory, $"{prefix}_{timestamp}{extension}");
    }

    private void OnFrameReceived(byte[] frameData)
    {
        FrameReceived?.Invoke(this, frameData);
    }

    private void OnStreamingStatusChanged(bool isStreaming)
    {
        StreamingStatusChanged?.Invoke(this, isStreaming);
    }

    private void OnRecordingStatusChanged(bool isRecording)
    {
        RecordingStatusChanged?.Invoke(this, isRecording);
    }

    private void OnConnectionError(string error)
    {
        ConnectionError?.Invoke(this, error);
    }

    public void Dispose()
    {
        StopCameraAsync().Wait();
        _currentFrame?.Dispose();
        _videoCapture?.Dispose();
        _videoWriter?.Dispose();
    }
}
#endif