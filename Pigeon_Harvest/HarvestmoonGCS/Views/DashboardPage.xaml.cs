 using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using Windows.UI;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Core.ViewModels;
using HarvestmoonGCS.Helpers;
using HarvestmoonGCS.ViewModels;
using HarvestmoonGCS.Controls;
using HarvestmoonGCS.Services;
using XamlEllipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using XamlLine = Microsoft.UI.Xaml.Shapes.Line;

namespace HarvestmoonGCS.Views;

public sealed partial class DashboardPage : Page
{
    private static readonly string[] DemoVideoPathCandidates =
    {
        // Single-file EXE: AppContext.BaseDirectory resolves to extraction temp folder
        Path.Combine(AppContext.BaseDirectory, "Assets", "demo_videos", "stream_multisector_60s_detected.mp4"),
        Path.Combine(AppContext.BaseDirectory, "Assets", "demo_videos", "stream_multisector_60s.mp4"),
        Path.Combine(AppContext.BaseDirectory, "Assets", "demo_videos", "stream_v7c_final.mp4"),
        Path.Combine(AppContext.BaseDirectory, "Assets", "demo_videos", "YDXJ_fused_only_detected.mp4"),
        Path.Combine(AppContext.BaseDirectory, "Assets", "demo_videos", "YDX_burned.mp4"),
        Path.Combine(AppContext.BaseDirectory, "Assets", "demo_videos", "stream_multisector_survey.mp4"),

        // Normal deployment: EXE folder/Assets/demo_videos
        Path.Combine(Directory.GetCurrentDirectory(), "Assets", "demo_videos", "stream_multisector_60s_detected.mp4"),
        Path.Combine(Directory.GetCurrentDirectory(), "Assets", "demo_videos", "stream_multisector_60s.mp4"),
        Path.Combine(Directory.GetCurrentDirectory(), "Assets", "demo_videos", "stream_v7c_final.mp4"),
        Path.Combine(Directory.GetCurrentDirectory(), "Assets", "demo_videos", "YDXJ_fused_only_detected.mp4"),
        Path.Combine(Directory.GetCurrentDirectory(), "Assets", "demo_videos", "YDX_burned.mp4"),
        Path.Combine(Directory.GetCurrentDirectory(), "Assets", "demo_videos", "stream_multisector_survey.mp4"),

        // Dev machine Linux paths
        "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/demo_videos/stream_multisector_60s_detected.mp4",
        "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/demo_videos/stream_v7c_final.mp4",
        "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/demo_videos/YDXJ_fused_only_detected.mp4",
        "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/stream_multisector_60s.mp4",
        "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/stream_v7c_final.mp4",
        "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/YDXJ_fused_only_detected.mp4",
    };
    private readonly FlightViewModel? _flightViewModel;
    private readonly MapViewModel? _mapViewModel;
    private readonly ICameraService? _cameraService;
    private readonly IRuncamWifiLinkService? _runcamService;
    private readonly HarvestFunctionalService? _harvestFunctionalService;
    private readonly IVideoRecorderService? _videoRecorderService;
    private readonly IOfflineMapService? _offlineMapService;
    private readonly IGeofenceService? _geofenceService;
    private readonly GeofenceMonitor? _geofenceMonitor;
    private readonly IncidentTimelineService? _timelineService;
    private readonly IMavLinkService? _mavLinkService;

    private TelemetryData? _telemetry;
    private byte[]? _lastFrameData;
    private bool _mapInitialized;
    private bool _mapViewModelSubscribed;
    private bool _cameraHandlersAttached;
    private bool _analysisRunning;
    private bool _gridOverlayDrawn;
    private bool _isPageActive;
    private DateTime _lastAnalysisTime = DateTime.MinValue;
    private double _centerLat = -6.9175;
    private double _centerLon = 107.6191;
    private DateTime _lastDashboardVehicleRender = DateTime.MinValue;
    private const int DashboardVehicleRenderIntervalMs = 200;

    // YOLO inference FPS counter
    private int _inferenceCount;
    private DateTime _lastFpsCalcTime = DateTime.UtcNow;
    private double _currentInferenceFps;

    // Throttling for telemetry UI updates.
    // MAVLink packets raise many PropertyChanged events per packet so we coalesce them
    // into at most one dashboard refresh every UiRefreshIntervalMs (~10 Hz).
    private const int UiRefreshIntervalMs = 100;
    private DateTime _lastUiRefresh = DateTime.MinValue;
    private bool _uiRefreshScheduled;

    // UI state
    private bool _isRecording;
    private bool _aiOn;
    private bool _showImuHud = true;
    private int _confThreshold = 40;
    private double _pitch = -5.0;  // slight nose-down pitch for realistic forward flight
    private double _roll;
    private double _yaw = 45.0;  // initial heading 45° (northeast)
    private bool _missionTimerStarted;
    private readonly DispatcherTimer _imuTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _missionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _missionStart = DateTime.Now;
    private int _telemetryFrameCount;
    private int _lastDetectionCount;
    private double _lastConfidence;
    private string? _lastAlertSignature;
    private DateTime _lastGeofenceMonitorCheck = DateTime.MinValue;
    private string? _lastGeofenceMonitorEvent;
    private string _lastGeofenceMonitorSeverity = "info";
    private DateTime _lastYoloTimelineEvent = DateTime.MinValue;
    private HarvestFunctionalService.YoloBenchmarkResult? _lastBenchmarkResult;

    // Real classify-stream detection data from Python (null = no data yet)
    private (int Healthy, int Stress, int Drought, int BareSoil, int Total, double Fhi)? _classifyRealData;
    private bool _classifyFirstFrameReceived;

    // Real FPS tracking for HSV classify stream
    private int _classifyFrameCount;
    private DateTime _classifyFrameStart = DateTime.MinValue;
    private double _classifyFps;

#if __ANDROID__
    private HarvestmoonGCS.Platforms.Android.Services.AndroidDemoVideoDecoder? _androidDemoDecoder;
#endif

    // Demo mode state
    private bool _isDemoRunning;
    private readonly object _modeLock = new();
    private bool _useFusedOnlyDemoSummary;
    private int _demoStep;
    private double _demoBattery = 95.0;
    private double _demoHeading = 45.0;  // tracks current bearing for smooth roll (start at 45° northeast)
    private bool _hasDemoImu;
    private double _demoPitchDeg = -5.0;
    private double _demoRollDeg;
    private double _demoYawDeg = 45.0;
    private double _demoAltitudeMeters = 60.0;
    private double _demoSpeedMetersPerSecond = 9.4;
    // Per-run random seed so telemetry values vary slightly each demo session
    private Random _demoRng = new Random();
    private double _demoAltitudeBase = 60.0;   // randomized at demo start
    private double _demoSpeedBase    = 9.4;    // randomized at demo start
    private double _demoBatteryStart = 95.0;   // randomized at demo start
    private int    _demoSatCount     = 15;     // randomized at demo start
    private double _demoBatteryVoltage = 12.6; // randomized at demo start
    private int _demoTicksPerSegment = 15; // Dynamically adapts to video duration (1:1 sync)
    private double _demoVideoDurationSeconds = 60.0;
    private double _demoGeofenceRadiusMeters = 550;
    private (double Lat, double Lon)[] _demoWaypoints = Array.Empty<(double, double)>();
    private string? _customVideoPath;
    private bool _isSwitchingScenario;
    private readonly IFileService? _fileService;
    private readonly IVideoScenarioService? _videoScenarioService;
    private readonly IWaypointService? _waypointService;
    private VideoScenarioProfile _currentScenario;
    private double DemoFieldLat => _currentScenario?.FieldCenterLat ?? -6.42580;
    private double DemoFieldLon => _currentScenario?.FieldCenterLon ?? 107.61890;
    private readonly DispatcherTimer _demoTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private const int DemoWaypointStartSequence = 1;

    // Current HSV stream configuration (for restarting when YOLO toggle changes)
    private string? _currentHsvSource;
    private string? _currentHsvModelPath;
    private bool _currentHsvDemo;
    private float _currentHsvPlaybackRate;
    private float _currentHsvMaxFps;
    private bool _currentHsvShowOverlay;

    private static readonly string[] DemoClassificationEvents =
    {
        "Lush Green (conf 0.93) · Sector C",
        "Inconsistent Growth (conf 0.81) · Sector D",
        "Lush Green (conf 0.96) · Sector A",
        "Inconsistent Growth (conf 0.74) · Sector B",
        "Drought/Severe Stress (conf 0.87) · Sector A",
        "Bare Soil / Gap (conf 0.78) · Sector E",
    };

    public DashboardPage()
    {
        this.InitializeComponent();

        _flightViewModel = App.Current.Services.GetService<FlightViewModel>();
        _mapViewModel = App.Current.Services.GetService<MapViewModel>();
        _cameraService = App.Current.Services.GetService<ICameraService>();
        _harvestFunctionalService = App.Current.Services.GetService<HarvestFunctionalService>();
        _fileService = App.Current.Services.GetService<IFileService>();
        _videoRecorderService = App.Current.Services.GetService<IVideoRecorderService>();
        _offlineMapService = App.Current.Services.GetService<IOfflineMapService>();
        _geofenceService = App.Current.Services.GetService<IGeofenceService>();
        _geofenceMonitor = App.Current.Services.GetService<GeofenceMonitor>();
        _timelineService = App.Current.Services.GetService<IncidentTimelineService>();
        _mavLinkService  = App.Current.Services.GetService<IMavLinkService>();
        _runcamService   = App.Current.Services.GetService<IRuncamWifiLinkService>();
        _videoScenarioService = App.Current.Services.GetService<IVideoScenarioService>();
        _waypointService = App.Current.Services.GetService<IWaypointService>();

        _currentScenario = _videoScenarioService?.CurrentScenario ?? new VideoScenarioService().CurrentScenario;
        _demoWaypoints = _currentScenario.Waypoints.Select(w => (w.Latitude, w.Longitude)).ToArray();
        _demoAltitudeBase = _currentScenario.CruiseAltitudeMeters;
        _demoSpeedBase = _currentScenario.CruiseSpeedMps;
        _demoHeading = _currentScenario.BearingDeg;
        _demoBattery = _currentScenario.InitialBatteryPct;
        _demoSatCount = _currentScenario.SatelliteCount;
        _demoGeofenceRadiusMeters = _currentScenario.GeofenceRadiusMeters;
        _centerLat = _currentScenario.FieldCenterLat;
        _centerLon = _currentScenario.FieldCenterLon;
        _demoVideoDurationSeconds = _currentScenario.VideoDurationSeconds;
        int wpCount = Math.Max(2, _currentScenario.Waypoints.Count);
        _demoTicksPerSegment = Math.Max(1, (int)Math.Round(_demoVideoDurationSeconds / (wpCount - 1)));

        _imuTimer.Tick += OnImuTick;
        _missionTimer.Tick += OnMissionTick;

        Loaded += DashboardPage_Loaded;
        Unloaded += DashboardPage_Unloaded;
        SizeChanged += DashboardPage_SizeChanged;

        if (_timelineService != null)
        {
            _timelineService.TimelineChanged += OnTimelineChanged;
        }
    }

    public void OnPageActivated()
    {
        _isPageActive = true;
        DashboardMapControl?.SetActive(true);
        DashboardMapControl?.InvalidateArrange();
        AttachCameraHandlers();
        ResumeVideoRendering();
        SubscribeToTelemetry();
        SubscribeToGeofenceMonitor();
        _imuTimer.Start();
        if (_missionTimerStarted) _missionTimer.Start();
    }

    public void OnPageDeactivated()
    {
        _isPageActive = false;
        DashboardMapControl?.SetActive(false);
        _imuTimer.Stop();
        _missionTimer.Stop();
        UnsubscribeFromTelemetry();
        UnsubscribeFromGeofenceMonitor();
    }

    private void DashboardPage_Loaded(object sender, RoutedEventArgs e)
    {
        _isPageActive = true;
        SubscribeToTelemetry();
        SubscribeToGeofenceMonitor();
        AttachCameraHandlers();
        SyncYoloStateFromService();
        // Load confidence threshold from AI settings if available
        _ = LoadConfThresholdFromSettingsAsync();
        if (AlertsStack.Children.Count == 0) UpdateAlertCenter(_flightViewModel?.Telemetry, _flightViewModel?.IsConnected == true);
        if (SummaryHealthyCount.Text is "0" or "0%") SeedSummary();
        UpdateFarmerReport(0, 0, 0, 0, 0);
        RebuildGridOverlay();
        ApplyLayerVisibility();
        RefreshDashboard();
        RenderIncidentTimeline();
        if (!_missionTimerStarted)
        {
            _missionStart = DateTime.Now;
            MissionIdText.Text = string.IsNullOrEmpty(_currentScenario?.MissionId) ? $"MH-{DateTime.Now:yyyyMMdd-HHmm}" : _currentScenario.MissionId;
            MissionStartedText.Text = DateTime.Now.ToString("HH:mm:ss");
            _missionTimerStarted = true;
        }

        if (_currentScenario != null)
        {
            if (_waypointService != null && _currentScenario.Waypoints.Count > 0)
            {
                _ = _waypointService.ReplaceWaypointsAsync(new List<HarvestmoonGCS.Core.Models.WaypointData>());
            }
            if (_mapViewModel != null)
            {
                _mapViewModel.Waypoints.Clear();
                _mapViewModel.IsGeofenceActive = false;
            }
            if (DashboardMapControl != null)
            {
                DashboardMapControl.ClearWaypoints();
                DashboardMapControl.SetCenter(_currentScenario.FieldCenterLat, _currentScenario.FieldCenterLon, _currentScenario.MapZoomLevel);
            }
        }

        _imuTimer.Start();
        _missionTimer.Start();

        // Start camera only if it's not already running globally.
        // On re-navigate back to Dashboard, just resume rendering the existing stream.
        if (_cameraService != null && _cameraService.IsStreaming)
        {
            ResumeVideoRendering();
        }
        else
        {
            _ = StartDashboardCameraAsync();
        }

        if (string.Equals(Environment.GetEnvironmentVariable("MOONHARVEST_AUTO_DEMO"), "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("MOONHARVEST_AUTO_DEMO"), "true", StringComparison.OrdinalIgnoreCase))
        {
            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(600);
                await StartDemoMode();
            });
        }
    }

