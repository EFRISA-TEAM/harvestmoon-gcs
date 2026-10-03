using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Dispatching;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.ViewModels;
using HarvestmoonGCS.ViewModels;
using HarvestmoonGCS.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using HarvestmoonGCS.Controls;

namespace HarvestmoonGCS.Views;

public sealed partial class CameraPage : Page
{
    private readonly ICameraService _cameraService;
    private readonly IRuncamWifiLinkService? _runcamService;
    private readonly IVideoRecorderService _videoRecorderService;
    private readonly IFileService? _fileService;
    private readonly IncidentTimelineService? _timelineService;
    private readonly HarvestFunctionalService? _harvestFunctionalService;
    private readonly IVideoScenarioService? _videoScenarioService;
    private readonly FlightViewModel? _flightViewModel;
    private readonly MapViewModel? _mapViewModel;
    private DispatcherTimer _recordingTimer;
    private DateTime _recordingStartTime;
    private bool _isStreaming;
    private bool _isInitialized;
    private bool _serviceHandlersAttached;
    private readonly CameraYoloProcessor _yoloProcessor = new();
    private bool _yoloReady;
    private string _lastYoloSummary = "YOLO: IDLE";
    private bool _vegetationOverlayEnabled = true;
    private float _minConf = 0.3f;
    private string? _lastClassifySource;
    private string? _lastClassifyModel;

    public CameraPage()
    {
        Serilog.Log.Information("[CameraPage] ========== CONSTRUCTOR STARTED ==========");
        
        try
        {
            this.InitializeComponent();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[CameraPage] ✗ FATAL: InitializeComponent failed!");
            throw;
        }
        
        // Get services from DI container
        try
        {
            _cameraService = App.GetService<ICameraService>();
            _runcamService = App.Current.Services.GetService<IRuncamWifiLinkService>();
            _videoRecorderService = App.GetService<IVideoRecorderService>();
            _fileService = App.Current.Services.GetService<IFileService>();
            _timelineService = App.Current.Services.GetService<IncidentTimelineService>();
            _harvestFunctionalService = App.Current.Services.GetService<HarvestFunctionalService>();
            _videoScenarioService = App.Current.Services.GetService<IVideoScenarioService>();
            _flightViewModel = App.Current.Services.GetService<FlightViewModel>();
            _mapViewModel = App.Current.Services.GetService<MapViewModel>();
            
            if (_cameraService == null)
            {
                Serilog.Log.Error("[CameraPage] ✗ ERROR: ICameraService is null!");
                return;
            }
            
            InitializeServices();

            this.Loaded += CameraPage_Loaded;
            this.Unloaded += CameraPage_Unloaded;
            this.SizeChanged += CameraPage_SizeChanged;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[CameraPage] ✗ ERROR initializing services");
        }
    }

    private void CameraPage_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyTabletLayout(ActualWidth);

        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        EnsureYoloInitialized();
        AttachServiceHandlers();
        LoadCameraSources();

        // Sync button state if a demo stream was already started before this page opened
        if (_cameraService != null && _cameraService.IsStreaming)
            OnStreamingStatusChanged(this, true);
        // Sync RunCam stream state (konek via ConnectDialog sebelum buka halaman Camera)
        else if (_runcamService != null &&
                 _runcamService.Status is RuncamConnectionStatus.CameraOnly or RuncamConnectionStatus.FullyConnected)
            OnRuncamStatusChanged(_runcamService, _runcamService.Status);

        // Initialize and sync PiP map
        SyncPipMap();

        if (VideoFilePathTextBox != null && (string.IsNullOrWhiteSpace(VideoFilePathTextBox.Text) || !File.Exists(VideoFilePathTextBox.Text)))
        {
            var scenario = _videoScenarioService?.CurrentScenario;
            var vid = _videoScenarioService?.ResolveVideoFilePath(scenario!) ?? scenario?.ResolvedVideoPath;
            if (!string.IsNullOrWhiteSpace(vid) && File.Exists(vid))
            {
                VideoFilePathTextBox.Text = vid;
                if (VideoFileTab != null) VideoFileTab.IsChecked = true;
            }
        }

