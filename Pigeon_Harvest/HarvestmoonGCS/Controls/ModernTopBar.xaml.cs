using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace HarvestmoonGCS.Controls;

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected
}

public sealed partial class ModernTopBar : UserControl
{
    private readonly DispatcherTimer _exitTimer;
    private bool _isConfirmingExit;
    private ConnectionStatus _lastVisualStatus = (ConnectionStatus)(-1);
    private double _lastRenderedBattery = double.NaN;
    private int _lastRenderedSats = int.MinValue;
    private string? _lastRenderedMode;
    private bool _lastRenderedArmed;
    private string? _lastRenderedEndpoint;
    private bool? _lastRenderedVoiceListening;
    private string? _lastRenderedVoiceStatus;

    public ModernTopBar()
    {
        this.InitializeComponent();
        
        _exitTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _exitTimer.Tick += (s, e) => ResetExitState();
        
        this.Loaded += (s, e) =>
        {
            _lastVisualStatus = (ConnectionStatus)(-1);
            UpdateConnectionVisuals();
        };
    }

    // Dependency Properties
    public static readonly DependencyProperty StatusProperty =
        DependencyProperty.Register(nameof(Status), typeof(ConnectionStatus), typeof(ModernTopBar), 
            new PropertyMetadata(ConnectionStatus.Disconnected, (d, e) => ((ModernTopBar)d).UpdateConnectionVisuals()));