    private void DashboardPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _isPageActive = false;
        // Keep camera service running globally so user can switch menus without tearing down the stream.
        // Only stop this page's timers and telemetry binding.
        UnsubscribeFromTelemetry();
        UnsubscribeFromGeofenceMonitor();
        // Leave camera handlers attached: events are idempotent and FrameReceived updates the cached
        // VideoStreamControl which is re-used when the page is reactivated from the page cache.
        // Detaching + reattaching creates a gap where frames are dropped and the control appears frozen.
        _imuTimer.Stop();
        _missionTimer.Stop();
    }

    private void OnTimelineChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(RenderIncidentTimeline);
    }

    private void ResumeVideoRendering()
    {
        // When returning to the dashboard, the VideoStreamControl keeps its _currentFrame in memory
        // because we never called ClearFrame(). But the overlay state may have drifted. Force the
        // canvas visible and invalidate so the cached frame paints immediately instead of showing
        // a blank panel until the next Python frame arrives.
        try
        {
            DashboardVideoStream?.HideOverlay();
            DashboardVideoStream?.EnsureStreamingVisible();
        }
        catch
        {
        }
    }

    private void DashboardPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RebuildGridOverlay();
    }

    // ================ TELEMETRY ================

    private void SubscribeToTelemetry()
    {
        if (_flightViewModel == null) return;
        _flightViewModel.PropertyChanged -= OnFlightViewModelPropertyChanged;
        _flightViewModel.PropertyChanged += OnFlightViewModelPropertyChanged;
        RewireTelemetry(_flightViewModel.Telemetry);
    }

    private void UnsubscribeFromTelemetry()
    {
        if (_flightViewModel == null) return;
        _flightViewModel.PropertyChanged -= OnFlightViewModelPropertyChanged;
        if (_telemetry != null)
        {
            _telemetry.PropertyChanged -= OnTelemetryPropertyChanged;
        }
        _telemetry = null;
    }

    private void RewireTelemetry(TelemetryData? nextTelemetry)
    {
        if (ReferenceEquals(_telemetry, nextTelemetry)) return;
        if (_telemetry != null) _telemetry.PropertyChanged -= OnTelemetryPropertyChanged;
        _telemetry = nextTelemetry;
        if (_telemetry != null) _telemetry.PropertyChanged += OnTelemetryPropertyChanged;
    }

    private void OnFlightViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FlightViewModel.TelemetryData) ||
            e.PropertyName == nameof(FlightViewModel.Telemetry) ||
            e.PropertyName == nameof(FlightViewModel.IsConnected))
        {
            RewireTelemetry(_flightViewModel?.Telemetry);
            ScheduleDashboardRefresh();
        }

        if (e.PropertyName == nameof(FlightViewModel.IsConnected))
        {
            var connected = _flightViewModel?.IsConnected == true;
            if (connected && !_isLiveRunning)
            {
                _ = StartLiveModeAsync();
            }
            else if (!connected && _isLiveRunning)
            {
                _ = StopLiveModeAsync();
            }
        }
    }

    private void OnTelemetryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _telemetryFrameCount++;
        ScheduleDashboardRefresh();
    }

    /// <summary>
    /// Coalesces many telemetry PropertyChanged events into at most one UI refresh per
    /// <see cref="UiRefreshIntervalMs"/>. Without this the dashboard re-renders 30+ times
    /// per MAVLink packet which saturates the UI thread.
    /// </summary>
    private void ScheduleDashboardRefresh()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastUiRefresh).TotalMilliseconds >= UiRefreshIntervalMs)
        {
            _lastUiRefresh = now;
            DispatcherQueue.TryEnqueue(RefreshDashboard);
            return;
        }
        if (_uiRefreshScheduled) return;
        _uiRefreshScheduled = true;
        var delay = Math.Max(1, UiRefreshIntervalMs - (int)(now - _lastUiRefresh).TotalMilliseconds);
        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _uiRefreshScheduled = false;
                _lastUiRefresh = DateTime.UtcNow;
                RefreshDashboard();
            });
        });
    }

    // Cached last-rendered values to skip redundant XAML text updates.
    private string? _lastAltitudeText;
    private string? _lastSpeedText;
    private string? _lastHeadingText;
    private string? _lastGpsText;
    private string? _lastBatteryText;
    private string? _lastModeText;
    private string? _lastAltSpdHudText;
    private string? _lastHdgModeText;
    private string? _lastHeadingPillText;
    private string? _lastFpsHudText;
    private string? _lastGpsHudText;
    private string? _lastMissionProgressText;
    private string? _lastWaypointProgressText;
    private string? _lastMavLinkStatusText;
    private bool? _lastConnected;
    private double? _lastMissionProgressValue;

    private void RefreshDashboard()
    {
        if (_flightViewModel == null) return;

        var telemetry = _flightViewModel.Telemetry;
        var connected = _flightViewModel.IsConnected;

        var altitude = telemetry?.Altitude ?? 0.0;
        var speed = telemetry != null
            ? (telemetry.Speed > 0 ? telemetry.Speed : telemetry.GroundSpeed)
            : 0.0;

        var battery = telemetry?.BatteryPercent is > 0
            ? telemetry.BatteryPercent
            : telemetry?.BatteryRemaining ?? 0;
        battery = Math.Clamp(battery, 0, 100);
        var voltage = telemetry?.BatteryVoltage ?? 0;

        var heading = telemetry?.Heading ?? 0;
        if (_isDemoRunning && _hasDemoImu)
        {
            altitude = _demoAltitudeMeters;
            speed = _demoSpeedMetersPerSecond;
            heading = _demoYawDeg;
        }
        var flightMode = (telemetry?.FlightMode.ToString() ?? (connected ? "AUTO" : "STANDBY")).ToUpperInvariant();
        var satCount = telemetry?.SatelliteCount ?? 0;
        var hdop = telemetry?.HDOP ?? 0;

        SetTextIfChanged(MavLinkStatusText, connected ? "UDP 14550 · Online" : "Disconnected", ref _lastMavLinkStatusText);
        if (_lastConnected != connected)
        {
            MavLinkStatusPill.Style = (Style)(connected ? Resources["PillSuccessStyle"] : Resources["PillCriticalStyle"]);
            _lastConnected = connected;
        }

        if (!connected && !_isDemoRunning)
        {
            SetTextIfChanged(TelemetryAltitudeText, "-- m", ref _lastAltitudeText);
            SetTextIfChanged(TelemetrySpeedText, "-- m/s", ref _lastSpeedText);
            SetTextIfChanged(TelemetryHeadingText, "--°", ref _lastHeadingText);
            SetTextIfChanged(TelemetryGpsText, "No Fix · 0 sats", ref _lastGpsText);
            SetTextIfChanged(TelemetryBatteryText, "--% · --V", ref _lastBatteryText);
            SetTextIfChanged(TelemetryModeText, "STANDBY", ref _lastModeText);

            SetTextIfChanged(AltSpdHudText, "ALT -- m · SPD -- m/s", ref _lastAltSpdHudText);
            SetTextIfChanged(HdgModeText, "HDG --° · MODE STANDBY", ref _lastHdgModeText);
            SetTextIfChanged(HeadingPillText, "HDG --°", ref _lastHeadingPillText);
        }
        else
        {
            SetTextIfChanged(TelemetryAltitudeText, $"{altitude:F1} m", ref _lastAltitudeText);
            SetTextIfChanged(TelemetrySpeedText, $"{speed:F1} m/s", ref _lastSpeedText);
            SetTextIfChanged(TelemetryHeadingText, $"{heading:F0}°", ref _lastHeadingText);
            SetTextIfChanged(TelemetryGpsText, $"RTK fix · {satCount} sats · HDOP {hdop:F1}", ref _lastGpsText);
            SetTextIfChanged(TelemetryBatteryText,
                _isDemoRunning
                    ? $"{(int)_demoBattery}% · {_demoBatteryVoltage:F1}V"
                    : $"{(int)battery}% · {voltage:F1}V",
                ref _lastBatteryText);
            SetTextIfChanged(TelemetryModeText, flightMode, ref _lastModeText);

            SetTextIfChanged(AltSpdHudText, $"ALT {altitude:F0} m · SPD {speed:F1} m/s", ref _lastAltSpdHudText);
            SetTextIfChanged(HdgModeText, $"HDG {heading:F0}° · MODE {flightMode}", ref _lastHdgModeText);
            SetTextIfChanged(HeadingPillText, $"HDG {heading:F0}°", ref _lastHeadingPillText);
        }
        // During demo, FpsHudText is owned by InjectDemoDetectionUI.
        // Skip the override here so it doesn't revert to "active/standby" on each telemetry tick.
        if (!_isDemoRunning)
            SetTextIfChanged(FpsHudText, _aiOn ? $"YOLOv8 · {(connected ? "active" : "standby")}" : "YOLO paused", ref _lastFpsHudText);

        var missionProgress = MissionProgressCalculator.Calculate(
            telemetry,
            _mapViewModel?.Waypoints,
            _mapViewModel?.WaypointRadius ?? 2.0);
        int demoCycleTicks = Math.Max(1, (_demoWaypoints.Length - 1) * 2 * _demoTicksPerSegment);
        var progress = _isDemoRunning
            ? Math.Clamp(((_demoStep % demoCycleTicks) * 100.0 / demoCycleTicks), 0, 100)
            : (connected ? missionProgress.ProgressPercent : 0.0);
        if (_lastMissionProgressValue != progress)
        {
            MissionProgressBar.Value = progress;
            _lastMissionProgressValue = progress;
        }
        SetTextIfChanged(MissionProgressText, connected ? $"{progress:F0}%" : "0%", ref _lastMissionProgressText);
        if (_isDemoRunning)
        {
            int currentSeq = DemoWaypointStartSequence + _currentDemoWaypointIndex;
            int nextSeq = DemoWaypointStartSequence + _nextDemoWaypointIndex;
            SetTextIfChanged(WaypointProgressText, $"WP {currentSeq} → {nextSeq}", ref _lastWaypointProgressText);
        }
        else if (connected)
        {
            SetTextIfChanged(WaypointProgressText, $"WP {missionProgress.CurrentWaypoint}/{missionProgress.TotalWaypoints}", ref _lastWaypointProgressText);
        }
        else
        {
            SetTextIfChanged(WaypointProgressText, "WP - → -", ref _lastWaypointProgressText);
        }

        // Frames counter updates roughly once per telemetry packet, OK to always write since it changes.
        MissionFramesText.Text = $"{_telemetryFrameCount:N0} frames";

        UpdateMap(telemetry, connected);

        var gpsHud = telemetry != null && IsValidCoordinate(telemetry.Latitude, telemetry.Longitude)
            ? $"{telemetry.Latitude:F5}, {telemetry.Longitude:F5}"
            : $"{_centerLat:F5}, {_centerLon:F5}";
        SetTextIfChanged(GpsHudText, gpsHud, ref _lastGpsHudText);
        UpdateAlertCenter(telemetry, connected);
        UpdateOfflineAndReadiness(telemetry, connected);
        _ = CheckGeofenceMonitorAsync(telemetry, connected);
    }

    private void SubscribeToGeofenceMonitor()
    {
        if (_geofenceMonitor == null)
        {
            return;
        }

        _geofenceMonitor.GeofenceViolated -= OnGeofenceViolated;
        _geofenceMonitor.GeofenceRestored -= OnGeofenceRestored;
        _geofenceMonitor.GeofenceViolated += OnGeofenceViolated;
        _geofenceMonitor.GeofenceRestored += OnGeofenceRestored;
    }

    private void UnsubscribeFromGeofenceMonitor()
    {
        if (_geofenceMonitor == null)
        {
            return;
        }

        _geofenceMonitor.GeofenceViolated -= OnGeofenceViolated;
        _geofenceMonitor.GeofenceRestored -= OnGeofenceRestored;
    }

    private void OnGeofenceViolated(object? sender, GeofenceViolationEventArgs e)
    {
        // Surface the breach to the operator instead of swallowing it. Geofence enforcement
        // is only meaningful if the dashboard actually tells the operator they left the fence.
        DispatcherQueue.TryEnqueue(() =>
        {
            AddAlertRow($"GEOFENCE BREACH: {e.Message}", "critical");
            _timelineService?.Add("geofence", $"Geofence breach: {e.Message}", "critical");
        });
    }

    private void OnGeofenceRestored(object? sender, GeofenceViolationEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AddAlertRow("Geofence restored - vehicle back inside boundary", "info");
            _timelineService?.Add("geofence", "Vehicle re-entered geofence", "success");
        });
    }

    private async Task CheckGeofenceMonitorAsync(TelemetryData? telemetry, bool connected)
    {
        if (_geofenceMonitor == null || !connected || telemetry == null ||
            !IsValidCoordinate(telemetry.Latitude, telemetry.Longitude))
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastGeofenceMonitorCheck).TotalMilliseconds < 1000)
        {
            return;
        }

        _lastGeofenceMonitorCheck = now;
        await _geofenceMonitor.CheckGeofenceViolationAsync(new GeoCoordinate(
            telemetry.Latitude,
            telemetry.Longitude,
            telemetry.Altitude));
    }

    private static void SetTextIfChanged(TextBlock target, string value, ref string? cache)
    {
        if (cache == value) return;
        target.Text = value;
        cache = value;
    }

    private void UpdateMap(TelemetryData? telemetry, bool connected)
    {
        if (telemetry != null && IsValidCoordinate(telemetry.Latitude, telemetry.Longitude))
        {
            _centerLat = telemetry.Latitude;
            _centerLon = telemetry.Longitude;
        }

        if (!_mapInitialized && DashboardMapControl != null)
        {
            InitializeDashboardMap();
            _mapInitialized = true;
        }

        if (connected && DashboardMapControl != null)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastDashboardVehicleRender).TotalMilliseconds >= DashboardVehicleRenderIntervalMs)
            {
                DashboardMapControl.UpdateVehiclePosition(_centerLat, _centerLon);
                _lastDashboardVehicleRender = now;
            }
        }
    }

    /// <summary>
    /// Replicates the map setup the dedicated Map menu uses so the dashboard's embedded map
    /// looks and behaves the same way: identical tile provider, offline service binding,
    /// waypoint list, and geofence rendering sourced from <see cref="MapViewModel"/>.
    /// </summary>
    private void InitializeDashboardMap()
    {
        if (DashboardMapControl == null) return;

        // Sync tile provider from MapViewModel (mirrors whatever user selected in Map page)
        SyncTileProviderFromViewModel();

        try
        {
            if (_offlineMapService != null)
            {
                DashboardMapControl.SetOfflineMapService(_offlineMapService);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[DashboardPage] Failed to bind offline map service");
        }

        // Use Karawang demo center unless we have live telemetry
        var initialLat = IsValidCoordinate(_centerLat, _centerLon) && (_centerLat != -6.9175) ? _centerLat : DemoFieldLat;
        var initialLon = IsValidCoordinate(_centerLat, _centerLon) && (_centerLon != 107.6191) ? _centerLon : DemoFieldLon;
        DashboardMapControl.SetCenter(initialLat, initialLon, 15);

        // Sync follow-vehicle state
        DashboardMapControl.SetFollowVehicle(_mapViewModel?.IsFollowing ?? false);

        SyncWaypointsFromViewModel();
        SyncGeofenceFromViewModel();
        SubscribeToMapViewModel();
    }

    private void SubscribeToMapViewModel()
    {
        if (_mapViewModel == null || _mapViewModelSubscribed) return;
        _mapViewModel.PropertyChanged += OnMapViewModelPropertyChanged;
        _mapViewModel.Waypoints.CollectionChanged += OnMapViewModelWaypointsChanged;
        _mapViewModelSubscribed = true;
    }

    private void UnsubscribeFromMapViewModel()
    {
        if (_mapViewModel == null || !_mapViewModelSubscribed) return;
        _mapViewModel.PropertyChanged -= OnMapViewModelPropertyChanged;
        _mapViewModel.Waypoints.CollectionChanged -= OnMapViewModelWaypointsChanged;
        _mapViewModelSubscribed = false;
    }

    private void OnMapViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MapViewModel.VehiclePosition):
                var pos = _mapViewModel?.VehiclePosition;
                if (pos != null && IsValidCoordinate(pos.Latitude, pos.Longitude))
                {
                    _centerLat = pos.Latitude;
                    _centerLon = pos.Longitude;
                    var headingDeg = _telemetry?.Heading ?? 0;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        DashboardMapControl?.UpdateVehiclePosition(pos.Latitude, pos.Longitude);
                        HeadingPillText.Text = $"HDG {headingDeg:F0}°";
                    });
                }
                break;
            case nameof(MapViewModel.IsGeofenceActive):
            case nameof(MapViewModel.GeofenceCenterLat):
            case nameof(MapViewModel.GeofenceCenterLon):
            case nameof(MapViewModel.GeofenceRadius):
            case nameof(MapViewModel.GeofenceType):
                DispatcherQueue.TryEnqueue(SyncGeofenceFromViewModel);
                break;
            case nameof(MapViewModel.MapProvider):
                DispatcherQueue.TryEnqueue(SyncTileProviderFromViewModel);
                break;
            case nameof(MapViewModel.IsFollowing):
                DispatcherQueue.TryEnqueue(() =>
                {
                    DashboardMapControl?.SetFollowVehicle(_mapViewModel?.IsFollowing ?? false);
                });
                break;
        }
    }

    private void OnMapViewModelWaypointsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(SyncWaypointsFromViewModel);
    }

    private void SyncWaypointsFromViewModel()
    {
        if (DashboardMapControl == null) return;
        DashboardMapControl.ClearWaypoints();
        if (_mapViewModel == null || _mapViewModel.Waypoints.Count == 0)
        {
            // No waypoints — show empty map just like Map menu does.
            return;
        }
        foreach (var wp in _mapViewModel.Waypoints.OrderBy(w => w.Sequence))
        {
            DashboardMapControl.AddWaypointMarker(wp.Sequence, wp.Latitude, wp.Longitude, wp.Altitude, wp.Command.ToString());
        }
    }

    private void SyncTileProviderFromViewModel()
    {
        if (DashboardMapControl == null || _mapViewModel == null) return;
        var provider = _mapViewModel.MapProvider?.ToLowerInvariant() switch
        {
            "arcgisimagery" => SkiaMapControl.MapTileProvider.ArcGISImagery,
            "arcgistopographic" => SkiaMapControl.MapTileProvider.ArcGISTopographic,
            "arcgisstreetmap" => SkiaMapControl.MapTileProvider.ArcGISStreetMap,
            "googlemap" => SkiaMapControl.MapTileProvider.GoogleMap,
            "googlesatellite" => SkiaMapControl.MapTileProvider.GoogleSatellite,
            "googleterrain" => SkiaMapControl.MapTileProvider.GoogleTerrain,
            "googlehybrid" => SkiaMapControl.MapTileProvider.GoogleHybrid,
            "openstreetmap" => SkiaMapControl.MapTileProvider.OpenStreetMap,
            _ => SkiaMapControl.MapTileProvider.ArcGISTopographic
        };
        DashboardMapControl.SetTileProvider(provider);
    }

    private void SyncGeofenceFromViewModel()
    {
        if (DashboardMapControl == null) return;
        if (_mapViewModel == null || !_mapViewModel.IsGeofenceActive)
        {
            // No geofence active — clear it, same as Map menu.
            DashboardMapControl.SetGeofence(true, 0, 0, 0);
            return;
        }

        if (_mapViewModel.GeofenceType == HarvestmoonGCS.Core.Models.GeofenceType.Circular)
        {
            DashboardMapControl.SetGeofence(true,
                _mapViewModel.GeofenceCenterLat,
                _mapViewModel.GeofenceCenterLon,
                _mapViewModel.GeofenceRadius);
        }
        else if (_mapViewModel.GeofenceVertices.Count >= 3)
        {
            var vertices = _mapViewModel.GeofenceVertices.Select(v => (v.Lat, v.Lon)).ToList();
            DashboardMapControl.SetGeofence(false, 0, 0, 0, vertices);
        }
    }

    // ================ CAMERA ================

    private void AttachCameraHandlers()
    {
        if (_cameraService == null || _cameraHandlersAttached) return;
        _cameraService.FrameReceived += OnDashboardFrameReceived;
        _cameraService.StreamingStatusChanged += OnDashboardCameraStreamingChanged;
        _cameraService.ConnectionError += OnDashboardCameraError;
        if (_cameraService is PythonCameraService pcs)
            pcs.ClassificationSummaryChanged += OnClassifyDetectionData;
        if (_runcamService != null)
        {
            _runcamService.FrameReceived += OnDashboardFrameReceived;
            _runcamService.StatusChanged += OnRuncamStatusChanged;
        }
        _cameraHandlersAttached = true;
    }

    private void DetachCameraHandlers()
    {
        if (_cameraService == null || !_cameraHandlersAttached) return;
        _cameraService.FrameReceived -= OnDashboardFrameReceived;
        _cameraService.StreamingStatusChanged -= OnDashboardCameraStreamingChanged;
        _cameraService.ConnectionError -= OnDashboardCameraError;
        if (_cameraService is PythonCameraService pcs)
            pcs.ClassificationSummaryChanged -= OnClassifyDetectionData;
        if (_runcamService != null)
        {
            _runcamService.FrameReceived -= OnDashboardFrameReceived;
            _runcamService.StatusChanged -= OnRuncamStatusChanged;
        }
        _cameraHandlersAttached = false;
    }

    /// <summary>RunCam connect/disconnect → samakan indikator video dashboard.</summary>
    private void OnRuncamStatusChanged(object? sender, Core.Services.RuncamConnectionStatus status)
    {
        bool live = status is Core.Services.RuncamConnectionStatus.CameraOnly
            or Core.Services.RuncamConnectionStatus.FullyConnected;
        OnDashboardCameraStreamingChanged(this, live || (_cameraService?.IsStreaming ?? false));
    }

    private void OnClassifyDetectionData(object? sender, string dataJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            // Python emits float percentages (e.g. 53.2) — GetInt32() would throw; use GetDouble()
            int healthy = 0, stress = 0, drought = 0, bareSoil = 0;
            if (root.TryGetProperty("classes", out var cls))
            {
                healthy = ReadClassPercent(cls, "Healthy", "healthy_crop", "Lush Green", "lush_green");
                stress = ReadClassPercent(cls, "Stress", "stressed_crop", "Inconsistent Growth", "inconsistent_growth");
                drought = ReadClassPercent(cls, "Drought", "Drought/Severe Stress", "drought_stress", "Disease");
                bareSoil = ReadClassPercent(cls, "Bare Soil", "BareSoil", "bare_soil", "Bare Soil / Gap", "Pest");
            }
            int total = healthy + stress + drought + bareSoil;
            double liveFhi = root.TryGetProperty("fhi", out var fhiProp) && fhiProp.TryGetDouble(out var fVal) ? fVal : -1.0;
            if (total == 0 && liveFhi < 0) return;
            _classifyRealData = (healthy, stress, drought, bareSoil, Math.Max(total, 1), liveFhi);
        }
        catch { }
    }

    private static int ReadClassPercent(System.Text.Json.JsonElement classes, params string[] names)
    {
        foreach (var name in names)
        {
            if (classes.TryGetProperty(name, out var value) && value.TryGetDouble(out var number))
            {
                return (int)Math.Round(number);
            }
        }

        return 0;
    }

    /// <summary>
    /// Path to derr.mp4 at project root.
    /// </summary>
    private static string ResolveDerpVideoPath()
    {
        // Try several locations where derr.mp4 might exist
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "derr.mp4"),
            Path.Combine(Directory.GetCurrentDirectory(), "derr.mp4"),
            Path.Combine(AppContext.BaseDirectory, "derr.mp4"),
        };

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }
        return candidates[0]; // fallback even if doesn't exist
    }

    private async Task StartDashboardCameraAsync()
    {
        if (_cameraService == null)
        {
            DashboardVideoStream?.ShowStatus("Camera service not available", false);
            return;
        }

        // If already streaming (e.g. user came back from another menu), just resume the visual.
        if (_cameraService.IsStreaming)
        {
            ResumeVideoRendering();
            return;
        }

        // Do NOT auto-start camera/video on dashboard load.
        // User must click "Start Demo" to play derr.mp4 with YOLO detections.
        DashboardVideoStream?.ShowStatus("Kamera standby · hubungkan UAV untuk mulai survey", false);
    }

    // Throttle UI frame rendering so very high-FPS camera sources don't saturate the UI thread.
    private const int VideoFrameIntervalMs = 50; // ~20 FPS — reduce UI thread load
    private DateTime _lastVideoFrameRender = DateTime.MinValue;

    private void OnDashboardFrameReceived(object? sender, byte[] frameData)
    {
        // Skip all work when the page isn't visible. The camera keeps streaming globally
        // so frames will resume when the user returns to the dashboard.
        if (!_isPageActive) return;

        // VideoStreamControl.UpdateFrame already marshals to the UI thread internally;
        // avoid a redundant DispatcherQueue.TryEnqueue hop here.
        var now = DateTime.UtcNow;
        if ((now - _lastVideoFrameRender).TotalMilliseconds < VideoFrameIntervalMs)
        {
            return;
        }
        _lastVideoFrameRender = now;
        _lastFrameData = frameData;

        // On first frame from classify stream, hide the loading overlay
        var classifyActive = (_cameraService as PythonCameraService)?.IsClassificationStream == true;
        if (classifyActive && !_classifyFirstFrameReceived)
        {
            _classifyFirstFrameReceived = true;
            _classifyFrameStart = now;
            _classifyFrameCount = 0;
            DispatcherQueue.TryEnqueue(() => { try { DashboardVideoStream?.HideOverlay(); } catch { } });
        }

        // Track FPS for classify stream
        if (classifyActive && _classifyFirstFrameReceived)
        {
            _classifyFrameCount++;
            var elapsed = (now - _classifyFrameStart).TotalSeconds;
            if (elapsed >= 3.0 && _classifyFrameCount > 0)
            {
                _classifyFps = _classifyFrameCount / elapsed;
                _classifyFrameCount = 0;
                _classifyFrameStart = now;
            }
        }

        DashboardVideoStream?.UpdateFrame(frameData);
        if (_videoRecorderService?.IsRecording == true)
        {
            _videoRecorderService.WriteFrame(frameData);
        }

        // When demo is playing a pre-annotated video (already has detection boxes baked in),
        // skip all HSV/YOLO analysis to avoid double overlay on top of existing annotations
        if (_useFusedOnlyDemoSummary)
        {
            DashboardVideoStream?.SetDetectionOverlays(Array.Empty<VideoDetectionOverlay>());
            return;
        }

        bool shouldAnalyze = _aiOn || (VegToggleSwitch?.IsOn == true);
        if (!classifyActive && shouldAnalyze && !_analysisRunning)
        {
            _lastAnalysisTime = DateTime.Now;
            _ = AnalyzeDashboardFrameAsync(frameData);
        }
    }

    private async Task AnalyzeDashboardFrameAsync(byte[] frameData)
    {
        _analysisRunning = true;
        try
        {
            // Fast path: decode+detect directly on the frame bytes, no disk I/O.
            var boxes = await _harvestFunctionalService!.DetectInFrameAsync(frameData);

            DispatcherQueue.TryEnqueue(() =>
            {
                // Discard if both YOLO and Vegetation Overlay are turned off
                bool shouldProcess = _aiOn || (VegToggleSwitch?.IsOn == true);
                if (!shouldProcess)
                {
                    return;
                }

                _lastDetectionCount = boxes.Count;
                if (boxes.Count > 0 && (DateTime.UtcNow - _lastYoloTimelineEvent).TotalSeconds >= 5)
                {
                    _lastYoloTimelineEvent = DateTime.UtcNow;
                    string modeStr = _aiOn ? "YOLO" : "OpenCV Fallback";
                    _timelineService?.Add("yolo", $"{modeStr} detection: {boxes.Count} object(s), avg confidence {boxes.Average(b => b.Confidence) * 100:F0}%", "info");
                    _ = _harvestFunctionalService?.SaveLatestYoloScreenshotAsync(frameData);
                }
                var avgConf = boxes.Count > 0 ? boxes.Average(b => (double)b.Confidence) : 0;
                _lastConfidence = avgConf;

                // During demo, InjectDemoDetectionUI owns confidence/FPS display — skip override.
                if (!_isDemoRunning)
                {
                    DetectionCountText.Text = _aiOn ? $"{boxes.Count} det" : "OpenCV active";
                    DetectionsListTitle.Text = _aiOn ? $"Live Detections ({boxes.Count})" : "OpenCV Health Zones";
                    ConfAvgText.Text = $"Conf avg {avgConf * 100:F0}%";
                    SummaryConfidenceText.Text = $"{avgConf * 100:F0}%";
                    MissionDetectionsText.Text = boxes.Count.ToString();

                    // Calculate real inference FPS
                    _inferenceCount++;
                    var now = DateTime.UtcNow;
                    var elapsed = (now - _lastFpsCalcTime).TotalSeconds;
                    if (elapsed >= 1.0)
                    {
                        _currentInferenceFps = _inferenceCount / elapsed;
                        _inferenceCount = 0;
                        _lastFpsCalcTime = now;
                    }
                    FpsHudText.Text = _aiOn
                        ? $"YOLOv8 · {_currentInferenceFps:F0} FPS"
                        : $"OpenCV · {_currentInferenceFps:F0} FPS";

                    // Update top bar AI FPS indicator
                    UpdateTopBarAiFps(_currentInferenceFps);
                }

                if (_aiOn)
                {
                    DashboardVideoStream?.SetDetectionOverlays(boxes
                        .Where(b => b.Confidence * 100 >= _confThreshold)
                        .Select(b => (demo: MapToDemo(b.ClassName), b))
                        .Where(t => !string.IsNullOrEmpty(t.demo))
                        .Select(t => new VideoDetectionOverlay
                        {
                            Label = t.demo,
                            Confidence = t.b.Confidence,
                            X = t.b.X,
                            Y = t.b.Y,
                            Width = t.b.Width,
                            Height = t.b.Height
                        }));
                }
                else
                {
                    // OpenCV fallback mode: clear normal target overlays, but we can draw severe/critical stress bounding boxes!
                    DashboardVideoStream?.SetDetectionOverlays(boxes
                        .Where(b => MapToDemo(b.ClassName) == "Drought/Severe Stress" || MapToDemo(b.ClassName) == "Bare Soil / Gap")
                        .Select(b => new VideoDetectionOverlay
                        {
                            Label = MapToDemo(b.ClassName),
                            Confidence = b.Confidence,
                            X = b.X,
                            Y = b.Y,
                            Width = b.Width,
                            Height = b.Height
                        }));
                }

                if (!_isDemoRunning)
                {
                    RenderDetectionListFromBoxes(boxes);
                    UpdateSummaryFromDetections(boxes);

                    // Update the vegetation overlay gradient wash!
                    int lushGreen = 0, inconsistent = 0, drought = 0, bareSoil = 0;
                    foreach (var box in boxes)
                    {
                        switch (MapToDemo(box.ClassName))
                        {
                            case "Inconsistent Growth": inconsistent++; break;
                            case "Drought/Severe Stress": drought++; break;
                            case "Bare Soil / Gap": bareSoil++; break;
                            case "Lush Green": lushGreen++; break;
                        }
                    }
                    int total = Math.Max(1, lushGreen + inconsistent + drought + bareSoil);
                    UpdateVegetationOverlay(
                        lushGreen * 100.0 / total,
                        inconsistent * 100.0 / total,
                        drought * 100.0 / total,
                        bareSoil * 100.0 / total
                    );
                }
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[DashboardPage] Analyze error");
        }
        finally
        {
            _analysisRunning = false;
        }
    }

    private void RenderDetectionListFromBoxes(List<HarvestFunctionalService.HarvestDetectionBox> boxes)
    {
        DetectionsListStack.Children.Clear();
        var filtered = boxes
            .Where(b => b.Confidence * 100 >= _confThreshold)
            .OrderByDescending(b => b.Confidence)
            .Take(20)
            .ToList();

        if (filtered.Count == 0)
        {
            DetectionsListStack.Children.Add(new TextBlock
            {
                Text = "No detections above threshold.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184))
            });
            return;
        }

        foreach (var det in filtered)
        {
            var demoName = MapToDemo(det.ClassName);
            if (string.IsNullOrEmpty(demoName)) continue;
            DetectionsListStack.Children.Add(BuildCropDetectionRow(demoName, det.Confidence));
        }
    }

    private void RenderDetectionList(HarvestFunctionalService.HarvestAnalysisResult result)
    {
        DetectionsListStack.Children.Clear();
        var filtered = result.DetectionBoxes
            .Where(b => b.Confidence * 100 >= _confThreshold)
            .OrderByDescending(b => b.Confidence)
            .Take(20)
            .ToList();

        if (filtered.Count == 0)
        {
            DetectionsListStack.Children.Add(new TextBlock
            {
                Text = "No detections above threshold.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184))
            });
            return;
        }

        foreach (var det in filtered)
        {
            var demoName = MapToDemo(det.ClassName);
            if (string.IsNullOrEmpty(demoName)) continue;
            DetectionsListStack.Children.Add(BuildCropDetectionRow(demoName, det.Confidence));
        }
    }

    private void UpdateSummaryAndNdvi(HarvestFunctionalService.HarvestAnalysisResult result)
    {
        // Hitung dari DetectionBoxes dengan 4 class v5 datasheet.
        int lushGreen = 0, inconsistent = 0, drought = 0, bareSoil = 0;
        foreach (var box in result.DetectionBoxes.Where(b => b.Confidence * 100 >= _confThreshold))
        {
            switch (MapToDemo(box.ClassName))
            {
                case "Inconsistent Growth": inconsistent++; break;
                case "Drought/Severe Stress": drought++; break;
                case "Bare Soil / Gap": bareSoil++; break;
                case "Lush Green": lushGreen++; break;
            }
        }

        int total           = Math.Max(1, lushGreen + inconsistent + drought + bareSoil);
        var lushGreenPct    = lushGreen    * 100.0 / total;
        var inconsistentPct = inconsistent * 100.0 / total;
        var droughtPct      = drought      * 100.0 / total;
        var bareSoilPct     = bareSoil     * 100.0 / total;

        SummaryHealthyCount.Text = $"{lushGreenPct:F0}%";
        if (SummaryWellIrrigatedCount != null) SummaryWellIrrigatedCount.Text = "0%";
        SummaryStressCount.Text  = $"{inconsistentPct:F0}%";
        if (SummarySoilIssuesCount != null) SummarySoilIssuesCount.Text = "0%";
        SummaryDiseaseCount.Text = $"{droughtPct:F0}%";
        SummaryPestCount.Text    = $"{bareSoilPct:F0}%";

        if (SummaryHealthyBar        != null) SummaryHealthyBar.Value        = lushGreenPct;
        if (SummaryWellIrrigatedBar  != null) SummaryWellIrrigatedBar.Value  = 0;
        if (SummaryStressBar         != null) SummaryStressBar.Value         = inconsistentPct;
        if (SummarySoilIssuesBar     != null) SummarySoilIssuesBar.Value     = 0;
        if (SummaryDiseaseBar        != null) SummaryDiseaseBar.Value        = droughtPct;
        if (SummaryPestBar           != null) SummaryPestBar.Value           = bareSoilPct;

        var dominant = DominantV5Label(lushGreenPct, inconsistentPct, droughtPct, bareSoilPct);
        if (SummaryDominantText != null) SummaryDominantText.Text = dominant;

        var priority = drought > 0 || bareSoil > 0 ? "High" : inconsistent > 0 ? "Medium" : "Low";
        SummaryPriorityText.Text = priority;
        SummaryPriorityPill.Style = (Style)(priority switch
        {
            "High" => Resources["PillCriticalStyle"],
            "Medium" => Resources["PillWarningStyle"],
            _ => Resources["PillSuccessStyle"]
        });
        SummaryPriorityText.Foreground = new SolidColorBrush(priority switch
        {
            "High" => Color.FromArgb(255, 185, 28, 28),
            "Medium" => Color.FromArgb(255, 178, 107, 0),
            _ => Color.FromArgb(255, 6, 95, 70)
        });
    }

    /// <summary>Remap detector class name to one of the 4 v5 datasheet labels.</summary>
    private static string MapToDemo(string className)
    {
        // Normalize: collapse whitespace, remove special chars, lowercase
        var key = System.Text.RegularExpressions.Regex.Replace(
            className.ToLowerInvariant().Trim(), @"[\s/\-]+", "_");
        return key switch
        {
            "healthy" or "healthy_crop" or "lush_green" or "well_irrigated" => "Lush Green",
            "stress" or "stressed_crop" or "inconsistent_growth" => "Inconsistent Growth",
            "drought" or "drought_stress" or "drought_severe_stress"
                or "drought__severe_stress" or "disease" => "Drought/Severe Stress",
            "bare_soil" or "bare_soil_gap" or "bare_soil__gap"
                or "soil_issues" or "pest" => "Bare Soil / Gap",
            _ => string.Empty
        };
    }

    private static Color ColorForLabel(string label)
    {
        var lower = label.ToLowerInvariant();
        if (lower.Contains("drought"))      return Color.FromArgb(255, 230,  81,   0);  // orange
        if (lower.Contains("bare") || lower.Contains("soil")) return Color.FromArgb(255,  93,  64,  55);  // brown
        if (lower.Contains("inconsistent")) return Color.FromArgb(255, 178, 107,   0);  // amber
        return Color.FromArgb(255, 46, 125, 50);                                         // green (lush)
    }

    private static (string Emoji, string Label, string Description) CropClassInfo(string className)
    {
        var lower = className.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
        if (lower.Contains("drought"))      return ("🟠", "Drought/Severe Stress", "Severe water stress");
        if (lower.Contains("bare") || lower.Contains("soil")) return ("🟤", "Bare Soil / Gap", "Bare or missing crop");
        if (lower.Contains("inconsistent")) return ("🟡", "Inconsistent Growth", "Stressed / uneven crop");
        return                                     ("🌿", "Lush Green",          "Healthy vegetation");
    }

    private static string DominantV5Label(double lushGreenPct, double inconsistentPct, double droughtPct, double bareSoilPct)
    {
        var max = Math.Max(Math.Max(lushGreenPct, inconsistentPct), Math.Max(droughtPct, bareSoilPct));
        if (Math.Abs(max - droughtPct) < 0.001) return $"Drought/Severe Stress {droughtPct:F0}%";
        if (Math.Abs(max - bareSoilPct) < 0.001) return $"Bare Soil / Gap {bareSoilPct:F0}%";
        if (Math.Abs(max - inconsistentPct) < 0.001) return $"Inconsistent Growth {inconsistentPct:F0}%";
        return $"Lush Green {lushGreenPct:F0}%";
    }

    private Grid BuildCropDetectionRow(string className, double confidence)
    {
        var (emoji, label, desc) = CropClassInfo(className);
        var color = ColorForLabel(className);

        var row = new Grid { Padding = new Thickness(6, 5, 6, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Emoji + color dot composite
        var emojiBlock = new TextBlock
        {
            Text = emoji,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(emojiBlock, 0);
        row.Children.Add(emojiBlock);

        // Label + description stacked
        var labelStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labelStack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
        });
        labelStack.Children.Add(new TextBlock
        {
            Text = desc,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 100, 116, 139)),
        });
        Grid.SetColumn(labelStack, 1);
        row.Children.Add(labelStack);

        // Confidence
        var confBlock = new TextBlock
        {
            Text = $"{confidence * 100:F0}%",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(confBlock, 2);
        row.Children.Add(confBlock);

        return row;
    }

    /// <summary>
    /// Updates Analysis Summary panel from actual YOLO detections.
    /// Maps COCO class names to agriculture categories for display.
    /// </summary>
    private void UpdateSummaryFromDetections(List<HarvestmoonGCS.Services.HarvestFunctionalService.HarvestDetectionBox> boxes)
    {
        if (boxes.Count == 0)
        {
            SummaryHealthyCount.Text  = "0%";
            SummaryStressCount.Text   = "0%";
            SummaryDiseaseCount.Text  = "0%";
            SummaryPestCount.Text     = "0%";
            if (SummaryHealthyBar        != null) SummaryHealthyBar.Value        = 0;
            if (SummaryWellIrrigatedBar  != null) SummaryWellIrrigatedBar.Value  = 0;
            if (SummaryStressBar         != null) SummaryStressBar.Value         = 0;
            if (SummarySoilIssuesBar     != null) SummarySoilIssuesBar.Value     = 0;
            if (SummaryDiseaseBar        != null) SummaryDiseaseBar.Value        = 0;
            if (SummaryPestBar           != null) SummaryPestBar.Value           = 0;
            if (SummaryWellIrrigatedCount != null) SummaryWellIrrigatedCount.Text = "0%";
            if (SummarySoilIssuesCount    != null) SummarySoilIssuesCount.Text   = "0%";
            SummaryPriorityText.Text = "Standby";
            if (SummaryDominantText != null) SummaryDominantText.Text = "—";
            return;
        }

        int drought = 0, bareSoil = 0, inconsistent = 0, lushGreen = 0;
        foreach (var box in boxes.Where(b => b.Confidence * 100 >= _confThreshold))
        {
            switch (MapToDemo(box.ClassName))
            {
                case "Inconsistent Growth": inconsistent++; break;
                case "Drought/Severe Stress": drought++; break;
                case "Bare Soil / Gap": bareSoil++; break;
                case "Lush Green": lushGreen++; break;
            }
        }

        int total           = Math.Max(1, lushGreen + inconsistent + drought + bareSoil);
        var lushGreenPct    = lushGreen   * 100 / total;
        var inconsistentPct = inconsistent * 100 / total;
        var droughtPct      = drought     * 100 / total;
        var bareSoilPct     = bareSoil    * 100 / total;

        SummaryHealthyCount.Text = $"{lushGreenPct:F0}%";
        SummaryStressCount.Text  = $"{inconsistentPct:F0}%";
        SummaryDiseaseCount.Text = $"{droughtPct:F0}%";
        SummaryPestCount.Text    = $"{bareSoilPct:F0}%";
        // WellIrrigated = proxy for LushGreen (most hydrated/healthy zones)
        // SoilIssues    = proxy for BareSoil  (exposed/degraded soil)
        if (SummaryWellIrrigatedCount != null) SummaryWellIrrigatedCount.Text = $"{lushGreenPct:F0}%";
        if (SummarySoilIssuesCount    != null) SummarySoilIssuesCount.Text    = $"{bareSoilPct:F0}%";

        if (SummaryHealthyBar        != null) SummaryHealthyBar.Value        = lushGreenPct;
        if (SummaryWellIrrigatedBar  != null) SummaryWellIrrigatedBar.Value  = lushGreenPct;
        if (SummaryStressBar         != null) SummaryStressBar.Value         = inconsistentPct;
        if (SummarySoilIssuesBar     != null) SummarySoilIssuesBar.Value     = bareSoilPct;
        if (SummaryDiseaseBar        != null) SummaryDiseaseBar.Value        = droughtPct;
        if (SummaryPestBar           != null) SummaryPestBar.Value           = bareSoilPct;

        var dominant = DominantV5Label(lushGreenPct, inconsistentPct, droughtPct, bareSoilPct);
        if (SummaryDominantText != null) SummaryDominantText.Text = dominant;

        var priority = drought > 0 || bareSoil > 0 ? "High" : inconsistent > 0 ? "Medium" : "Low";
        SummaryPriorityText.Text = priority;
        SummaryPriorityPill.Style = (Style)(priority switch
        {
            "High" => Resources["PillCriticalStyle"],
            "Medium" => Resources["PillWarningStyle"],
            _ => Resources["PillSuccessStyle"]
        });
        SummaryPriorityText.Foreground = new SolidColorBrush(priority switch
        {
            "High" => Color.FromArgb(255, 185, 28, 28),
            "Medium" => Color.FromArgb(255, 178, 107, 0),
            _ => Color.FromArgb(255, 6, 95, 70)
        });
    }

    private void OnDashboardCameraStreamingChanged(object? sender, bool isStreaming)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (isStreaming)
            {
                try { DashboardVideoStream?.HideOverlay(); } catch { }
            }
            else
            {
                DashboardVideoStream?.ShowStatus("Camera stopped", false);
            }
        });
    }

    private void OnDashboardCameraError(object? sender, string error)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            DashboardVideoStream?.ShowStatus(error, false);
        });
    }

    // ================ OVERLAY / AI TOGGLES ================

    private void SyncYoloStateFromService()
    {
        if (_harvestFunctionalService != null)
        {
            _aiOn = _harvestFunctionalService.IsYoloOptionEnabled;
            if (YoloToggleSwitch != null) YoloToggleSwitch.IsOn = _aiOn;
            if (VegToggleSwitch != null) VegToggleSwitch.IsOn = _harvestFunctionalService.IsVegOverlayEnabled;
            if (ImuToggleSwitch != null) ImuToggleSwitch.IsOn = _harvestFunctionalService.IsImuOverlayEnabled;

            // Subscribe to external toggle changes (e.g. sidebar Yolo toggle)
            _harvestFunctionalService.YoloOptionChanged -= OnServiceYoloOptionChanged;
            _harvestFunctionalService.YoloOptionChanged += OnServiceYoloOptionChanged;
        }
    }

    private void OnServiceYoloOptionChanged(object? sender, bool enabled)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _aiOn = enabled;
            YoloToggleSwitch.IsOn = enabled;
            AiStatusText.Text = enabled ? "AI ON" : "AI Paused";
            AiStatusPill.Style = (Style)(enabled ? Resources["PillSuccessStyle"] : Resources["PillCriticalStyle"]);
            PauseAiLabel.Text = enabled ? "Pause AI" : "Resume AI";

            if (!enabled)
            {
                DashboardVideoStream?.SetDetectionOverlays(Array.Empty<VideoDetectionOverlay>());
                DetectionCountText.Text = "0 det";
                DetectionsListTitle.Text = "Live Detections (0)";
                ConfAvgText.Text = "Conf avg 0%";
                FpsHudText.Text = "YOLO paused";
                DetectionsListStack.Children.Clear();
                DetectionsListStack.Children.Add(new TextBlock
                {
                    Text = "YOLO dimatikan.",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184))
                });
            }
        });
    }

    private void YoloToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _aiOn = YoloToggleSwitch.IsOn;
        _harvestFunctionalService?.SetYoloOptionEnabled(_aiOn);
        AiStatusText.Text = _aiOn ? "AI ON" : "AI Paused";
        AiStatusPill.Style = (Style)(_aiOn ? Resources["PillSuccessStyle"] : Resources["PillCriticalStyle"]);
        PauseAiLabel.Text = _aiOn ? "Pause AI" : "Resume AI";
        if (!_aiOn)
        {
            // Immediately clear all bounding boxes from the video overlay
            DashboardVideoStream?.SetDetectionOverlays(Array.Empty<VideoDetectionOverlay>());
            DetectionCountText.Text = "0 det";
            DetectionsListTitle.Text = "Live Detections (0)";
            ConfAvgText.Text = "Conf avg 0%";
            FpsHudText.Text = "YOLO paused";
            DetectionsListStack.Children.Clear();
            DetectionsListStack.Children.Add(new TextBlock
            {
                Text = "YOLO dimatikan.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 148, 163, 184))
            });
        }

        // If an HSV stream is currently running, restart it with the new YOLO setting
        var pcs = _cameraService as PythonCameraService;
        if (pcs?.IsClassificationStream == true && !string.IsNullOrWhiteSpace(_currentHsvSource))
        {
            _ = RestartHsvStreamWithYoloAsync(_aiOn);
        }
    }

    private async Task RestartHsvStreamWithYoloAsync(bool yoloEnabled)
    {
        if (_cameraService == null || string.IsNullOrWhiteSpace(_currentHsvSource)) return;

        try
        {
            Serilog.Log.Information("[DashboardPage] Restarting HSV stream with YOLO={Enabled}", yoloEnabled);
            await _cameraService.StopCameraAsync();
            bool started = await _cameraService.StartHsvStreamAsync(
                source: _currentHsvSource!,
                modelPath: _currentHsvModelPath,
                maxFps: _currentHsvMaxFps,
                showOverlay: _currentHsvShowOverlay,
                demo: _currentHsvDemo,
                playbackRate: _currentHsvPlaybackRate,
                yoloEnabled: yoloEnabled);
            if (!started)
            {
                Serilog.Log.Warning("[DashboardPage] Failed to restart HSV stream with YOLO={Enabled}", yoloEnabled);
            }
            else
            {
                Serilog.Log.Information("[DashboardPage] HSV stream restarted successfully with YOLO={Enabled}", yoloEnabled);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[DashboardPage] Error restarting HSV stream with YOLO={Enabled}", yoloEnabled);
        }
    }

    private void VegToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_harvestFunctionalService != null && VegToggleSwitch != null)
        {
            _harvestFunctionalService.IsVegOverlayEnabled = VegToggleSwitch.IsOn;
        }
        ApplyLayerVisibility();
    }
    private void ImuToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (ImuToggleSwitch != null)
        {
            _showImuHud = ImuToggleSwitch.IsOn;
            if (_harvestFunctionalService != null)
            {
                _harvestFunctionalService.IsImuOverlayEnabled = _showImuHud;
            }
            ImuHudPanel.Visibility = _showImuHud ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ApplyLayerVisibility()
    {
        VegetationOverlayLayer.Visibility = VegToggleSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
        GridOverlayCanvas.Visibility = Visibility.Collapsed;
        HeatmapOverlayLayer.Visibility = Visibility.Collapsed;
        ImuHudPanel.Visibility = ImuToggleSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConfThresholdSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        _confThreshold = (int)Math.Round(e.NewValue);
        ConfThresholdValue.Text = $"{_confThreshold}%";
    }

    /// <summary>
    /// Load MinConfidence from AISettings so slider matches what was saved in AISettingsPage.
    /// </summary>
    private Task LoadConfThresholdFromSettingsAsync()
    {
        try
        {
            var settingsService = App.Current.Services.GetService<ISettingsService>();
            if (settingsService == null) return Task.CompletedTask;
            // GetSetting returns object for "AISettings" key; cast to AISettings
            var aiObj = settingsService.GetSetting<object>("AISettings", null!);
            if (aiObj is not HarvestmoonGCS.Core.Models.AI.AISettings ai) return Task.CompletedTask;
            // AISettings.Analysis.MinConfidence is 0..1, _confThreshold is 0..100
            int loaded = (int)Math.Round(ai.Analysis.MinConfidence * 100);
            loaded = Math.Clamp(loaded, 10, 95);
            _confThreshold = loaded;
            if (ConfThresholdValue != null) ConfThresholdValue.Text = $"{_confThreshold}%";
            if (FindName("ConfThresholdSlider") is Microsoft.UI.Xaml.Controls.Slider s)
                s.Value = loaded;
        }
        catch { /* settings not yet saved — keep default 40% */ }
        return Task.CompletedTask;
    }

    private DateTime _lastGridRebuild = DateTime.MinValue;
    private double _lastGridWidth, _lastGridHeight;

    private void RebuildGridOverlay()
    {
        if (GridOverlayCanvas == null) return;
        var width = VideoRoot?.ActualWidth ?? 0;
        var height = VideoRoot?.ActualHeight ?? 0;
        if (width <= 0 || height <= 0) return;

        // Only rebuild when size actually changed significantly or on first draw.
        if (_gridOverlayDrawn && Math.Abs(_lastGridWidth - width) < 8 && Math.Abs(_lastGridHeight - height) < 8)
        {
            return;
        }
        _lastGridWidth = width;
        _lastGridHeight = height;

        GridOverlayCanvas.Children.Clear();
        var brush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        const double step = 48;
        for (double x = 0; x <= width; x += step)
        {
            var line = new XamlLine
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = height,
                Stroke = brush, StrokeThickness = 1
            };
            GridOverlayCanvas.Children.Add(line);
        }
        for (double y = 0; y <= height; y += step)
        {
            var line = new XamlLine
            {
                X1 = 0, Y1 = y, X2 = width, Y2 = y,
                Stroke = brush, StrokeThickness = 1
            };
            GridOverlayCanvas.Children.Add(line);
        }
        _gridOverlayDrawn = true;
    }

    // ================ HUD ACTIONS ================

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        _isRecording = !_isRecording;
        RecordLabel.Text = _isRecording ? "Stop" : "Record";
        RecStatusText.Text = _isRecording ? "REC" : "IDLE";
        if (_isRecording)
        {
            try
            {
                // MyVideos is empty on some Linux desktops and on Android, which produced a
                // path rooted at an empty string and made the recorder fail. Fall back to a
                // directory that is guaranteed to exist.
                var videoRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                if (string.IsNullOrWhiteSpace(videoRoot))
                {
                    videoRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                }
                if (string.IsNullOrWhiteSpace(videoRoot))
                {
                    videoRoot = Path.Combine(Path.GetTempPath(), "HarvestmoonGCS");
                }

                Directory.CreateDirectory(videoRoot);
                var path = Path.Combine(videoRoot, $"harvest_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
                var started = _videoRecorderService != null && await _videoRecorderService.StartRecordingAsync(path);
                _timelineService?.Add("recording", started ? $"Video recording started: {path}" : "Video recording failed to start", started ? "success" : "warning");
                if (!started)
                {
                    // Roll the button state back so the next tap retries instead of trying to stop.
                    _isRecording = false;
                    RecordLabel.Text = "Record";
                    RecStatusText.Text = "IDLE";
                }
            }
            catch (Exception ex)
            {
                _timelineService?.Add("recording", $"Video recording error: {ex.Message}", "warning");
            }
        }
        else
        {
            try
            {
                var path = _videoRecorderService?.CurrentRecordingPath ?? string.Empty;
                if (_videoRecorderService != null)
                {
                    await _videoRecorderService.StopRecordingAsync();
                }

                if (!string.IsNullOrWhiteSpace(path))
                {
                    await _harvestFunctionalService!.AttachVideoRecordingToLatestReportAsync(path);
                    _timelineService?.Add("recording", $"Video recording saved: {path}", "success");
                }
            }
            catch (Exception ex)
            {
                _timelineService?.Add("recording", $"Video recording stop error: {ex.Message}", "warning");
            }
        }

        UpdateOfflineAndReadiness(_flightViewModel?.Telemetry, _flightViewModel?.IsConnected == true);
    }

    private async void SnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"harvest_snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            DashboardVideoStream?.SaveCurrentFrame(path);
            if (_lastFrameData != null && _harvestFunctionalService != null)
            {
                var saved = await _harvestFunctionalService.SaveLatestYoloScreenshotAsync(_lastFrameData);
                _timelineService?.Add("snapshot", $"YOLO screenshot saved: {saved}", "success");
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[DashboardPage] Snapshot failed");
            AddAlertRow($"Snapshot failed: {ex.Message}", "error");
        }
    }

    private void PauseAiButton_Click(object sender, RoutedEventArgs e)
    {
        YoloToggleSwitch.IsOn = !YoloToggleSwitch.IsOn;
    }

    public async void ToggleDemoMode()
    {
        try
        {
            bool shouldStart;
            lock (_modeLock)
            {
                if (_isDemoRunning) { shouldStart = false; }
                else { shouldStart = true; }
            }
            if (!shouldStart) { await StopDemoModeAsync(); return; }
            await StartDemoMode();
        }
        catch (Exception ex)
        {
            // Public async void entry point — an escaping exception would kill the process.
            Serilog.Log.Error(ex, "[DashboardPage] ToggleDemoMode failed");
            AddAlertRow($"Demo mode error: {ex.Message}", "error");
        }
    }

    private void StartDemoButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleDemoMode();
    }

    private async Task StartDemoMode()
    {
        bool shouldStart;
        lock (_modeLock)
        {
            if (_isDemoRunning || _harvestFunctionalService == null) { shouldStart = false; }
            else { _isDemoRunning = true; shouldStart = true; }
        }
        if (!shouldStart) return;
        
        _demoStep = 0;
        ReadinessChecklistPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        MissionMetadataPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        if (VegetationOverlayLayer != null)
        {
            VegetationOverlayLayer.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        }

        _demoRng = new Random();
        _demoAltitudeBase   = _currentScenario.CruiseAltitudeMeters + (_demoRng.NextDouble() - 0.5) * 1.0;
        _demoSpeedBase      = _currentScenario.CruiseSpeedMps + (_demoRng.NextDouble() - 0.5) * 0.4;
        _demoBatteryStart   = _currentScenario.InitialBatteryPct - _demoRng.NextDouble() * 2.0;
        _demoSatCount       = _currentScenario.SatelliteCount;
        _demoBatteryVoltage = 12.3 + _demoRng.NextDouble() * 0.6;
        _demoBattery = _demoBatteryStart;
        _demoHeading = _currentScenario.BearingDeg;
        SetDemoButtonState(running: true);
        _harvestFunctionalService.SetDemoModeActive(true);

        if (_currentScenario != null)
        {
            if (_mapViewModel != null)
            {
                _mapViewModel.Waypoints.Clear();
                foreach (var wp in _currentScenario.Waypoints) _mapViewModel.Waypoints.Add(wp);
                _mapViewModel.GeofenceCenterLat = _currentScenario.FieldCenterLat;
                _mapViewModel.GeofenceCenterLon = _currentScenario.FieldCenterLon;
                _mapViewModel.GeofenceRadius    = _currentScenario.GeofenceRadiusMeters;
                _mapViewModel.GeofenceType      = GeofenceType.Circular;
                _mapViewModel.IsGeofenceActive  = true;
            }
            if (_waypointService != null && _currentScenario.Waypoints.Count > 0)
            {
                _ = _waypointService.ReplaceWaypointsAsync(_currentScenario.Waypoints);
            }
            if (DashboardMapControl != null)
            {
                DashboardMapControl.ClearWaypoints();
                foreach (var wp in _currentScenario.Waypoints)
                {
                    DashboardMapControl.AddWaypointMarker(wp.Sequence, wp.Latitude, wp.Longitude, wp.Altitude, "WAYPOINT");
                }
                DashboardMapControl.SetGeofence(true, _currentScenario.FieldCenterLat, _currentScenario.FieldCenterLon, _currentScenario.GeofenceRadiusMeters);
                DashboardMapControl.SetFollowVehicle(true);
            }
            _geofenceService?.SetGeofenceCenter(_currentScenario.FieldCenterLat, _currentScenario.FieldCenterLon);
            _geofenceService?.SetGeofenceRadius(_currentScenario.GeofenceRadiusMeters);
            _geofenceService?.SetGeofenceActive(true);
        }

        try
        {
            var videoPath = ResolveDetectedVideoPath();
            var videoName = Path.GetFileName(videoPath ?? _currentScenario?.VideoFileName ?? "");
            _useFusedOnlyDemoSummary = (!string.IsNullOrWhiteSpace(videoName) &&
                (videoName.Contains("detected", StringComparison.OrdinalIgnoreCase) ||
                 videoName.Contains("fused", StringComparison.OrdinalIgnoreCase)))
#if __ANDROID__
                || true
#endif
                ;

#if __ANDROID__
            if (VegetationOverlayLayer != null)
            {
                VegetationOverlayLayer.Visibility = Visibility.Collapsed;
            }

            if (_androidDemoDecoder == null || !_androidDemoDecoder.IsRunning)
            {
                if (_androidDemoDecoder != null)
                {
                    await _androidDemoDecoder.StopAsync();
                    try { _androidDemoDecoder.Dispose(); } catch { }
                    _androidDemoDecoder = null;
                }

                var androidContext = Android.App.Application.Context;
                var assetName = _currentScenario?.Id switch
                {
                    "scenario-15hst"     => "demo_videos/stream_v7c_final_detected.mp4",
                    "scenario-irrigated" => "demo_videos/YDXJ_fused_only_detected.mp4",
                    "scenario-drought"   => "demo_videos/YDX_burned_detected.mp4",
                    _                    => "demo_videos/stream_multisector_60s_detected.mp4"
                };
                string videoSource = (!string.IsNullOrWhiteSpace(_customVideoPath) && File.Exists(_customVideoPath)) ? _customVideoPath : assetName;

                DashboardVideoStream?.SetHighQualityMode(true);
                DashboardVideoStream?.HideOverlay();

                _androidDemoDecoder = new HarvestmoonGCS.Platforms.Android.Services.AndroidDemoVideoDecoder(androidContext, videoSource);
                _androidDemoDecoder.FrameDecoded += (_, jpeg) => OnDashboardFrameReceived(this, jpeg);
                await _androidDemoDecoder.StartAsync();
                _timelineService?.Add("camera", $"Video: {Path.GetFileName(videoSource)} (Android)", "success");
            }
            else
            {
                DashboardVideoStream?.SetHighQualityMode(true);
                DashboardVideoStream?.HideOverlay();
            }
#else
            if (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath) && _cameraService != null)
            {
                var previewFrame = await Task.Run(() => ExtractFirstFrameBytes(videoPath));
                if (previewFrame.Length > 0)
                {
                    _lastFrameData = previewFrame;
                    // Enable high-quality bilinear rendering for demo video (crisp, no blur/pixelation)
                    DashboardVideoStream?.SetHighQualityMode(true);
                    DashboardVideoStream?.UpdateFrame(previewFrame);
                    DashboardVideoStream?.HideOverlay();
                    FpsHudText.Text = $"{_currentScenario.ShortDescription} · loading detection...";
                }

                // Determine if video is pre-annotated (already has detection boxes drawn on it)
                var videoFileName = Path.GetFileName(videoPath);
                bool isPreAnnotated = videoFileName.Contains("detected", StringComparison.OrdinalIgnoreCase) ||
                                      videoFileName.Contains("fused", StringComparison.OrdinalIgnoreCase);

                // For pre-annotated videos: play as-is WITHOUT adding HSV/YOLO overlay on top
                // For raw videos: add YOLO/HSV overlay as normal
                bool yoloEnabled = isPreAnnotated ? false : (_harvestFunctionalService?.IsYoloOptionEnabled ?? false);
                bool showOverlay = !isPreAnnotated; // don't overlay on pre-annotated video

                await _cameraService.StopCameraAsync();
                var modelPath = PythonCameraService.ResolveHealthModelPath();
                bool started = await _cameraService.StartHsvStreamAsync(
                    source: videoPath,
                    modelPath: modelPath,
                    maxFps: 15f,
                    showOverlay: showOverlay,
                    demo: true,
                    playbackRate: 1.0f,
                    yoloEnabled: yoloEnabled);

                if (started)
                {
                    // Save HSV stream config for potential restart on YOLO toggle
                    _currentHsvSource = videoPath;
                    _currentHsvModelPath = modelPath;
                    _currentHsvDemo = true;
                    _currentHsvPlaybackRate = 1.0f;
                    _currentHsvMaxFps = 15f;
                    _currentHsvShowOverlay = showOverlay;
                }

                if (!started)
                {
                    await _cameraService.StartCameraAsync(videoPath);
                }
                _timelineService?.Add("camera", $"Video: {Path.GetFileName(videoPath)} · HSV{(yoloEnabled ? "+YOLO" : "")} · 15 FPS loop", "success");
            }
            else
            {
                throw new FileNotFoundException($"Demo video not found: {string.Join(", ", DemoVideoPathCandidates)}");
            }
#endif

            // 2. AI state: respect current toggle (don't force on)
            _aiOn = _harvestFunctionalService?.IsYoloOptionEnabled ?? false;
            if (YoloToggleSwitch != null) YoloToggleSwitch.IsOn = _aiOn;

            // 3. Geofence around current scenario field
            _geofenceService?.SetGeofenceCenter(_currentScenario.FieldCenterLat, _currentScenario.FieldCenterLon);
            _geofenceService?.SetGeofenceRadius(_currentScenario.GeofenceRadiusMeters);
            _geofenceService?.SetGeofenceActive(true);

            // 4. MAVLink connection
            _mavLinkService?.SimulateConnection(true);
            if (_flightViewModel != null) _flightViewModel.IsConnected = true;

            // 4a. Seed scenario waypoints into IWaypointService and MapViewModel
            if (_waypointService != null && _currentScenario.Waypoints.Count > 0)
            {
                await _waypointService.ReplaceWaypointsAsync(_currentScenario.Waypoints);
            }
            if (_mapViewModel != null)
            {
                _mapViewModel.Waypoints.Clear();
                foreach (var wp in _currentScenario.Waypoints)
                {
                    _mapViewModel.Waypoints.Add(wp);
                }
                _mapViewModel.GeofenceCenterLat = _currentScenario.FieldCenterLat;
                _mapViewModel.GeofenceCenterLon = _currentScenario.FieldCenterLon;
                _mapViewModel.GeofenceRadius    = _currentScenario.GeofenceRadiusMeters;
                _mapViewModel.GeofenceType      = GeofenceType.Circular;
                _mapViewModel.IsGeofenceActive  = true;
            }

            // 4b. Center map on scenario field and follow UAV
            if (DashboardMapControl != null)
            {
                DashboardMapControl.ClearWaypoints();
                foreach (var wp in _currentScenario.Waypoints)
                {
                    DashboardMapControl.AddWaypointMarker(wp.Sequence, wp.Latitude, wp.Longitude, wp.Altitude, "WAYPOINT");
                }
                DashboardMapControl.SetGeofence(true, _currentScenario.FieldCenterLat, _currentScenario.FieldCenterLon, _currentScenario.GeofenceRadiusMeters);
                DashboardMapControl.SetCenter(_currentScenario.FieldCenterLat, _currentScenario.FieldCenterLon, _currentScenario.MapZoomLevel);
                DashboardMapControl.SetFollowVehicle(true);
            }

            // 5. Initial telemetry
            InjectDemoTelemetry(0);

            // 6. Seed timeline
            _timelineService?.Add("connected", "MAVLink connected · ",     "success");
            _timelineService?.Add("tlog",      "Auto TLOG recorder armed",                         "success");
            _timelineService?.Add("armed",     $"Armed · AUTO mode · alt {_currentScenario.CruiseAltitudeMeters:F0}m · survey {_currentScenario.MissionId}", "success");
            _timelineService?.Add("waypoint",  $"Survey aktif · {_currentScenario.Waypoints.Count} WP · Bearing {_currentScenario.BearingDeg:F0}° · {_currentScenario.AreaName}", "info");
            RenderIncidentTimeline();
            UpdateAlertCenter(_flightViewModel?.Telemetry, true);
            UpdateDemoFieldReport();

            // 7. Reset UI counters
            _demoStep = 0;
            _currentDemoWaypointIndex = 0;
            _nextDemoWaypointIndex = 1;
            _lastDetectionCount = 0;
            _lastConfidence = 0;
            DetectionCountText.Text = "0 det";
            int initialFps = 15;
            FpsHudText.Text = $"{_currentScenario.ShortDescription} · {initialFps} FPS";
            if (MissionIdText != null) MissionIdText.Text = _currentScenario.MissionId;
            if (_useFusedOnlyDemoSummary)
            {
                ApplyDemoVideoAnalysisSummary();
            }
            AddAlertRow($"UAV survey active · {_currentScenario.AreaName} · AUTO mode", "info");

            // 8. Start tick timer (1 s interval)
            _demoTimer.Tick -= OnDemoTick;
            _demoTimer.Tick += OnDemoTick;
            _demoTimer.Start();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[DashboardPage] StartDemoMode failed");
            _timelineService?.Add("error", $"Error: {ex.Message}", "critical");
            await StopDemoModeAsync();
        }
    }

    private async Task StopDemoModeAsync()
    {
        _isDemoRunning = false;

        // Stop Python HSV stream process
        if (_cameraService != null) await _cameraService.StopCameraAsync();

#if __ANDROID__
        if (_androidDemoDecoder != null)
        {
            await _androidDemoDecoder.StopAsync();
            try { _androidDemoDecoder?.Dispose(); } catch { }
            _androidDemoDecoder = null;
        }
#endif
        _harvestFunctionalService?.SetDemoModeActive(false);
        ReadinessChecklistPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        MissionMetadataPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        _classifyRealData = null;
        _classifyFirstFrameReceived = false;
        _useFusedOnlyDemoSummary = false;
        // Restore fast nearest-neighbour rendering for live camera stream
        DashboardVideoStream?.SetHighQualityMode(false);
        _classifyFps = 0;
        _classifyFrameCount = 0;
        _classifyFrameStart = DateTime.MinValue;
        _hasDemoImu = false;
        _demoTimer.Stop();
        _demoTimer.Tick -= OnDemoTick;
        _geofenceService?.SetGeofenceActive(false);
        _mavLinkService?.SimulateConnection(false);
        if (_flightViewModel != null) _flightViewModel.IsConnected = false;

        // Restore MapViewModel and map control to idle state
        if (_mapViewModel != null)
        {
            _mapViewModel.Waypoints.Clear();
            _mapViewModel.IsGeofenceActive = false;
        }
        DashboardMapControl?.SetFollowVehicle(false);
        DashboardMapControl?.ClearWaypoints();
        SetDemoButtonState(running: false);
        UpdateModeBadge("STANDBY");
        _timelineService?.Add("disconnected", "Demo mode dihentikan", "warning");
        AddAlertRow("Demo mode dihentikan", "warning");
        UpdateAlertCenter(_flightViewModel?.Telemetry, false);
        UpdateFarmerReport(0, 0, 0, 0, 0);
        RenderIncidentTimeline();
    }

    private void OnDemoTick(object? sender, object e)
    {
        if (!_isDemoRunning) return;
        _demoStep++;
        InjectDemoTelemetry(_demoStep);
        InjectDemoTimelineEvents(_demoStep);
        InjectDemoDetectionUI(_demoStep);

        // Simulasi geofence check — cek apakah UAV keluar batas setiap 5 tick
        if (_demoStep % 5 == 0 && _flightViewModel?.Telemetry != null)
        {
            UpdateAlertCenter(_flightViewModel.Telemetry, true);
        }

        // Continuous round-trip survey loop (WP0 -> WPN -> WP0 -> ...)
        int oneWaySegs = Math.Max(1, _demoWaypoints.Length - 1);
        int totalCycleSegments = oneWaySegs * 2;
        int _demoLoopPeriod = totalCycleSegments * _demoTicksPerSegment;
        if (_demoStep >= _demoLoopPeriod) _demoStep = 0;
    }

    private int _currentDemoWaypointIndex;
    private int _nextDemoWaypointIndex = 1;

    private void InjectDemoTelemetry(int step)
    {
        if (_flightViewModel == null) return;

        // --- Continuous ping-pong interpolation along survey transect ---
        int oneWaySegs = Math.Max(1, _demoWaypoints.Length - 1);
        int totalCycleSegments = oneWaySegs * 2;
        int globalStep = step % (totalCycleSegments * _demoTicksPerSegment);
        int cycleSeg = globalStep / _demoTicksPerSegment;
        double t = (globalStep % _demoTicksPerSegment) / (double)_demoTicksPerSegment;

        double lat0, lon0, lat1, lon1;
        if (cycleSeg < oneWaySegs)
        {
                // Outbound survey leg (West to East)
                int idx = cycleSeg;
                _currentDemoWaypointIndex = idx;
                _nextDemoWaypointIndex = Math.Min(idx + 1, _demoWaypoints.Length - 1);
                (lat0, lon0) = _demoWaypoints[idx];
                (lat1, lon1) = _demoWaypoints[_nextDemoWaypointIndex];
            }
            else
            {
                // Inbound return leg (East to West)
                int idx = oneWaySegs - (cycleSeg - oneWaySegs);
                _currentDemoWaypointIndex = idx;
                _nextDemoWaypointIndex = Math.Max(0, idx - 1);
            (lat0, lon0) = _demoWaypoints[idx];
            (lat1, lon1) = _demoWaypoints[_nextDemoWaypointIndex];
        }

        // Linear interpolation — smooth continuous position without teleport
        double lat = lat0 + (lat1 - lat0) * t;
        double lon = lon0 + (lon1 - lon0) * t;

        // True bearing from current segment start → end
        double newHeading = GeodesicBearing(lat0, lon0, lat1, lon1);

        // Detect turns: heading change > 45° → UAV is banking
        double headingDelta = ((newHeading - _demoHeading + 540) % 360) - 180; // [-180, +180]
        bool isTurning      = Math.Abs(headingDelta) > 45;
        _demoHeading        = newHeading;

        // Altitude: gentle oscillation around ~60 m AGL to keep the demo stable and believable.
        double altitude     = _demoAltitudeBase + Math.Sin(step * 0.31) * 0.6;

        // Roll: gentle banking during turns, subtle waggle on straights
        double roll = isTurning
            ? Math.Sign(headingDelta) * 10.0
            : Math.Sin(step * 0.4) * 5.0;

        // Pitch: very subtle nose-down oscillation for level cruise flight feel
        double pitch = -2.5 + Math.Sin(step * 0.23) * 2.5;

        // Speed: slightly slower through turns, using randomized base
        double speedTurn = Math.Max(6.5, _demoSpeedBase - 1.8);
        double speed = isTurning ? speedTurn : _demoSpeedBase;

        // Vertical speed: tiny oscillation
        double verticalSpeed = Math.Sin(step * 0.5) * 0.15;

        // Smooth battery drain and voltage curve (3S LiPo: 12.36V - 12.54V)
        _demoBattery = Math.Max(84.0, _demoBatteryStart - (step % 120) * 0.05);
        _demoBatteryVoltage = 11.1 + (_demoBattery / 100.0) * 1.5;

        var telemetry = new TelemetryData
        {
            Latitude           = lat,
            Longitude          = lon,
            Altitude           = altitude,
            RelativeAltitude   = altitude,
            Heading            = newHeading,
            Speed              = speed,
            GroundSpeed        = speed,
            BatteryPercentage  = _demoBattery,
            BatteryRemaining   = (int)_demoBattery,
            BatteryVoltage     = _demoBatteryVoltage,
            SatelliteCount     = _demoSatCount,
            HDOP               = 0.4,
            GPSFixType         = 6,   // GPS_FIX_TYPE_RTK_FIXED (real MAVLink enum tops out at 8 — 15 was never a valid fix type)
            FlightMode         = HarvestmoonGCS.Core.Models.FlightMode.AUTO,
            IsArmed            = true,
            ThrottlePercent    = isTurning ? 60 : 68,
            Roll               = roll  * Math.PI / 180.0,
            Pitch              = pitch * Math.PI / 180.0,
            Yaw                = newHeading,
            VerticalSpeed      = verticalSpeed,
        };
        SetDemoImu(roll, pitch, newHeading, altitude, speed);
        _flightViewModel.UpdateTelemetry(telemetry);

        // Fire real MAVLink telemetry event so FlightPage, MissionPage etc. all respond
        _mavLinkService?.SimulateTelemetry(BuildDemoFlightData(telemetry));

        // Keep MapViewModel vehicle marker in sync so Map page shows UAV moving
        _mapViewModel?.UpdateVehiclePosition(lat, lon, altitude, (float)newHeading);
    }

    /// <summary>
    /// Computes the initial compass bearing from (lat1,lon1) to (lat2,lon2) in degrees [0,360).
    /// </summary>
    private static double GeodesicBearing(double lat1, double lon1, double lat2, double lon2)
    {
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double φ1   = lat1 * Math.PI / 180.0;
        double φ2   = lat2 * Math.PI / 180.0;
        double y    = Math.Sin(dLon) * Math.Cos(φ2);
        double x    = Math.Cos(φ1) * Math.Sin(φ2) - Math.Sin(φ1) * Math.Cos(φ2) * Math.Cos(dLon);
        return (Math.Atan2(y, x) * 180.0 / Math.PI + 360.0) % 360.0;
    }

    private static HarvestmoonGCS.Models.FlightData BuildDemoFlightData(TelemetryData t)
    {
        var fd = new HarvestmoonGCS.Models.FlightData
        {
            FlightMode      = t.FlightMode,
            IsArmed         = t.IsArmed,
            BatteryRemaining = (int)Math.Clamp(t.BatteryPercentage, 0, 100),
        };
        fd.GPS.Latitude  = (int)(t.Latitude * 1e7);
        fd.GPS.Longitude = (int)(t.Longitude * 1e7);
        fd.Altitude      = (int)t.Altitude;
        fd.AltitudeFloat = (float)t.Altitude;
        fd.Barometers    = (float)t.RelativeAltitude;
        fd.Speed         = (float)t.Speed;
        fd.GroundSpeed   = (float)(t.GroundSpeed > 0 ? t.GroundSpeed : t.Speed);
        fd.VerticalSpeed = (float)t.VerticalSpeed;
        fd.BatteryVolt   = (ushort)(t.BatteryVoltage * 1000); // V → mV
        fd.BatteryCurr   = (ushort)(t.BatteryCurrent * 10);   // A → dA
        fd.ThrottlePercent = t.ThrottlePercent;
        fd.Sats          = (byte)Math.Min(t.SatelliteCount, 255);
        fd.Hdop          = (ushort)(t.HDOP * 100);
        fd.Signal        = (byte)Math.Min(t.SignalStrength, 255);
        fd.IMU.Roll      = (float)(t.Roll * 180.0 / Math.PI);
        fd.IMU.Pitch     = (float)(t.Pitch * 180.0 / Math.PI);
        fd.IMU.Yaw       = (float)t.Heading;
        return fd;
    }

    private void InjectDemoTimelineEvents(int step)
    {
        int oneWaySegs = Math.Max(1, _demoWaypoints.Length - 1);
        int ticksPerSeg = Math.Max(1, _demoTicksPerSegment);
        int totalCycleSegments = oneWaySegs * 2;
        int totalCycleTicks = totalCycleSegments * ticksPerSeg;
        int cycleStep = step % totalCycleTicks;

         // Dynamic sector & waypoint events at segment transitions
        if (cycleStep % ticksPerSeg == 0)
        {
            int segIndex = cycleStep / ticksPerSeg;
            if (segIndex < oneWaySegs)
            {
                if (segIndex < _currentScenario.TimelineSectorEvents.Count)
                {
                    _timelineService?.Add("waypoint", _currentScenario.TimelineSectorEvents[segIndex], "success");
                }
                else
                {
                    int wpNum = segIndex + 1;
                    char sectorLetter = (char)('A' + segIndex);
                    _timelineService?.Add("waypoint", $"WP {wpNum} → Memasuki Sektor {sectorLetter} · Outbound aktif", "success");
                }
            }
            else if (segIndex == oneWaySegs)
            {
                int wpEnd = oneWaySegs + 1;
                _timelineService?.Add("waypoint", $"WP {wpEnd} → Titik putar balik {_currentScenario.AreaName} tercapai · Banking turnaround", "warning");
            }
            else
            {
                int wpNum = oneWaySegs - (segIndex - oneWaySegs) + 1;
                char sectorLetter = (char)('A' + Math.Max(0, wpNum - 1));
                _timelineService?.Add("waypoint", $"WP {wpNum} → Re-inspeksi Sektor {sectorLetter} · Inbound sweep", "info");
            }
        }
        else if (cycleStep == 2)
        {
            _timelineService?.Add("yolo", $"YOLO inference aktif · Survei {_currentScenario.MissionId} ({_demoVideoDurationSeconds:F0}s)", "success");
        }
        else if (cycleStep == Math.Max(1, ticksPerSeg / 2))
        {
            _timelineService?.Add("yolo", $"Status kanopi {_currentScenario.AreaName} terpindai real-time (Target FHI {_currentScenario.TargetFhi:F0}%)", "info");
        }
        else if (cycleStep == totalCycleTicks - 2)
        {
            _timelineService?.Add("waypoint", $"WP 1 → Putaran survei {_currentScenario.MissionId} selesai · Siklus loop aktif", "success");
        }

        if (step % 5 == 0 && step > 0) RenderIncidentTimeline();
    }

    // Rotating demo detection frames — proposal-aligned 4-class labels only
    private static readonly (string Class, double Conf)[][] DemoDetectionFrames =
    {
        new[] { ("Lush Green", 0.93), ("Lush Green", 0.88), ("Inconsistent Growth", 0.79) },
        new[] { ("Lush Green", 0.91), ("Inconsistent Growth", 0.82), ("Bare Soil / Gap", 0.74) },
        new[] { ("Lush Green", 0.96), ("Lush Green", 0.90), ("Inconsistent Growth", 0.77), ("Drought/Severe Stress", 0.73) },
        new[] { ("Drought/Severe Stress", 0.87), ("Inconsistent Growth", 0.81), ("Lush Green", 0.89) },
        new[] { ("Lush Green", 0.94), ("Inconsistent Growth", 0.76), ("Bare Soil / Gap", 0.71), ("Inconsistent Growth", 0.83) },
        new[] { ("Lush Green", 0.92), ("Lush Green", 0.88), ("Drought/Severe Stress", 0.80) },
        new[] { ("Inconsistent Growth", 0.85), ("Bare Soil / Gap", 0.78), ("Lush Green", 0.91) },
        new[] { ("Lush Green", 0.95), ("Lush Green", 0.89), ("Inconsistent Growth", 0.65), ("Drought/Severe Stress", 0.72) },
    };

    private void InjectDemoDetectionUI(int step)
    {
        double dLushGreen, dStress, dDrought, dBareSoil, avgConf;
        int count;

        // Use REAL Python detection data when classify stream has sent at least one result
        if (_classifyRealData.HasValue)
        {
            var r = _classifyRealData.Value;
            double tot = Math.Max(1, r.Total);
            dLushGreen = r.Healthy * 100.0 / tot;
            dStress    = r.Stress  * 100.0 / tot;
            dDrought   = r.Drought * 100.0 / tot;
            dBareSoil  = r.BareSoil * 100.0 / tot;
            count    = r.Total;
            avgConf  = 0.86; // fixed confidence avg 86% for demo

            // Build detection rows from real class counts
            DetectionsListStack.Children.Clear();
            if (r.Stress  > 0) DetectionsListStack.Children.Add(BuildCropDetectionRow("Inconsistent Growth", NormalizeDemoConfidence(0.84 + Math.Sin(step * 0.4) * 0.02)));
            if (r.Healthy > 0) DetectionsListStack.Children.Add(BuildCropDetectionRow("Lush Green",           NormalizeDemoConfidence(0.88 + Math.Sin(step * 0.2) * 0.02)));
            if (r.Drought > 0) DetectionsListStack.Children.Add(BuildCropDetectionRow("Drought/Severe Stress", NormalizeDemoConfidence(0.83 + Math.Sin(step * 0.5) * 0.02)));
            if (r.BareSoil > 0) DetectionsListStack.Children.Add(BuildCropDetectionRow("Bare Soil / Gap",      NormalizeDemoConfidence(0.82 + Math.Sin(step * 0.6) * 0.02)));
        }
        else
        {
            // Fall back to scenario detection frames
            var frames = _currentScenario.DetectionFrames;
            var frame = (frames != null && frames.Length > 0)
                ? frames[step % frames.Length]
                : DemoDetectionFrames[step % DemoDetectionFrames.Length];

            count   = frame.Length;
            avgConf = 0.85;

            double jitter = Math.Sin(step * 0.7) * 1.5;
            dLushGreen = Math.Clamp(_currentScenario.LushGreenPct + jitter * 0.5, 0, 100);
            dStress    = Math.Clamp(_currentScenario.InconsistentGrowthPct - jitter * 0.4, 0, 100);
            dDrought   = Math.Clamp(_currentScenario.DroughtPct + jitter * 0.1, 0, 100);
            dBareSoil  = Math.Clamp(_currentScenario.BareSoilPct + Math.Cos(step * 0.4) * 0.3, 0, 100);

            DetectionsListStack.Children.Clear();
            foreach (var (cls, conf) in frame)
                DetectionsListStack.Children.Add(BuildCropDetectionRow(cls, NormalizeDemoConfidence(conf)));
        }

        _lastDetectionCount = count;
        _lastConfidence = avgConf;

        DetectionCountText.Text  = $"{count} det";
        MissionDetectionsText.Text = ((_demoStep / 2) + count).ToString();
        ConfAvgText.Text         = $"Conf avg {avgConf * 100:F0}%";
        SummaryConfidenceText.Text = $"{avgConf * 100:F0}%";
        DetectionsListTitle.Text = $"Live Detections ({Math.Min(count, 4)})";

        int[] _fpsTable = { 15, 16, 15, 16, 15, 16, 15, 16, 15, 16 };
        int demofps = _fpsTable[step % _fpsTable.Length];
        FpsHudText.Text = $"{_currentScenario.ShortDescription} · {demofps} FPS";

        if (_useFusedOnlyDemoSummary)
        {
            dLushGreen = _currentScenario.LushGreenPct;
            dStress = _currentScenario.InconsistentGrowthPct;
            dDrought = _currentScenario.DroughtPct;
            dBareSoil = _currentScenario.BareSoilPct;
        }

        if (SummaryHealthyCount       != null) SummaryHealthyCount.Text       = $"{dLushGreen:F0}%";
        if (SummaryWellIrrigatedCount != null) SummaryWellIrrigatedCount.Text = "0%";
        if (SummaryStressCount        != null) SummaryStressCount.Text        = $"{dStress:F0}%";
        if (SummarySoilIssuesCount    != null) SummarySoilIssuesCount.Text    = "0%";
        if (SummaryDiseaseCount       != null) SummaryDiseaseCount.Text       = $"{dDrought:F0}%";
        if (SummaryPestCount          != null) SummaryPestCount.Text          = $"{dBareSoil:F0}%";
        if (SummaryHealthyBar        != null) SummaryHealthyBar.Value        = dLushGreen;
        if (SummaryWellIrrigatedBar  != null) SummaryWellIrrigatedBar.Value  = 0;
        if (SummaryStressBar         != null) SummaryStressBar.Value         = dStress;
        if (SummarySoilIssuesBar     != null) SummarySoilIssuesBar.Value     = 0;
        if (SummaryDiseaseBar        != null) SummaryDiseaseBar.Value        = dDrought;
        if (SummaryPestBar           != null) SummaryPestBar.Value           = dBareSoil;

        if (SummaryDominantText != null) SummaryDominantText.Text = DominantV5Label(dLushGreen, dStress, dDrought, dBareSoil);

        if (_useFusedOnlyDemoSummary)
        {
            UpdateDemoFieldReport();
        }
        else
        {
            var liveFhi = (_classifyRealData.HasValue && _classifyRealData.Value.Fhi > 0)
                ? _classifyRealData.Value.Fhi
                : _currentScenario.TargetFhi;
            UpdateFarmerReport(dLushGreen, dStress, dDrought, dBareSoil, 0, overrideFhi: liveFhi);
        }
        UpdateVegetationOverlay(dLushGreen, dStress, dDrought, dBareSoil);
        UpdateAlertCenter(_flightViewModel?.Telemetry, true);
    }

    private void ApplyDemoVideoAnalysisSummary()
    {
        var sc = _currentScenario;
        if (SummaryHealthyCount       != null) SummaryHealthyCount.Text       = $"{sc.LushGreenPct:F0}%";
        if (SummaryWellIrrigatedCount != null) SummaryWellIrrigatedCount.Text = "0%";
        if (SummaryStressCount        != null) SummaryStressCount.Text        = $"{sc.InconsistentGrowthPct:F0}%";
        if (SummarySoilIssuesCount    != null) SummarySoilIssuesCount.Text    = "0%";
        if (SummaryDiseaseCount       != null) SummaryDiseaseCount.Text       = $"{sc.DroughtPct:F0}%";
        if (SummaryPestCount          != null) SummaryPestCount.Text          = $"{sc.BareSoilPct:F0}%";
        if (SummaryHealthyBar         != null) SummaryHealthyBar.Value        = sc.LushGreenPct;
        if (SummaryWellIrrigatedBar   != null) SummaryWellIrrigatedBar.Value  = 0;
        if (SummaryStressBar          != null) SummaryStressBar.Value         = sc.InconsistentGrowthPct;
        if (SummarySoilIssuesBar      != null) SummarySoilIssuesBar.Value     = 0;
        if (SummaryDiseaseBar         != null) SummaryDiseaseBar.Value        = sc.DroughtPct;
        if (SummaryPestBar            != null) SummaryPestBar.Value           = sc.BareSoilPct;
        if (SummaryDominantText       != null) SummaryDominantText.Text       = DominantV5Label(
            sc.LushGreenPct,
            sc.InconsistentGrowthPct,
            sc.DroughtPct,
            sc.BareSoilPct);
        if (SummaryPriorityText != null)
        {
            SummaryPriorityText.Text = sc.PriorityLevel;
            SummaryPriorityText.Foreground = new SolidColorBrush(sc.PriorityLevel switch
            {
                "Optimal"       => Color.FromArgb(255, 6, 95, 70),
                "Good"          => Color.FromArgb(255, 22, 101, 52),
                "Caution"       => Color.FromArgb(255, 178, 107, 0),
                "Action Needed" => Color.FromArgb(255, 185, 28, 28),
                _               => Color.FromArgb(255, 178, 107, 0)
            });
        }
        if (SummaryPriorityPill != null)
        {
            SummaryPriorityPill.Style = (Style)(sc.PriorityLevel switch
            {
                "Optimal" or "Good" => Resources["PillSuccessStyle"],
                "Action Needed"     => Resources["PillCriticalStyle"],
                _                   => Resources["PillWarningStyle"]
            });
        }
        UpdateVegetationOverlay(
            sc.LushGreenPct,
            sc.InconsistentGrowthPct,
            sc.DroughtPct,
            sc.BareSoilPct);
    }

    private static double NormalizeDemoConfidence(double confidence)
    {
        return Math.Clamp(confidence, 0.81, 0.89);
    }

    private void UpdateDemoFieldReport()
    {
        if (!_isDemoRunning)
        {
            return;
        }

        var liveFhi = (_classifyRealData.HasValue && _classifyRealData.Value.Fhi > 0)
            ? _classifyRealData.Value.Fhi
            : _currentScenario.TargetFhi;

        UpdateFarmerReport(
            _currentScenario.LushGreenPct,
            _currentScenario.InconsistentGrowthPct,
            _currentScenario.DroughtPct,
            _currentScenario.BareSoilPct,
            0,
            overrideFhi: liveFhi);
    }

    private void UpdateFarmerReport(double healthyPct, double stressPct, double droughtPct, double bareSoilPct, double legacySoilPct, double overrideFhi = -1)
    {
        if (FarmerFhiScore == null || FarmerFhiLabel == null || FarmerReportStatusText == null)
        {
            return;
        }

        double fhi;
        if (overrideFhi >= 0)
        {
            fhi = overrideFhi;
        }
        else
        {
            var total = healthyPct + stressPct + droughtPct + bareSoilPct;
            if (total > 0)
            {
                var hNorm = healthyPct / total;
                var sNorm = stressPct / total;
                var dNorm = droughtPct / total;
                var bNorm = bareSoilPct / total;
                fhi = Math.Clamp(
                    (hNorm * 1.00 + sNorm * 0.77 + bNorm * 0.90 + dNorm * 0.20) * 100.0,
                    0,
                    100);
            }
            else
            {
                fhi = 0;
            }
        }

        FarmerFhiScore.Text = fhi <= 0 ? "--" : $"{fhi:F0}";

        // P3: FHI progress bar visual — update warna berdasarkan nilai
        if (FhiProgressBar != null)
        {
            FhiProgressBar.Value = fhi;
            FhiProgressBar.Foreground = new SolidColorBrush(fhi switch
            {
                >= 80 => Color.FromArgb(255, 22, 163, 74),   // hijau — excellent
                >= 60 => Color.FromArgb(255, 101, 163, 13),  // hijau muda — good
                >= 40 => Color.FromArgb(255, 234, 179, 8),   // kuning — caution
                _     => Color.FromArgb(255, 220, 38, 38)    // merah — critical
            });
        }
        if (FhiProgressLabel != null)
            FhiProgressLabel.Text = fhi <= 0 ? "0%" : $"{fhi:F0}%";
        FarmerFhiLabel.Text = fhi <= 0
            ? "Waiting for detection data from the YDXJ video..."
            : fhi >= 80
                ? "Field Health Index: Excellent. Most crop areas appear healthy."
                : fhi >= 60
                    ? "Field Health Index: Good. Several areas need monitoring."
                    : fhi >= 40
                        ? "Field Health Index: Moderate. Some areas require action."
                        : "Field Health Index: Low. Prioritize field inspection.";

        var status = fhi <= 0 ? "Standby" : fhi >= 80 ? "Optimal" : fhi >= 55 ? "Caution" : "Critical";
        FarmerReportStatusText.Text = status;
        if (FarmerReportStatusPill != null)
        {
            FarmerReportStatusPill.Style = (Style)(status switch
            {
                "Optimal" => Resources["PillSuccessStyle"],
                "Caution" => Resources["PillWarningStyle"],
                "Critical" => Resources["PillCriticalStyle"],
                _ => Resources["PillInfoStyle"]
            });
        }
        FarmerReportStatusText.Foreground = new SolidColorBrush(status switch
        {
            "Optimal" => Color.FromArgb(255, 6, 95, 70),
            "Caution" => Color.FromArgb(255, 178, 107, 0),
            "Critical" => Color.FromArgb(255, 185, 28, 28),
            _ => Color.FromArgb(255, 29, 78, 216)
        });

        if (FarmerRecommendationsStack != null)
        {
            FarmerRecommendationsStack.Children.Clear();
            foreach (var recommendation in BuildFarmerRecommendations(healthyPct, stressPct, droughtPct, bareSoilPct, legacySoilPct))
            {
                FarmerRecommendationsStack.Children.Add(new TextBlock
                {
                    Text = recommendation,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 79, 107, 92)),
                    TextWrapping = TextWrapping.Wrap
                });
            }
        }

        if (FarmerNextActionText != null)
        {
            FarmerNextActionText.Text = status switch
            {
                "Optimal" => "Next survey: 7 days. Continue routine field maintenance.",
                "Caution" => "Next survey: 3 days. Focus on plots with uneven growth.",
                "Critical" => "Repeat survey within 24 hours. Mark priority zones for rapid inspection and action.",
                _ => "Start the YDXJ demo to generate field recommendations."
            };
        }

        if (FarmerReportTimestamp != null)
        {
            FarmerReportTimestamp.Text = fhi <= 0 ? "Not updated yet" : $"Updated: {DateTime.Now:HH:mm:ss}";
        }
    }

    private static IEnumerable<string> BuildFarmerRecommendations(double healthyPct, double stressPct, double droughtPct, double bareSoilPct, double legacySoilPct)
    {
        if (healthyPct + stressPct + droughtPct + bareSoilPct + legacySoilPct <= 0)
        {
            yield return "Run Start Detection with the YDXJ video to generate automatic recommendations.";
            yield break;
        }

        if (droughtPct >= 8)
        {
            yield return "Prioritize drought/severe stress areas; check soil moisture and irrigation timing.";
        }

        if (bareSoilPct >= 5)
        {
            yield return "Review bare soil/gap areas; prepare replanting or spacing correction.";
        }

        if (stressPct >= 20)
        {
            yield return "Check irrigation and nutrition in uneven-growth areas.";
        }

        if (legacySoilPct >= 10)
        {
            yield return "Review exposed or dry soil areas; verify spacing and replanting needs.";
        }

        if (healthyPct >= 70 && stressPct < 15 && droughtPct < 5 && bareSoilPct < 5)
        {
            yield return "Most crop areas appear healthy; continue routine fertilization and irrigation.";
        }
        else
        {
            yield return "Create inspection waypoints for priority zones before spraying or fertilization.";
        }
    }

    private void SetDemoButtonState(bool running)
    {
        StartDemoButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new FontIcon { Glyph = running ? "\uE71A" : "\uE768", FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = running ? "Stop Demo" : "Start Demo", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), VerticalAlignment = VerticalAlignment.Center }
            }
        };
        StartDemoButton.Background = running
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 234, 88, 12)) // #EA580C
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 46, 125, 50)); // #2E7D32

        // Update mode badge
        UpdateModeBadge(running ? "DEMO" : (_isLiveRunning ? "LIVE" : "STANDBY"));
    }

    private bool _isLiveRunning;

    private void UpdateModeBadge(string mode)
    {
        if (ModeBadge == null || ModeText == null) return;
        ModeText.Text = mode;
        ModeBadge.Background = new SolidColorBrush(mode switch
        {
            "DEMO"    => Color.FromArgb(255, 251, 140,  0),   // amber
            "LIVE"    => Color.FromArgb(255,  46, 125, 50),   // green
            "STANDBY" => Color.FromArgb(255,  29,  78, 216),  // blue
            _         => Color.FromArgb(255,  29,  78, 216),
        });
    }

    /// <summary>
    /// Live mode now has no separate manual entry point — it is driven entirely by the
    /// dashboard's actual UAV connection state (top bar Connect/Disconnect). As soon as
    /// <see cref="FlightViewModel.IsConnected"/> becomes true, the local camera's real HSV/YOLO
    /// detection stream starts automatically; disconnecting stops it. This removes the
    /// duplicate "Start Live" dialog that used to ask for its own separate MAVLink address.
    /// </summary>
    private async Task StartLiveModeAsync()
    {
        if (_isLiveRunning) return;

        try
        {
            if (_isDemoRunning) await StopDemoModeAsync();

            UpdateModeBadge("LIVE");
            if (LiveViaConnectionText != null) LiveViaConnectionText.Text = "Live link";
            if (LiveViaConnectionPill != null)
            {
                LiveViaConnectionPill.Background = new SolidColorBrush(Color.FromArgb(255, 46, 125, 50));
                LiveViaConnectionPill.Visibility = Visibility.Visible;
            }

            var cameraSource = Environment.GetEnvironmentVariable("MOONHARVEST_CAMERA_SOURCE");
            if (string.IsNullOrWhiteSpace(cameraSource))
                cameraSource = "0";

            if (_cameraService != null)
            {
                string? modelPath = null;
                if (_cameraService is PythonCameraService)
                    modelPath = PythonCameraService.ResolveHealthModelPath();

                DashboardVideoStream?.ShowStatus("Memulai deteksi live...", true);
                bool yoloEnabled = _harvestFunctionalService?.IsYoloOptionEnabled ?? false;
                var ok = await _cameraService.StartHsvStreamAsync(
                    source: cameraSource,
                    modelPath: modelPath,
                    maxFps: 15f,
                    demo: false,       // REAL mode — tidak loop, bukan data simulasi
                    playbackRate: 1.0f,
                    yoloEnabled: yoloEnabled);
                if (!ok)
                {
                    AddAlertRow("Gagal memulai stream — cek kamera lokal", "error");
                    _timelineService?.Add("error", "Camera stream gagal dimulai", "error");
                    await StopLiveModeAsync();
                    return;
                }

                if (ok)
                {
                    // Save HSV stream config for potential restart on YOLO toggle
                    _currentHsvSource = cameraSource;
                    _currentHsvModelPath = modelPath;
                    _currentHsvDemo = false;
                    _currentHsvPlaybackRate = 1.0f;
                    _currentHsvMaxFps = 15f;
                    _currentHsvShowOverlay = true; // default for live
                }

                _timelineService?.Add("camera", $"Live stream: {cameraSource}", "success");
            }

            _isLiveRunning = true;

            // AI state: respect current toggle (don't force on)
            _aiOn = _harvestFunctionalService?.IsYoloOptionEnabled ?? false;
            if (YoloToggleSwitch != null) YoloToggleSwitch.IsOn = _aiOn;

            ReadinessChecklistPanel.Visibility = Visibility.Visible;
            MissionMetadataPanel.Visibility = Visibility.Visible;

            // Always start a fresh mission ID/timer/report for this live session — do NOT gate
            // on _missionTimerStarted, which DashboardPage_Loaded already sets true as soon as
            // the page opens (before any connection), purely to show a placeholder ID. Reusing
            // that flag here meant this block silently never ran once the page had loaded, and
            // no live session ever created a Reports-history row.
            _missionStart = DateTime.Now;
            MissionIdText.Text = $"MH-{DateTime.Now:yyyyMMdd-HHmmss}-LIVE";
            MissionStartedText.Text = DateTime.Now.ToString("HH:mm:ss");
            _missionTimerStarted = true;

            // Create a real Reports-history row for this live session as soon as it starts, so
            // it actually shows up in Reports/Crop-Analysis aggregates — previously only "Run
            // Analysis" on Crop Analysis ever inserted a row; a live Dashboard session produced
            // export files but never appeared in mission history at all.
            if (_harvestFunctionalService != null)
            {
                _ = _harvestFunctionalService.AddReportAsync(new HarvestFunctionalService.HarvestReportRecord
                {
                    Id = MissionIdText.Text,
                    Area = "Dashboard Live Session",
                    DateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Duration = "00:00:00",
                    Detections = 0,
                    Priority = "Low",
                    AiModelUsed = _harvestFunctionalService.IsYoloOptionEnabled ? "YOLOv8n ONNX local" : "OpenCV fallback",
                    OperatorNote = "Live session started from Dashboard",
                });
            }
            _missionTimer.Start();

            UpdateAlertCenter(_flightViewModel?.Telemetry, _mavLinkService?.IsConnected == true);
            _timelineService?.Add("armed", $"Live mode aktif (UAV terhubung) · source: {cameraSource}", "success");
            RenderIncidentTimeline();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "StartLiveModeAsync failed");
            AddAlertRow($"Live mode error: {ex.Message}", "error");
            await StopLiveModeAsync();
        }
    }

    /// <summary>
    /// Stops only the camera/detection stream. Does NOT touch the MAVLink connection —
    /// disconnecting the UAV is exclusively a top-bar Connect/Disconnect action now.
    /// </summary>
    private async Task StopLiveModeAsync()
    {
        _isLiveRunning = false;
        UpdateModeBadge("STANDBY");

        if (LiveViaConnectionText != null) LiveViaConnectionText.Text = "Live via Connect";
        if (LiveViaConnectionPill != null)
        {
            LiveViaConnectionPill.Background = new SolidColorBrush(Colors.Transparent);
            LiveViaConnectionPill.Visibility = Visibility.Collapsed;
        }

        if (_cameraService != null) await _cameraService.StopCameraAsync();

        ReadinessChecklistPanel.Visibility = Visibility.Collapsed;
        MissionMetadataPanel.Visibility = Visibility.Collapsed;
        _missionTimer.Stop();
        _missionTimerStarted = false;
        _timelineService?.Add("disconnected", "Live mode dihentikan (UAV terputus)", "warning");
        DashboardVideoStream?.ShowStatus("Kamera standby · hubungkan UAV untuk mulai survey", false);
        UpdateAlertCenter(_flightViewModel?.Telemetry, false);
        RenderIncidentTimeline();
    }

    private async void DemoScenarioComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (_isSwitchingScenario) return;
            var idx = DemoScenarioComboBox?.SelectedIndex ?? 0;
            if (idx == 4)
            {
                // Custom video item
                if (string.IsNullOrWhiteSpace(_customVideoPath) || !File.Exists(_customVideoPath))
                {
                    await PromptAndLoadCustomVideoAsync();
                }
                else
                {
                    var profile = _videoScenarioService?.LoadScenarioForVideo(_customVideoPath) ??
                                  _videoScenarioService?.PredefinedScenarios[0];
                    if (profile != null)
                    {
                        await ApplyScenarioProfileAsync(profile);
                    }
                }
                return;
            }

            if (_videoScenarioService != null && idx >= 0 && idx < _videoScenarioService.PredefinedScenarios.Count)
            {
                var profile = _videoScenarioService.SelectScenarioByIndex(idx);
                await ApplyScenarioProfileAsync(profile);
            }
            else
            {
                var videoPath = ResolveDetectedVideoPath();
                if (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath))
                {
                    await ApplyCustomDemoVideoAsync(videoPath);
                }
            }
        }
        catch (Exception ex)
        {
            // This handler spawns ffprobe/python to probe the video; an escaping exception
            // would terminate the process (the "force close" symptom on Windows).
            Serilog.Log.Error(ex, "[DashboardPage] Scenario switch failed");
            AddAlertRow($"Scenario switch failed: {ex.Message}", "error");
        }
    }

    private async Task PromptAndLoadCustomVideoAsync()
    {
        try
        {
            var selected = await (_fileService?.PickFileAsync(new[] { ".mp4", ".mkv", ".avi", ".mov" }) ?? Task.FromResult<string?>(null));
            if (!string.IsNullOrWhiteSpace(selected) && File.Exists(selected))
            {
                await ApplyCustomDemoVideoAsync(selected);
            }
            else if (DemoScenarioComboBox != null && DemoScenarioComboBox.SelectedIndex == 4 && string.IsNullOrWhiteSpace(_customVideoPath))
            {
                _isSwitchingScenario = true;
                try
                {
                    DemoScenarioComboBox.SelectedIndex = 0;
                }
                finally
                {
                    _isSwitchingScenario = false;
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[DashboardPage] PromptAndLoadCustomVideoAsync error");
        }
    }

    private async void ChangeVideoButton_Click(object sender, RoutedEventArgs e)
    {
        await PromptAndLoadCustomVideoAsync();
    }

    private void VideoRoot_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        if (VideoDropOverlay != null)
        {
            VideoDropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void VideoRoot_DragLeave(object sender, DragEventArgs e)
    {
        if (VideoDropOverlay != null)
        {
            VideoDropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private async void VideoRoot_Drop(object sender, DragEventArgs e)
    {
        if (VideoDropOverlay != null)
        {
            VideoDropOverlay.Visibility = Visibility.Collapsed;
        }

        try
        {
            string? targetPath = null;
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var item = items.FirstOrDefault(i =>
                {
                    var ext = Path.GetExtension(i.Path).ToLowerInvariant();
                    return ext == ".mp4" || ext == ".mkv" || ext == ".avi" || ext == ".mov";
                });
                if (item != null)
                {
                    targetPath = item.Path;
                }
            }

            if (string.IsNullOrWhiteSpace(targetPath) && e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                var text = await e.DataView.GetTextAsync();
                var cleaned = text.Trim().Trim('"', '\'', '\r', '\n');
                if (cleaned.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    if (Uri.TryCreate(cleaned, UriKind.Absolute, out var uri))
                        cleaned = uri.LocalPath;
                }
                if (File.Exists(cleaned))
                {
                    targetPath = cleaned;
                }
            }

            if (!string.IsNullOrWhiteSpace(targetPath) && File.Exists(targetPath))
            {
                await ApplyCustomDemoVideoAsync(targetPath);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[DashboardPage] VideoRoot_Drop failed");
        }
    }

    /// <summary>
    /// Off-thread duration probe. Synchronous probing spawned ffprobe/python and blocked the
    /// caller for up to 7 s; the callers are awaited from UI handlers, so that stall showed up
    /// as UI lag. The actual work now runs on the thread pool.
    /// </summary>
    public static Task<double> ProbeVideoDurationAsync(string videoPath, double fallbackSeconds = 60.0)
        => Task.Run(() => ProbeVideoDuration(videoPath, fallbackSeconds));

    public static double ProbeVideoDuration(string videoPath, double fallbackSeconds = 60.0)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            return fallbackSeconds;
        }

        var fn = Path.GetFileName(videoPath);
        if (fn.Equals("stream_multisector_60s.mp4", StringComparison.OrdinalIgnoreCase) ||
            fn.Equals("stream_multisector_60s_detected.mp4", StringComparison.OrdinalIgnoreCase))
            return 60.0;
        if (fn.Equals("stream_v7c_final.mp4", StringComparison.OrdinalIgnoreCase) ||
            fn.Equals("15d.mp4", StringComparison.OrdinalIgnoreCase))
            return 15.5;
        if (fn.Equals("YDXJ_fused_only_detected.mp4", StringComparison.OrdinalIgnoreCase))
            return 35.0;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                if (proc.WaitForExit(3000))
                {
                    var output = proc.StandardOutput.ReadToEnd().Trim();
                    if (double.TryParse(output, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dur) && dur > 1.0)
                    {
                        return dur;
                    }
                }
                else
                {
                    try { proc.Kill(); } catch { }
                }
            }
        }
        catch { }

        try
        {
            var (yoloPy, _) = PythonCameraService.ResolvePythonCommand(".venv-yolo");
            var (camPy, _)  = PythonCameraService.ResolvePythonCommand(null);
            var pyCmd = yoloPy ?? camPy ?? "python3";
            var pyCode = $"import cv2; c=cv2.VideoCapture('{videoPath}'); fps=c.get(cv2.CAP_PROP_FPS); fc=c.get(cv2.CAP_PROP_FRAME_COUNT); print(fc/max(1,fps))";
            var psi = new ProcessStartInfo
            {
                FileName = pyCmd,
                Arguments = $"-c \"{pyCode}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                if (proc.WaitForExit(4000))
                {
                    var output = proc.StandardOutput.ReadToEnd().Trim();
                    if (double.TryParse(output, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dur) && dur > 1.0)
                    {
                        return dur;
                    }
                }
                else
                {
                    try { proc.Kill(); } catch { }
                }
            }
        }
        catch { }

        return fallbackSeconds;
    }

    public async Task ApplyCustomDemoVideoAsync(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) return;
        var dur = await ProbeVideoDurationAsync(videoPath, 60.0);
        var profile = _videoScenarioService?.LoadScenarioForVideo(videoPath, dur) ??
                      new VideoScenarioService().LoadScenarioForVideo(videoPath, dur);
        await ApplyScenarioProfileAsync(profile);
    }

    public async Task ApplyScenarioProfileAsync(VideoScenarioProfile profile, bool startDemoIfNeeded = true)
    {
        if (profile == null) return;
        _currentScenario = profile;
        _videoScenarioService?.SetCurrentScenario(profile);

        // 1. Resolve video path
        var videoPath = _videoScenarioService?.ResolveVideoFilePath(profile) ?? profile.ResolvedVideoPath;
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
        {
            videoPath = ResolveDetectedVideoPath();
        }
        if (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath))
        {
            profile.ResolvedVideoPath = videoPath;
            var probedDur = await ProbeVideoDurationAsync(videoPath, profile.VideoDurationSeconds);
            if (probedDur > 5.0) _demoVideoDurationSeconds = probedDur;
            else _demoVideoDurationSeconds = profile.VideoDurationSeconds;
        }
        else
        {
            _demoVideoDurationSeconds = profile.VideoDurationSeconds;
        }
        _customVideoPath = videoPath;

        // 2. Flight & Navigation parameters
        int wpCount = Math.Max(2, profile.Waypoints.Count);
        _demoTicksPerSegment = Math.Max(1, (int)Math.Round(_demoVideoDurationSeconds / (wpCount - 1)));
        _demoSpeedBase = profile.CruiseSpeedMps;
        _demoAltitudeBase = profile.CruiseAltitudeMeters;
        _demoGeofenceRadiusMeters = profile.GeofenceRadiusMeters;
        _demoHeading = profile.BearingDeg;
        _demoBattery = profile.InitialBatteryPct;
        _demoSatCount = profile.SatelliteCount;
        _centerLat = profile.FieldCenterLat;
        _centerLon = profile.FieldCenterLon;
        _demoWaypoints = profile.Waypoints.Select(w => (w.Latitude, w.Longitude)).ToArray();

        // 3. Update UI - ComboBox
        _isSwitchingScenario = true;
        try
        {
            int targetIdx = profile.Id switch
            {
                "scenario-multisector" => 0,
                "scenario-15hst"       => 1,
                "scenario-irrigated"   => 2,
                "scenario-drought"     => 3,
                _                      => 4
            };

            var fn = Path.GetFileName(videoPath ?? profile.VideoFileName);
            if (targetIdx == 4 && CustomVideoComboItem != null)
            {
                CustomVideoComboItem.Content = $"📁 {fn} ({_demoVideoDurationSeconds:F0}s · {wpCount} WP)";
            }
            if (DemoScenarioComboBox != null && DemoScenarioComboBox.SelectedIndex != targetIdx)
            {
                DemoScenarioComboBox.SelectedIndex = targetIdx;
            }
        }
        finally
        {
            _isSwitchingScenario = false;
        }

        // 4. Update DashboardMapControl Center only
        if (DashboardMapControl != null)
        {
            DashboardMapControl.SetCenter(profile.FieldCenterLat, profile.FieldCenterLon, profile.MapZoomLevel);
        }

        // 7. Update Mission ID & HUD Text
        if (MissionIdText != null)
        {
            MissionIdText.Text = profile.MissionId;
        }
        var videoName = Path.GetFileName(videoPath ?? profile.VideoFileName);
        FpsHudText.Text = $"{profile.ShortDescription} · {videoName} ({_demoVideoDurationSeconds:F0}s)";

        // 8. Incident Timeline
        _timelineService?.Add("camera",   $"Video disinkronkan: {videoName} ({_demoVideoDurationSeconds:F0}s)", "info");
        _timelineService?.Add("waypoint", $"Rute {profile.MissionId}: {wpCount} WP @ {profile.AreaName} (Alt {profile.CruiseAltitudeMeters:F0}m · Bearing {profile.BearingDeg:F0}°)", "success");
        _timelineService?.Add("geofence", $"Geofence disesuaikan: {profile.GeofenceRadiusMeters:F0}m @ {profile.RegionName}", "info");
        RenderIncidentTimeline();

        // 9. Reset counters and detection
        _demoStep = 0;
        _currentDemoWaypointIndex = 0;
        _nextDemoWaypointIndex = 1;
        _classifyRealData = null;

        var isPreAnnotated = videoName.Contains("detected", StringComparison.OrdinalIgnoreCase) ||
                             videoName.Contains("fused", StringComparison.OrdinalIgnoreCase);
        _useFusedOnlyDemoSummary = isPreAnnotated;

        // 10. Start or update video stream
