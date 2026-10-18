using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Services;
using Serilog;
using IThemeService = HarvestmoonGCS.Core.Services.IThemeService;

namespace HarvestmoonGCS.Controls;

public sealed partial class ModernSidebar : UserControl
{
    private readonly IThemeService _themeService;
    private readonly HarvestFunctionalService? _harvestFunctionalService;
    private Button? _currentActiveButton;
    private string? _currentTarget = "Dashboard";

    public event EventHandler<string>? NavigationRequested;
    public event EventHandler? PIAButtonClicked;

    public ModernSidebar()
    {
        this.InitializeComponent();

        _themeService = App.GetService<IThemeService>() ??
                        throw new InvalidOperationException("IThemeService is not registered in DI container.");
        _harvestFunctionalService = App.Current.Services.GetService<HarvestFunctionalService>();

        this.Loaded += ModernSidebar_Loaded;
        this.Unloaded += ModernSidebar_Unloaded;
        this.ActualThemeChanged += OnActualThemeChanged;
    }

    /// <summary>
    /// Switches the sidebar between full-width (with labels) and icon-only compact mode.
    /// Called from MainPage_Modern.ApplyResponsiveLayout() on Android.
    /// </summary>
    public void ApplyCompactMode(bool compact)
    {
        const double IconOnlyWidth = 52.0;
        const double FullWidth = 180.0;

        if (SidebarRoot == null) return;

        SidebarRoot.Width = compact ? IconOnlyWidth : FullWidth;

        // Toggle logo panels
        if (LogoFull != null)   LogoFull.Visibility   = compact ? Visibility.Collapsed : Visibility.Visible;
        if (LogoCompact != null) LogoCompact.Visibility = compact ? Visibility.Visible  : Visibility.Collapsed;

        // Hide/show nav labels — all TextBlock children named *NavLabel
        SetNavLabelsVisible(!compact);

        // Hide active dots and yolo label text in compact mode (dots take space)
        if (YoloOptionLabel != null)
            YoloOptionLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

        // Center icon when compact, left-align when full
        var iconMargin = compact ? new Thickness(0) : new Thickness(0, 0, 7, 0);
        SetNavIconMargins(iconMargin);
    }

    private void SetNavLabelsVisible(bool visible)
    {
        var vis = visible ? Visibility.Visible : Visibility.Collapsed;
        if (FlightNavLabel != null)      FlightNavLabel.Visibility      = vis;
        if (CameraNavLabel != null)      CameraNavLabel.Visibility      = vis;
        if (MapNavLabel != null)         MapNavLabel.Visibility         = vis;
        if (MissionNavLabel != null)     MissionNavLabel.Visibility     = vis;
        if (StatsNavLabel != null)       StatsNavLabel.Visibility       = vis;
        if (EdgeModeNavLabel != null)    EdgeModeNavLabel.Visibility    = vis;
        if (AISettingsNavLabel != null)  AISettingsNavLabel.Visibility  = vis;
        if (TlogNavLabel != null)        TlogNavLabel.Visibility        = vis;
#if !__WASM__
        if (PIANavLabel != null)         PIANavLabel.Visibility         = vis;
#endif
        // Also hide active dots in compact mode to avoid layout overflow
        if (FlightActiveDot != null)     FlightActiveDot.Visibility     = vis;
        if (CameraActiveDot != null)     CameraActiveDot.Visibility     = vis;
        if (MapActiveDot != null)        MapActiveDot.Visibility        = vis;
        if (MissionActiveDot != null)    MissionActiveDot.Visibility    = vis;
        if (StatsActiveDot != null)      StatsActiveDot.Visibility      = vis;
        if (EdgeModeActiveDot != null)   EdgeModeActiveDot.Visibility   = vis;
        if (AISettingsActiveDot != null) AISettingsActiveDot.Visibility = vis;
        if (TlogActiveDot != null)       TlogActiveDot.Visibility       = vis;
    }