    public ConnectionStatus Status
    {
        get => (ConnectionStatus)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public static readonly DependencyProperty BatteryProperty =
        DependencyProperty.Register(nameof(Battery), typeof(double), typeof(ModernTopBar), 
            new PropertyMetadata(0.0, (d, e) => ((ModernTopBar)d).UpdateBatteryVisuals()));

    public double Battery
    {
        get => (double)GetValue(BatteryProperty);
        set => SetValue(BatteryProperty, value);
    }

    public static readonly DependencyProperty GpsSatsProperty =
        DependencyProperty.Register(nameof(GpsSats), typeof(int), typeof(ModernTopBar), 
            new PropertyMetadata(0, (d, e) => ((ModernTopBar)d).UpdateGpsVisuals()));

    public int GpsSats
    {
        get => (int)GetValue(GpsSatsProperty);
        set => SetValue(GpsSatsProperty, value);
    }

    public static readonly DependencyProperty ModeProperty =
        DependencyProperty.Register(nameof(Mode), typeof(string), typeof(ModernTopBar), 
            new PropertyMetadata("—", (d, e) => ((ModernTopBar)d).UpdateModeVisuals()));

    public string Mode
    {
        get => (string)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public static readonly DependencyProperty IsArmedProperty =
        DependencyProperty.Register(nameof(IsArmed), typeof(bool), typeof(ModernTopBar), 
            new PropertyMetadata(false, (d, e) => ((ModernTopBar)d).UpdateArmedVisuals()));

    public bool IsArmed
    {
        get => (bool)GetValue(IsArmedProperty);
        set => SetValue(IsArmedProperty, value);
    }

    public static readonly DependencyProperty AiFpsProperty =
        DependencyProperty.Register(nameof(AiFps), typeof(double), typeof(ModernTopBar),
            new PropertyMetadata(0.0, (d, e) => ((ModernTopBar)d).UpdateAiFpsVisuals()));

    public double AiFps
    {
        get => (double)GetValue(AiFpsProperty);
        set => SetValue(AiFpsProperty, value);
    }

    private void UpdateAiFpsVisuals()
    {
        if (AIFpsText != null)
        {
            AIFpsText.Text = AiFps > 0 ? $"AI {AiFps:F0} FPS" : "AI — FPS";
        }
    }

    public static readonly DependencyProperty EndpointProperty =
        DependencyProperty.Register(nameof(Endpoint), typeof(string), typeof(ModernTopBar), 
            new PropertyMetadata(null, (d, e) => ((ModernTopBar)d).UpdateEndpointVisuals()));

    public string Endpoint
    {
        get => (string)GetValue(EndpointProperty);
        set => SetValue(EndpointProperty, value);
    }

    public static readonly DependencyProperty IsVoiceListeningProperty =
        DependencyProperty.Register(nameof(IsVoiceListening), typeof(bool), typeof(ModernTopBar),
            new PropertyMetadata(false, (d, e) => ((ModernTopBar)d).UpdateVoiceVisuals()));

    public bool IsVoiceListening
    {
        get => (bool)GetValue(IsVoiceListeningProperty);
        set => SetValue(IsVoiceListeningProperty, value);
    }

    public static readonly DependencyProperty VoiceStatusProperty =
        DependencyProperty.Register(nameof(VoiceStatus), typeof(string), typeof(ModernTopBar),
            new PropertyMetadata("idle", (d, e) => ((ModernTopBar)d).UpdateVoiceVisuals()));

    public string VoiceStatus
    {
        get => (string)GetValue(VoiceStatusProperty);
        set => SetValue(VoiceStatusProperty, value);
    }

    // Events
    public event EventHandler? ConnectClicked;
    public event EventHandler? ExitClicked;
    public event EventHandler? RTLClicked;
    public event EventHandler? MissionClicked;

    // Mission state — starts false since no mission is actually running until a real
    // MISSION_START command succeeds (see MainPage_Modern.TopBar_MissionClicked).
    private bool _isMissionRunning;

    /// <summary>Whether the top bar currently shows the mission as running (reflects a real, confirmed state — see <see cref="SetMissionRunning"/>).</summary>
    public bool IsMissionRunning => _isMissionRunning;

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectClicked?.Invoke(this, EventArgs.Empty);
    }

    private void RTLButton_Click(object sender, RoutedEventArgs e)
    {
        RTLClicked?.Invoke(this, EventArgs.Empty);
    }

    private void MissionButton_Click(object sender, RoutedEventArgs e)
    {
        // Requests the opposite of the current confirmed state; the handler in
        // MainPage_Modern sends the real MAVLink command and calls SetMissionRunning
        // with whatever actually happened \u2014 this button never flips itself optimistically.
        MissionClicked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reflects the real mission execution state in the UI. Call this only after a genuine
    /// MAVLink start/pause command has been confirmed (or failed), not on click.
    /// </summary>
    public void SetMissionRunning(bool running)
    {
        _isMissionRunning = running;
        MissionLabel.Text = running ? "Stop Mission" : "Start Mission";
        MissionIcon.Glyph = "\uE768";
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isConfirmingExit)
        {
            _isConfirmingExit = true;
            VisualStateManager.GoToState(this, "ExitConfirm", true);
            _exitTimer.Start();
        }
        else
        {
            ResetExitState();
            ExitClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ResetExitState()
    {
        _isConfirmingExit = false;
        _exitTimer.Stop();
        VisualStateManager.GoToState(this, "ExitNormal", true);
    }

    // Page title update method
    public void UpdatePageTitle(string title, string subtitle, string iconGlyph)
    {
        PageTitle.Text = title;
        // In tablet compact mode the subtitle is hidden, but we store the value
        // so it can be restored if the mode changes.
        PageSubtitle.Text = subtitle;
        PageIcon.Glyph = iconGlyph;
    }

    /// <summary>
    /// Applies a compact layout suited for the Realme Pad Mini (1340×800) and other
    /// Android tablets where the top bar needs to fit in less vertical/horizontal space.
    /// Desktop layout is unaffected — this is only called when IsAndroidDevice() is true.
    /// </summary>
    public void ApplyTabletCompactMode(bool compact)
    {
        // Reduce top bar height and internal padding
        if (Content is Border rootBorder)
        {
            rootBorder.Height  = compact ? 44 : 56;
            rootBorder.Padding = compact ? new Thickness(10, 0) : new Thickness(16, 0);
        }

        // Shrink page title font
        if (PageTitle != null)    PageTitle.FontSize    = compact ? 12 : 14;
        if (PageSubtitle != null) PageSubtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (PageIcon != null)     PageIcon.FontSize     = compact ? 14 : 16;

        // Compact status pills — smaller padding and font
        var pillPadding  = compact ? new Thickness(6, 4) : new Thickness(10, 6);
        var pillFontSize = compact ? 10.0 : 11.0;
        SetPillCompact(WifiPill,    WifiLabel,    pillPadding, pillFontSize);
        SetPillCompact(BatteryPill, BatteryText,  pillPadding, pillFontSize);
        SetPillCompact(GpsPill,     GpsText,      pillPadding, pillFontSize);

        // Compact action buttons
        double btnHeight = compact ? 30 : 34;
        var btnPadding = compact ? new Thickness(8, 0) : new Thickness(12, 0);
        if (RTLButton != null)
        {
            RTLButton.Height = btnHeight;
            RTLButton.Padding = compact ? new Thickness(7, 0) : new Thickness(10, 0);
        }
        if (MissionButton != null)
        {
            MissionButton.Height = btnHeight;
            MissionButton.Padding = btnPadding;
        }

        // Shrink button font sizes
        SetButtonLabelFontSize(RTLButton,     compact ? 11.0 : 12.0);
        SetButtonLabelFontSize(MissionButton, compact ? 11.0 : 12.0);

        // Connect button
        if (ConnectButton != null)
        {
            ConnectButton.Height = btnHeight;
            ConnectButton.Padding = compact ? new Thickness(8, 0, 10, 0) : new Thickness(12, 0, 14, 0);
        }
        if (ConnectionLabel != null) ConnectionLabel.FontSize = compact ? 10.0 : 11.0;
    }

    private static void SetPillCompact(Border? pill, TextBlock? label, Thickness padding, double fontSize)
    {
        if (pill  != null) pill.Padding  = padding;
        if (label != null) label.FontSize = fontSize;
    }

    private static void SetButtonLabelFontSize(Button? btn, double size)
    {
        if (btn?.Content is StackPanel sp)
        {
            foreach (var child in sp.Children)
            {
                if (child is TextBlock tb) tb.FontSize = size;
            }
        }
    }

    private void UpdateConnectionVisuals()
    {
        if (_lastVisualStatus == Status)
        {
            return;
        }

        _lastVisualStatus = Status;

        string state = Status switch
        {
            ConnectionStatus.Connected => "Connected",
            ConnectionStatus.Connecting => "Connecting",
            _ => "Disconnected"
        };
        VisualStateManager.GoToState(this, state, true);
        if (state == "Connected")
        {
            (Resources["DotPulseAnimation"] as Storyboard)?.Begin();
        }
        else
        {
            (Resources["DotPulseAnimation"] as Storyboard)?.Stop();
        }

        if (ConnectButton != null)
        {
            ConnectButton.Style = Status switch
            {
                ConnectionStatus.Connected => (Style)Application.Current.Resources["ConnectedButtonStyle"],
                ConnectionStatus.Connecting => (Style)Application.Current.Resources["ConnectingButtonStyle"],
                _ => (Style)Application.Current.Resources["DisconnectedButtonStyle"]
            };
        }

        bool isConnected = Status == ConnectionStatus.Connected;
        if (RTLButton != null)
        {
            RTLButton.Opacity = isConnected ? 1.0 : 0.45;
        }
        if (MissionButton != null)
        {
            MissionButton.Opacity = isConnected ? 1.0 : 0.45;
        }

        UpdateWifiPillVisuals();
        UpdateTelemetryColors();
    }

    private void UpdateWifiPillVisuals()
    {
        if (WifiPill == null || WifiIcon == null || WifiLabel == null)
        {
            return;
        }

        switch (Status)
        {
            case ConnectionStatus.Connected:
                WifiLabel.Text = "Connected";
                WifiIcon.Glyph = "\uE701";
                WifiLabel.Foreground = ResolveBrush("ConnectedBrush", "SuccessBrush");
                WifiIcon.Foreground = ResolveBrush("ConnectedBrush", "SuccessBrush");
                WifiPill.Background = ResolveBrush("ConnectedBgBrush", "SuccessBrush20");
                WifiPill.BorderBrush = ResolveBrush("ConnectedBorderBrush", "SuccessBrush40");
                break;

            case ConnectionStatus.Connecting:
                WifiLabel.Text = "Connecting...";
                WifiIcon.Glyph = "\uE895";
                WifiLabel.Foreground = ResolveBrush("ConnectingBrush", "WarningBrush");
                WifiIcon.Foreground = ResolveBrush("ConnectingBrush", "WarningBrush");
                WifiPill.Background = ResolveBrush("ConnectingBgBrush", "WarningBrush40");
                WifiPill.BorderBrush = ResolveBrush("ConnectingBorderBrush", "WarningBrush40");
                break;

            case ConnectionStatus.Disconnected:
            default:
                WifiLabel.Text = "Disconnected";
                WifiIcon.Glyph = "\uE703";
                WifiLabel.Foreground = ResolveBrush("DisconnectedBrush", "ErrorBrush");
                WifiIcon.Foreground = ResolveBrush("DisconnectedBrush", "ErrorBrush");
                WifiPill.Background = ResolveBrush("DisconnectedBgBrush", "ErrorBrush20");
                WifiPill.BorderBrush = ResolveBrush("DisconnectedBorderBrush", "ErrorBrush40");
                break;
        }
    }

    private Brush ResolveBrush(string localKey, string fallbackThemeKey)
    {
        if (Resources.TryGetValue(localKey, out var res) && res is Brush b1)
        {
            return b1;
        }
        if (Application.Current.Resources.TryGetValue(fallbackThemeKey, out var appRes) && appRes is Brush b2)
        {
            return b2;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void UpdateBatteryVisuals()
    {
        if (BatteryText == null || BatteryIcon == null)
        {
            return;
        }

        bool isConnected = Status == ConnectionStatus.Connected;
        var nextText = (isConnected && Battery > 0) ? $"{Battery:F0}%" : "—";
        if (!string.Equals(BatteryText.Text, nextText, StringComparison.Ordinal))
        {
            BatteryText.Text = nextText;
        }

        if (isConnected && !double.IsNaN(_lastRenderedBattery) && Math.Abs(_lastRenderedBattery - Battery) < 0.5)
        {
            return;
        }

        _lastRenderedBattery = Battery;
        
        if (isConnected && Battery > 0)
        {
            var brushKey = Battery > 50 ? "SuccessBrush" : (Battery > 20 ? "WarningBrush" : "ErrorBrush");
            var brush = Application.Current.Resources.TryGetValue(brushKey, out var b) ? (Brush)b : BatteryText.Foreground;
            BatteryText.Foreground = brush;
            BatteryIcon.Foreground = brush;
        }
        else
        {
            var muted = (Brush)Application.Current.Resources["MutedForegroundBrush"];
            BatteryText.Foreground = muted;
            BatteryIcon.Foreground = muted;
        }
    }

    private void UpdateGpsVisuals()
    {
        if (GpsText == null || GpsIcon == null)
        {
            return;
        }

        bool isConnected = Status == ConnectionStatus.Connected;
        var nextText = isConnected ? (GpsSats > 0 ? $"Fix ({GpsSats})" : "No Fix") : "—";
        if (!string.Equals(GpsText.Text, nextText, StringComparison.Ordinal))
        {
            GpsText.Text = nextText;
        }

        if (isConnected && _lastRenderedSats == GpsSats)
        {
            return;
        }

        _lastRenderedSats = GpsSats;
        
        if (isConnected && GpsSats > 0)
        {
            var infoBrush = (Brush)Application.Current.Resources["InfoBrush"];
            GpsText.Foreground = infoBrush;
            GpsIcon.Foreground = infoBrush;
        }
        else
        {
            var muted = (Brush)Application.Current.Resources["MutedForegroundBrush"];
            GpsText.Foreground = muted;
            GpsIcon.Foreground = muted;
        }
    }

    private void UpdateModeVisuals()
    {
        if (ModeText == null || ModeIcon == null)
        {
            return;
        }

        bool isConnected = Status == ConnectionStatus.Connected;
        var nextMode = isConnected ? Mode : "—";
        if (!string.Equals(ModeText.Text, nextMode, StringComparison.Ordinal))
        {
            ModeText.Text = nextMode;
        }

        if (isConnected && string.Equals(_lastRenderedMode, Mode, StringComparison.Ordinal))
        {
            return;
        }

        _lastRenderedMode = Mode;
        
        if (isConnected && !string.Equals(Mode, "—", StringComparison.Ordinal))
        {
            var navBrush = (Brush)Application.Current.Resources["NavStatsBrush"];
            ModeText.Foreground = navBrush;
            ModeIcon.Foreground = navBrush;
        }
        else
        {
            var muted = (Brush)Application.Current.Resources["MutedForegroundBrush"];
            ModeText.Foreground = muted;
            ModeIcon.Foreground = muted;
        }
    }

    private void UpdateArmedVisuals()
    {
        if (_lastRenderedArmed == IsArmed && Status == _lastVisualStatus)
        {
            return;
        }

        _lastRenderedArmed = IsArmed;
        ArmedBadge.Visibility = Visibility.Collapsed;
    }

    private void UpdateEndpointVisuals()
    {
        if (string.Equals(_lastRenderedEndpoint, Endpoint, StringComparison.Ordinal) && Status == _lastVisualStatus)
        {
            return;
        }

        _lastRenderedEndpoint = Endpoint;
        bool hasEndpoint = !string.IsNullOrEmpty(Endpoint) && Status == ConnectionStatus.Connected;
        EndpointBadge.Visibility = hasEndpoint ? Visibility.Visible : Visibility.Collapsed;
        EndpointText.Text = Endpoint;
    }

    private void UpdateTelemetryColors()
    {
        UpdateBatteryVisuals();
        UpdateGpsVisuals();
        UpdateModeVisuals();
        UpdateArmedVisuals();
        UpdateEndpointVisuals();
        UpdateVoiceVisuals();
    }

    private void UpdateVoiceVisuals()
    {
        if (_lastRenderedVoiceListening == IsVoiceListening &&
            string.Equals(_lastRenderedVoiceStatus, VoiceStatus, StringComparison.Ordinal))
        {
            return;
        }

        _lastRenderedVoiceListening = IsVoiceListening;
        _lastRenderedVoiceStatus = VoiceStatus;

        string text;
        if (IsVoiceListening)
        {
            text = "listening";
        }
        else if (!string.IsNullOrWhiteSpace(VoiceStatus) &&
                 !string.Equals(VoiceStatus, "voice idle", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(VoiceStatus, "idle", StringComparison.OrdinalIgnoreCase))
        {
            text = VoiceStatus;
        }
        else
        {
            text = "idle";
        }

        VoiceStateText.Text = text;

        if (IsVoiceListening)
        {
            VoiceStateText.Foreground = (Brush)Application.Current.Resources["SuccessBrush"];
            VoiceIcon.Foreground = (Brush)Application.Current.Resources["SuccessBrush"];
            return;
        }

        if (string.Equals(text, "voice unavailable", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "voice error", StringComparison.OrdinalIgnoreCase))
        {
            VoiceStateText.Foreground = (Brush)Application.Current.Resources["WarningBrush"];
            VoiceIcon.Foreground = (Brush)Application.Current.Resources["WarningBrush"];
            return;
        }

        VoiceStateText.Foreground = (Brush)Application.Current.Resources["MutedForegroundBrush"];
        VoiceIcon.Foreground = (Brush)Application.Current.Resources["MutedForegroundBrush"];
    }
}