#if __ANDROID__
        if (VegetationOverlayLayer != null)
        {
            VegetationOverlayLayer.Visibility = Visibility.Collapsed;
        }

        if (_androidDemoDecoder != null)
        {
            await _androidDemoDecoder.StopAsync();
            try { _androidDemoDecoder.Dispose(); } catch { }
            _androidDemoDecoder = null;
        }

        var androidContext = Android.App.Application.Context;
        var assetName = _currentScenario?.Id switch
        {
            "scenario-15hst"     => "demo_videos/stream_v7c_final_detected.mp4",
            "scenario-irrigated" => "demo_videos/YDXJ_fused_only_detected.mp4",
            "scenario-drought"   => "demo_videos/YDX_burned_detected.mp4",
            _                    => "demo_videos/stream_multisector_60s_detected.mp4"
        };
        string videoSource = (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath)) ? videoPath : assetName;

        DashboardVideoStream?.SetHighQualityMode(true);
        DashboardVideoStream?.HideOverlay();

        _androidDemoDecoder = new HarvestmoonGCS.Platforms.Android.Services.AndroidDemoVideoDecoder(androidContext, videoSource);
        _androidDemoDecoder.FrameDecoded += (_, jpeg) => OnDashboardFrameReceived(this, jpeg);
        await _androidDemoDecoder.StartAsync();
        _timelineService?.Add("camera", $"Video: {Path.GetFileName(videoSource)} (Android)", "success");
