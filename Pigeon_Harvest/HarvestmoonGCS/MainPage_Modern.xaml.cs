using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using HarvestmoonGCS.ViewModels;
using HarvestmoonGCS.Views;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Helpers;
using HarvestmoonGCS.Services;
using HarvestmoonGCS.Controls;
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Windows.UI;
using Windows.Storage;
using Serilog;
using System.Collections.Generic;
using System.Linq;
#if ANDROID
using Android.Content.Res;
using AndroidApp = Android.App.Application;
using AndroidOrientation = Android.Content.Res.Orientation;
#endif

namespace HarvestmoonGCS;

public sealed partial class MainPage_Modern : Page
{
    private MainViewModel? _viewModel;
    private readonly IMavLinkService? _mavLinkService;
    private readonly ILocalizationService? _localizationService;
    private readonly PageCacheManager? _pageCacheManager;
    private readonly IVideoScenarioService? _videoScenarioService;
    private readonly HarvestFunctionalService? _harvestFunctionalService;
    private DateTime _lastTopBarTelemetryUpdate = DateTime.MinValue;
    private const int TopBarUpdateIntervalMs = 200;
    private (double Battery, int Sats, string Mode, bool IsArmed)? _lastTopBarState;
    private bool? _lastConnectionStatus;
    private bool _isNavigating;
    private string? _currentTarget;
    private string? _pendingNavigationTarget;
    private readonly ObservabilityService? _observabilityService;
    private DispatcherTimer? _observabilityOverlayTimer;
    private DispatcherTimer? _navigationPreloadTimer;
    private readonly Queue<Type> _navigationPreloadQueue = new();
    private bool _navigationPreloadStarted;
    private const double DefaultSidebarWidth = 232.0;
    private const double CompactSidebarWidth = 156.0;
    private const double VeryCompactSidebarWidth = 132.0;
    private const double OverlayMarginRight = 16.0;
    private const double OverlayMinimumWidth = 220.0;
    private const double OverlayMaximumWidth = 440.0;
#if !__WASM__
    private readonly ChatViewModel? _chatViewModel;
#endif

