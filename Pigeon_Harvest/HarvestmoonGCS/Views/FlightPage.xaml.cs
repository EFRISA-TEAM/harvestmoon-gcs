using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Streams;
using HarvestmoonGCS.Core.ViewModels;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Models;
using HarvestmoonGCS.Core.Helpers;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Core.Services.Optimization;
using HarvestmoonGCS.Services;
using HarvestmoonGCS.Controls;
using HarvestmoonGCS.ViewModels;
using Serilog;
using MavLinkNet;
using ConnectionType = HarvestmoonGCS.Core.Models.ConnectionType;
#if ANDROID
using Android.Content.Res;
using AndroidApp = Android.App.Application;
using AndroidOrientation = Android.Content.Res.Orientation;
#endif

namespace HarvestmoonGCS.Views;

/// <summary>
/// Flight Page - Rewrite to match visual layout and HUD overlay of PIGEON.
/// </summary>
public sealed partial class FlightPage : Page, INotifyPropertyChanged
{
    private const int MaxConsoleMessages = 100;
    private readonly CameraYoloProcessor _yoloProcessor = new();
    private bool _yoloReady;
    private string _lastYoloSummary = "YOLO: IDLE";
    private FlightViewModel? _viewModel;
    private MapViewModel? _mapViewModel;
    private IMavLinkService? _mavLinkService;
    private IVideoScenarioService? _videoScenarioService;
    private IWaypointService? _waypointService;
    private RealTimeDataService? _realTimeDataService;
    private DispatcherTimer? _uiRefreshTimer;
    private readonly object _telemetrySync = new();
    private TelemetryData? _pendingTelemetryData;
    private bool _hasPendingTelemetryData;
    private readonly object _cameraFrameSync = new();
    private byte[]? _pendingFrameData;
    private bool _hasPendingFrameData;
    
    private DateTime _lastMapUpdate = DateTime.MinValue;
    private const int DefaultMapUpdateIntervalMs = 150;
    private DateTime _lastCameraFrameUiUpdate = DateTime.MinValue;
    private const int DefaultUiRefreshIntervalMs = 60;
    private const int DefaultCameraFrameIntervalMs = 80;
    private int _uiRefreshIntervalMs = DefaultUiRefreshIntervalMs;
    private int _mapUpdateIntervalMs = DefaultMapUpdateIntervalMs;
    private int _cameraFrameIntervalMs = DefaultCameraFrameIntervalMs;
    private readonly ObservabilityService? _observabilityService;
    private const double DefaultInstrumentSize = 128.0;
    private const double CompactInstrumentSize = 96.0;
    private const double NarrowInstrumentSize = 84.0;

    private ConnectionType _selectedConnectionType = ConnectionType.UDP;
    private bool _quickActionsVisible = true;
    private bool _consoleExpanded = false;
    private DateTime _lastTelemetryRenderTime = DateTime.MinValue;
    private TelemetryData? _targetTelemetryData;
    private TelemetryData? _renderTelemetryData;
    private const double DefaultTelemetrySmoothingRatePerSecond = 10.0;
    private double _telemetrySmoothingRatePerSecond = DefaultTelemetrySmoothingRatePerSecond;
    private IOptimizedTelemetryHandler? _optimizedTelemetryHandler;
    private IOptimizedRenderer? _optimizedRenderer;
    private DateTime _lastOptimizationSyncUtc = DateTime.MinValue;
    private DateTime _benchmarkStartedUtc = DateTime.UtcNow;
    private DateTime _lastBenchmarkReportUtc = DateTime.MinValue;
    private DateTime _lastBenchmarkConsoleUtc = DateTime.MinValue;
    private long _telemetryIngestedTotal;
    private long _telemetryRenderedTotal;
    private double _latestDispatchLatencyMs;
    private double _dispatchLatencyEmaMs;
    private bool _fpsBaselineCaptured;
    private double _fpsBaseline;
    private readonly List<ConsoleMessageEntry> _consoleAllMessages = new();
    private readonly ObservableCollection<ConsoleMessageEntry> _consoleVisibleMessages = new();
    private readonly object _consoleQueueSync = new();
    private readonly Queue<PendingConsoleMessage> _consolePendingMessages = new();
    private DateTime _lastConsoleAutoScrollTime = DateTime.MinValue;
    private bool _autoScrollEnabled = true;
    private int _infoCount;
    private int _warnCount;
    private int _errorCount;
    private const int ConsoleBatchFlushSize = 18;
    private const int ConsoleBatchFlushSizeWhenBacklogged = 48;
    private const int ConsoleBacklogThreshold = 120;
    private const int MaxPendingConsoleMessages = 400;
    private const int ConsoleAutoScrollIntervalMs = 180;