#else
        if (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath))
        {
            var previewFrame = await Task.Run(() => ExtractFirstFrameBytes(videoPath));
            if (previewFrame.Length > 0)
            {
                _lastFrameData = previewFrame;
                DashboardVideoStream?.SetHighQualityMode(true);
                DashboardVideoStream?.UpdateFrame(previewFrame);
                DashboardVideoStream?.HideOverlay();
            }
            if (VegetationOverlayLayer != null)
            {
                VegetationOverlayLayer.Visibility = Visibility.Collapsed;
            }

            if (_cameraService != null)
            {
                await _cameraService.StopCameraAsync();
                var modelPath = PythonCameraService.ResolveHealthModelPath();

                // Pre-annotated videos already have detection boxes — don't add HSV/YOLO overlay on top
                var vFn = Path.GetFileName(videoPath);
                bool isPreAnnotatedVideo = vFn.Contains("detected", StringComparison.OrdinalIgnoreCase) ||
                                           vFn.Contains("fused", StringComparison.OrdinalIgnoreCase);
                bool yoloEnabled = isPreAnnotatedVideo ? false : (_harvestFunctionalService?.IsYoloOptionEnabled ?? false);
                bool showOverlay = !isPreAnnotatedVideo;

                bool started = await _cameraService.StartHsvStreamAsync(
                    source: videoPath,
                    modelPath: modelPath,
                    maxFps: 15f,
                    showOverlay: showOverlay,
                    demo: true,
                    playbackRate: 1.0f,
                    yoloEnabled: yoloEnabled);

                if (!started)
                {
                    await _cameraService.StartCameraAsync(videoPath);
                }

                // Save HSV stream config for potential restart on YOLO toggle
                _currentHsvSource = videoPath;
                _currentHsvModelPath = modelPath;
                _currentHsvDemo = true;
                _currentHsvPlaybackRate = 1.0f;
                _currentHsvMaxFps = 15f;
                _currentHsvShowOverlay = showOverlay;
            }
        }