    public MainPage_Modern()
    {
        this.InitializeComponent();
        this.Loaded += MainPage_Modern_Loaded;
        this.Unloaded += MainPage_Modern_Unloaded;

        try
        {
            _viewModel = App.Current.Services.GetService<MainViewModel>();
            DataContext = _viewModel;

            _localizationService = App.Current.Services.GetService<ILocalizationService>();
            _pageCacheManager = App.Current.Services.GetService<PageCacheManager>();
            _mavLinkService = App.Current.Services.GetService<IMavLinkService>();
            _observabilityService = App.Current.Services.GetService<ObservabilityService>();
            _videoScenarioService = App.Current.Services.GetService<IVideoScenarioService>();
            _harvestFunctionalService = App.Current.Services.GetService<HarvestFunctionalService>();
            App.Current.Services.GetService<MissionLoggingService>();
            if (_mavLinkService != null)
            {
                _mavLinkService.TelemetryReceived += OnTelemetryReceived;
                _mavLinkService.ConnectionStatusChanged += OnConnectionStatusChanged;
            }
            if (_videoScenarioService != null)
            {
                _videoScenarioService.ScenarioChanged += OnScenarioChanged;
            }

#if !__WASM__
            _chatViewModel = App.Current.Services.GetService<ChatViewModel>();
            if (PIAPanelOverlay != null && _chatViewModel != null)
            {
                PIAPanelOverlay.ViewModel = _chatViewModel;
            }
            SubscribeToChatViewModel();
#endif

            InitializeObservabilityOverlay();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MainPage_Modern constructor failed");
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyResponsiveLayout();
            SynchronizeTopBarConnection();
            NavigateToPage("Dashboard");
            UpdateTopBarForTarget("Dashboard");
            StartNavigationPreload();
        });
    }

    private void MainPage_Modern_Loaded(object sender, RoutedEventArgs e)
    {
        this.SizeChanged -= MainPage_Modern_SizeChanged;
        this.SizeChanged += MainPage_Modern_SizeChanged;

        ApplyResponsiveLayout();
        SynchronizeTopBarConnection();
        // Navigate to initial page
        NavigateToPage("Dashboard");
        UpdateTopBarForTarget("Dashboard");
        StartNavigationPreload();

        // Auto-connect saat startup DIMATIKAN (sesuai permintaan): koneksi hanya
        // dibuat ketika pengguna menekan Connect di dialog.

#if !__WASM__
        // Subscribe to PIA button click from sidebar
        Sidebar.PIAButtonClicked -= OnPIAButtonClicked;
        Sidebar.PIAButtonClicked += OnPIAButtonClicked;
        if (PIAPanelOverlay != null)
        {
            PIAPanelOverlay.SettingsRequested -= OnPIASettingsRequested;
            PIAPanelOverlay.SettingsRequested += OnPIASettingsRequested;
        }
#endif
    }

    /// <summary>
    /// Auto-connect saat aplikasi dibuka: telemetri MAVLink dari UAV (Orange Pi)
    /// langsung masuk tanpa perlu klik Connect.
    /// </summary>
    private async Task TryAutoConnectOnStartupAsync()
    {
        try
        {
            // Beri jeda singkat agar UI + service siap.
            await Task.Delay(1500);

            var connected = await Views.ConnectDialog.TryAutoConnectAsync(
                _mavLinkService,
                App.Current.Services.GetService<IRuncamWifiLinkService>(),
                App.Current.Services.GetService<ISettingsService>());

            if (connected)
            {
                Serilog.Log.Information("[MainPage] Auto-connect berhasil (UAV terhubung).");
                DispatcherQueue.TryEnqueue(SynchronizeTopBarConnection);
            }
            else
            {
                Serilog.Log.Information("[MainPage] Auto-connect tidak menemukan UAV (bisa connect manual).");
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[MainPage] Auto-connect gagal");
        }
    }

    private void MainPage_Modern_Unloaded(object sender, RoutedEventArgs e)
    {        this.SizeChanged -= MainPage_Modern_SizeChanged;

        if (_mavLinkService != null)
        {
            _mavLinkService.TelemetryReceived -= OnTelemetryReceived;
            _mavLinkService.ConnectionStatusChanged -= OnConnectionStatusChanged;
        }

        if (_videoScenarioService != null)
        {
            _videoScenarioService.ScenarioChanged -= OnScenarioChanged;
        }

        if (_observabilityOverlayTimer != null)
        {
            _observabilityOverlayTimer.Stop();
            _observabilityOverlayTimer.Tick -= OnObservabilityOverlayTick;
            _observabilityOverlayTimer = null;
        }

        if (_navigationPreloadTimer != null)
        {
            _navigationPreloadTimer.Stop();
            _navigationPreloadTimer.Tick -= OnNavigationPreloadTick;
            _navigationPreloadTimer = null;
        }

#if !__WASM__
        Sidebar.PIAButtonClicked -= OnPIAButtonClicked;
        if (PIAPanelOverlay != null)
        {
            PIAPanelOverlay.SettingsRequested -= OnPIASettingsRequested;
        }
        UnsubscribeFromChatViewModel();
#endif
    }

#if !__WASM__
    private void SubscribeToChatViewModel()
    {
        if (_chatViewModel == null)
        {
            return;
        }

        _chatViewModel.PropertyChanged -= OnChatViewModelPropertyChanged;
        _chatViewModel.PropertyChanged += OnChatViewModelPropertyChanged;
        UpdateTopBarVoiceIndicator();
    }

    private void UnsubscribeFromChatViewModel()
    {
        if (_chatViewModel == null)
        {
            return;
        }

        _chatViewModel.PropertyChanged -= OnChatViewModelPropertyChanged;
    }

    private void OnChatViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.IsListening) ||
            e.PropertyName == nameof(ChatViewModel.VoiceExecutionStatus) ||
            e.PropertyName == nameof(ChatViewModel.VoiceAvailabilityStatus))
        {
            UpdateTopBarVoiceIndicator();
        }
    }

    private void UpdateTopBarVoiceIndicator()
    {
        if (_chatViewModel == null || TopBar == null)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            TopBar.IsVoiceListening = _chatViewModel.IsListening;

            var nextStatus = _chatViewModel.IsListening
                ? "listening"
                : !string.IsNullOrWhiteSpace(_chatViewModel.VoiceExecutionStatus)
                    ? _chatViewModel.VoiceExecutionStatus
                    : _chatViewModel.VoiceAvailabilityStatus;

            TopBar.VoiceStatus = nextStatus ?? "idle";
        });
    }

    private void OnPIASettingsRequested(object? sender, EventArgs e)
    {
        if (PIAPanelOverlay == null)
        {
            return;
        }

        PIAPanelOverlay.Close();
        PIAPanelOverlay.Visibility = Visibility.Collapsed;

        if (ContentFrame.Content?.GetType() == typeof(AISettingsPage))
        {
            _currentTarget = "AISettings";
            return;
        }

        var navigated = ContentFrame.Navigate(typeof(AISettingsPage));
        if (navigated)
        {
            _currentTarget = "AISettings";
        }
    }

    private void OnPIAButtonClicked(object? sender, EventArgs e)
    {
        if (PIAPanelOverlay == null || _chatViewModel == null)
            return;

        if (PIAPanelOverlay.Visibility == Visibility.Visible)
        {
            PIAPanelOverlay.Close();
            PIAPanelOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            PIAPanelOverlay.Visibility = Visibility.Visible;
            PIAPanelOverlay.Open();
        }
    }