    private readonly SolidColorBrush _consoleInfoTypeBrush = new SolidColorBrush(Microsoft.UI.Colors.SkyBlue);
    private readonly SolidColorBrush _consoleWarnTypeBrush = new SolidColorBrush(Microsoft.UI.Colors.Goldenrod);
    private readonly SolidColorBrush _consoleErrorTypeBrush = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
    private readonly SolidColorBrush _consoleDebugTypeBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray);
    private readonly SolidColorBrush _consoleInfoBorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x4D, 0x3B, 0x82, 0xF6));
    private readonly SolidColorBrush _consoleWarnBorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x4D, 0xEA, 0xB3, 0x08));
    private readonly SolidColorBrush _consoleErrorBorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x4D, 0xEF, 0x44, 0x44));
    private readonly SolidColorBrush _consoleDebugBorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x4D, 0x75, 0x75, 0x75));
    private readonly SolidColorBrush _consoleInfoBackgroundBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x0D, 0x3B, 0x82, 0xF6));
    private readonly SolidColorBrush _consoleWarnBackgroundBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x0D, 0xEA, 0xB3, 0x08));
    private readonly SolidColorBrush _consoleErrorBackgroundBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x0D, 0xEF, 0x44, 0x44));
    private readonly SolidColorBrush _consoleDebugBackgroundBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x0D, 0x75, 0x75, 0x75));

    public FlightViewModel? ViewModel => _viewModel;

    public event PropertyChangedEventHandler? PropertyChanged;

    private sealed class ConsoleMessageEntry
    {
        public DateTime Timestamp { get; init; }
        public string TimeText => Timestamp.ToString("HH:mm:ss");
        public string Type { get; init; } = "INFO";
        public string Severity { get; init; } = "INFO";
        public SolidColorBrush TypeBrush { get; init; } = new SolidColorBrush(Microsoft.UI.Colors.SkyBlue);
        public SolidColorBrush BorderBrush { get; init; } = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x4D, 0x3B, 0x82, 0xF6)); // blue-500/30
        public SolidColorBrush BackgroundBrush { get; init; } = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x0D, 0x3B, 0x82, 0xF6)); // blue-500/5
        public string Message { get; init; } = string.Empty;
        public string Details { get; init; } = string.Empty;
        public Visibility DetailsVisibility => string.IsNullOrEmpty(Details) ? Visibility.Collapsed : Visibility.Visible;
        public string SeverityIcon { get; init; } = string.Empty;
    }

    private sealed class PendingConsoleMessage
    {
        public DateTime Timestamp { get; init; }
        public string Type { get; init; } = "INFO";
        public string Severity { get; init; } = "INFO";
        public string Message { get; init; } = string.Empty;
        public string Details { get; init; } = string.Empty;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public FlightPage()
    {
        InitializeComponent();
        this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        _observabilityService = App.Current.Services.GetService<ObservabilityService>();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        SizeChanged += OnFlightPageSizeChanged;

        _viewModel = App.Current.Services.GetService<FlightViewModel>();
        _mapViewModel = App.Current.Services.GetService<MapViewModel>();
        _mavLinkService = App.Current.Services.GetService<IMavLinkService>();
        _videoScenarioService = App.Current.Services.GetService<IVideoScenarioService>();
        if (_videoScenarioService != null)
        {
            _videoScenarioService.ScenarioChanged -= OnScenarioChanged;
            _videoScenarioService.ScenarioChanged += OnScenarioChanged;
        }
        _waypointService = App.Current.Services.GetService<IWaypointService>();
        _optimizedTelemetryHandler = App.Current.Services.GetService<IOptimizedTelemetryHandler>();
        _optimizedRenderer = App.Current.Services.GetService<IOptimizedRenderer>();
        _benchmarkStartedUtc = DateTime.UtcNow;
        _lastBenchmarkReportUtc = DateTime.MinValue;
        _lastBenchmarkConsoleUtc = DateTime.MinValue;
        _fpsBaselineCaptured = false;
        _fpsBaseline = 0;
        _telemetryIngestedTotal = 0;
        _telemetryRenderedTotal = 0;
        _latestDispatchLatencyMs = 0;
        _dispatchLatencyEmaMs = 0;
        if (_viewModel == null)
            return;

        if (_mavLinkService != null)
        {
            _mavLinkService.TelemetryReceived += OnMavLinkTelemetryReceived;
            _mavLinkService.MessageReceived += OnMavLinkMessageReceived;
            _mavLinkService.PacketReceived += OnMavLinkPacketReceived;
        }

        DataContext = this;


        // ARM / DISARM
        if (btn_arm_disarm != null)
        {
            btn_arm_disarm.Click += (_, __) =>
            {
                if (_viewModel.IsArmed)
                    _viewModel.DisarmCommand.Execute(null);
                else
                    _viewModel.ArmCommand.Execute(null);
            };
        }

        // Quick Actions Commands
        if (cb_flight_mode != null) cb_flight_mode.SelectionChanged += OnFlightModeSelectionChanged;
        if (btn_send_mission != null) btn_send_mission.Click += OnStartMissionClicked;
        if (btn_rtl != null) btn_rtl.Click += (s, e) => _viewModel.SendCommand(Command.RTL);
        if (btn_takeoff != null) btn_takeoff.Click += (s, e) => _viewModel.SendCommand(Command.TAKE_OFF);
        if (btn_land != null) btn_land.Click += (s, e) => _viewModel.SendCommand(Command.LAND);
        if (btn_toggle_quick_actions != null) btn_toggle_quick_actions.Click += OnToggleQuickActions;

        // Initialise YOLO for detection
        _yoloReady = _yoloProcessor.Initialize();
        _lastYoloSummary = _yoloReady ? "YOLO: READY" : $"YOLO: {_yoloProcessor.Status}";
        AddConsoleMessage("YOLO", _lastYoloSummary, _yoloReady ? "INFO" : "WARNING");

        // Camera shortcuts
        if (btn_camera_shortcut != null) btn_camera_shortcut.Click += OnStartStopStream;

        if (ConsoleMessagesList != null)
        {
            ConsoleMessagesList.ItemsSource = _consoleVisibleMessages;
        }
        if (btn_console_toggle != null) btn_console_toggle.Click += OnConsoleToggle;
        if (btn_console_clear != null) btn_console_clear.Click += OnConsoleClear;
        if (btn_console_export != null) btn_console_export.Click += OnConsoleExport;
        if (tb_console_search != null) tb_console_search.TextChanged += OnConsoleSearchChanged;
        if (cb_console_filter != null) cb_console_filter.SelectionChanged += OnConsoleFilterChanged;
        if (btn_console_filter != null) btn_console_filter.Click += OnConsoleFilterButtonClicked;
        if (btn_console_autoscroll != null) btn_console_autoscroll.Click += OnAutoScrollToggle;
        UpdateConsoleUiState();

        // Telemetry initial update
        UpdateConnectionStatus();
        UpdateArmButton();
        UpdateTelemetryFromViewModel();
        UpdateQuickActionsVisibility();

        // Set background image for camera control
        if (liveCam != null)
        {
            liveCam.SetBackgroundImage("ms-appx:///Assets/logo/dirgantara.png");
        }
        
        InitializeUiRefreshTimer();
        InitializeRealTimeDataService();
        InitializeMapControl();
        ApplyResponsiveLayout();

        var cameraService = App.Current.Services.GetService<ICameraService>();
        if (cameraService != null)
        {
            cameraService.FrameReceived += OnCameraFrameReceived;
            cameraService.StreamingStatusChanged += OnStreamingStatusChanged;
        }

        // RunCam WiFi Link 2: frame & status masuk otomatis setelah Connect.
        var runcamService = App.Current.Services.GetService<IRuncamWifiLinkService>();
        if (runcamService != null)
        {
            runcamService.FrameReceived += OnCameraFrameReceived;
            runcamService.StatusChanged += OnRuncamStatusChanged;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SizeChanged -= OnFlightPageSizeChanged;

        if (_uiRefreshTimer != null)
        {
            _uiRefreshTimer.Tick -= OnUiRefreshTimerTick;
            _uiRefreshTimer.Stop();
            _uiRefreshTimer = null;
        }

        if (_realTimeDataService != null)
        {
            _realTimeDataService.TelemetryReceived -= OnRealTimeTelemetryReceived;
            _realTimeDataService.ConnectionStatusChanged -= OnRealTimeConnectionStatusChanged;
            _realTimeDataService.ErrorOccurred -= OnRealTimeErrorOccurred;
        }

        if (_mavLinkService != null)
        {
            _mavLinkService.TelemetryReceived -= OnMavLinkTelemetryReceived;
            _mavLinkService.MessageReceived -= OnMavLinkMessageReceived;
            _mavLinkService.PacketReceived -= OnMavLinkPacketReceived;
            _mavLinkService = null;
        }

        _optimizedTelemetryHandler = null;
        _optimizedRenderer = null;
        _yoloProcessor.Dispose();
        _yoloReady = false;

        if (cb_flight_mode != null)
        {
            cb_flight_mode.SelectionChanged -= OnFlightModeSelectionChanged;
        }

        if (btn_toggle_quick_actions != null)
        {
            btn_toggle_quick_actions.Click -= OnToggleQuickActions;
        }

        if (btn_send_mission != null)
        {
            btn_send_mission.Click -= OnStartMissionClicked;
        }

        if (btn_console_toggle != null) btn_console_toggle.Click -= OnConsoleToggle;
        if (btn_console_clear != null) btn_console_clear.Click -= OnConsoleClear;
        if (btn_console_export != null) btn_console_export.Click -= OnConsoleExport;
        if (tb_console_search != null) tb_console_search.TextChanged -= OnConsoleSearchChanged;
        if (cb_console_filter != null) cb_console_filter.SelectionChanged -= OnConsoleFilterChanged;
        if (btn_console_filter != null) btn_console_filter.Click -= OnConsoleFilterButtonClicked;
        if (btn_console_autoscroll != null) btn_console_autoscroll.Click -= OnAutoScrollToggle;

        if (_videoScenarioService != null)
        {
            _videoScenarioService.ScenarioChanged -= OnScenarioChanged;
        }

        var cameraService = App.Current.Services.GetService<ICameraService>();
        if (cameraService != null)
        {
            cameraService.FrameReceived -= OnCameraFrameReceived;
            cameraService.StreamingStatusChanged -= OnStreamingStatusChanged;
        }

        var runcamService = App.Current.Services.GetService<IRuncamWifiLinkService>();
        if (runcamService != null)
        {
            runcamService.FrameReceived -= OnCameraFrameReceived;
            runcamService.StatusChanged -= OnRuncamStatusChanged;
        }

        Loaded += OnLoaded;

    }

    /// <summary>RunCam connect/disconnect → sinkronkan indikator live cam flight page.</summary>
    private void OnRuncamStatusChanged(object? sender, RuncamConnectionStatus status)
    {
        bool live = status is RuncamConnectionStatus.CameraOnly or RuncamConnectionStatus.FullyConnected;
        OnStreamingStatusChanged(this, live || (App.Current.Services.GetService<ICameraService>()?.IsStreaming ?? false));
    }

    private void OnFlightPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        double pageWidth = ActualWidth;
        if (double.IsNaN(pageWidth) || pageWidth <= 0)
        {
            if (XamlRoot?.Size.Width is > 0)
            {
                pageWidth = XamlRoot.Size.Width;
            }
            else
            {
                return;
            }
        }

        bool isCompact = IsAndroidPortrait() || pageWidth < 1100;
        bool isNarrow = pageWidth < 860;

        double instrumentSize = isNarrow
            ? NarrowInstrumentSize
            : isCompact
                ? CompactInstrumentSize
                : DefaultInstrumentSize;

        RightHudInstrumentsPanel.Margin = isNarrow
            ? new Thickness(0, 72, 8, 0)
            : isCompact
                ? new Thickness(0, 76, 10, 0)
                : new Thickness(0, 80, 16, 0);

        RightHudInstrumentsPanel.Spacing = isCompact ? 10 : 16;

        ApplyInstrumentSizing(AttitudeHost, ind_attitude, instrumentSize);
        ApplyInstrumentSizing(AirspeedHost, ind_airspeed, instrumentSize);
        ApplyInstrumentSizing(HeadingHost, ind_heading, instrumentSize);

        LeftHudActionsPanel.Visibility = isNarrow ? Visibility.Collapsed : Visibility.Visible;

        LiveCamHost.Width = isNarrow ? 140 : isCompact ? 160 : 192;
        LiveCamHost.Height = isNarrow ? 105 : isCompact ? 120 : 144;
        LiveCamHost.Margin = isNarrow
            ? new Thickness(8, 0, 0, 72)
            : isCompact
                ? new Thickness(10, 0, 0, 56)
                : new Thickness(16, 0, 0, 16);

        QuickActionsHost.Width = isNarrow ? 180 : isCompact ? 220 : 256;
        QuickActionsHost.Padding = isNarrow ? new Thickness(10) : isCompact ? new Thickness(12) : new Thickness(16);
        QuickActionsHost.Margin = isNarrow
            ? new Thickness(0, 0, 8, 72)
            : isCompact
                ? new Thickness(0, 0, 10, 56)
                : new Thickness(0, 0, 16, 16);

        QuickActionsPanel.Spacing = isCompact ? 10 : 16;

        if (MavConsoleHost != null)
        {
            MavConsoleHost.Width = _consoleExpanded ? (isNarrow ? 280 : isCompact ? 350 : 400) : double.NaN;
            MavConsoleHost.Margin = isNarrow
                ? new Thickness(0, 4, 8, 0)
                : isCompact
                    ? new Thickness(0, 4, 10, 0)
                    : new Thickness(0, 4, 84, 0);
            MavConsoleHost.MaxHeight = _consoleExpanded ? (isNarrow ? 200 : isCompact ? 250 : 300) : 40;
        }

        TelemetryScrollHost.Margin = isNarrow
            ? new Thickness(8, 0, 8, 8)
            : isCompact
                ? new Thickness(10, 0, 10, 10)
                : new Thickness(12, 0, 12, 12);

        TelemetryBar.HorizontalAlignment = isCompact ? HorizontalAlignment.Left : HorizontalAlignment.Center;
    }

    private static void ApplyInstrumentSizing(Border host, FrameworkElement instrument, double size)
    {
        host.Width = size;
        host.Height = size;
        host.CornerRadius = new CornerRadius(size / 2);
        instrument.Width = size;
        instrument.Height = size;
    }

    private static bool IsAndroidPortrait()
    {
#if ANDROID
        var orientation = AndroidApp.Context?.Resources?.Configuration?.Orientation;
        return orientation == AndroidOrientation.Portrait;
#else
        return false;
#endif
    }

    private void InitializeRealTimeDataService()
    {
        _realTimeDataService = App.Current.Services.GetService<IRealTimeDataService>() as RealTimeDataService;
        if (_realTimeDataService != null)
        {
            _realTimeDataService.TelemetryReceived += OnRealTimeTelemetryReceived;
            _realTimeDataService.ConnectionStatusChanged += OnRealTimeConnectionStatusChanged;
            _realTimeDataService.ErrorOccurred += OnRealTimeErrorOccurred;
        }
    }

    private void OnScenarioChanged(object? sender, VideoScenarioProfile scenario)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (scenario != null)
            {
                AddConsoleMessage("SCENARIO", $"Switched to: {scenario.Name} ({scenario.RegionName})", "INFO");
                if (_mapViewModel != null && scenario.Waypoints.Count > 0)
                {
                    _mapViewModel.Waypoints.Clear();
                    foreach (var wp in scenario.Waypoints) _mapViewModel.Waypoints.Add(wp);
                    _mapViewModel.GeofenceCenterLat = scenario.FieldCenterLat;
                    _mapViewModel.GeofenceCenterLon = scenario.FieldCenterLon;
                    _mapViewModel.GeofenceRadius = scenario.GeofenceRadiusMeters;
                }
                if (_waypointService != null && scenario.Waypoints.Count > 0)
                {
                    _ = _waypointService.ReplaceWaypointsAsync(scenario.Waypoints);
                }
                if (mapControl != null)
                {
                    mapControl.SetCenter(scenario.FieldCenterLat, scenario.FieldCenterLon, scenario.MapZoomLevel);
                    SyncMapFromViewModel();
                }
            }
        });
    }

    public void OnPageActivated()
    {
        var scenario = _videoScenarioService?.CurrentScenario;
        if (scenario != null)
        {
            if (_mapViewModel != null)
            {
                _mapViewModel.Waypoints.Clear();
                foreach (var wp in scenario.Waypoints) _mapViewModel.Waypoints.Add(wp);
                _mapViewModel.GeofenceCenterLat = scenario.FieldCenterLat;
                _mapViewModel.GeofenceCenterLon = scenario.FieldCenterLon;
                _mapViewModel.GeofenceRadius = scenario.GeofenceRadiusMeters;
            }
            if (mapControl != null)
            {
                mapControl.SetCenter(scenario.FieldCenterLat, scenario.FieldCenterLon, scenario.MapZoomLevel);
            }
        }
        SyncMapFromViewModel();
    }

    private void InitializeMapControl()
    {
        if (mapControl == null) return;
        var scenario = _videoScenarioService?.CurrentScenario;
        double initLat = (_viewModel?.Telemetry?.Latitude != null && _viewModel.Telemetry.Latitude != 0)
            ? _viewModel.Telemetry.Latitude
            : (scenario?.FieldCenterLat ?? -6.24361);
        double initLon = (_viewModel?.Telemetry?.Longitude != null && _viewModel.Telemetry.Longitude != 0)
            ? _viewModel.Telemetry.Longitude
            : (scenario?.FieldCenterLon ?? 107.36556);
        mapControl.SetCenter(initLat, initLon, 15);
        mapControl.SetFollowVehicle(true);
        mapControl.SetMapControlsVisible(false);
        SyncMapFromViewModel();
    }

    private void SyncMapFromViewModel()
    {
        if (mapControl == null) return;
        mapControl.ClearWaypoints();
        if (_mapViewModel?.Waypoints != null && _mapViewModel.Waypoints.Count > 0)
        {
            foreach (var wp in _mapViewModel.Waypoints.OrderBy(w => w.Sequence))
            {
                mapControl.AddWaypointMarker(wp.Sequence, wp.Latitude, wp.Longitude, wp.Altitude, wp.Command.ToString());
            }
        }
        if (_mapViewModel?.IsGeofenceActive == true)
        {
            mapControl.SetGeofence(true, _mapViewModel.GeofenceCenterLat, _mapViewModel.GeofenceCenterLon, _mapViewModel.GeofenceRadius);
        }
        else
        {
            mapControl.SetGeofence(true, 0, 0, 0);
        }
    }

    private async void OnTakeoffClicked(object sender, RoutedEventArgs e)
    {
        if (_mavLinkService == null || !_mavLinkService.IsConnected)
        {
            AddConsoleMessage("STATUSTEXT", "Vehicle not connected. Cannot takeoff.", "WARN");
            return;
        }
        btn_takeoff.IsEnabled = false;
        try
        {
            AddConsoleMessage("COMMAND", "Takeoff command sent (altitude: 10m)...");
            var success = await _mavLinkService.TakeoffAsync(10.0f);
            AddConsoleMessage("COMMAND_ACK", success ? "Takeoff accepted" : "Takeoff rejected", success ? "INFO" : "WARN");
        }
        catch (Exception ex)
        {
            AddConsoleMessage("STATUSTEXT", $"Takeoff failed: {ex.Message}", "ERROR");
        }
        finally
        {
            btn_takeoff.IsEnabled = true;
        }
    }

    private async void OnLandClicked(object sender, RoutedEventArgs e)
    {
        if (_mavLinkService == null || !_mavLinkService.IsConnected)
        {
            AddConsoleMessage("STATUSTEXT", "Vehicle not connected. Cannot land.", "WARN");
            return;
        }
        btn_land.IsEnabled = false;
        try
        {
            AddConsoleMessage("COMMAND", "Land command sent...");
            var success = await _mavLinkService.LandAsync();
            AddConsoleMessage("COMMAND_ACK", success ? "Land accepted" : "Land rejected", success ? "INFO" : "WARN");
        }
        catch (Exception ex)
        {
            AddConsoleMessage("STATUSTEXT", $"Land failed: {ex.Message}", "ERROR");
        }
        finally
        {
            btn_land.IsEnabled = true;
        }
    }

    private async void OnStartMissionClicked(object sender, RoutedEventArgs e)
    {
        if (_mavLinkService == null || !_mavLinkService.IsConnected)
        {
            AddConsoleMessage("STATUSTEXT", "Vehicle not connected. Cannot start mission.");
            return;
        }

        btn_send_mission.IsEnabled = false;
        try
        {
            AddConsoleMessage("MISSION_ITEM", "Start mission command requested...");
            var success = await _mavLinkService.StartMissionAsync(0, 0);
            AddConsoleMessage("COMMAND_ACK", success ? "Mission start accepted" : "Mission start rejected");
        }
        catch (Exception ex)
        {
            AddConsoleMessage("STATUSTEXT", $"Start mission failed: {ex.Message}");
        }
        finally
        {
            btn_send_mission.IsEnabled = true;
        }
    }

    private void OnRealTimeTelemetryReceived(object? sender, TelemetryData telemetryData)
    {
        _observabilityService?.Track("flight.telemetry.ingest");
        QueueTelemetryData(telemetryData);
    }

    private void OnMavLinkTelemetryReceived(object? sender, FlightData flightData)
    {
        _observabilityService?.Track("flight.telemetry.mavlink");

        var telemetryData = new TelemetryData
        {
            Timestamp = DateTime.UtcNow,
            Latitude = flightData.GPS.Latitude / 1e7,
            Longitude = flightData.GPS.Longitude / 1e7,
            Altitude = flightData.AltitudeFloat,
            // M1: RelativeAltitude dari GLOBAL_POSITION_INT.relative_alt (AGL), bukan MSL
            RelativeAltitude = flightData.Barometers > 0 ? flightData.Barometers : flightData.AltitudeFloat,
            Barometers = flightData.Barometers > 0 ? flightData.Barometers : flightData.AltitudeFloat,
            Roll = flightData.IMU.Roll,
            Pitch = flightData.IMU.Pitch,
            Yaw = flightData.IMU.Yaw,
            Heading = flightData.IMU.Yaw,
            // M2: GroundSpeed dari VFR_HUD.groundspeed, AirSpeed dari VFR_HUD.airspeed (berbeda)
            GroundSpeed = flightData.GroundSpeed > 0 ? flightData.GroundSpeed : flightData.Speed,
            AirSpeed = flightData.Speed,
            // H4: VerticalSpeed dari VFR_HUD.climb (m/s), bukan hardcoded 0
            VerticalSpeed = flightData.VerticalSpeed,
            BatteryVoltage = flightData.BatteryVolt,
            BatteryCurrent = flightData.BatteryCurr,
            BatteryRemaining = flightData.BatteryRemaining,
            BatteryPercentage = flightData.BatteryRemaining > 0 ? flightData.BatteryRemaining : 0,
            FlightMode = flightData.FlightMode,
            // H2: IsArmed dari BaseMode bit, bukan hanya FlightMode
            IsArmed = flightData.IsArmed,
            SatelliteCount = flightData.GPS.Sats,
            HDOP = flightData.Hdop / 100.0,
            SignalStrength = flightData.Signal,
            ThrottlePercent = flightData.ThrottlePercent
        };

        QueueTelemetryData(telemetryData);
    }

    private void QueueTelemetryData(TelemetryData telemetryData)
    {
        var nowUtc = DateTime.UtcNow;
        lock (_telemetrySync)
        {
            _pendingTelemetryData = telemetryData;
            _hasPendingTelemetryData = true;
        }

        Interlocked.Increment(ref _telemetryIngestedTotal);
        UpdateDispatchLatency(ComputeDispatchLatencyMs(telemetryData.Timestamp, nowUtc));
    }

    private void UpdateDispatchLatency(double latencyMs)
    {
        _latestDispatchLatencyMs = latencyMs;
        _dispatchLatencyEmaMs = _dispatchLatencyEmaMs <= 0
            ? latencyMs
            : (_dispatchLatencyEmaMs * 0.8) + (latencyMs * 0.2);
    }

    private static double ComputeDispatchLatencyMs(DateTime sourceTimestamp, DateTime nowUtc)
    {
        if (sourceTimestamp == DateTime.MinValue)
        {
            return 0;
        }

        var sourceUtc = sourceTimestamp.Kind == DateTimeKind.Utc
            ? sourceTimestamp
            : sourceTimestamp.ToUniversalTime();
        var latencyMs = (nowUtc - sourceUtc).TotalMilliseconds;
        if (latencyMs < 0)
        {
            return 0;
        }

        return Math.Min(latencyMs, 10_000);
    }

    private void OnRealTimeConnectionStatusChanged(object? sender, bool isConnected)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateConnectionStatus();
        });
    }

    private void OnRealTimeErrorOccurred(object? sender, string error)
    {
        System.Diagnostics.Debug.WriteLine($"[FlightPage] Real-time data error: {error}");
    }

    private void InitializeUiRefreshTimer()
    {
        if (_uiRefreshTimer != null)
        {
            _uiRefreshTimer.Tick -= OnUiRefreshTimerTick;
            _uiRefreshTimer.Stop();
        }

        _uiRefreshTimer = new DispatcherTimer();
        RefreshRuntimeOptimizationSettings(force: true);
        _uiRefreshTimer.Interval = TimeSpan.FromMilliseconds(_uiRefreshIntervalMs);
        _uiRefreshTimer.Tick += OnUiRefreshTimerTick;
        _uiRefreshTimer.Start();
    }

    private void OnUiRefreshTimerTick(object? sender, object e)
    {
        RefreshRuntimeOptimizationSettings();

        TelemetryData? pendingTelemetry = null;
        lock (_telemetrySync)
        {
            if (_hasPendingTelemetryData)
            {
                pendingTelemetry = _pendingTelemetryData;
                _pendingTelemetryData = null;
                _hasPendingTelemetryData = false;
            }
        }

        if (pendingTelemetry != null)
        {
            _targetTelemetryData = pendingTelemetry;
        }

        if (_targetTelemetryData != null)
        {
            RenderSmoothedTelemetry();
        }
        else
        {
            UpdateTelemetryFromViewModel();
        }

        ProcessPendingConsoleMessages();
        FlushPendingCameraFrame();
        ReportBenchmarkSnapshotIfDue();
    }

    private void RefreshRuntimeOptimizationSettings(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastOptimizationSyncUtc).TotalSeconds < 1.5)
        {
            return;
        }

        _lastOptimizationSyncUtc = now;
        var telemetryIntervalMs = Math.Clamp(
            _optimizedTelemetryHandler?.GetRecommendedDispatchIntervalMs() ?? DefaultUiRefreshIntervalMs,
            20,
            500);
        var rendererMetrics = _optimizedRenderer?.GetMetrics();
        var targetFps = Math.Clamp(rendererMetrics?.TargetFPS ?? 30, 12, 60);
        var renderIntervalMs = (int)Math.Round(1000.0 / targetFps);
        var nextUiIntervalMs = Math.Clamp(Math.Min(telemetryIntervalMs, renderIntervalMs), 20, 120);

        _uiRefreshIntervalMs = nextUiIntervalMs;
        _mapUpdateIntervalMs = Math.Clamp(_uiRefreshIntervalMs + 40, 80, 260);
        _cameraFrameIntervalMs = Math.Clamp(_uiRefreshIntervalMs + 20, 50, 220);
        _telemetrySmoothingRatePerSecond = Math.Clamp(targetFps * 0.35, 6.0, 18.0);

        if (_uiRefreshTimer != null &&
            Math.Abs(_uiRefreshTimer.Interval.TotalMilliseconds - _uiRefreshIntervalMs) >= 1)
        {
            _uiRefreshTimer.Interval = TimeSpan.FromMilliseconds(_uiRefreshIntervalMs);
        }
    }

    private void ReportBenchmarkSnapshotIfDue()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastBenchmarkReportUtc).TotalSeconds < 5)
        {
            return;
        }

        _lastBenchmarkReportUtc = now;
        var ingestTotal = Interlocked.Read(ref _telemetryIngestedTotal);
        var renderTotal = Interlocked.Read(ref _telemetryRenderedTotal);
        var elapsedSeconds = Math.Max(1, (now - _benchmarkStartedUtc).TotalSeconds);
        var ingestRate = ingestTotal / elapsedSeconds;
        var renderRate = renderTotal / elapsedSeconds;

        var telemetryStats = _optimizedTelemetryHandler?.GetStats();
        var totalProcessed = telemetryStats?.TotalPacketsProcessed ?? 0;
        var totalFiltered = telemetryStats?.TotalPacketsFiltered ?? 0;
        var processedOrDropped = totalProcessed + totalFiltered;
        var dropRatePercent = processedOrDropped > 0
            ? (totalFiltered * 100.0) / processedOrDropped
            : 0;

        var rendererMetrics = _optimizedRenderer?.GetMetrics();
        var fpsNow = rendererMetrics?.CurrentFPS > 0
            ? rendererMetrics.CurrentFPS
            : 1000.0 / Math.Max(1, _uiRefreshIntervalMs);
        var targetFps = rendererMetrics?.TargetFPS ?? 0;

        if (!_fpsBaselineCaptured && (now - _benchmarkStartedUtc).TotalSeconds >= 8 && fpsNow > 0)
        {
            _fpsBaselineCaptured = true;
            _fpsBaseline = fpsNow;
            AddConsoleMessage("PERF", $"Baseline benchmark captured at {_fpsBaseline:F1} FPS", "DEBUG");
        }

        var fpsCompare = _fpsBaselineCaptured
            ? $"{_fpsBaseline:F1}->{fpsNow:F1}"
            : $"warmup->{fpsNow:F1}";
        var latencyMs = _dispatchLatencyEmaMs > 0 ? _dispatchLatencyEmaMs : _latestDispatchLatencyMs;

        var summary =
            $"Drop {dropRatePercent:F1}% | Lat {latencyMs:F1}ms | FPS {fpsCompare} (target {targetFps}) | " +
            $"In {ingestRate:F1}/s Out {renderRate:F1}/s | UI {_uiRefreshIntervalMs}ms";

        Log.Information("[FlightPerf] {Summary}", summary);
        _observabilityService?.Track("flight.benchmark.tick");
        if (dropRatePercent >= 15)
        {
            _observabilityService?.Track("flight.benchmark.drop_high");
        }
        if (latencyMs >= 250)
        {
            _observabilityService?.Track("flight.benchmark.latency_high");
        }
        if (targetFps > 0 && fpsNow < (targetFps * 0.6))
        {
            _observabilityService?.Track("flight.benchmark.fps_low");
        }

        if ((now - _lastBenchmarkConsoleUtc).TotalSeconds >= 15)
        {
            _lastBenchmarkConsoleUtc = now;
            AddConsoleMessage("PERF", summary, "DEBUG");
        }
    }

    private void UpdateTelemetryFromViewModel()
    {
        if (_viewModel?.TelemetryData == null) return;
        UpdateTelemetry(_viewModel.TelemetryData);
    }

    private void UpdateTelemetry(TelemetryData t)
    {
        if (t == null) return;

        Interlocked.Increment(ref _telemetryRenderedTotal);
        _observabilityService?.Track("flight.telemetry.apply");

        // Update Stats Bar — existing fields
        if (tb_alt != null) tb_alt.Text = t.Altitude.ToString("F0");
        if (tb_speed != null) tb_speed.Text = t.AirSpeed.ToString("F1");
        if (tb_roll != null) tb_roll.Text = t.Roll.ToString("F1");
        if (tb_pitch != null) tb_pitch.Text = t.Pitch.ToString("F1");
        if (tb_heading != null) tb_heading.Text = t.Heading.ToString("F0");

        int rssiPercent = Math.Clamp(t.SignalStrength, 0, 100);
        if (tb_rssi != null) tb_rssi.Text = rssiPercent.ToString();

        // P1: Satellite count + GPS fix quality
        if (tb_sats != null) tb_sats.Text = t.SatelliteCount.ToString();
        if (tb_gps_fix != null)
        {
            tb_gps_fix.Text = t.GPSFixType switch
            {
                >= 15 => "RTK FIXED",
                6     => "RTK FLOAT",
                5     => "3D+SBAS",
                4     => "DGPS",
                3     => "3D FIX",
                2     => "2D FIX",
                1     => "NO GPS",
                _     => "NO FIX"
            };
            tb_gps_fix.Foreground = t.GPSFixType >= 3
                ? new SolidColorBrush(Microsoft.UI.Colors.LimeGreen)
                : new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
        }

        // P1: Battery percentage + voltage
        if (tb_battery != null)
        {
            var batPct = t.BatteryRemaining > 0 ? t.BatteryRemaining : (int)t.BatteryPercentage;
            tb_battery.Text = batPct > 0 ? batPct.ToString() : "--";
            tb_battery.Foreground = batPct switch
            {
                > 50 => new SolidColorBrush(Microsoft.UI.Colors.LimeGreen),
                > 20 => new SolidColorBrush(Microsoft.UI.Colors.Orange),
                _    => new SolidColorBrush(Microsoft.UI.Colors.OrangeRed)
            };
        }
        if (tb_batt_volt != null && t.BatteryVoltage > 0)
            tb_batt_volt.Text = $"{t.BatteryVoltage:F1}V";

        // P1: Vertical speed (climb rate)
        if (tb_vspeed != null)
        {
            tb_vspeed.Text = t.VerticalSpeed.ToString("F1");
            tb_vspeed.Foreground = t.VerticalSpeed > 0.2
                ? new SolidColorBrush(Microsoft.UI.Colors.LimeGreen)
                : t.VerticalSpeed < -0.2
                    ? new SolidColorBrush(Microsoft.UI.Colors.OrangeRed)
                    : new SolidColorBrush(Microsoft.UI.Colors.White);
        }

        // P1: Flight mode + ARM status
        if (tb_flight_mode != null)
            tb_flight_mode.Text = t.FlightMode.ToString().Replace("_", " ");
        if (tb_arm_status != null)
        {
            tb_arm_status.Text = t.IsArmed ? "ARMED" : "DISARMED";
            tb_arm_status.Foreground = t.IsArmed
                ? new SolidColorBrush(Microsoft.UI.Colors.OrangeRed)
                : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x9C, 0xA3, 0xAF));
        }

        // Update avionics instruments
        if (ind_attitude != null)
        {
            ind_attitude.PitchAngle = (float)t.Pitch;
            ind_attitude.RollAngle = (float)t.Roll;
        }
        
        if (ind_airspeed != null) ind_airspeed.Airspeed = (int)t.AirSpeed;
        if (ind_heading != null) ind_heading.Heading = (int)t.Yaw;
        
        // Update arm button color
        UpdateArmButton();

        // Update map
        if (mapControl != null && Math.Abs(t.Latitude) > 1e-6 && Math.Abs(t.Longitude) > 1e-6)
        {
            var now = DateTime.Now;
            if ((now - _lastMapUpdate).TotalMilliseconds >= _mapUpdateIntervalMs)
            {
                mapControl.UpdateVehiclePosition(t.Latitude, t.Longitude);
                _lastMapUpdate = now;
                _observabilityService?.Track("flight.map.render");
            }
        }
    }

    private void RenderSmoothedTelemetry()
    {
        if (_targetTelemetryData == null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var deltaSeconds = _lastTelemetryRenderTime == DateTime.MinValue
            ? _uiRefreshIntervalMs / 1000.0
            : Math.Max(0.001, (now - _lastTelemetryRenderTime).TotalSeconds);
        _lastTelemetryRenderTime = now;

        if (_renderTelemetryData == null)
        {
            _renderTelemetryData = CloneTelemetry(_targetTelemetryData);
            UpdateTelemetry(_renderTelemetryData);
            return;
        }

        var alpha = 1.0 - Math.Exp(-_telemetrySmoothingRatePerSecond * deltaSeconds);
        alpha = Math.Clamp(alpha, 0.08, 0.7);

        _renderTelemetryData = BlendTelemetry(_renderTelemetryData, _targetTelemetryData, alpha);
        UpdateTelemetry(_renderTelemetryData);

        if (IsTelemetryClose(_renderTelemetryData, _targetTelemetryData))
        {
            _renderTelemetryData = CloneTelemetry(_targetTelemetryData);
        }
    }

    private static TelemetryData CloneTelemetry(TelemetryData source)
    {
        return new TelemetryData
        {
            Timestamp = source.Timestamp,
            Latitude = source.Latitude,
            Longitude = source.Longitude,
            Altitude = source.Altitude,
            RelativeAltitude = source.RelativeAltitude,
            Roll = source.Roll,
            Pitch = source.Pitch,
            Yaw = source.Yaw,
            Heading = source.Heading,
            GroundSpeed = source.GroundSpeed,
            AirSpeed = source.AirSpeed,
            VerticalSpeed = source.VerticalSpeed,
            Barometers = source.Barometers,
            BatteryVoltage = source.BatteryVoltage,
            BatteryCurrent = source.BatteryCurrent,
            BatteryRemaining = source.BatteryRemaining,
            FlightMode = source.FlightMode,
            IsArmed = source.IsArmed,
            SatelliteCount = source.SatelliteCount,
            HDOP = source.HDOP,
            SignalStrength = source.SignalStrength,
            ThrottlePercent = source.ThrottlePercent,
            BatteryPercentage = source.BatteryPercentage,
            GPSFixType = source.GPSFixType,
            Speed = source.Speed
        };
    }

    private static TelemetryData BlendTelemetry(TelemetryData from, TelemetryData to, double alpha)
    {
        static double Lerp(double a, double b, double t) => a + ((b - a) * t);
        static int LerpInt(int a, int b, double t) => (int)Math.Round(a + ((b - a) * t));

        static double LerpAngle(double fromAngle, double toAngle, double t)
        {
            var delta = ((toAngle - fromAngle + 540.0) % 360.0) - 180.0;
            return (fromAngle + (delta * t) + 360.0) % 360.0;
        }

        return new TelemetryData
        {
            Timestamp = to.Timestamp,
            Latitude = Lerp(from.Latitude, to.Latitude, alpha),
            Longitude = Lerp(from.Longitude, to.Longitude, alpha),
            Altitude = Lerp(from.Altitude, to.Altitude, alpha),
            RelativeAltitude = Lerp(from.RelativeAltitude, to.RelativeAltitude, alpha),
            Roll = Lerp(from.Roll, to.Roll, alpha),
            Pitch = Lerp(from.Pitch, to.Pitch, alpha),
            Yaw = LerpAngle(from.Yaw, to.Yaw, alpha),
            Heading = LerpAngle(from.Heading, to.Heading, alpha),
            GroundSpeed = Lerp(from.GroundSpeed, to.GroundSpeed, alpha),
            AirSpeed = Lerp(from.AirSpeed, to.AirSpeed, alpha),
            VerticalSpeed = Lerp(from.VerticalSpeed, to.VerticalSpeed, alpha),
            Barometers = Lerp(from.Barometers, to.Barometers, alpha),
            BatteryVoltage = Lerp(from.BatteryVoltage, to.BatteryVoltage, alpha),
            BatteryCurrent = Lerp(from.BatteryCurrent, to.BatteryCurrent, alpha),
            BatteryRemaining = LerpInt(from.BatteryRemaining, to.BatteryRemaining, alpha),
            FlightMode = to.FlightMode,
            IsArmed = to.IsArmed,
            SatelliteCount = LerpInt(from.SatelliteCount, to.SatelliteCount, alpha),
            HDOP = Lerp(from.HDOP, to.HDOP, alpha),
            SignalStrength = LerpInt(from.SignalStrength, to.SignalStrength, alpha),
            ThrottlePercent = LerpInt(from.ThrottlePercent, to.ThrottlePercent, alpha),
            BatteryPercentage = Lerp(from.BatteryPercentage, to.BatteryPercentage, alpha),
            GPSFixType = to.GPSFixType,
            Speed = Lerp(from.Speed, to.Speed, alpha)
        };
    }

    private static bool IsTelemetryClose(TelemetryData current, TelemetryData target)
    {
        return Math.Abs(current.Latitude - target.Latitude) < 0.000001 &&
               Math.Abs(current.Longitude - target.Longitude) < 0.000001 &&
               Math.Abs(current.Altitude - target.Altitude) < 0.2 &&
               Math.Abs(current.Roll - target.Roll) < 0.2 &&
               Math.Abs(current.Pitch - target.Pitch) < 0.2 &&
               Math.Abs(current.AirSpeed - target.AirSpeed) < 0.1 &&
               Math.Abs(current.Heading - target.Heading) < 0.5;
    }

    private void UpdateConnectionStatus()
    {
        if (_viewModel == null)
        {
            return;
        }

        bool isConnected = _viewModel.IsConnected;
        if (_selectedConnectionType == ConnectionType.WebSocket && _realTimeDataService != null)
        {
            isConnected = _realTimeDataService.IsConnected;
        }
    }

    private void UpdateArmButton()
    {
        if (_viewModel == null) return;
        
        bool isArmed = _viewModel.IsArmed;
        if (btn_arm_disarm != null)
        {
            btn_arm_disarm.Content = isArmed ? "DISARM" : "ARM";
            btn_arm_disarm.Background = isArmed
                ? new SolidColorBrush(Microsoft.UI.Colors.OrangeRed)
                : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xEF, 0x44, 0x44));
        }
        
    }

    private void OnToggleConnection(object? sender, EventArgs e)
    {
        if (_viewModel == null) return;
        
        if (_viewModel.IsConnected)
        {
            _viewModel.Disconnect();
        }
        else
        {
            _viewModel.Connect();
        }
        
        UpdateConnectionStatus();
    }

    private async void OnFlightModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel == null || cb_flight_mode?.SelectedItem is not ComboBoxItem selectedItem)
        {
            return;
        }

        var mode = selectedItem.Content?.ToString()?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(mode) || !_viewModel.IsConnected)
        {
            return;
        }

        try
        {
            await _viewModel.SetFlightModeAsync(mode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to set flight mode from quick actions: {Mode}", mode);
        }
    }

    private void OnToggleQuickActions(object sender, RoutedEventArgs e)
    {
        _quickActionsVisible = !_quickActionsVisible;
        UpdateQuickActionsVisibility();
    }

    private void OnMavLinkMessageReceived(object? sender, string message)
    {
        AddConsoleMessage(GuessMessageType(message), message, "INFO");
    }

    private void OnMavLinkPacketReceived(object? sender, MavLinkPacketBase packet)
    {
        var (type, severity, formatted) = FormatPacketForConsole(packet);
        AddConsoleMessage(type, formatted, severity);
    }

    private void AddConsoleMessage(string type, string message, string severity = "INFO", string? details = null)
    {
        var pending = new PendingConsoleMessage
        {
            Timestamp = DateTime.Now,
            Type = string.IsNullOrWhiteSpace(type) ? "INFO" : type,
            Severity = string.IsNullOrWhiteSpace(severity) ? "INFO" : severity.ToUpperInvariant(),
            Message = message ?? string.Empty,
            Details = details ?? string.Empty
        };

        lock (_consoleQueueSync)
        {
            _consolePendingMessages.Enqueue(pending);
            while (_consolePendingMessages.Count > MaxPendingConsoleMessages)
            {
                _consolePendingMessages.Dequeue();
            }
        }
    }

    private void ProcessPendingConsoleMessages()
    {
        List<PendingConsoleMessage>? batch = null;
        lock (_consoleQueueSync)
        {
            if (_consolePendingMessages.Count == 0)
            {
                return;
            }

            var batchSize = _consolePendingMessages.Count >= ConsoleBacklogThreshold
                ? ConsoleBatchFlushSizeWhenBacklogged
                : ConsoleBatchFlushSize;

            batch = new List<PendingConsoleMessage>(Math.Min(batchSize, _consolePendingMessages.Count));
            while (batch.Count < batchSize && _consolePendingMessages.Count > 0)
            {
                batch.Add(_consolePendingMessages.Dequeue());
            }
        }

        foreach (var pending in batch)
        {
            AppendConsoleMessage(pending);
        }

        ApplyConsoleFilter();
    }

    private void AppendConsoleMessage(PendingConsoleMessage pending)
    {
        var entry = new ConsoleMessageEntry
        {
            Timestamp = pending.Timestamp,
            Type = pending.Type,
            Severity = pending.Severity,
            TypeBrush = ResolveSeverityBrush(pending.Severity),
            BorderBrush = ResolveBorderBrush(pending.Severity),
            BackgroundBrush = ResolveBackgroundBrush(pending.Severity),
            Message = pending.Message,
            Details = pending.Details,
            SeverityIcon = ResolveSeverityIcon(pending.Severity)
        };

        _consoleAllMessages.Add(entry);
        AdjustSeverityCounter(entry.Severity, +1);
        if (_consoleAllMessages.Count > MaxConsoleMessages)
        {
            var removed = _consoleAllMessages[0];
            _consoleAllMessages.RemoveAt(0);
            AdjustSeverityCounter(removed.Severity, -1);
        }
    }

    private void AdjustSeverityCounter(string severity, int delta)
    {
        var normalized = severity.ToUpperInvariant();
        if (normalized is "EMERGENCY" or "ALERT" or "CRITICAL" or "ERROR")
        {
            _errorCount = Math.Max(0, _errorCount + delta);
            return;
        }

        if (normalized is "WARNING" or "NOTICE")
        {
            _warnCount = Math.Max(0, _warnCount + delta);
            return;
        }

        _infoCount = Math.Max(0, _infoCount + delta);
    }

    private SolidColorBrush ResolveSeverityBrush(string severity)
    {
        return severity.ToUpperInvariant() switch
        {
            "EMERGENCY" or "ALERT" or "CRITICAL" or "ERROR" => _consoleErrorTypeBrush,
            "WARNING" or "NOTICE" => _consoleWarnTypeBrush,
            "DEBUG" => _consoleDebugTypeBrush,
            _ => _consoleInfoTypeBrush
        };
    }

    private SolidColorBrush ResolveBorderBrush(string severity)
    {
        return severity.ToUpperInvariant() switch
        {
            "EMERGENCY" or "ALERT" or "CRITICAL" or "ERROR" => _consoleErrorBorderBrush,
            "WARNING" or "NOTICE" => _consoleWarnBorderBrush,
            "DEBUG" => _consoleDebugBorderBrush,
            _ => _consoleInfoBorderBrush
        };
    }

    private SolidColorBrush ResolveBackgroundBrush(string severity)
    {
        return severity.ToUpperInvariant() switch
        {
            "EMERGENCY" or "ALERT" or "CRITICAL" or "ERROR" => _consoleErrorBackgroundBrush,
            "WARNING" or "NOTICE" => _consoleWarnBackgroundBrush,
            "DEBUG" => _consoleDebugBackgroundBrush,
            _ => _consoleInfoBackgroundBrush
        };
    }

    private string ResolveSeverityIcon(string severity)
    {
        return severity.ToUpperInvariant() switch
        {
            "EMERGENCY" or "ALERT" or "CRITICAL" or "ERROR" => "\xE7BA",
            "WARNING" or "NOTICE" => "\xE7BA",
            _ => "\xE946"
        };
    }

    private static string GetSeverityLabel(MavSeverity severity)
    {
        return severity switch
        {
            MavSeverity.Emergency => "EMERGENCY",
            MavSeverity.Alert => "ALERT",
            MavSeverity.Critical => "CRITICAL",
            MavSeverity.Error => "ERROR",
            MavSeverity.Warning => "WARNING",
            MavSeverity.Notice => "NOTICE",
            MavSeverity.Info => "INFO",
            MavSeverity.Debug => "DEBUG",
            _ => "INFO"
        };
    }

    private static string ExtractCharArrayText(char[] chars)
    {
        return new string(chars).TrimEnd('\0').Trim();
    }

    private static (string Type, string Severity, string Message) FormatPacketForConsole(MavLinkPacketBase packet)
    {
        var sysComp = $"SYS:{packet.SystemId}/COMP:{packet.ComponentId}";
        var message = packet.Message;

        if (message is UasStatustext statusText)
        {
            var severity = GetSeverityLabel(statusText.Severity);
            var text = ExtractCharArrayText(statusText.Text);
            return ("STATUSTEXT", severity, $"[{sysComp}] {text}");
        }

        if (message is UasCommandAck commandAck)
        {
            var result = commandAck.Result.ToString().ToUpperInvariant();
            var severity = (result.Contains("FAILED") || result.Contains("DENIED") || result.Contains("UNSUPPORTED")) ? "ERROR" : "INFO";
            return ("COMMAND_ACK", severity, $"[{sysComp}] CMD={commandAck.Command} RESULT={commandAck.Result}");
        }

        if (message is UasHeartbeat heartbeat)
        {
            return ("HEARTBEAT", "INFO", $"[{sysComp}] TYPE={heartbeat.Type} MODE={heartbeat.CustomMode} STATUS={heartbeat.SystemStatus}");
        }

        if (message is UasSysStatus sysStatus)
        {
            return ("SYS_STATUS", "INFO", $"[{sysComp}] VBat={sysStatus.VoltageBattery / 1000.0:F2}V Current={sysStatus.CurrentBattery / 100.0:F1}A Rem={sysStatus.BatteryRemaining}%");
        }

        if (message is UasGpsRawInt gps)
        {
            return ("GPS_RAW_INT", "INFO", $"[{sysComp}] FIX={gps.FixType} SAT={gps.SatellitesVisible} HDOP={gps.Eph / 100.0:F2}");
        }

        if (message is UasParamValue paramValue)
        {
            var name = ExtractCharArrayText(paramValue.ParamId);
            return ("PARAM_VALUE", "DEBUG", $"[{sysComp}] {name}={paramValue.ParamValue}");
        }

        if (message is UasMissionItemInt missionItemInt)
        {
            return ("MISSION_ITEM_INT", "INFO", $"[{sysComp}] SEQ={missionItemInt.Seq} CMD={missionItemInt.Command} LAT={missionItemInt.X / 1e7:F6} LON={missionItemInt.Y / 1e7:F6} ALT={missionItemInt.Z:F1}");
        }

        if (message is UasMissionItem missionItem)
        {
            return ("MISSION_ITEM", "INFO", $"[{sysComp}] SEQ={missionItem.Seq} CMD={missionItem.Command} LAT={missionItem.X:F6} LON={missionItem.Y:F6} ALT={missionItem.Z:F1}");
        }

        if (message is UasBatteryStatus batteryStatus)
        {
            var v0 = batteryStatus.Voltages != null && batteryStatus.Voltages.Length > 0
                ? batteryStatus.Voltages[0] / 1000.0
                : 0.0;
            return ("BATTERY_STATUS", "INFO", $"[{sysComp}] BAT#{batteryStatus.Id} V0={v0:F2}V Rem={batteryStatus.BatteryRemaining}% Temp={batteryStatus.Temperature / 100.0:F1}°C");
        }

        var type = message?.GetType().Name?.Replace("Uas", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant() ?? $"MSG_{packet.MessageId}";
        return (type, "DEBUG", $"[{sysComp}] msgid={packet.MessageId} seq={packet.PacketSequenceNumber}");
    }

    private static string GuessMessageType(string message)
    {
        var text = message?.ToUpperInvariant() ?? string.Empty;
        if (text.Contains("HEARTBEAT")) return "HEARTBEAT";
        if (text.Contains("GPS")) return "GPS_RAW_INT";
        if (text.Contains("ATTITUDE") || text.Contains("ROLL") || text.Contains("PITCH") || text.Contains("YAW")) return "ATTITUDE";
        if (text.Contains("VFR") || text.Contains("AIRSPEED") || text.Contains("THROTTLE")) return "VFR_HUD";
        if (text.Contains("MISSION")) return "MISSION_ITEM";
        if (text.Contains("PARAM")) return "PARAM_VALUE";
        if (text.Contains("BATTERY") || text.Contains("VOLT")) return "BATTERY_STATUS";
        if (text.Contains("ACK") || text.Contains("COMMAND")) return "COMMAND_ACK";
        return "STATUSTEXT";
    }

    private void OnConsoleToggle(object sender, RoutedEventArgs e)
    {
        _consoleExpanded = !_consoleExpanded;
        UpdateConsoleUiState();
    }

    private void OnConsoleClear(object sender, RoutedEventArgs e)
    {
        lock (_consoleQueueSync)
        {
            _consolePendingMessages.Clear();
        }
        _consoleAllMessages.Clear();
        _infoCount = 0;
        _warnCount = 0;
        _errorCount = 0;
        ApplyConsoleFilter(allowAutoScroll: false);
    }

    private async void OnConsoleExport(object sender, RoutedEventArgs e)
    {
        ProcessPendingConsoleMessages();
        try
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var path = Path.Combine(folder, $"mavlink_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            var builder = new StringBuilder();
            foreach (var msg in _consoleAllMessages)
            {
                builder.Append('[')
                    .Append(msg.Timestamp.ToString("HH:mm:ss"))
                    .Append("] ")
                    .Append(msg.Type)
                    .Append(" - ")
                    .AppendLine(msg.Message);
            }

            await File.WriteAllTextAsync(path, builder.ToString());
            AddConsoleMessage("STATUSTEXT", $"Console exported: {path}");
        }
        catch (Exception ex)
        {
            AddConsoleMessage("STATUSTEXT", $"Export failed: {ex.Message}");
        }
    }

    private void OnConsoleSearchChanged(object sender, TextChangedEventArgs e)
    {
        ApplyConsoleFilter(allowAutoScroll: false);
    }

    private void OnConsoleFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyConsoleFilter(allowAutoScroll: false);
    }

    private void OnAutoScrollToggle(object sender, RoutedEventArgs e)
    {
        _autoScrollEnabled = !_autoScrollEnabled;
        if (btn_console_autoscroll != null)
        {
            btn_console_autoscroll.Background = _autoScrollEnabled
                ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x16, 0xA1, 0x85))
                : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x33, 0x33, 0x33));
        }
        ApplyConsoleFilter(allowAutoScroll: false);
    }

    private void OnConsoleFilterButtonClicked(object sender, RoutedEventArgs e)
    {
        ShowFilterPopup();
    }

    private void ApplyConsoleFilter(bool allowAutoScroll = true)
    {
        var search = tb_console_search?.Text?.Trim() ?? string.Empty;
        var selectedFilter = (cb_console_filter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "ALL";

        IEnumerable<ConsoleMessageEntry> filtered = _consoleAllMessages;

        if (!string.Equals(selectedFilter, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(m => string.Equals(m.Type, selectedFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(m =>
                m.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                m.Type.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                m.Details.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        _consoleVisibleMessages.Clear();
        foreach (var item in filtered)
        {
            _consoleVisibleMessages.Add(item);
        }

        if (tb_console_count != null)
        {
            tb_console_count.Text = _consoleAllMessages.Count.ToString();
        }

        if (tb_filtered_count != null && ConsoleFilterCount != null)
        {
            var showFiltered = !_consoleExpanded && _consoleVisibleMessages.Count != _consoleAllMessages.Count;
            ConsoleFilterCount.Visibility = showFiltered ? Visibility.Visible : Visibility.Collapsed;
            tb_filtered_count.Text = _consoleVisibleMessages.Count.ToString();
        }

        if (tb_info_count != null)
        {
            tb_info_count.Text = $"Info: {_infoCount}";
        }

        if (tb_warn_count != null)
        {
            tb_warn_count.Text = $"Warn: {_warnCount}";
        }

        if (tb_error_count != null)
        {
            tb_error_count.Text = $"Error: {_errorCount}";
        }

        if (tb_console_footer != null)
        {
            tb_console_footer.Text = $"Total: {_consoleVisibleMessages.Count}/{_consoleAllMessages.Count}";
        }

        if (allowAutoScroll && _autoScrollEnabled && _consoleVisibleMessages.Count > 0)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastConsoleAutoScrollTime).TotalMilliseconds >= ConsoleAutoScrollIntervalMs)
            {
                ConsoleMessagesList?.ScrollIntoView(_consoleVisibleMessages[_consoleVisibleMessages.Count - 1]);
                _lastConsoleAutoScrollTime = now;
            }
        }
    }

    private void ShowFilterPopup()
    {
    }

    private void UpdateConsoleUiState()
    {
        if (ConsoleContent != null)
        {
            ConsoleContent.Visibility = _consoleExpanded ? Visibility.Visible : Visibility.Collapsed;
        }

        if (btn_console_toggle != null)
        {
            var icon = new FontIcon
            {
                Glyph = _consoleExpanded ? "\xE70D" : "\xE76C",
                FontSize = 14
            };
            btn_console_toggle.Content = icon;
        }

        if (MavConsoleHost != null)
        {
            MavConsoleHost.MaxHeight = _consoleExpanded ? 300 : 40;
            MavConsoleHost.Width = _consoleExpanded ? 320 : double.NaN;
        }
    }

    private void UpdateQuickActionsVisibility()
    {
        if (QuickActionsContent != null)
        {
            QuickActionsContent.Visibility = _quickActionsVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        if (btn_toggle_quick_actions != null)
        {
            btn_toggle_quick_actions.Content = _quickActionsVisible ? "▾" : "▸";
        }
    }

    // Bundled assets come first so demo playback works on any machine/OS; the absolute
    // /home/... entries are a convenience fallback for the developer's own checkout.
    private static readonly string[] _demoVideoCandidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "Assets", "demo_videos", "stream_multisector_60s.mp4"),
        Path.Combine(AppContext.BaseDirectory, "Assets", "demo_videos", "stream_v7c_final.mp4"),
        Path.Combine(AppContext.BaseDirectory, "Assets", "demo_videos", "YDXJ_fused_only_detected.mp4"),
        "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/stream_multisector_60s.mp4",
        "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/demo_videos/stream_multisector_60s.mp4",
        "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/stream_v7c_final.mp4",
        "/home/fawwazfa/Program/Harvestmoon/vidio/15d.mp4",
        "/home/fawwazfa/Program/Harvestmoon/vidio/YDXJ0012_demo(1).mp4",
        "/home/fawwazfa/Program/Harvestmoon/vidio/YDX_burned.mp4",
        "/home/fawwazfa/Program/Harvestmoon/vid/15d.mp4",
        "/home/fawwazfa/Program/Harvestmoon/vid/YDXJ0012_demo.mp4",
        "/home/fawwazfa/Program/Harvestmoon/vid/YDX_burned.mp4",
    };

    private async void OnStartStopStream(object sender, RoutedEventArgs e)
    {
        try
        {
            var cameraService = App.Current.Services.GetService<ICameraService>();
            if (cameraService == null) return;

        if (cameraService.IsStreaming)
        {
            await cameraService.StopCameraAsync();
            AddConsoleMessage("CAMERA", "Video stream stopped");
        }
        else
        {
            var scenario = _videoScenarioService?.CurrentScenario;
            string? videoPath = null;
            if (scenario != null && _videoScenarioService != null)
            {
                videoPath = _videoScenarioService.ResolveVideoFilePath(scenario);
            }
            if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            {
                videoPath = Array.Find(_demoVideoCandidates, File.Exists);
            }

            if (videoPath == null)
            {
                AddConsoleMessage("CAMERA", "Tidak ada video deteksi ditemukan", "ERROR");
                return;
            }

            // Sync scenario if not matching
            if (_videoScenarioService != null && (scenario == null || !string.Equals(scenario.VideoFileName, Path.GetFileName(videoPath), StringComparison.OrdinalIgnoreCase)))
            {
                scenario = _videoScenarioService.LoadScenarioForVideo(videoPath);
                _videoScenarioService.SetCurrentScenario(scenario);
            }

            if (scenario != null)
            {
                AddConsoleMessage("MISSION", $"Active Scenario: {scenario.Name} ({scenario.RegionName})", "INFO");
                AddConsoleMessage("NAV", $"Field: {scenario.FieldCenterLat:F5}, {scenario.FieldCenterLon:F5} | Bear {scenario.BearingDeg:F0}° | Target FHI: {scenario.TargetFhi:F1}%", "INFO");

                // Sync waypoints and map
                if (_mapViewModel != null && scenario.Waypoints.Count > 0)
                {
                    _mapViewModel.Waypoints.Clear();
                    foreach (var wp in scenario.Waypoints) _mapViewModel.Waypoints.Add(wp);
                    _mapViewModel.GeofenceCenterLat = scenario.FieldCenterLat;
                    _mapViewModel.GeofenceCenterLon = scenario.FieldCenterLon;
                    _mapViewModel.GeofenceRadius = scenario.GeofenceRadiusMeters;
                }
                if (_waypointService != null && scenario.Waypoints.Count > 0)
                {
                    _ = _waypointService.ReplaceWaypointsAsync(scenario.Waypoints);
                }
                if (mapControl != null)
                {
                    mapControl.SetCenter(scenario.FieldCenterLat, scenario.FieldCenterLon, 15);
                    SyncMapFromViewModel();
                }
            }

            AddConsoleMessage("CAMERA", $"Playing: {Path.GetFileName(videoPath)}");
            var success = await cameraService.StartCameraAsync(videoPath);
            if (!success)
                AddConsoleMessage("CAMERA", $"Gagal membuka {Path.GetFileName(videoPath)}", "ERROR");
        }
        }
        catch (Exception ex)
        {
            // Unhandled exceptions in async void terminate the process on Windows.
            Serilog.Log.Error(ex, "[FlightPage] Start/stop stream failed");
            AddConsoleMessage("CAMERA", $"Stream error: {ex.Message}", "ERROR");
        }
    }
    
    private async void OnCameraFrameReceived(object? sender, byte[] frameData)
    {
        try
        {
            _observabilityService?.Track("flight.camera.ingest");

            // Run YOLO inference asynchronously (same pattern as CameraPage).
            // Gated on the live YOLO toggle so a disabled switch never runs inference.
            var yoloEnabled = App.Current.Services
                .GetService<HarvestFunctionalService>()?.IsYoloOptionEnabled == true;
            var yoloResult = (_yoloReady && yoloEnabled)
                ? await _yoloProcessor.ProcessAsync(frameData)
                : null;

            lock (_cameraFrameSync)
            {
                _pendingFrameData = frameData;
                _pendingYoloResult = yoloResult;
                _hasPendingFrameData = true;
            }
        }
        catch (Exception ex)
        {
            // An unhandled exception in an async void frame handler terminates the process,
            // which was the main "force close" path on Windows. Swallow and log instead.
            Serilog.Log.Error(ex, "[FlightPage] Camera frame handling failed");
        }
    }
    
    private void OnStreamingStatusChanged(object? sender, bool isStreaming)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!isStreaming && liveCam != null)
            {
                liveCam.ClearFrame();
            }
        });
    }

    private YoloFrameResult? _pendingYoloResult;

    private void FlushPendingCameraFrame()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastCameraFrameUiUpdate).TotalMilliseconds < _cameraFrameIntervalMs)
        {
            return;
        }

        byte[]? frameToRender = null;
        YoloFrameResult? yoloResult = null;
        lock (_cameraFrameSync)
        {
            if (_hasPendingFrameData)
            {
                frameToRender = _pendingFrameData;
                yoloResult = _pendingYoloResult;
                _pendingFrameData = null;
                _pendingYoloResult = null;
                _hasPendingFrameData = false;
            }
        }

        if (frameToRender != null && liveCam != null)
        {
            if (yoloResult != null && yoloResult.Detections.Count > 0)
            {
                // Update frame with YOLO result data
                liveCam.UpdateFrame(yoloResult.FrameData);
                liveCam.SetDetectionOverlays(yoloResult.Detections);
                
                // Log detection summary to console
                _lastYoloSummary = $"YOLO: {yoloResult.DetectionCount} | {yoloResult.Fps:F1} FPS | {yoloResult.Summary}";
                AddConsoleMessage("YOLO", _lastYoloSummary, "INFO");
            }
            else
            {
                // No detections or YOLO not ready — just show raw frame
                liveCam.UpdateFrame(frameToRender);
                if (!_yoloReady)
                {
                    liveCam.SetDetectionOverlays(Array.Empty<VideoDetectionOverlay>());
                }
            }
            _lastCameraFrameUiUpdate = now;
            _observabilityService?.Track("flight.camera.render");
        }
    }
}