#endif

        // 11. Run simulation telemetry & reports
        if (startDemoIfNeeded)
        {
            await StartDemoMode();
        }
        else
        {
            InjectDemoTelemetry(0);
            UpdateDemoFieldReport();
        }
    }

    private string? ResolveDetectedVideoPath()
    {
        if (_videoScenarioService != null)
        {
            var p = _videoScenarioService.ResolveVideoFilePath(_currentScenario);
            if (p != null) return p;
        }

        if (DemoScenarioComboBox != null)
        {
            var idx = DemoScenarioComboBox.SelectedIndex;
            if (idx == 0) // Multi-Sektor 60s
            {
                var p = ResolveFilePath(
                    Path.Combine(AppContext.BaseDirectory, "Assets", "demo_videos", "stream_multisector_60s_detected.mp4"),
                    Path.Combine(AppContext.BaseDirectory, "Assets", "demo_videos", "stream_multisector_60s.mp4"),
                    Path.Combine(Directory.GetCurrentDirectory(), "Assets", "demo_videos", "stream_multisector_60s_detected.mp4"),
                    Path.Combine(Directory.GetCurrentDirectory(), "Assets", "demo_videos", "stream_multisector_60s.mp4"),
                    "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/stream_multisector_60s.mp4",
                    "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/stream_multisector_60s_detected.mp4",
                    "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/stream_multisector_survey.mp4");
                if (p != null) return p;
            }
            else if (idx == 1) // Sawah 15 HST
            {
                var p = ResolveFilePath(
                    Path.Combine(AppContext.BaseDirectory, "Assets", "demo_videos", "stream_v7c_final.mp4"),
                    Path.Combine(AppContext.BaseDirectory, "Assets", "demo_videos", "15d_detected.mp4"),
                    Path.Combine(Directory.GetCurrentDirectory(), "Assets", "demo_videos", "stream_v7c_final.mp4"),
                    "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/stream_v7c_final.mp4",
                    "/home/fawwazfa/Program/Harvestmoon/vidio/15d.mp4",
                    "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/demo_videos/stream_v7c_final.mp4");
                if (p != null) return p;
            }
            else if (idx == 2) // Lahan Irigasi (YDXJ)
            {
                var p = ResolveFilePath(
                    Path.Combine(AppContext.BaseDirectory, "Assets", "demo_videos", "YDXJ_fused_only_detected.mp4"),
                    Path.Combine(Directory.GetCurrentDirectory(), "Assets", "demo_videos", "YDXJ_fused_only_detected.mp4"),
                    "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/YDXJ_fused_only_detected.mp4",
                    "/home/fawwazfa/Program/Harvestmoon/vidio/YDXJ0012_demo(1).mp4",
                    "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/demo_videos/YDXJ_fused_only_detected.mp4");
                if (p != null) return p;
            }
            else if (idx == 3) // Lahan Kering (YDX_burned)
            {
                var p = ResolveFilePath(
                    Path.Combine(AppContext.BaseDirectory, "Assets", "demo_videos", "YDX_burned.mp4"),
                    Path.Combine(Directory.GetCurrentDirectory(), "Assets", "demo_videos", "YDX_burned.mp4"),
                    "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/YDX_burned.mp4",
                    "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/demo_videos/YDX_burned.mp4",
                    "/home/fawwazfa/Program/Harvestmoon/vidio/YDX_burned.mp4");
                if (p != null) return p;
            }
            else if (idx == 4) // Custom Video
            {
                if (!string.IsNullOrWhiteSpace(_customVideoPath) && File.Exists(_customVideoPath))
                {
                    return _customVideoPath;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(_customVideoPath) && File.Exists(_customVideoPath))
        {
            return _customVideoPath;
        }

        var envOverride = Environment.GetEnvironmentVariable("MOONHARVEST_DEMO_VIDEO");
        if (!string.IsNullOrWhiteSpace(envOverride) && File.Exists(envOverride))
        {
            return envOverride;
        }

        foreach (var candidate in DemoVideoPathCandidates)
        {
            if (File.Exists(candidate) && new FileInfo(candidate).Length > 10_000)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ResolveFilePath(params string[] candidates)
    {
        foreach (var c in candidates)
        {
            if (File.Exists(c) && new FileInfo(c).Length > 10_000) return c;
        }
        return null;
    }

    private static (double Lat, double Lon)[] GenerateStraightLineWaypoints(
        double centerLat, double centerLon, int count, double totalMeters, double bearingDeg)
    {
        const double mPerDegLat = 111320.0;
        double mPerDegLon = mPerDegLat * Math.Cos(centerLat * Math.PI / 180);
        double spacingMeters = totalMeters / (count - 1);
        double bearingRad = bearingDeg * Math.PI / 180;
        double dLat = spacingMeters * Math.Cos(bearingRad) / mPerDegLat;
        double dLon = spacingMeters * Math.Sin(bearingRad) / mPerDegLon;
        double startLat = centerLat - dLat * (count - 1) / 2.0;
        double startLon = centerLon - dLon * (count - 1) / 2.0;
        var wps = new (double, double)[count];
        for (int i = 0; i < count; i++)
            wps[i] = (startLat + i * dLat, startLon + i * dLon);
        return wps;
    }

    private (double Lat, double Lon)[] GenerateDemoRiceFieldWaypoints()
    {
        if (_currentScenario != null && _currentScenario.Waypoints.Count > 0)
        {
            return _currentScenario.Waypoints.Select(w => (w.Latitude, w.Longitude)).ToArray();
        }
        return GenerateStraightLineWaypoints(DemoFieldLat, DemoFieldLon, count: 5, totalMeters: 600, bearingDeg: 90.0);
    }

    private static (double Lat, double Lon) OffsetCoordinate(double latitude, double longitude, double distanceMeters, double bearingDeg)
    {
        const double mPerDegLat = 111320.0;
        var mPerDegLon = mPerDegLat * Math.Cos(latitude * Math.PI / 180);
        var bearingRad = bearingDeg * Math.PI / 180.0;
        var dLat = distanceMeters * Math.Cos(bearingRad) / mPerDegLat;
        var dLon = distanceMeters * Math.Sin(bearingRad) / mPerDegLon;
        return (latitude + dLat, longitude + dLon);
    }

    private static (double Lat, double Lon)[] GenerateSurveyWaypoints(
        double centerLat, double centerLon, int rows, int cols, double spacingMeters)
    {
        const double mPerDegLat = 111320.0;
        double mPerDegLon = mPerDegLat * Math.Cos(centerLat * Math.PI / 180);
        double dLat = spacingMeters / mPerDegLat;
        double dLon = spacingMeters / mPerDegLon;
        double startLat = centerLat - (rows / 2.0) * dLat;
        double startLon = centerLon - (cols / 2.0) * dLon;
        var wps = new List<(double, double)>();
        for (int r = 0; r < rows; r++)
        {
            double lat = startLat + r * dLat;
            bool ltr = r % 2 == 0;
            for (int c = 0; c < cols; c++)
            {
                int col = ltr ? c : (cols - 1 - c);
                wps.Add((lat, startLon + col * dLon));
            }
        }
        return wps.ToArray();
    }

        // ================ EXPORTS ================

    private async void ExportPdfButton_Click(object sender, RoutedEventArgs e) => await ExportReportAsync("pdf");
    private async void ExportCsvButton_Click(object sender, RoutedEventArgs e) => await ExportReportAsync("csv");
    private async void ExportJsonButton_Click(object sender, RoutedEventArgs e) => await ExportReportAsync("json");

    private async Task ExportReportAsync(string format)
    {
        if (_harvestFunctionalService == null)
        {
            AddAlertRow("Export failed: HarvestFunctionalService not available", "error");
            return;
        }

        var telemetry = _flightViewModel?.Telemetry;
        var connected = _flightViewModel?.IsConnected == true;
        var videoPath = _videoRecorderService?.CurrentRecordingPath ?? string.Empty;

        // --- Map snapshot (guarded) ---
        string mapPath = string.Empty;
        try
        {
            var mapSnapshotBytes = DashboardMapControl?.CaptureSnapshotPng();
            mapPath = _harvestFunctionalService.SaveMapSnapshot(
                mapSnapshotBytes,
                telemetry?.Latitude ?? _centerLat,
                telemetry?.Longitude ?? _centerLon,
                connected ? "Live dashboard position" : "Dashboard standby position");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[DashboardPage] Map snapshot failed, continuing export without it");
            AddAlertRow("Map snapshot unavailable, exporting without map", "warning");
        }

        // --- YOLO screenshot (guarded) ---
        string yoloPath = string.Empty;
        try
        {
            byte[]? frameForScreenshot = _lastFrameData;
            if (frameForScreenshot == null || frameForScreenshot.Length == 0)
            {
                // Demo mode fallback: try to extract a frame from the current demo video
                if (_isDemoRunning && !string.IsNullOrWhiteSpace(_customVideoPath) && File.Exists(_customVideoPath))
                {
                    frameForScreenshot = ExtractFirstFrameBytes(_customVideoPath);
                }
                // If still null, check if we have a demo video playing via the camera service
                if (frameForScreenshot == null || frameForScreenshot.Length == 0)
                {
                    // Try the first available demo video candidate
                    foreach (var candidate in DemoVideoPathCandidates)
                    {
                        if (File.Exists(candidate))
                        {
                            frameForScreenshot = ExtractFirstFrameBytes(candidate);
                            if (frameForScreenshot != null && frameForScreenshot.Length > 0) break;
                        }
                    }
                }
            }

            if (frameForScreenshot != null && frameForScreenshot.Length > 0)
            {
                yoloPath = await _harvestFunctionalService.SaveLatestYoloScreenshotAsync(frameForScreenshot);
            }
            else
            {
                AddAlertRow("No camera frame available for YOLO screenshot, exporting without it", "warning");
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[DashboardPage] YOLO screenshot failed, continuing export without it");
            AddAlertRow("YOLO screenshot unavailable, exporting without it", "warning");
        }

        var record = new HarvestFunctionalService.HarvestReportRecord
        {
            Id = MissionIdText.Text,
            Area = "Dashboard Session",
            DateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Duration = MissionElapsedText.Text,
            Detections = _lastDetectionCount,
            Priority = _lastDetectionCount > 0 ? "Medium" : "Low",
            AiModelUsed = _harvestFunctionalService.IsYoloOptionEnabled ? "YOLOv8n ONNX local" : "OpenCV fallback",
            OperatorNote = $"Avg conf {(_lastConfidence * 100):F0}%",
            MapScreenshotPath = mapPath,
            YoloScreenshotPath = yoloPath,
            VideoRecordingPath = videoPath,
            IncidentTimelineJson = _timelineService?.ToJson() ?? string.Empty
        };

        try
        {
            await _harvestFunctionalService.CreateEvidenceBundleAsync(record);
            var baseName = $"dashboard-{DateTime.Now:yyyyMMddHHmmss}";
            string exportPath = string.Empty;
            switch (format)
            {
                case "pdf": exportPath = await _harvestFunctionalService.ExportReportPdfAsync(record, baseName); break;
                case "csv": exportPath = await _harvestFunctionalService.ExportReportCsvAsync(record, baseName); break;
                case "json": exportPath = await _harvestFunctionalService.ExportReportJsonAsync(record, baseName); break;
            }
            if (string.IsNullOrWhiteSpace(exportPath))
            {
                AddAlertRow($"Export completed ({format.ToUpper()})", "info");
            }
            else if (exportPath.StartsWith("EXPORT_FAILED", StringComparison.OrdinalIgnoreCase))
            {
                AddAlertRow($"Export failed: {exportPath}", "error");
            }
            else
            {
                AddAlertRow($"Export successful: {exportPath}", "info");
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[DashboardPage] Export {Format} failed", format);
            AddAlertRow($"Export failed: {ex.Message}", "error");
        }
    }

    private static string? FindDemoVideoPath()
    {
        var root = FindRepoRoot();
        var candidates = new[]
        {
            Path.Combine(root, "vision_trial", "testkamera.mp4"),
            Path.Combine(root, "vision_trial", "output", "crop_weed_trial.mp4"),
            Path.Combine(root, "testkamera.mp4"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "testkamera.mp4")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "HarvestmoonGCS")) &&
                Directory.Exists(Path.Combine(dir.FullName, "vision_trial")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static byte[] ExtractFirstFrameBytes(string videoPath)
    {
#if __ANDROID__
        try
        {
            if (File.Exists(videoPath))
            {
                using var mmr = new Android.Media.MediaMetadataRetriever();
                mmr.SetDataSource(videoPath);
                using var bmp = mmr.GetFrameAtTime(0);
                if (bmp != null)
                {
                    using var ms = new MemoryStream();
                    bmp.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg!, 80, ms);
                    return ms.ToArray();
                }
            }
        }
        catch { }
        return Array.Empty<byte>();
#else
        if (OperatingSystem.IsAndroid())
        {
            return Array.Empty<byte>();
        }

        try
        {
            var python = ResolvePreviewPythonCommand();
            if (string.IsNullOrWhiteSpace(python))
            {
                return Array.Empty<byte>();
            }

            const string script = """
import cv2, sys
cap = cv2.VideoCapture(sys.argv[1])
ok, frame = cap.read()
cap.release()
if not ok or frame is None:
    sys.exit(2)
h, w = frame.shape[:2]
if w > 960:
    frame = cv2.resize(frame, (960, int(h * 960 / w)))
ok, buf = cv2.imencode(".jpg", frame, [int(cv2.IMWRITE_JPEG_QUALITY), 80])
if not ok:
    sys.exit(3)
sys.stdout.buffer.write(buf.tobytes())
""";

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = python,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(script);
            process.StartInfo.ArgumentList.Add(videoPath);

            if (!process.Start())
            {
                return Array.Empty<byte>();
            }

            using var output = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(output);
            if (!process.WaitForExit(2500))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return Array.Empty<byte>();
            }

            return process.ExitCode == 0 && output.Length > 0 ? output.ToArray() : Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[DashboardPage] Failed to extract demo frame from {Video}", videoPath);
            return Array.Empty<byte>();
        }
#endif
    }

    private static string? ResolvePreviewPythonCommand()
    {
        if (OperatingSystem.IsAndroid())
        {
            return null;
        }

        var candidates = new[]
        {
            "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/.venv-yolo/bin/python3",
            "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/.venv-camera/bin/python",
            "python3"
        };

        return candidates.FirstOrDefault(command =>
        {
            if (Path.IsPathRooted(command))
            {
                return File.Exists(command);
            }

            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = command,
                    ArgumentList = { "--version" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return process != null && process.WaitForExit(1000) && process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        });
    }

    private void AddAlertRow(string text, string severity)
    {
        _lastAlertSignature = null;
        AlertsStack.Children.Insert(0, BuildAlertRow(text, DateTime.Now.ToString("HH:mm:ss"), severity));
    }

    private void GoCropAnalysisButton_Click(object sender, RoutedEventArgs e)
    {
        // Navigation event is handled by sidebar; simulate by finding parent frame
        if (this.Frame != null)
        {
            this.Frame.Navigate(typeof(StatsPage));
        }
    }

    // ================ IMU HUD ================

    private void OnImuTick(object? sender, object e)
    {
        if (_isDemoRunning && _hasDemoImu)
        {
            ApplyImuValues(_demoPitchDeg, _demoRollDeg, _demoYawDeg);
            return;
        }

        // Use real telemetry when available; skip the random wobble to avoid forcing a redraw
        // every 700 ms. When connected we still redraw because the telemetry path already did.
        var telemetry = _telemetry;
        if (telemetry != null)
        {
            ApplyImuTelemetry(telemetry);
            return;
        }

        if (_flightViewModel?.IsConnected == true)
        {
            // If connected but telemetry is null, just hold at 0.
            _pitch = 0;
            _roll = 0;
            _yaw = 0;
        }
        else
        {
            // Gentle idle animation when disconnected so the artificial horizon isn't frozen.
            var rnd = Random.Shared;
            _pitch = Math.Clamp(_pitch + (rnd.NextDouble() - 0.5) * 10, -25, 25);
            _roll  = Math.Clamp(_roll  + (rnd.NextDouble() - 0.5) * 14, -35, 35);
            _yaw   = (_yaw + (rnd.NextDouble() - 0.5) * 6 + 360) % 360;
        }
        ImuRollTransform.Angle = _roll;
        ImuPitchTransform.Y = Math.Clamp(_pitch * 1.4, -42, 42);
        ImuLabelText.Text = $"P{_pitch:F0}° R{_roll:F0}° Y{_yaw:F0}°";
    }

    private void SetDemoImu(double rollDeg, double pitchDeg, double yawDeg, double altitudeMeters, double speedMetersPerSecond)
    {
        _hasDemoImu = true;
        _demoRollDeg = rollDeg;
        _demoPitchDeg = pitchDeg;
        _demoYawDeg = (yawDeg + 360.0) % 360.0;
        _demoAltitudeMeters = altitudeMeters;
        _demoSpeedMetersPerSecond = speedMetersPerSecond;

        ApplyImuValues(_demoPitchDeg, _demoRollDeg, _demoYawDeg);
        SetTextIfChanged(TelemetryHeadingText, $"{_demoYawDeg:F0}°", ref _lastHeadingText);
        SetTextIfChanged(HeadingPillText, $"HDG {_demoYawDeg:F0}°", ref _lastHeadingPillText);
        SetTextIfChanged(HdgModeText, $"HDG {_demoYawDeg:F0}° · MODE AUTO", ref _lastHdgModeText);
    }

    private void ApplyImuTelemetry(TelemetryData telemetry)
    {
        ApplyImuValues(
            telemetry.Pitch,
            telemetry.Roll,
            telemetry.Heading);
    }

    private void ApplyImuValues(double pitchDeg, double rollDeg, double yawDeg)
    {
        _pitch = pitchDeg;
        _roll = rollDeg;
        _yaw = (yawDeg + 360.0) % 360.0;
        ImuRollTransform.Angle = _roll;
        ImuPitchTransform.Y = Math.Clamp(_pitch * 1.4, -42, 42);
        ImuLabelText.Text = $"P{_pitch:F0}° R{_roll:F0}° Y{_yaw:F0}°";
    }

    private void OnMissionTick(object? sender, object e)
    {
        var elapsed = DateTime.Now - _missionStart;
        MissionElapsedText.Text = $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        var plannedSeconds = _mapViewModel?.CalculateMissionDuration() ?? 0;
        var total = plannedSeconds > 0
            ? TimeSpan.FromSeconds(plannedSeconds)
            : TimeSpan.FromMinutes(12);
        var remaining = total - elapsed;
        if (remaining.TotalSeconds <= 0) remaining = TimeSpan.Zero;
        MissionEtaText.Text = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
            : $"{remaining.Minutes:D2}:{remaining.Seconds:D2}";
    }

    // ================ ALERTS / SUMMARY SEED ================

    private void UpdateAlertCenter(TelemetryData? telemetry, bool connected)
    {
        var alerts = BuildRuntimeAlerts(telemetry, connected).ToList();
        var signature = string.Join("|", alerts.Select(alert => $"{alert.Severity}:{alert.Text}"));
        if (signature == _lastAlertSignature)
        {
            return;
        }

        _lastAlertSignature = signature;
        AlertsStack.Children.Clear();
        foreach (var alert in alerts)
        {
            AlertsStack.Children.Add(BuildAlertRow(alert.Text, DateTime.Now.ToString("HH:mm:ss"), alert.Severity));
        }
    }

    private void UpdateOfflineAndReadiness(TelemetryData? telemetry, bool connected)
    {
        var modelName = Path.GetFileName(_harvestFunctionalService?.RuntimeModelPath ?? "bundled-auto");
        OfflineModeText.Text = _harvestFunctionalService?.IsYoloRuntimeReady == true
            ? "Running offline · YOLO active · No cloud dependency"
            : "Running offline · YOLO active · No cloud dependency";
        ModelRuntimeText.Text = $"Model: {modelName} · threshold {_harvestFunctionalService?.RuntimeConfidenceThreshold * 100:F0}%";
        RecorderRuntimeText.Text = _videoRecorderService?.IsRecording == true
            ? $"Recorder: recording · {_videoRecorderService.CurrentRecordingPath}"
            : "Recorder: standby";

        var battery = telemetry?.BatteryPercent is > 0
            ? telemetry.BatteryPercent
            : telemetry?.BatteryRemaining ?? 0;
        var hasValidGps = telemetry != null && IsValidCoordinate(telemetry.Latitude, telemetry.Longitude);
        var gpsReady = hasValidGps && (telemetry?.SatelliteCount ?? 0) >= 6;
        var geofenceReady = _mapViewModel?.IsGeofenceActive == true || _geofenceService?.CurrentGeofence.IsActive == true;
        var wpCount = _mapViewModel?.Waypoints?.Count ?? 0;
        var waypointReady = wpCount > 0;
        var yoloReady = _harvestFunctionalService?.IsYoloRuntimeReady == true;
        var cameraReady = _cameraService?.IsStreaming == true;
        var recorderReady = _videoRecorderService != null;
        var battOk = battery >= 20;

        SetChecklistText(ReadyTelemetryText, connected, connected ? "Telemetry Link OK" : "Telemetry Offline");
        SetChecklistText(ReadyGpsText, gpsReady, gpsReady ? $"GPS 3D Fix ({telemetry!.SatelliteCount} Sats)" : "GPS No Fix");
        SetChecklistText(ReadyBatteryText, battOk, battery > 0 ? $"Battery {battery:F0}% OK" : "Battery Standby");
        SetChecklistText(ReadyCameraText, cameraReady, cameraReady ? "Camera Feed Active" : "Camera Feed Standby");
        SetChecklistText(ReadyYoloText, yoloReady, yoloReady ? "AI Model Ready" : "AI Initializing");
        SetChecklistText(ReadyGeofenceText, geofenceReady, geofenceReady ? "Geofence Active" : "Geofence Standby");
        SetChecklistText(ReadyWaypointText, waypointReady, waypointReady ? $"Mission WPs ({wpCount})" : "No Mission WPs");
        SetChecklistText(ReadyRecorderText, recorderReady, _videoRecorderService?.IsRecording == true ? "Recording Blackbox" : "Recorder Armed");

        // Calculate overall GO / NO-GO status
        bool isScenarioActive = _isDemoRunning || _videoScenarioService?.CurrentScenario != null;
        bool allGo = (connected || isScenarioActive) &&
                     (gpsReady || isScenarioActive) &&
                     (battOk || isScenarioActive) &&
                     waypointReady;

        if (ReadinessStatusPill != null && ReadinessStatusText != null)
        {
            if (allGo)
            {
                ReadinessStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, 236, 253, 245));
                ReadinessStatusPill.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
                ReadinessStatusText.Text = "GO FOR FLIGHT";
                ReadinessStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 6, 95, 70));
            }
            else
            {
                ReadinessStatusPill.Background = new SolidColorBrush(Color.FromArgb(255, 254, 243, 199));
                ReadinessStatusPill.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 245, 158, 11));
                ReadinessStatusText.Text = "PRE-FLIGHT CHECK";
                ReadinessStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 146, 64, 14));
            }
        }
    }

    private static void SetChecklistText(TextBlock textBlock, bool ok, string label)
    {
        textBlock.Text = $"{(ok ? "✓" : "○")} {label}";
        textBlock.Foreground = new SolidColorBrush(ok
            ? Color.FromArgb(255, 6, 95, 70)
            : Color.FromArgb(255, 100, 116, 139));
    }

    private void RenderIncidentTimeline()
    {
        // IncidentTimelineStack removed — log is now inside Mission Log panel
    }

    private IEnumerable<(string Text, string Severity)> BuildRuntimeAlerts(TelemetryData? telemetry, bool connected)
    {
        if (!connected)
        {
            yield return ("Connection lost - MAVLink disconnected", "critical");
        }

        var satelliteCount = telemetry?.SatelliteCount ?? 0;
        var hasGps = telemetry != null && IsValidCoordinate(telemetry.Latitude, telemetry.Longitude);
        if (!hasGps || satelliteCount < 6)
        {
            yield return ($"GPS weak - sats {satelliteCount}", "warning");
        }

        var battery = telemetry?.BatteryPercent is > 0
            ? telemetry.BatteryPercent
            : telemetry?.BatteryRemaining ?? 0;
        if (battery > 0 && battery < 20)
        {
            yield return ($"Battery low - {battery:F0}%", battery < 12 ? "critical" : "warning");
        }

        if (_harvestFunctionalService?.IsYoloOptionEnabled == true)
        {
            yield return (_harvestFunctionalService.IsYoloRuntimeReady
                ? "YOLO offline runtime ready"
                : _harvestFunctionalService.YoloStatusMessage,
                _harvestFunctionalService.IsYoloRuntimeReady ? "info" : "warning");
        }

        yield return ($"Mission detections: {_lastDetectionCount}", _lastDetectionCount > 0 ? "info" : "warning");
    }

    // BuildGeofenceAlerts removed — geofence alerts no longer shown in alert center
    private IEnumerable<(string Text, string Severity)> BuildGeofenceAlerts_Disabled(TelemetryData? telemetry, bool hasGps)
    {
        var geofenceActive = _mapViewModel?.IsGeofenceActive == true ||
            _geofenceService?.CurrentGeofence.IsActive == true;

        if (!geofenceActive)
        {
            yield return ("Geofence inactive - set boundary in Map", "warning");
            yield break;
        }

        if (telemetry == null || !hasGps)
        {
            yield return ("Geofence active - waiting for valid GPS", "warning");
            yield break;
        }

        var altitude = telemetry.Altitude;
        double distanceToBoundary;
        if (_mapViewModel?.IsGeofenceActive == true)
        {
            distanceToBoundary = CalculateMapViewModelGeofenceDistance(telemetry.Latitude, telemetry.Longitude, altitude);
        }
        else if (_geofenceService != null)
        {
            distanceToBoundary = _geofenceService.CalculateDistanceToBoundary(telemetry.Latitude, telemetry.Longitude, altitude);
        }
        else
        {
            yield break;
        }

        if (distanceToBoundary < 0)
        {
            yield return ($"GEOFENCE BREACH - outside by {Math.Abs(distanceToBoundary):F0} m", "critical");
        }
        else if (distanceToBoundary < 30)
        {
            yield return ($"Geofence warning - {distanceToBoundary:F0} m to boundary", "warning");
        }
        else
        {
            yield return ($"Geofence safe - {distanceToBoundary:F0} m to boundary", "info");
        }
    }

    private double CalculateMapViewModelGeofenceDistance(double latitude, double longitude, double altitude)
    {
        if (_mapViewModel == null)
        {
            return double.MaxValue;
        }

        if (altitude > _mapViewModel.GeofenceMaxAltitude)
        {
            return -(altitude - _mapViewModel.GeofenceMaxAltitude);
        }

        if (_mapViewModel.GeofenceType == GeofenceType.Circular)
        {
            var distanceFromCenter = GeoMath.CalculateDistance(
                _mapViewModel.GeofenceCenterLat,
                _mapViewModel.GeofenceCenterLon,
                latitude,
                longitude);
            return _mapViewModel.GeofenceRadius - distanceFromCenter;
        }

        if (_mapViewModel.GeofenceVertices.Count < 3)
        {
            return double.MaxValue;
        }

        var inside = IsPointInPolygon(latitude, longitude, _mapViewModel.GeofenceVertices);
        var minDistance = double.MaxValue;
        for (var i = 0; i < _mapViewModel.GeofenceVertices.Count; i++)
        {
            var a = _mapViewModel.GeofenceVertices[i];
            var b = _mapViewModel.GeofenceVertices[(i + 1) % _mapViewModel.GeofenceVertices.Count];
            minDistance = Math.Min(minDistance, DistanceToSegmentMeters(latitude, longitude, a.Lat, a.Lon, b.Lat, b.Lon));
        }

        return inside ? minDistance : -minDistance;
    }

    private static bool IsPointInPolygon(double latitude, double longitude, IEnumerable<GeofenceVertex> vertices)
    {
        var points = vertices.ToList();
        var inside = false;
        var j = points.Count - 1;
        for (var i = 0; i < points.Count; i++)
        {
            var vi = points[i];
            var vj = points[j];
            if ((vi.Lon > longitude) != (vj.Lon > longitude) &&
                latitude < (vj.Lat - vi.Lat) * (longitude - vi.Lon) / (vj.Lon - vi.Lon) + vi.Lat)
            {
                inside = !inside;
            }
            j = i;
        }

        return inside;
    }

    private static double DistanceToSegmentMeters(double lat, double lon, double lat1, double lon1, double lat2, double lon2)
    {
        var x = lon;
        var y = lat;
        var x1 = lon1;
        var y1 = lat1;
        var x2 = lon2;
        var y2 = lat2;
        var dx = x2 - x1;
        var dy = y2 - y1;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((x - x1) * dx + (y - y1) * dy) / lengthSquared, 0, 1);
        var projectedLat = y1 + t * dy;
        var projectedLon = x1 + t * dx;
        return GeoMath.CalculateDistance(lat, lon, projectedLat, projectedLon);
    }

    private Border BuildAlertRow(string text, string time, string severity)
    {
        var tint = severity switch
        {
            "critical" => Color.FromArgb(255, 254, 242, 242),
            "warning" => Color.FromArgb(255, 255, 251, 235),
            _ => Color.FromArgb(255, 239, 246, 255)
        };
        var dotColor = severity switch
        {
            "critical" => Color.FromArgb(255, 211, 47, 47),
            "warning" => Color.FromArgb(255, 251, 192, 45),
            _ => Color.FromArgb(255, 25, 118, 210)
        };

        var border = new Border
        {
            Background = new SolidColorBrush(tint),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new XamlEllipse { Width = 8, Height = 8, Fill = new SolidColorBrush(dotColor), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 15, 48, 36)), TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(new TextBlock { Text = time, FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(255, 100, 118, 107)) });
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var ack = new Button
        {
            Content = "ACK",
            Background = new SolidColorBrush(Colors.Transparent),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 46, 125, 50)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 2, 6, 2),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        ack.Click += (_, _) => AlertsStack.Children.Remove(border);
        Grid.SetColumn(ack, 2);
        grid.Children.Add(ack);

        border.Child = grid;
        return border;
    }

    private void SeedSummary()
    {
        SummaryHealthyCount.Text = "0%";
        SummaryStressCount.Text  = "0%";
        SummaryDiseaseCount.Text = "0%";
        SummaryPestCount.Text    = "0%";
        if (SummaryWellIrrigatedCount != null) SummaryWellIrrigatedCount.Text = "0%";
        if (SummarySoilIssuesCount    != null) SummarySoilIssuesCount.Text    = "0%";
        SummaryPriorityText.Text   = "Standby";
        SummaryConfidenceText.Text = "0%";
        if (SummaryDominantText      != null) SummaryDominantText.Text      = "—";
        if (SummaryHealthyBar        != null) SummaryHealthyBar.Value        = 0;
        if (SummaryWellIrrigatedBar  != null) SummaryWellIrrigatedBar.Value  = 0;
        if (SummaryStressBar         != null) SummaryStressBar.Value         = 0;
        if (SummarySoilIssuesBar     != null) SummarySoilIssuesBar.Value     = 0;
        if (SummaryDiseaseBar        != null) SummaryDiseaseBar.Value        = 0;
        if (SummaryPestBar           != null) SummaryPestBar.Value           = 0;
    }

    private static bool IsValidCoordinate(double lat, double lon)
    {
        if (double.IsNaN(lat) || double.IsNaN(lon)) return false;
        if (lat is < -90 or > 90 || lon is < -180 or > 180) return false;
        return Math.Abs(lat) > 0.000001 || Math.Abs(lon) > 0.000001;
    }

    /// <summary>
    /// Updates the vegetation overlay gradient in real-time based on detection percentages.
    /// Green dominant → more green; drought/bareSoil dominant → more red/orange.
    /// This replaces the static hardcoded gradient so the overlay visually reflects analysis results.
    /// </summary>
    private void UpdateVegetationOverlay(double healthyPct, double stressPct, double droughtPct, double bareSoilPct)
    {
        if (VegetationOverlayLayer == null) return;
        if (VegToggleSwitch?.IsOn != true) return;

        // Normalize to 0-1 scale
        double total = Math.Max(1, healthyPct + stressPct + droughtPct + bareSoilPct);
        double h = healthyPct / total;
        double s = stressPct  / total;
        double d = droughtPct / total;
        double b = bareSoilPct / total;

        // Color stops:
        //   start = healthy zone color (green → amber → red depending on ratio)
        //   mid   = stress zone (amber)
        //   end   = drought/bare zone (red/brown)
        byte startA = 160;
        byte midA   = 120;
        byte endA   = 160;

        // Start color: interpolate green (healthy) → amber (stressed)
        byte startR = (byte)(46  + (s + d + b) * (230 - 46));
        byte startG = (byte)(125 - (d + b) * (125 - 60));
        byte startB = (byte)(50  - (d + b) * 30);

        // Mid color: amber, brighter when stress is high
        byte midR = (byte)(251);
        byte midG = (byte)(Math.Max(100, 192 - d * 150));
        byte midB = (byte)(45);

        // End color: orange-red for drought, brown for bare soil
        byte endR = (byte)(Math.Min(255, 185 + b * 50));
        byte endG = (byte)(Math.Max(20, 60 - b * 40));
        byte endB = (byte)(10);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint   = new Windows.Foundation.Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(startA, startR, startG, startB), Offset = 0.0 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(midA,   midR,   midG,   midB),   Offset = 0.5 + s * 0.2 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(endA,   endR,   endG,   endB),   Offset = 1.0 });

        VegetationOverlayLayer.Fill = brush;
    }

    private void UpdateTopBarAiFps(double fps)
    {
        // FPS shown in video HUD only (FpsHudText) — top bar FPS removed to avoid duplication
    }
}