    private void SetNavIconMargins(Thickness margin)
    {
        // Find FontIcons in each nav button and adjust margin
        SetIconMarginInButton(FlightNavButton, margin);
        SetIconMarginInButton(CameraNavButton, margin);
        SetIconMarginInButton(MapNavButton, margin);
        SetIconMarginInButton(MissionNavButton, margin);
        SetIconMarginInButton(StatsNavButton, margin);
        SetIconMarginInButton(EdgeModeNavButton, margin);
        SetIconMarginInButton(AISettingsNavButton, margin);
        SetIconMarginInButton(TlogNavButton, margin);
    }

    private static void SetIconMarginInButton(Button? btn, Thickness margin)
    {
        if (btn?.Content is Grid grid && grid.Children.Count > 0 && grid.Children[0] is FontIcon icon)
        {
            icon.Margin = margin;
        }
    }

    private void ModernSidebar_Loaded(object sender, RoutedEventArgs e)
    {
        // PulseAnimation dinonaktifkan untuk meningkatkan performance
        // PulseAnimation.Begin();

        SetActiveButton(FlightNavButton);

        ApplyThemeToggleVisualState();
        ApplyYoloOptionVisualState();
        if (_harvestFunctionalService != null)
        {
            _harvestFunctionalService.YoloOptionChanged += OnYoloOptionChanged;
        }
        _themeService.ThemeChanged += OnThemeChanged;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        // Re-apply active button styles when theme changes
        if (_currentActiveButton != null)
        {
            RefreshActiveButtonStyle(_currentActiveButton);
        }
    }

    private void RefreshActiveButtonStyle(Button button)
    {
        // Re-apply theme-aware colors
        button.Background = (Brush)Application.Current.Resources["SidebarSelectedBrush"];
        Brush activeBorderBrush = GetActiveBorderBrushForButton(button);
        button.BorderBrush = activeBorderBrush;
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            var target = button.Tag as string;
            if (!string.IsNullOrEmpty(target))
            {
                _currentTarget = target;
                SetActiveButton(button);
                NavigationRequested?.Invoke(this, target);
            }
        }
    }

#if !__WASM__
    private void PIAButton_Click(object sender, RoutedEventArgs e)
    {
        PIAButtonClicked?.Invoke(this, EventArgs.Empty);
    }