        if (_videoScenarioService != null)
        {
            _videoScenarioService.ScenarioChanged -= OnScenarioChanged;
            _videoScenarioService.ScenarioChanged += OnScenarioChanged;
        }

        if (_flightViewModel != null)
        {
            _flightViewModel.PropertyChanged -= FlightViewModel_PropertyChanged;
            _flightViewModel.PropertyChanged += FlightViewModel_PropertyChanged;
        }
    }

    public void OnPageActivated()
    {
        EnsureYoloInitialized();
        AttachServiceHandlers();
        if (_cameraService != null && _cameraService.IsStreaming)
        {
            OnStreamingStatusChanged(this, true);
        }
        else if (_runcamService != null &&
                 _runcamService.Status is RuncamConnectionStatus.CameraOnly or RuncamConnectionStatus.FullyConnected)
        {
            OnRuncamStatusChanged(_runcamService, _runcamService.Status);
        }
        if (VideoFilePathTextBox != null && (string.IsNullOrWhiteSpace(VideoFilePathTextBox.Text) || !File.Exists(VideoFilePathTextBox.Text)))
        {
            var scenario = _videoScenarioService?.CurrentScenario;
            var vid = _videoScenarioService?.ResolveVideoFilePath(scenario!) ?? scenario?.ResolvedVideoPath;
            if (!string.IsNullOrWhiteSpace(vid) && File.Exists(vid))
            {
                VideoFilePathTextBox.Text = vid;
            }
        }
        SyncPipMap();
        if (_flightViewModel != null)
        {
            _flightViewModel.PropertyChanged -= FlightViewModel_PropertyChanged;
            _flightViewModel.PropertyChanged += FlightViewModel_PropertyChanged;
        }
    }

    private void SyncPipMap()
    {
        if (PipMap == null) return;

        var scenario = _videoScenarioService?.CurrentScenario;
        double cLat = (_flightViewModel?.Telemetry?.Latitude != null && _flightViewModel.Telemetry.Latitude != 0)
            ? _flightViewModel.Telemetry.Latitude
            : (scenario?.FieldCenterLat ?? -6.24361);
        double cLon = (_flightViewModel?.Telemetry?.Longitude != null && _flightViewModel.Telemetry.Longitude != 0)
            ? _flightViewModel.Telemetry.Longitude
            : (scenario?.FieldCenterLon ?? 107.36556);
        PipMap.SetCenter(cLat, cLon, 16);
        PipMap.SetFollowVehicle(true);
        PipMap.SetMapControlsVisible(false);

        if (_mapViewModel?.Waypoints != null && _mapViewModel.Waypoints.Count > 0)
        {
            PipMap.ClearWaypoints();
            foreach (var wp in _mapViewModel.Waypoints)
            {
                PipMap.AddWaypointMarker(wp.Sequence, wp.Latitude, wp.Longitude, wp.Altitude, "WP");
            }
        }

        if (_mapViewModel?.IsGeofenceActive == true)
        {
            PipMap.SetGeofence(true, _mapViewModel.GeofenceCenterLat, _mapViewModel.GeofenceCenterLon, _mapViewModel.GeofenceRadius);
        }
        else
        {
            PipMap.SetGeofence(true, 0, 0, 0);
        }
    }

    private void FlightViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlightViewModel.Telemetry) && _flightViewModel?.Telemetry != null)
        {
            var t = _flightViewModel.Telemetry;
            DispatcherQueue.TryEnqueue(() =>
            {
                PipMap?.UpdateVehiclePosition(t.Latitude, t.Longitude);
            });
        }
    }

    private void CameraPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _isInitialized = false;

        if (_videoScenarioService != null)
        {
            _videoScenarioService.ScenarioChanged -= OnScenarioChanged;
        }

        if (_flightViewModel != null)
        {
            _flightViewModel.PropertyChanged -= FlightViewModel_PropertyChanged;
        }

        DetachServiceHandlers();
        _yoloProcessor.Dispose();
        _yoloReady = false;

        if (_recordingTimer != null)
        {
            _recordingTimer.Stop();
        }
    }

    private void OnScenarioChanged(object? sender, VideoScenarioProfile profile)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (profile != null)
            {
                var vid = _videoScenarioService?.ResolveVideoFilePath(profile) ?? profile.ResolvedVideoPath;
                if (!string.IsNullOrWhiteSpace(vid) && File.Exists(vid) && VideoFilePathTextBox != null)
                {
                    VideoFilePathTextBox.Text = vid;
                }
                SyncPipMap();
            }
        });
    }

    private void CameraPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyTabletLayout(e.NewSize.Width);
    }

    private void ApplyTabletLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var compact = width < 820;
        CameraHudGrid.Margin = compact ? new Thickness(10, 56, 10, 10) : new Thickness(16, 68, 16, 16);
        PipMapBorder.Width = compact ? 168 : 208;
        PipMapBorder.Height = compact ? 126 : 156;
        CameraControlPanelHost.MaxWidth = compact ? 360 : 440;
        SelectionPanel.Padding = compact ? new Thickness(10) : new Thickness(12);
    }

    private void InitializeServices()
    {
        _recordingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        VideoStream.ShowFps = true;
    }

    private void EnsureYoloInitialized()
    {
        // YOLO is opt-in. Only initialise the detector when the user has actually enabled
        // the YOLO option, so a disabled toggle never runs inference on incoming frames.
        if (_harvestFunctionalService?.IsYoloOptionEnabled != true)
        {
            _yoloReady = false;
            _lastYoloSummary = "YOLO: OFF";
            if (YoloStatusText != null)
            {
                YoloStatusText.Text = _lastYoloSummary;
            }
            return;
        }

        _yoloReady = _yoloProcessor.Initialize();
        _lastYoloSummary = _yoloReady ? "YOLO: READY" : $"YOLO: {_yoloProcessor.Status}";
        if (YoloStatusText != null)
        {
            YoloStatusText.Text = _lastYoloSummary;
        }
    }

    private void OnYoloOptionChanged(object? sender, bool enabled)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            EnsureYoloInitialized();
            if (!enabled)
            {
                // Drop any overlay left over from before the toggle was switched off.
                VideoStream.SetDetectionOverlays(Array.Empty<VideoDetectionOverlay>());
            }
        });
    }

    private void AttachServiceHandlers()
    {
        if (_serviceHandlersAttached)
        {
            return;
        }

        if (_cameraService != null)
        {
            _cameraService.FrameReceived += OnFrameReceived;
            _cameraService.StreamingStatusChanged += OnStreamingStatusChanged;
            _cameraService.ConnectionError += OnConnectionError;

            // Subscribe to classification summary events if available
            if (_cameraService is PythonCameraService pcs)
            {
                pcs.ClassificationSummaryChanged += OnClassificationSummaryChanged;
            }
        }

        // RunCam WiFi Link 2: tampilkan stream & status tanpa perlu Start Camera manual.
        if (_runcamService != null)
        {
            _runcamService.FrameReceived += OnFrameReceived;
            _runcamService.StatusChanged += OnRuncamStatusChanged;
        }

        _videoRecorderService.RecordingStatusChanged += OnRecordingStatusChanged;
        _videoRecorderService.RecordingError += OnRecordingError;
        _recordingTimer.Tick += RecordingTimer_Tick;

        if (_harvestFunctionalService != null)
        {
            _harvestFunctionalService.YoloOptionChanged -= OnYoloOptionChanged;
            _harvestFunctionalService.YoloOptionChanged += OnYoloOptionChanged;
        }

        _serviceHandlersAttached = true;
    }

    private void DetachServiceHandlers()
    {
        if (!_serviceHandlersAttached)
        {
            return;
        }

        if (_cameraService != null)
        {
            _cameraService.FrameReceived -= OnFrameReceived;
            _cameraService.StreamingStatusChanged -= OnStreamingStatusChanged;
            _cameraService.ConnectionError -= OnConnectionError;

            if (_cameraService is PythonCameraService pcs)
            {
                pcs.ClassificationSummaryChanged -= OnClassificationSummaryChanged;
            }
        }

        if (_runcamService != null)
        {
            _runcamService.FrameReceived -= OnFrameReceived;
            _runcamService.StatusChanged -= OnRuncamStatusChanged;
        }

        if (_harvestFunctionalService != null)
        {
            _harvestFunctionalService.YoloOptionChanged -= OnYoloOptionChanged;
        }

        _videoRecorderService.RecordingStatusChanged -= OnRecordingStatusChanged;
        _videoRecorderService.RecordingError -= OnRecordingError;
        _recordingTimer.Tick -= RecordingTimer_Tick;
        _serviceHandlersAttached = false;
    }

    private async void LoadCameraSources()
    {
        try
        {
            await _cameraService.InitializeAsync();
            var sources = await _cameraService.GetAvailableSourcesAsync();
            
            CameraSourceComboBox.ItemsSource = sources;
            
            if (sources.Count > 0)
            {
                // Prioritas: kamera lokal/USB dulu → baru network MJPEG → terakhir UDP drone
                var preferredSource =
                    // 1. Kamera lokal atau USB
                    sources.FirstOrDefault(s => s.Type is CameraSourceType.LocalCamera or CameraSourceType.USB)
                    // 2. Network MJPEG (bukan udp-h264-drone)
                    ?? sources.FirstOrDefault(s => s.Type == CameraSourceType.NetworkStream
                                                   && !s.Id.StartsWith("udp-h264", StringComparison.OrdinalIgnoreCase)
                                                   && !s.Id.Equals("network", StringComparison.OrdinalIgnoreCase))
                    // 3. UDP H.264 drone (fallback terakhir saja)
                    ?? sources.FirstOrDefault(s => s.Id.StartsWith("udp-h264", StringComparison.OrdinalIgnoreCase))
                    ?? sources[0];

                CameraSourceComboBox.SelectedItem = preferredSource;

                // Auto-start kalau kamera lokal/USB tersedia
                if (preferredSource.Type is CameraSourceType.LocalCamera or CameraSourceType.USB)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(500);
                        DispatcherQueue.TryEnqueue(async () =>
                        {
                            if (!_isStreaming)
                                await _cameraService.StartCameraAsync(preferredSource.Id);
                        });
                    });
                }
            }
            else
            {
                ShowInfo("Tidak ada kamera terdeteksi. Hubungkan kamera USB atau sambungkan Orange Pi ke jaringan.");
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[CameraPage] ✗ ERROR loading camera sources");
        }
    }

    private async void OnFrameReceived(object sender, byte[] frameData)
    {
        if (_videoRecorderService.IsRecording)
        {
            _videoRecorderService.WriteFrame(frameData);
        }

        // HSV/classification stream — kotak deteksi sudah digambar di Python.
        // UpdateFrame dipanggil langsung dari background thread (bukan DispatcherQueue)
        // sehingga decode JPEG terjadi di background, bukan UI thread.
        if (_cameraService is PythonCameraService pcs && pcs.IsClassificationStream)
        {
            VideoStream.UpdateFrame(frameData);
            return;
        }

        YoloFrameResult? yoloResult = null;
        // Double gate: _yoloReady already reflects the toggle, but re-check the live flag so a
        // toggle flipped between frames can never run one more inference pass.
        if (_yoloReady && _harvestFunctionalService?.IsYoloOptionEnabled == true)
        {
            try
            {
                yoloResult = await _yoloProcessor.ProcessAsync(frameData);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[CameraPage] YOLO inference error");
                yoloResult = null;
            }
        }

        // UpdateFrame di bawah juga dipanggil dari background — decode terjadi di background
        if (yoloResult != null)
        {
            VideoStream.UpdateFrame(yoloResult.FrameData);
            DispatcherQueue.TryEnqueue(() =>
            {
                VideoStream.SetDetectionOverlays(yoloResult.Detections);
                _lastYoloSummary = $"YOLO: {yoloResult.DetectionCount} | {yoloResult.Fps:F1} FPS | {yoloResult.Summary}";
                YoloStatusText.Text = _lastYoloSummary;
            });
        }
        else
        {
            VideoStream.UpdateFrame(frameData);
            if (!_yoloReady)
            {
                DispatcherQueue.TryEnqueue(() =>
                    VideoStream.SetDetectionOverlays(Array.Empty<VideoDetectionOverlay>()));
            }
        }
    }

    private void OnStreamingStatusChanged(object sender, bool isStreaming)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _isStreaming = isStreaming;
            UpdateLiveStatus(isStreaming);
            UpdateControlStates();
            
            if (isStreaming)
            {
                StartStopButton.Content = "Stop Camera";
            }
            else
            {
                StartStopButton.Content = "Start Camera";
            }
        });
    }

    /// <summary>
    /// Sinkronkan indikator LIVE saat RunCam WiFi Link 2 connect/disconnect.
    /// Frame RunCam masuk lewat OnFrameReceived yang sama, jadi cukup update state di sini.
    /// </summary>
    private void OnRuncamStatusChanged(object? sender, RuncamConnectionStatus status)
    {
        bool cameraLive = status is RuncamConnectionStatus.CameraOnly or RuncamConnectionStatus.FullyConnected;
        bool anyLive = cameraLive || (_cameraService?.IsStreaming ?? false);
        DispatcherQueue.TryEnqueue(() =>
        {
            _isStreaming = anyLive;
            UpdateLiveStatus(anyLive);
            UpdateControlStates();
            StartStopButton.Content = anyLive ? "Stop Camera" : "Start Camera";
            if (cameraLive && MetadataDeviceText != null)
            {
                MetadataSourceText.Text = "SRC: RUNCAM_WIFI_LINK";
                MetadataDeviceText.Text = $"URL: {_runcamService?.CurrentConfig.ActiveStreamUrl}";
            }
        });
    }

    private void OnRecordingStatusChanged(object sender, bool isRecording)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (isRecording)
            {
                RecordButton.Content = "⏹️";
                _recordingStartTime = DateTime.Now;
                _recordingTimer.Start();
            }
            else
            {
                RecordButton.Content = "⏺️";
                _recordingTimer.Stop();
            }
        });
    }

    private void OnConnectionError(object sender, string error)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ShowError(error);
        });
    }

    private void OnRecordingError(object sender, string error)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _timelineService?.Add("recording", $"Recording error: {error}", "warning");
            ShowError(error);
        });
    }

    private void OnClassificationSummaryChanged(object sender, string dataJson)
    {
        // dataJson is now the full detection data JSON: {"count":31,"summary":"...","classes":{...}}
        var displaySummary = dataJson;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(dataJson);
            if (doc.RootElement.TryGetProperty("summary", out var s))
                displaySummary = s.GetString() ?? dataJson;
        }
        catch { }

        DispatcherQueue.TryEnqueue(() =>
        {
            _lastYoloSummary = $"Classification: {displaySummary}";
            if (YoloStatusText != null)
                YoloStatusText.Text = _lastYoloSummary;
        });
    }

    private async void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isStreaming)
            {
                await StopCamera();
            }
            else
            {
                await StartCamera();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[CameraPage] Start/Stop toggled failed");
            ShowError($"Gagal mengubah status kamera: {ex.Message}");
        }
    }

    private async Task<bool> StartCamera()
    {
        if (RtspTab.IsChecked == true)
        {
            string rtspUrl = RtspUrlTextBox.Text;
            if (string.IsNullOrEmpty(rtspUrl) || rtspUrl == "rtsp://")
            {
                ShowError("Enter a valid RTSP URL.");
                return false;
            }

            var connected = await _cameraService.StartCameraAsync(rtspUrl);
            if (!connected)
            {
                ShowError("Failed to open RTSP stream. Check URL, network, and camera credentials.");
            }

            return connected;
        }

        if (VideoFileTab.IsChecked == true)
        {
            var path = VideoFilePathTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                var scenario = _videoScenarioService?.CurrentScenario;
                path = _videoScenarioService?.ResolveVideoFilePath(scenario!) ?? scenario?.ResolvedVideoPath;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    VideoFilePathTextBox.Text = path;
                }
            }

            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                ShowError("Select a valid MP4/MOV/AVI video file.");
                return false;
            }

            // If PythonCameraService is available, use HSV + YOLO fusion stream
            if (_cameraService is PythonCameraService pcs)
            {
                var modelPath = PythonCameraService.ResolveHealthModelPath();
                _lastClassifySource = path;
                _lastClassifyModel = modelPath;
                bool yoloEnabled = _harvestFunctionalService?.IsYoloOptionEnabled ?? false;
                var classifyConnected = await pcs.StartHsvStreamAsync(
                    source: path,
                    modelPath: modelPath,
                    maxFps: 15f,
                    showOverlay: _vegetationOverlayEnabled,
                    demo: true,
                    playbackRate: 1.0f,
                    yoloEnabled: yoloEnabled);

                // Only chase the classify fallback when YOLO is on — otherwise an
                // HSV-only stream is exactly what the user asked for.
                if (yoloEnabled && !classifyConnected && !string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath))
                {
                    classifyConnected = await pcs.StartClassifyStreamAsync(
                        path, modelPath,
                        showOverlay: _vegetationOverlayEnabled,
                        minConf: _minConf);
                }

                if (classifyConnected)
                {
                    _timelineService?.Add("camera", $"Video stream started: {Path.GetFileName(path)}", "success");
                    return true;
                }
            }

            var connected = await _cameraService.StartCameraAsync(path);
            if (!connected)
            {
                ShowError("Failed to open video file. Check video format and OpenCV codec.");
            }

            _timelineService?.Add("camera", $"Video file source opened: {path}", connected ? "success" : "warning");
            return connected;
        }

        if (PythonBridgeTab.IsChecked == true)
        {
            var source = PythonBridgeSourceTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(source))
            {
                source = "0";
            }

            var connected = await _cameraService.StartCameraAsync(source);
            _timelineService?.Add("camera", $"Python bridge source opened: {source}", connected ? "success" : "warning");
            return connected;
        }

        var selectedSource = CameraSourceComboBox.SelectedItem as CameraSource;
        if (selectedSource == null)
        {
            ShowError("Please select a camera source");
            return false;
        }

        try
        {
            var success = await _cameraService.StartCameraAsync(selectedSource.Id);
            _timelineService?.Add("camera", $"Local camera source opened: {selectedSource.Name}", success ? "success" : "warning");
            if (!success)
            {
                ShowError("Failed to start camera. Check camera permissions or select another source.");
            }
            return success;
        }
        catch (Exception ex)
        {
            ShowError($"Error starting camera: {ex.Message}");
            return false;
        }
    }

    private async Task StopCamera()
    {
        try
        {
            // Matikan stream RunCam juga jika sedang aktif (source-nya bukan ICameraService).
            if (_runcamService != null &&
                _runcamService.Status is RuncamConnectionStatus.CameraOnly or RuncamConnectionStatus.FullyConnected)
            {
                await _runcamService.StopCameraStreamAsync();
            }
            await _cameraService.StopCameraAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Error stopping camera: {ex.Message}");
        }
    }

    private async void ScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var success = await _cameraService.TakePictureAsync();
            if (success)
            {
                _timelineService?.Add("snapshot", "Camera screenshot saved", "success");
            }
            if (success)
            {
                ShowInfo("Screenshot saved");
            }
            else
            {
                ShowError("Failed to take screenshot");
            }
        }
        catch (Exception ex)
        {
            ShowError($"Error taking screenshot: {ex.Message}");
        }
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_videoRecorderService.IsRecording)
            {
                var outputPath = _videoRecorderService.CurrentRecordingPath;
                await _videoRecorderService.StopRecordingAsync();
                if (!string.IsNullOrWhiteSpace(outputPath) && _harvestFunctionalService != null)
                {
                    await _harvestFunctionalService.AttachVideoRecordingToLatestReportAsync(outputPath);
                }

                _timelineService?.Add("recording", $"Video recording stopped: {outputPath}", "success");
                ShowInfo($"Recording saved: {outputPath}");
            }
            else
            {
                if (!_isStreaming)
                {
                    ShowError("Start camera/video first before recording.");
                    return;
                }

                var outputPath = await _videoRecorderService.GetDefaultRecordingPathAsync();
                var success = await _videoRecorderService.StartRecordingAsync(outputPath, width: 1280, height: 720, fps: 30);
                if (success)
                {
                    _timelineService?.Add("recording", $"Video recording started: {outputPath}", "success");
                    ShowInfo($"Recording started: {outputPath}");
                }
                else
                {
                    ShowError("Failed to start recording");
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[CameraPage] Recording toggle failed");
            ShowError($"Gagal merekam: {ex.Message}");
        }
    }

    private void RefreshSourcesButton_Click(object sender, RoutedEventArgs e)
    {
        LoadCameraSources();
    }

    private bool _suppressSourceAutoRestart;

    private async void CameraSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateMetadata();

        // Picking a different device while streaming must actually switch the feed.
        // _suppressSourceAutoRestart guards the programmatic selection made by
        // LoadCameraSources() while it probes sources, and by the restart itself.
        if (_suppressSourceAutoRestart) return;
        if (!_isStreaming) return;
        if (LocalTab?.IsChecked != true) return;
        if (CameraSourceComboBox.SelectedItem is not CameraSource) return;

        _suppressSourceAutoRestart = true;
        try
        {
            await StopCamera();
            await StartCamera();
            _timelineService?.Add("camera", "Camera source switched", "info");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[CameraPage] Camera source switch failed");
            ShowError($"Gagal berpindah kamera: {ex.Message}");
        }
        finally
        {
            _suppressSourceAutoRestart = false;
        }
    }

    private void RecordingTimer_Tick(object sender, object e)
    {
        // We can add a recording timer text in the HUD if needed, 
        // but it was removed in the new HUD to match PIGEON.
    }

    private void UpdateLiveStatus(bool isLive)
    {
        if (isLive)
        {
            LiveBadge.Visibility = Visibility.Visible;
            (Resources["LivePulseAnimation"] as Storyboard)?.Begin();
            MetadataPanel.Visibility = Visibility.Visible;
            UpdateMetadata();
        }
        else
        {
            LiveBadge.Visibility = Visibility.Collapsed;
            (Resources["LivePulseAnimation"] as Storyboard)?.Stop();
            MetadataPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateMetadata()
    {
        if (RtspTab.IsChecked == true)
        {
            MetadataSourceText.Text = "SRC: RTSP_STREAM";
            MetadataDeviceText.Text = $"URL: {RtspUrlTextBox.Text}";
        }
        else if (VideoFileTab?.IsChecked == true)
        {
            MetadataSourceText.Text = "SRC: VIDEO_FILE";
            MetadataDeviceText.Text = $"FILE: {System.IO.Path.GetFileName(VideoFilePathTextBox.Text)}";
        }
        else if (PythonBridgeTab?.IsChecked == true)
        {
            MetadataSourceText.Text = "SRC: PYTHON_BRIDGE";
            MetadataDeviceText.Text = $"SRC: {PythonBridgeSourceTextBox.Text}";
        }
        else
        {
            var selectedSource = CameraSourceComboBox.SelectedItem as CameraSource;
            MetadataSourceText.Text = "SRC: LOCAL_WEBCAM";
            MetadataDeviceText.Text = $"DEV: {selectedSource?.Name ?? "None"}";
        }

        YoloStatusText.Text = _lastYoloSummary;
    }

    private void UpdateControlStates()
    {
        ScreenshotButton.IsEnabled = _isStreaming;
        RecordButton.IsEnabled = _isStreaming;
    }

    private void TogglePanelButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectionPanel.Visibility == Visibility.Visible)
        {
            SelectionPanel.Visibility = Visibility.Collapsed;
            ToggleIcon.Glyph = "⌃";
        }
        else
        {
            SelectionPanel.Visibility = Visibility.Visible;
            ToggleIcon.Glyph = "⌄";
        }
    }

    private void LocalTab_Checked(object sender, RoutedEventArgs e)
    {
        if (LocalConfigGrid != null) LocalConfigGrid.Visibility = Visibility.Visible;
        if (RtspConfigGrid != null) RtspConfigGrid.Visibility = Visibility.Collapsed;
        if (VideoFileConfigGrid != null) VideoFileConfigGrid.Visibility = Visibility.Collapsed;
        if (PythonBridgeConfigGrid != null) PythonBridgeConfigGrid.Visibility = Visibility.Collapsed;
        UpdateMetadata();
    }

    private void RtspTab_Checked(object sender, RoutedEventArgs e)
    {
        if (LocalConfigGrid != null) LocalConfigGrid.Visibility = Visibility.Collapsed;
        if (RtspConfigGrid != null) RtspConfigGrid.Visibility = Visibility.Visible;
        if (VideoFileConfigGrid != null) VideoFileConfigGrid.Visibility = Visibility.Collapsed;
        if (PythonBridgeConfigGrid != null) PythonBridgeConfigGrid.Visibility = Visibility.Collapsed;
        UpdateMetadata();
    }

    private void VideoFileTab_Checked(object sender, RoutedEventArgs e)
    {
        if (LocalConfigGrid != null) LocalConfigGrid.Visibility = Visibility.Collapsed;
        if (RtspConfigGrid != null) RtspConfigGrid.Visibility = Visibility.Collapsed;
        if (VideoFileConfigGrid != null) VideoFileConfigGrid.Visibility = Visibility.Visible;
        if (PythonBridgeConfigGrid != null) PythonBridgeConfigGrid.Visibility = Visibility.Collapsed;
        UpdateMetadata();
    }

    private void PythonBridgeTab_Checked(object sender, RoutedEventArgs e)
    {
        if (LocalConfigGrid != null) LocalConfigGrid.Visibility = Visibility.Collapsed;
        if (RtspConfigGrid != null) RtspConfigGrid.Visibility = Visibility.Collapsed;
        if (VideoFileConfigGrid != null) VideoFileConfigGrid.Visibility = Visibility.Collapsed;
        if (PythonBridgeConfigGrid != null) PythonBridgeConfigGrid.Visibility = Visibility.Visible;
        UpdateMetadata();
    }

    private async void BrowseVideoFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_fileService == null)
            {
                ShowError("File picker not available.");
                return;
            }

            var file = await _fileService.PickFileAsync(new[] { ".mp4", ".mov", ".avi", ".mkv" });
            if (!string.IsNullOrWhiteSpace(file))
            {
                VideoFilePathTextBox.Text = file;
                UpdateMetadata();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[CameraPage] Browse video file failed");
            ShowError($"Gagal memilih file video: {ex.Message}");
        }
    }

    // TASK-04: Vegetation Overlay toggle
    private async void VegetationOverlayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        try
        {
            _vegetationOverlayEnabled = VegetationOverlayToggle.IsOn;
            // Restart classify stream with new overlay flag if active
            await RestartClassifyStreamIfActive();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[CameraPage] Vegetation overlay toggle failed");
            ShowError($"Gagal mengubah overlay vegetasi: {ex.Message}");
        }
    }

    // TASK-06: Confidence slider
    private void ConfidenceSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _minConf = (float)e.NewValue;
        if (ConfidenceValueText != null)
            ConfidenceValueText.Text = $"{_minConf:F2}";
    }

    private async Task RestartClassifyStreamIfActive()
    {
        if (_cameraService is not PythonCameraService pcs) return;
        if (!pcs.IsClassificationStream || !_isStreaming) return;
        if (string.IsNullOrWhiteSpace(_lastClassifySource) || string.IsNullOrWhiteSpace(_lastClassifyModel)) return;

        // Never escalate an HSV-only stream into the YOLO classify pipeline just because the
        // vegetation overlay was toggled. Only the YOLO option decides whether YOLO runs.
        if (_harvestFunctionalService?.IsYoloOptionEnabled != true)
        {
            Serilog.Log.Information("[CameraPage] Vegetation overlay toggled with YOLO off — keeping HSV-only stream");
            return;
        }

        await pcs.StartClassifyStreamAsync(
            _lastClassifySource, _lastClassifyModel,
            showOverlay: _vegetationOverlayEnabled,
            minConf: _minConf);
    }

    // ContentDialog.ShowAsync() throws InvalidOperationException if another dialog on this
    // XamlRoot is already open — a real risk here since a flaky RTSP/camera source can fire
    // ConnectionError and a recording error in quick succession. Guard against overlapping calls
    // instead of relying solely on GlobalExceptionHandler to catch the crash after the fact.
    private bool _isDialogShowing;

    private async void ShowError(string message)
    {
        if (_isDialogShowing) return;
        _isDialogShowing = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Error",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[CameraPage] ShowError failed");
        }
        finally
        {
            _isDialogShowing = false;
        }
    }

    private async void ShowInfo(string message)
    {
        if (_isDialogShowing) return;
        _isDialogShowing = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Info",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[CameraPage] ShowInfo failed");
        }
        finally
        {
            _isDialogShowing = false;
        }
    }
}