#endif

    private void MainPage_Modern_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        if (Sidebar == null)
        {
            return;
        }

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

        bool useCompactLayout = IsAndroidPortrait() || pageWidth < 960;
        bool useVeryCompactLayout = IsAndroidPhone() || pageWidth < 760;

        double sidebarWidth = useVeryCompactLayout
            ? VeryCompactSidebarWidth
            : useCompactLayout ? CompactSidebarWidth : DefaultSidebarWidth;
        Sidebar.Width = sidebarWidth;

        if (Sidebar.Content is FrameworkElement sidebarRoot)
        {
            sidebarRoot.Width = sidebarWidth;
        }

        if (ObservabilityOverlay != null)
        {
            double availableOverlayWidth = pageWidth - sidebarWidth - (OverlayMarginRight * 2);
            double overlayWidth = Math.Clamp(availableOverlayWidth, OverlayMinimumWidth, OverlayMaximumWidth);
            ObservabilityOverlay.Width = overlayWidth;
        }

        if (useCompactLayout && ObservabilityOverlay?.Visibility == Visibility.Visible)
        {
            ObservabilityOverlay.Visibility = Visibility.Collapsed;
            _observabilityOverlayTimer?.Stop();
        }

        // --- Android tablet responsive: icon-only sidebar + compact top bar ---
        bool isAndroid = IsAndroidDevice();
        Sidebar.ApplyCompactMode(isAndroid);
        TopBar.ApplyTabletCompactMode(isAndroid);
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

    // Treat only small Android handsets as "very compact" by smallest width.
    // Tablets (>= 600dp) should use compact/default sidebar sizing.
    private static bool IsAndroidPhone()
    {
#if ANDROID
        var smallestWidthDp = AndroidApp.Context?.Resources?.Configuration?.SmallestScreenWidthDp ?? 0;
        return smallestWidthDp > 0 && smallestWidthDp < 600;
#else
        return false;
#endif
    }

    private static bool IsAndroidDevice()
    {
#if ANDROID
        return true;
#else
        return false;
#endif
    }

    private void Sidebar_NavigationRequested(object sender, string target)
    {
        if (string.Equals(target, "Connection", StringComparison.Ordinal))
        {
            _ = ShowConnectDialogAsync();
            return;
        }

        NavigateToPage(target);
    }

    private void NavigateToPage(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        Type? pageType = ResolvePageType(target);
        if (pageType == null)
        {
            return;
        }

        if (_isNavigating)
        {
            _pendingNavigationTarget = target;
            return;
        }

        if (string.Equals(_currentTarget, target, StringComparison.Ordinal) && ContentFrame.Content?.GetType() == pageType)
        {
            UpdateTopBarForTarget(target);
            return;
        }

        _isNavigating = true;
        bool forceFrameNavigation = target is "Map";
        bool pageAlreadyCached = !forceFrameNavigation && _pageCacheManager?.IsCached(pageType) == true;
        bool useLoadingOverlay = target is "Map" && !pageAlreadyCached;
        if (useLoadingOverlay)
        {
            MainLoadingIndicator.IsLoading = true;
            MainLoadingIndicator.LoadingText = $"Loading {target}...";
        }

        try
        {
            if (ContentFrame.Content?.GetType() == pageType)
            {
                _currentTarget = target;
                UpdateTopBarForTarget(target);
                return;
            }

            // Deactivate the current page before switching (stops timers, telemetry, etc.)
            if (ContentFrame.Content is Page leavingPage)
                DeactivatePage(leavingPage);

            Page? pageInstance = null;
            if (_pageCacheManager != null && !forceFrameNavigation)
            {
                pageInstance = _pageCacheManager.GetOrCreatePage(pageType);
                ContentFrame.Content = pageInstance;
                ActivatePage(pageInstance);
            }
            else
            {
                if (forceFrameNavigation)
                {
                    pageInstance = Activator.CreateInstance(pageType) as Page;
                    if (pageInstance == null)
                    {
                        throw new InvalidOperationException($"Could not create page instance for {pageType.FullName}");
                    }

                    ContentFrame.Content = pageInstance;
                    ActivatePage(pageInstance);
                }
                else
                {
                    bool navigated = ContentFrame.Navigate(pageType);
                    if (!navigated)
                    {
                        throw new InvalidOperationException($"Frame navigation returned false for {pageType.FullName}");
                    }

                    if (ContentFrame.Content is Page navigatedPage)
                    {
                        ActivatePage(navigatedPage);
                    }
                }
            }

            Log.Debug("MainPage_Modern: navigated to {PageType}", pageType.Name);
            _currentTarget = target;
            UpdateTopBarForTarget(target);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Navigation failed for target: {Target}", target);
#if __ANDROID__
            Android.Util.Log.Error("PigeonGCS", $"Navigation FAILED for {target}: {ex}");
#endif
        }
        finally
        {
            _isNavigating = false;
            if (useLoadingOverlay)
            {
                MainLoadingIndicator.IsLoading = false;
            }

            if (!string.IsNullOrEmpty(_pendingNavigationTarget) &&
                !string.Equals(_pendingNavigationTarget, _currentTarget, StringComparison.Ordinal))
            {
                var pending = _pendingNavigationTarget;
                _pendingNavigationTarget = null;
                DispatcherQueue.TryEnqueue(() => NavigateToPage(pending));
            }
            else
            {
                _pendingNavigationTarget = null;
            }
        }
    }

    private void StartNavigationPreload()
    {
        if (_navigationPreloadStarted || _pageCacheManager == null)
        {
            return;
        }

        _navigationPreloadStarted = true;
        foreach (var pageType in GetPreloadPageTypes())
        {
            if (!_pageCacheManager.IsCached(pageType))
            {
                _navigationPreloadQueue.Enqueue(pageType);
            }
        }

        if (_navigationPreloadQueue.Count == 0)
        {
            return;
        }

        _navigationPreloadTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        _navigationPreloadTimer.Tick += OnNavigationPreloadTick;
        _navigationPreloadTimer.Start();
    }

    private void OnNavigationPreloadTick(object? sender, object e)
    {
        if (_pageCacheManager == null)
        {
            _navigationPreloadTimer?.Stop();
            return;
        }

        if (_isNavigating)
        {
            return;
        }

        while (_navigationPreloadQueue.Count > 0)
        {
            var pageType = _navigationPreloadQueue.Dequeue();
            if (_pageCacheManager.IsCached(pageType))
            {
                continue;
            }

            try
            {
                _pageCacheManager.GetOrCreatePage(pageType);
                Log.Debug("Preloaded navigation page {PageType}", pageType.Name);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to preload navigation page {PageType}", pageType.Name);
            }

            break;
        }

        if (_navigationPreloadQueue.Count == 0)
        {
            _navigationPreloadTimer?.Stop();
        }
    }

    private static IEnumerable<Type> GetPreloadPageTypes()
    {
        yield return typeof(DashboardPage);
        yield return typeof(CameraPage);
        yield return typeof(MissionPlannerPage);
        yield return typeof(StatsPage);
        yield return typeof(AISettingsPage);
        yield return typeof(ReportsHarvestPage);
        yield return typeof(SettingsPage);
        yield return typeof(EdgeModePage);
    }

    private static Type? ResolvePageType(string target)
    {
        return target switch
        {
            "Dashboard" => typeof(DashboardPage),
            "Camera" => typeof(CameraPage),
            "Map" => typeof(MapPage),
            "MissionPlanner" => typeof(MissionPlannerPage),
            "Stats" => typeof(StatsPage),
            "AISettings" => typeof(AISettingsPage),
            "Tlog" => typeof(ReportsHarvestPage),
            "Settings" => typeof(SettingsPage),
            "Flight" => typeof(DashboardPage),
            "EdgeMode" => typeof(EdgeModePage),
            _ => null
        };
    }

    private static void ActivatePage(Page page)
    {
        if (page is DashboardPage dashboardPage)
        {
            dashboardPage.OnPageActivated();
            return;
        }

        if (page is FlightPage flightPage)
        {
            flightPage.OnPageActivated();
            return;
        }

        if (page is MapPage mapPage)
        {
            mapPage.OnPageActivated();
            return;
        }

        if (page is MissionPlannerPage missionPlannerPage)
        {
            missionPlannerPage.OnPageActivated();
            return;
        }

        if (page is ReportsHarvestPage reportsPage)
        {
            // Pages are preloaded/cached at startup, so without this the mission list only ever
            // reflected whatever reports existed at app launch — a live session started later
            // never appeared until the app was restarted.
            reportsPage.OnPageActivated();
            return;
        }

        if (page is AISettingsPage aiSettingsPage)
        {
            aiSettingsPage.OnPageActivated();
            return;
        }

        if (page is StatsPage statsPage)
        {
            statsPage.OnPageActivated();
            return;
        }

        if (page is CameraPage cameraPage)
        {
            cameraPage.OnPageActivated();
            return;
        }

        if (page is EdgeModePage edgeModePage)
        {
            edgeModePage.OnPageActivated();
            return;
        }

        if (page is SettingsPage settingsPage)
        {
            settingsPage.OnPageActivated();
        }
    }

    private static void DeactivatePage(Page page)
    {
        if (page is DashboardPage dashboardPage)
            dashboardPage.OnPageDeactivated();
    }

    private void OnScenarioChanged(object? sender, VideoScenarioProfile e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateTopBarForTarget(_currentTarget ?? "Dashboard");
        });
    }

    private void UpdateTopBarForTarget(string target)
    {
        var scenario = _videoScenarioService?.CurrentScenario;
        var locationText = scenario != null ? $"{scenario.AreaName}, {scenario.RegionName}" : "Sukamerta, Karawang";

        switch (target)
        {
            case "Dashboard":
            case "Flight":
                TopBar.UpdatePageTitle("Dashboard", $"/ Live Operations · {locationText}", "\uE80F");
                break;
            case "Map":
                TopBar.UpdatePageTitle("Map", $"/ Full Map · Waypoints · Geofence · {scenario?.AreaName ?? "Offline Tiles"}", "\uE707");
                break;
            case "MissionPlanner":
                TopBar.UpdatePageTitle("Mission Planner", $"/ {locationText} · 1050m Transect", "\uE707");
                break;
            case "Camera":
                TopBar.UpdatePageTitle("Camera", $"/ Live UAV Capture · {locationText}", "\uE714");
                break;
            case "Stats":
                TopBar.UpdatePageTitle("Crop Analysis", $"/ AI Monitoring · {locationText}", "\uE9D9");
                break;
            case "AISettings":
                TopBar.UpdatePageTitle("AI Settings", "/ Diagnostics & Models", "\uE7C1");
                break;
            case "Tlog":
                TopBar.UpdatePageTitle("Reports", $"/ Mission History & Evidence · {scenario?.RegionName ?? "Karawang"}", "\uE8A5");
                break;
            case "Settings":
                TopBar.UpdatePageTitle("Settings", "/ System Preferences", "\uE713");
                break;
            case "EdgeMode":
                TopBar.UpdatePageTitle("Edge Mode", "/ Offline AI · Zero-Internet YOLO Inference", "\uE950");
                break;
            default:
                TopBar.UpdatePageTitle("Dashboard", $"/ Live Operations · {locationText}", "\uE80F");
                break;
        }
    }

    private async void TopBar_ExitClicked(object sender, EventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Exit Application",
            Content = "Are you sure you want to exit?",
            PrimaryButtonText = "Yes",
            CloseButtonText = "No",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            Application.Current.Exit();
        }
    }

    /// <summary>
    /// Sends a real MAVLink RTL (Return-to-Launch) flight mode change. Previously this button
    /// only raised an event with no subscriber anywhere in the app — clicking it did nothing.
    /// </summary>
    private async void TopBar_RTLClicked(object sender, EventArgs e)
    {
        if (_mavLinkService?.IsConnected != true)
        {
            await ShowSimpleDialogAsync("RTL", "Vehicle not connected — cannot send Return-to-Launch.");
            return;
        }

        var confirm = new ContentDialog
        {
            Title = "Return to Launch",
            Content = "Send RTL command to the connected vehicle now?",
            PrimaryButtonText = "Send RTL",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var ok = await _mavLinkService.SetFlightModeAsync("RTL");
            Log.Information("RTL command sent, success={Success}", ok);
            if (!ok)
            {
                await ShowSimpleDialogAsync("RTL", "Vehicle did not acknowledge the RTL command.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send RTL command");
            await ShowSimpleDialogAsync("RTL", $"Failed to send RTL: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts/pauses the currently-uploaded mission via a real MAVLink command. Previously this
    /// button only toggled its own label locally — clicking it never sent anything to the
    /// vehicle. The button's visual state now only changes once the command is confirmed.
    /// </summary>
    private async void TopBar_MissionClicked(object sender, EventArgs e)
    {
        if (_mavLinkService?.IsConnected != true)
        {
            await ShowSimpleDialogAsync("Mission", "Vehicle not connected — cannot start/pause mission.");
            return;
        }

        var requestRunning = !TopBar.IsMissionRunning;
        try
        {
            var ok = requestRunning
                ? await _mavLinkService.StartMissionAsync()
                : await _mavLinkService.PauseMissionAsync();

            if (ok)
            {
                TopBar.SetMissionRunning(requestRunning);
            }
            else
            {
                await ShowSimpleDialogAsync("Mission", requestRunning
                    ? "Vehicle did not acknowledge mission start."
                    : "Vehicle did not acknowledge mission pause.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send mission start/pause command");
            await ShowSimpleDialogAsync("Mission", $"Command failed: {ex.Message}");
        }
    }

    private async Task ShowSimpleDialogAsync(string title, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show dialog");
        }
    }

    private async void TopBar_ConnectClicked(object sender, EventArgs e)
    {
        Log.Information("Connect clicked on TopBar");
        await ShowConnectDialogAsync();
    }

    private async Task ShowConnectDialogAsync()
    {
        try
        {
            MainLoadingIndicator.IsLoading = false;
            var dialog = new ConnectDialog(_mavLinkService)
            {
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to show ConnectDialog");
        }
    }
    private void SynchronizeTopBarConnection()
    {
        bool isConnected = _mavLinkService?.IsConnected == true;
        _lastConnectionStatus = isConnected;
        TopBar.Status = isConnected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected;
        if (!isConnected)
        {
            _lastTopBarState = null;
            TopBar.Battery = 0;
            TopBar.GpsSats = 0;
            TopBar.Mode = "—";
            TopBar.IsArmed = false;
            TopBar.SetMissionRunning(false);
            TopBar.Endpoint = null;
        }
    }

    private void OnConnectionStatusChanged(object? sender, bool isConnected)
    {
        if (_lastConnectionStatus.HasValue && _lastConnectionStatus.Value == isConnected)
        {
            return;
        }
        _lastConnectionStatus = isConnected;
        DispatcherQueue.TryEnqueue(() =>
        {
            var nextStatus = isConnected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected;
            if (TopBar.Status != nextStatus)
            {
                TopBar.Status = nextStatus;
                _observabilityService?.Track("topbar.status.update");
            }

            if (!isConnected)
            {
                _lastTopBarState = null;
                TopBar.Battery = 0;
                TopBar.GpsSats = 0;
                TopBar.Mode = "—";
                TopBar.IsArmed = false;
                TopBar.SetMissionRunning(false);
                TopBar.Endpoint = null;
            }
        });
    }

    private void OnTelemetryReceived(object? sender, HarvestmoonGCS.Models.FlightData data)
    {
        if (_mavLinkService?.IsConnected != true)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastTopBarTelemetryUpdate).TotalMilliseconds < TopBarUpdateIntervalMs)
        {
            return;
        }

        _observabilityService?.Track("topbar.telemetry.ingest");

        double batteryPercent = data.BatteryRemaining >= 0 ? data.BatteryRemaining : 0;
        int sats = Math.Max(0, (int)data.Sats);
        string mode = data.FlightMode.ToString().ToUpperInvariant();
        bool isArmed = data.FlightMode != HarvestmoonGCS.Core.Models.FlightMode.DISARMED;

        var newState = (Battery: batteryPercent, Sats: sats, Mode: mode, IsArmed: isArmed);
        if (_lastTopBarState.HasValue)
        {
            var previous = _lastTopBarState.Value;
            if (Math.Abs(previous.Battery - newState.Battery) < 0.05 &&
                previous.Sats == newState.Sats &&
                previous.Mode == newState.Mode &&
                previous.IsArmed == newState.IsArmed)
            {
                return;
            }
        }

        _lastTopBarTelemetryUpdate = now;
        _lastTopBarState = newState;

        DispatcherQueue.TryEnqueue(() =>
        {
            _observabilityService?.Track("topbar.telemetry.render");
            TopBar.Battery = batteryPercent;
            TopBar.GpsSats = sats;
            TopBar.Mode = mode;
            TopBar.IsArmed = isArmed;
        });
    }

    private void InitializeObservabilityOverlay()
    {
        if (_observabilityService == null)
        {
            return;
        }

        _observabilityOverlayTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000)
        };
        _observabilityOverlayTimer.Tick += OnObservabilityOverlayTick;
    }

    private void OnObservabilityOverlayTick(object? sender, object e)
    {
        if (_observabilityService == null || ObservabilityText == null || ObservabilityOverlay == null)
        {
            return;
        }

        if (ObservabilityOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        var snapshot = _observabilityService.GetSnapshot();
        if (snapshot.Count == 0)
        {
            ObservabilityText.Text = "Waiting for telemetry...";
            return;
        }

        static string FormatLine(ObservabilityService.CounterSnapshot value)
            => $"{value.Key,-26} {value.RatePerSecond,6:F1}/s   total {value.Total}";

        var preferredOrder = new[]
        {
            "mavlink.telemetry.ingest",
            "mavlink.telemetry.dispatch",
            "realtime.telemetry.ingest",
            "realtime.telemetry.dispatch",
            "flight.telemetry.apply",
            "flight.map.render",
            "flight.camera.render",
            "stats.chart.flush",
            "map.vehicle.render",
            "tlog.map.render",
            "topbar.telemetry.render",
            "topbar.status.update"
        };

        var lines = new List<string>
        {
            "OBSERVABILITY (rate/s)",
            "----------------------"
        };

        foreach (var key in preferredOrder)
        {
            if (snapshot.TryGetValue(key, out var value))
            {
                lines.Add(FormatLine(value));
            }
        }

        foreach (var value in snapshot.Values.OrderBy(v => v.Key))
        {
            if (preferredOrder.Contains(value.Key, StringComparer.Ordinal))
            {
                continue;
            }

            lines.Add(FormatLine(value));
        }

        ObservabilityText.Text = string.Join(Environment.NewLine, lines);
    }

    private void ToggleObservabilityOverlay(object sender, RoutedEventArgs e)
    {
        if (ObservabilityOverlay == null)
        {
            return;
        }

        var show = ObservabilityOverlay.Visibility != Visibility.Visible;
        ObservabilityOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (_observabilityOverlayTimer != null)
        {
            if (show)
            {
                _observabilityOverlayTimer.Start();
                OnObservabilityOverlayTick(this, EventArgs.Empty);
            }
            else
            {
                _observabilityOverlayTimer.Stop();
            }
        }

    }
}