#endif

    private void YoloOption_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_harvestFunctionalService == null)
        {
            return;
        }

        _harvestFunctionalService.SetYoloOptionEnabled(!_harvestFunctionalService.IsYoloOptionEnabled);
        ApplyYoloOptionVisualState();
        Log.Information("YOLO option toggled from sidebar: {Enabled}", _harvestFunctionalService.IsYoloOptionEnabled);
    }

    private void OnYoloOptionChanged(object? sender, bool enabled)
    {
        DispatcherQueue.TryEnqueue(ApplyYoloOptionVisualState);
    }

    private void ApplyYoloOptionVisualState()
    {
        bool enabled = _harvestFunctionalService?.IsYoloOptionEnabled == true;
        bool ready = _harvestFunctionalService?.IsYoloRuntimeReady == true;
        bool demoMode = _harvestFunctionalService?.IsDemoModeActive == true;
        bool showAsReady = ready || demoMode;
        YoloOptionStatusDot.Fill = enabled
            ? (showAsReady ? (Brush)Application.Current.Resources["SuccessBrush"] : (Brush)Application.Current.Resources["ErrorBrush"])
            : (Brush)Application.Current.Resources["MutedForegroundBrush"];
        YoloOptionLabel.Text = !enabled ? "Yolo Off" : showAsReady ? "Yolo Active" : "Yolo Fallback";
        YoloOptionLabel.Foreground = enabled
            ? (showAsReady ? (Brush)Application.Current.Resources["SuccessBrush"] : (Brush)Application.Current.Resources["ErrorBrush"])
            : (Brush)Application.Current.Resources["MutedForegroundBrush"];
    }

    private void SetActiveButton(Button button)
    {
        if (ReferenceEquals(_currentActiveButton, button))
        {
            return;
        }

        if (_currentActiveButton != null)
        {
            _currentActiveButton.ClearValue(Button.BackgroundProperty);
            _currentActiveButton.ClearValue(Button.BorderBrushProperty);

            UpdateActiveDot(_currentActiveButton, 0);
        }

        _currentActiveButton = button;

        button.Background = (Brush)Application.Current.Resources["SidebarSelectedBrush"];

        Brush activeBorderBrush = GetActiveBorderBrushForButton(button);
        button.BorderBrush = activeBorderBrush;

        UpdateActiveDot(button, 1);
    }

    private void UpdateActiveDot(Button button, double opacity)
    {
        if (button == FlightNavButton) FlightActiveDot.Opacity = opacity;
        else if (button == CameraNavButton) CameraActiveDot.Opacity = opacity;
        else if (button == MapNavButton) MapActiveDot.Opacity = opacity;
        else if (button == MissionNavButton) MissionActiveDot.Opacity = opacity;
        else if (button == StatsNavButton) StatsActiveDot.Opacity = opacity;
        else if (button == EdgeModeNavButton) EdgeModeActiveDot.Opacity = opacity;
        else if (button == AISettingsNavButton) AISettingsActiveDot.Opacity = opacity;
        else if (button == TlogNavButton) TlogActiveDot.Opacity = opacity;
#if !__WASM__
        else if (button == PIANavButton) PIAActiveDot.Opacity = opacity;
#endif
    }

    private Brush GetActiveBorderBrushForButton(Button button)
    {
        if (button == FlightNavButton) return (Brush)Application.Current.Resources["NavFlightBrush"];
        if (button == CameraNavButton) return (Brush)Application.Current.Resources["NavCameraBrush"];
        if (button == MapNavButton) return (Brush)Application.Current.Resources["NavMapBrush"];
        if (button == MissionNavButton) return (Brush)Application.Current.Resources["NavMapBrush"];
        if (button == StatsNavButton) return (Brush)Application.Current.Resources["NavStatsBrush"];
        if (button == AISettingsNavButton) return (Brush)Application.Current.Resources["PrimaryBrush"];
        if (button == TlogNavButton) return (Brush)Application.Current.Resources["NavTlogBrush"];
#if !__WASM__
        if (button == PIANavButton) return (Brush)Application.Current.Resources["PrimaryBrush"];
#endif

        return (Brush)Application.Current.Resources["PrimaryBrush"];
    }

    private Brush GetActiveBorderBrush(Button button)
    {
        return GetActiveBorderBrushForButton(button);
    }

    private async void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _themeService.ToggleThemeAsync();
            ApplyThemeToggleVisualState();
            Log.Information("Theme toggled from Sidebar");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to toggle theme from Sidebar");
        }
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyThemeToggleVisualState();
            if (_currentActiveButton != null)
            {
                RefreshActiveButtonStyle(_currentActiveButton);
            }
        });
    }

    private void ApplyThemeToggleVisualState()
    {
        var resolvedTheme = _themeService.GetResolvedTheme();
        bool isDark = resolvedTheme == ThemeMode.Dark;
        var trackColor = isDark
            ? ((SolidColorBrush)Application.Current.Resources["ThemeToggleTrackDarkBrush"]).Color
            : ((SolidColorBrush)Application.Current.Resources["ThemeToggleTrackLightBrush"]).Color;
        var thumbColor = isDark
            ? ((SolidColorBrush)Application.Current.Resources["ThemeToggleThumbDarkBrush"]).Color
            : ((SolidColorBrush)Application.Current.Resources["PopoverBrush"]).Color;
        var iconColor = isDark
            ? ((SolidColorBrush)Application.Current.Resources["PrimaryForegroundBrush"]).Color
            : ((SolidColorBrush)Application.Current.Resources["ThemeToggleSunBrush"]).Color;
        var glyph = isDark ? "\uE708" : "\uE706";

        ThemeToggleTrack.Background = new SolidColorBrush(trackColor);
        ThemeToggleThumb.Background = new SolidColorBrush(thumbColor);
        ThemeToggleIcon.Foreground = new SolidColorBrush(iconColor);
        ThemeToggleIcon.Glyph = glyph;
        ThemeToggleThumbTransform.X = isDark ? 2 : 28;
    }

    private void ModernSidebar_Unloaded(object sender, RoutedEventArgs e)
    {
        _themeService.ThemeChanged -= OnThemeChanged;
        if (_harvestFunctionalService != null)
        {
            _harvestFunctionalService.YoloOptionChanged -= OnYoloOptionChanged;
        }
        this.ActualThemeChanged -= OnActualThemeChanged;
    }
}
