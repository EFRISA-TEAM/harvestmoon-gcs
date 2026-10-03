#if !__WASM__
using System;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using HarvestmoonGCS.Core.Models.AI;
using HarvestmoonGCS.ViewModels;

namespace HarvestmoonGCS.Views.Controls;

/// <summary>
/// PIA Chat Panel — Slide-in overlay matching PIGEON React UI design.
/// Full PIA chat panel with urgency-based message styling, quick commands, and orb animations.
/// </summary>
public sealed partial class PIAPanel : UserControl
{
    private ChatViewModel? _viewModel;
    private bool _isMuted = false;
    private bool _isQuickCommandsVisible = true;
    private ObservableCollection<ChatMessage>? _boundMessages;
    private ObservableCollection<MaintenanceTask>? _boundMaintenanceTasks;
    private ObservableCollection<Anomaly>? _boundRecentAnomalies;
    private ObservableCollection<PerformanceTrend>? _boundPerformanceTrend;
    private readonly DispatcherTimer _voicePulseTimer;
    private double _voicePulsePhase;

    public event EventHandler? SettingsRequested;

    public PIAPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _voicePulseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _voicePulseTimer.Tick += OnVoicePulseTick;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Gets or sets the ChatViewModel for data binding
    /// </summary>
    public ChatViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            DetachCollectionSubscriptions();
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = value;

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                BindViewModel();
            }
        }
    }

    /// <summary>
    /// Opens the panel.
    /// </summary>
    public void Open()
    {
        if (_viewModel != null)
        {
            _viewModel.IsOpen = true;
        }

        Visibility = Visibility.Visible;
        SlideTransform.X = 0;
    }

    /// <summary>
    /// Closes the panel.
    /// </summary>
    public void Close()
    {
        if (_viewModel != null)
        {
            _viewModel.IsOpen = false;
        }

        SlideTransform.X = 400;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Toggles the panel open/closed state
    /// </summary>
    public void Toggle()
    {
        if (_viewModel?.IsOpen == true)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null)
        {
            BindViewModel();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachCollectionSubscriptions();
        _voicePulseTimer.Stop();
    }

    private void BindViewModel()
    {
        if (_viewModel == null) return;

        AttachMessageSubscription(_viewModel.Messages);
        AttachMaintenanceTasksSubscription(_viewModel.MaintenanceTasks);
        AttachAnomaliesSubscription(_viewModel.RecentAnomalies);
        AttachPerformanceTrendSubscription(_viewModel.PerformanceTrend);
        RefreshMessages();
        UpdateVoiceStatus();
        UpdateSystemReadiness();
        UpdateInsightPanel();
        UpdateBatteryForecastPanel();
        UpdateValidationPanel();
        UpdateSessionSummaryPanel();
        UpdatePerformancePanel();
        UpdateMaintenancePanel();
        UpdateAnomaliesPanel();

        // Update connection status
        UpdateConnectionStatus();

        // Update health status
        UpdateHealthStatus();

        // Update orb state
        UpdateOrbState();

        // Update thinking state
        UpdateThinkingState();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(ChatViewModel.IsOpen):
                    if (_viewModel?.IsOpen == true)
                    {
                        Visibility = Visibility.Visible;
                        SlideTransform.X = 0;
                    }
                    else
                    {
                        SlideTransform.X = 400;
                        Visibility = Visibility.Collapsed;
                    }
                    break;

                case nameof(ChatViewModel.IsConnected):
                    UpdateConnectionStatus();
                    break;

                case nameof(ChatViewModel.HealthStatus):
                    UpdateHealthStatus();
                    break;

                case nameof(ChatViewModel.IsThinking):
                    UpdateThinkingState();
                    break;

                case nameof(ChatViewModel.IsListening):
                case nameof(ChatViewModel.IsSpeaking):
                    UpdateOrbState();
                    UpdateVoiceStatus();
                    break;

                case nameof(ChatViewModel.IsVoiceAvailable):
                case nameof(ChatViewModel.LastRecognizedCommand):
                case nameof(ChatViewModel.VoiceExecutionStatus):
                case nameof(ChatViewModel.LastVoiceConfidence):
                case nameof(ChatViewModel.VoiceAvailabilityStatus):
                    UpdateVoiceStatus();
                    UpdateSystemReadiness();
                    break;

                case nameof(ChatViewModel.SystemReadiness):
                case nameof(ChatViewModel.ProviderDiagnostic):
                case nameof(ChatViewModel.IsAiReady):
                case nameof(ChatViewModel.IsFallbackReady):
                case nameof(ChatViewModel.IsTelemetryReady):
                    UpdateSystemReadiness();
                    break;

                case nameof(ChatViewModel.InsightStatus):
                case nameof(ChatViewModel.InsightSummary):
                    UpdateInsightPanel();
                    break;

                case nameof(ChatViewModel.BatteryForecast):
                    UpdateBatteryForecastPanel();
                    break;

                case nameof(ChatViewModel.ValidationSummary):
                case nameof(ChatViewModel.ResearchExportStatus):
                    UpdateValidationPanel();
                    break;

                case nameof(ChatViewModel.LastSessionSummary):
                    UpdateSessionSummaryPanel();
                    break;

                case nameof(ChatViewModel.PerformanceScore):
                case nameof(ChatViewModel.PerformanceGrade):
                case nameof(ChatViewModel.PerformanceFeedback):
                case nameof(ChatViewModel.PerformanceTrend):
                    UpdatePerformancePanel();
                    break;

                case nameof(ChatViewModel.MaintenanceSummary):
                case nameof(ChatViewModel.MaintenanceTasks):
                    UpdateMaintenancePanel();
                    break;

                case nameof(ChatViewModel.RecentAnomalies):
                    UpdateAnomaliesPanel();
                    break;

                case nameof(ChatViewModel.Messages):
                    MessagesList.ItemsSource = _viewModel?.Messages.Select(m => new MessageViewModel(m)).ToList();
                    ScrollToBottom();
                    break;
            }
        });
    }

    private void UpdateConnectionStatus()
    {
        if (_viewModel == null) return;

        if (_viewModel.IsConnected)
        {
            ConnectionDotColor.Color = Color.FromArgb(255, 16, 185, 129); // emerald-500
            ConnectionStatus.Text = "MAVLink Connected";
            ConnectionStatus.Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
        }
        else
        {
            ConnectionDotColor.Color = Color.FromArgb(255, 239, 68, 68); // red-500
            ConnectionStatus.Text = "No UAV Connection";
            ConnectionStatus.Foreground = new SolidColorBrush(Color.FromArgb(255, 239, 68, 68));
        }
    }

    private void UpdateHealthStatus()
    {
        if (_viewModel == null) return;

        var status = _viewModel.HealthStatus;
        Color statusColor;
        string statusText;
        string iconGlyph;

        switch (status)
        {
            case "Critical":
                statusColor = Color.FromArgb(255, 220, 38, 38); // red-600
                statusText = "CRITICAL";
                iconGlyph = "\uE7BA"; // Warning icon
                break;
            case "Warning":
                statusColor = Color.FromArgb(255, 245, 158, 11); // amber-500
                statusText = "WARNING";
                iconGlyph = "\uE7BA"; // Warning icon
                break;
            case "Good":
                statusColor = Color.FromArgb(255, 16, 185, 129); // emerald-500
                statusText = "ALL SYSTEMS NOMINAL";
                iconGlyph = "\uE73E"; // Accept icon
                break;
            default: // Disconnected
                statusColor = Color.FromArgb(255, 100, 116, 139); // slate-500
                statusText = "DISCONNECTED";
                iconGlyph = "\uE701"; // WifiOff icon
                break;
        }

        HealthBarColor.Color = statusColor;
        HealthBadgeColor.Color = statusColor;
        HealthLabel.Text = statusText;
        HealthIcon.Glyph = iconGlyph;
    }

    private void UpdateOrbState()
    {
        if (_viewModel == null) return;

        if (_viewModel.IsThinking)
        {
            // Thinking state: Purple
            OrbColorStart.Color = Color.FromArgb(255, 147, 51, 234); // purple-600
            OrbColorMid.Color = Color.FromArgb(255, 139, 92, 246);   // violet-500
            OrbColorEnd.Color = Color.FromArgb(255, 99, 102, 241);   // indigo-600
            OuterRing.Stroke = new SolidColorBrush(Color.FromArgb(128, 168, 85, 247)); // purple-500/50
            InnerRing.Stroke = new SolidColorBrush(Color.FromArgb(128, 168, 85, 247));
            OrbIcon.Glyph = "\uE7C1"; // Brain icon (using generic icon)
        }
        else if (_viewModel.IsListening)
        {
            // Voice listening state: green
            OrbColorStart.Color = Color.FromArgb(255, 22, 163, 74);
            OrbColorMid.Color = Color.FromArgb(255, 16, 185, 129);
            OrbColorEnd.Color = Color.FromArgb(255, 5, 150, 105);
            OuterRing.Stroke = new SolidColorBrush(Color.FromArgb(128, 74, 222, 128));
            InnerRing.Stroke = new SolidColorBrush(Color.FromArgb(128, 74, 222, 128));
            OrbIcon.Glyph = "\uE720";
        }
        else if (!_viewModel.IsConnected || _viewModel.HealthStatus is "Critical" or "Warning")
        {
            // Warning/error state: Red
            OrbColorStart.Color = Color.FromArgb(255, 220, 38, 38); // red-600
            OrbColorMid.Color = Color.FromArgb(255, 239, 68, 68);   // red-500
            OrbColorEnd.Color = Color.FromArgb(255, 185, 28, 28);   // red-700
            OuterRing.Stroke = new SolidColorBrush(Color.FromArgb(128, 248, 113, 113)); // red-400/50
            InnerRing.Stroke = new SolidColorBrush(Color.FromArgb(128, 248, 113, 113));
            OrbIcon.Glyph = "\uE7BA"; // Warning icon
        }
        else
        {
            // Idle state: Blue
            OrbColorStart.Color = Color.FromArgb(255, 37, 99, 235);  // blue-600
            OrbColorMid.Color = Color.FromArgb(255, 6, 182, 212);    // cyan-500
            OrbColorEnd.Color = Color.FromArgb(255, 29, 78, 216);    // blue-700
            OuterRing.Stroke = new SolidColorBrush(Color.FromArgb(77, 59, 130, 246)); // blue-500/30
            InnerRing.Stroke = new SolidColorBrush(Color.FromArgb(77, 59, 130, 246));
            OrbIcon.Glyph = "\uE7C1"; // Activity icon
        }

        // Update speaking indicator
        SpeakingIndicator.Visibility = (_viewModel.IsSpeaking && !_isMuted) 
            ? Visibility.Visible 
            : Visibility.Collapsed;

        UpdateVoicePulseState();
    }

    private void UpdateThinkingState()
    {
        if (_viewModel?.IsThinking == true)
        {
            ThinkingBubble.Visibility = Visibility.Visible;
            StatusText.Text = "analyzing";
            StatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 167, 139, 250)); // purple-400
        }
        else
        {
            ThinkingBubble.Visibility = Visibility.Collapsed;
            StatusText.Text = "ready";
            StatusText.Foreground = ResolveThemeBrush(
                "MutedForegroundBrush",
                Color.FromArgb(255, 51, 65, 85)); // slate-700 fallback
        }
    }

    private void UpdateVoiceStatus()
    {
        if (_viewModel == null) return;

        VoiceStatusText.Text = _viewModel.VoiceExecutionStatus;
        VoiceCommandText.Text = _viewModel.LastRecognizedCommand;
        VoiceConfidenceText.Text = $"{_viewModel.LastVoiceConfidence:P0}";
        VoiceAvailabilityText.Text = string.IsNullOrWhiteSpace(_viewModel.VoiceAvailabilityStatus)
            ? "voice diagnostic tidak tersedia"
            : _viewModel.VoiceAvailabilityStatus;

        if (!_viewModel.IsVoiceAvailable)
        {
            VoiceIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 245, 158, 11));
            return;
        }

        if (_viewModel.IsListening)
        {
            VoiceIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
        }
        else
        {
            VoiceIcon.Foreground = ResolveThemeBrush(
                "MutedForegroundBrush",
                Color.FromArgb(255, 100, 116, 139));
        }

        UpdateVoicePulseState();
    }

    private void UpdateVoicePulseState()
    {
        if (_viewModel == null)
        {
            return;
        }

        if (_viewModel.IsListening)
        {
            if (!_voicePulseTimer.IsEnabled)
            {
                _voicePulsePhase = 0;
                _voicePulseTimer.Start();
            }

            return;
        }

        _voicePulseTimer.Stop();
        ResetVoicePulseVisual();
    }

    private void OnVoicePulseTick(object? sender, object e)
    {
        if (_viewModel?.IsListening != true)
        {
            _voicePulseTimer.Stop();
            ResetVoicePulseVisual();
            return;
        }

        _voicePulsePhase += 0.25;
        if (_voicePulsePhase > Math.PI * 2)
        {
            _voicePulsePhase -= Math.PI * 2;
        }

        var scale = 1.0 + (0.06 * (Math.Sin(_voicePulsePhase) + 1.0) / 2.0);
        var ringOpacity = 0.45 + (0.35 * (Math.Sin(_voicePulsePhase + 0.7) + 1.0) / 2.0);
        var iconOpacity = 0.70 + (0.30 * (Math.Sin(_voicePulsePhase + 1.2) + 1.0) / 2.0);

        ApplyScaleTransform(MainOrb, scale);
        ApplyScaleTransform(OuterRing, scale + 0.02);
        MainOrb.Opacity = iconOpacity;
        VoiceIcon.Opacity = iconOpacity;
        OuterRing.Opacity = ringOpacity;
    }

    private void ResetVoicePulseVisual()
    {
        ApplyScaleTransform(MainOrb, 1.0);
        ApplyScaleTransform(OuterRing, 1.0);
        MainOrb.Opacity = 1.0;
        VoiceIcon.Opacity = 1.0;
        OuterRing.Opacity = 1.0;
    }

    private static void ApplyScaleTransform(FrameworkElement element, double scale)
    {
        if (element == null)
        {
            return;
        }

        if (element.RenderTransform is not ScaleTransform transform)
        {
            transform = new ScaleTransform
            {
                CenterX = element.ActualWidth > 0 ? element.ActualWidth / 2 : 16,
                CenterY = element.ActualHeight > 0 ? element.ActualHeight / 2 : 16,
                ScaleX = 1.0,
                ScaleY = 1.0
            };
            element.RenderTransform = transform;
        }

        transform.ScaleX = scale;
        transform.ScaleY = scale;
    }

    private void UpdateSystemReadiness()
    {
        if (_viewModel == null) return;

        SystemReadinessText.Text = string.IsNullOrWhiteSpace(_viewModel.SystemReadiness)
            ? "AI:unknown | Voice:unknown | Telemetry:unknown | MAVLink:unknown | Fallback:unknown"
            : _viewModel.SystemReadiness;

        ProviderDiagnosticText.Text = string.IsNullOrWhiteSpace(_viewModel.ProviderDiagnostic)
            ? "provider diagnostic belum tersedia."
            : _viewModel.ProviderDiagnostic;
    }

    private void UpdateInsightPanel()
    {
        if (_viewModel == null) return;

        InsightStatusText.Text = _viewModel.InsightStatus;
        InsightSummaryText.Text = string.IsNullOrWhiteSpace(_viewModel.InsightSummary)
            ? "Belum ada analisis."
            : _viewModel.InsightSummary;

        InsightStatusText.Foreground = _viewModel.InsightStatus switch
        {
            "CRITICAL" => new SolidColorBrush(Color.FromArgb(255, 220, 38, 38)),
            "WARNING" => new SolidColorBrush(Color.FromArgb(255, 245, 158, 11)),
            "GOOD" => new SolidColorBrush(Color.FromArgb(255, 16, 185, 129)),
            _ => ResolveThemeBrush("ForegroundBrush", Color.FromArgb(255, 226, 232, 240))
        };
    }

    private void UpdateBatteryForecastPanel()
    {
        if (_viewModel == null) return;

        BatteryForecastText.Text = string.IsNullOrWhiteSpace(_viewModel.BatteryForecast)
            ? "Belum ada prediksi baterai."
            : _viewModel.BatteryForecast;
    }

    private void UpdateValidationPanel()
    {
        if (_viewModel == null) return;

        ValidationSummaryText.Text = string.IsNullOrWhiteSpace(_viewModel.ValidationSummary)
            ? "Belum ada metrik validasi."
            : _viewModel.ValidationSummary;

        ResearchExportStatusText.Text = string.IsNullOrWhiteSpace(_viewModel.ResearchExportStatus)
            ? "Belum ada export report."
            : _viewModel.ResearchExportStatus;
    }

    private void UpdateSessionSummaryPanel()
    {
        if (_viewModel == null) return;

        SessionSummaryText.Text = string.IsNullOrWhiteSpace(_viewModel.LastSessionSummary)
            ? "Belum ada ringkasan sesi."
            : _viewModel.LastSessionSummary;
    }

    private void UpdatePerformancePanel()
    {
        if (_viewModel == null) return;

        PerformanceScoreText.Text = $"{_viewModel.PerformanceScore:0.0} (Grade {_viewModel.PerformanceGrade})";
        PerformanceFeedbackText.Text = string.IsNullOrWhiteSpace(_viewModel.PerformanceFeedback)
            ? "Belum ada skor performa."
            : _viewModel.PerformanceFeedback;

        var bars = _viewModel.PerformanceTrend
            .TakeLast(12)
            .Select(t => new PerformanceTrendItemViewModel(t))
            .ToList();

        PerformanceTrendChart.ItemsSource = bars;
        PerformanceTrendEmptyText.Visibility = bars.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMaintenancePanel()
    {
        if (_viewModel == null) return;

        if (_viewModel.MaintenanceTasks.Count == 0)
        {
            MaintenanceStatusText.Text = "Belum ada jadwal maintenance.";
            MaintenanceList.ItemsSource = null;
            ViewMaintenanceScheduleButton.IsEnabled = false;
            return;
        }

        MaintenanceStatusText.Text = $"{_viewModel.MaintenanceTasks.Count} task terjadwal.";
        ViewMaintenanceScheduleButton.IsEnabled = true;
        MaintenanceList.ItemsSource = _viewModel.MaintenanceTasks
            .OrderBy(t => t.DueDate)
            .Take(6)
            .Select(t => new MaintenanceTaskItemViewModel(t))
            .ToList();
    }

    private void UpdateAnomaliesPanel()
    {
        if (_viewModel == null) return;

        if (_viewModel.RecentAnomalies.Count == 0)
        {
            AnomaliesList.ItemsSource = new[] { "Tidak ada anomali terbaru." };
            return;
        }

        AnomaliesList.ItemsSource = _viewModel.RecentAnomalies
            .Take(5)
            .Select(a => $"[{a.Severity}] {a.Message}")
            .ToList();
    }

    private Brush ResolveThemeBrush(string resourceKey, Color fallbackColor)
    {
        if (Application.Current?.Resources.TryGetValue(resourceKey, out var value) == true &&
            value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallbackColor);
    }

    private void ScrollToBottom()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ScrollAnchor?.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = true,
                VerticalAlignmentRatio = 1.0
            });
        });
    }

    // ── Event Handlers ──────────────────────────────────────────────────────────

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        MuteIcon.Glyph = _isMuted ? "\uE74F" : "\uE767"; // VolumeOff vs Volume
        _viewModel?.ToggleMuteCommand?.Execute(null);
        UpdateOrbState();
    }

    private void Voice_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.IsListening != true)
        {
            VoiceStatusText.Text = "starting voice...";
            VoiceAvailabilityText.Text = "membuka mikrofon...";
            VoiceIcon.Foreground = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
        }

        _viewModel?.ToggleVoiceListeningCommand?.Execute(null);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.ClearCommand?.Execute(null);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ExportResearchJson_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.ExportResearchJsonCommand?.Execute(null);
    }

    private void ExportResearchCsv_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.ExportResearchCsvCommand?.Execute(null);
    }

    private void ToggleQuickCommands_Click(object sender, RoutedEventArgs e)
    {
        _isQuickCommandsVisible = !_isQuickCommandsVisible;
        QuickCommandsPanel.Visibility = _isQuickCommandsVisible ? Visibility.Visible : Visibility.Collapsed;
        QuickToggleIcon.Glyph = _isQuickCommandsVisible ? "\uE70D" : "\uE70E"; // ChevronDown vs ChevronUp
    }

    private void QuickCommand_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string query)
        {
            _viewModel?.QuickCommand?.Execute(query);
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e)
    {
        SendMessage();
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        btnSend.IsEnabled = !string.IsNullOrWhiteSpace(InputBox.Text);
    }

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            SendMessage();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            InputBox.Text = string.Empty;
            e.Handled = true;
        }
    }

    private void SendMessage()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text) || _viewModel == null) return;

        _viewModel.InputText = text;
        _viewModel.SendCommand?.Execute(null);
        InputBox.Text = string.Empty;
    }

    private async void ViewMaintenanceSchedule_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_viewModel == null)
            {
                return;
            }

            var tasks = _viewModel.MaintenanceTasks
                .OrderBy(t => t.DueDate)
                .ToList();

        var stack = new StackPanel
        {
            Spacing = 8
        };

        if (tasks.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Belum ada jadwal maintenance.",
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            foreach (var task in tasks)
            {
                stack.Children.Add(new Border
                {
                    Background = ResolveThemeBrush("CardBrush95", Color.FromArgb(255, 30, 41, 59)),
                    BorderBrush = ResolveThemeBrush("BorderBrush", Color.FromArgb(255, 71, 85, 105)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8),
                    Child = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = task.Component,
                                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                            },
                            new TextBlock
                            {
                                Text = $"{task.Type} | Priority {task.Priority}",
                                FontSize = 12
                            },
                            new TextBlock
                            {
                                Text = $"Due {task.DueDate.ToLocalTime():dd MMM yyyy HH:mm}",
                                FontSize = 12
                            },
                            new TextBlock
                            {
                                Text = task.RecommendedAction,
                                TextWrapping = TextWrapping.Wrap,
                                FontSize = 12
                            }
                        }
                    }
                });
            }
        }

        var dialog = new ContentDialog
        {
            Title = "Maintenance Schedule",
            Content = new ScrollViewer
            {
                Content = stack,
                MaxHeight = 440
            },
            CloseButtonText = "Tutup",
            XamlRoot = XamlRoot
        };

            // ShowAsync throws if another ContentDialog is already open on this XamlRoot.
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[PIAPanel] Maintenance schedule dialog failed");
        }
    }

    private void AttachMessageSubscription(ObservableCollection<ChatMessage> messages)
    {
        if (ReferenceEquals(_boundMessages, messages))
        {
            return;
        }

        if (_boundMessages != null)
        {
            _boundMessages.CollectionChanged -= OnMessagesCollectionChanged;
        }

        _boundMessages = messages;
        _boundMessages.CollectionChanged += OnMessagesCollectionChanged;
    }

    private void AttachMaintenanceTasksSubscription(ObservableCollection<MaintenanceTask> maintenanceTasks)
    {
        if (ReferenceEquals(_boundMaintenanceTasks, maintenanceTasks))
        {
            return;
        }

        if (_boundMaintenanceTasks != null)
        {
            _boundMaintenanceTasks.CollectionChanged -= OnMaintenanceTasksChanged;
        }

        _boundMaintenanceTasks = maintenanceTasks;
        _boundMaintenanceTasks.CollectionChanged += OnMaintenanceTasksChanged;
    }

    private void AttachAnomaliesSubscription(ObservableCollection<Anomaly> anomalies)
    {
        if (ReferenceEquals(_boundRecentAnomalies, anomalies))
        {
            return;
        }

        if (_boundRecentAnomalies != null)
        {
            _boundRecentAnomalies.CollectionChanged -= OnRecentAnomaliesChanged;
        }

        _boundRecentAnomalies = anomalies;
        _boundRecentAnomalies.CollectionChanged += OnRecentAnomaliesChanged;
    }

    private void AttachPerformanceTrendSubscription(ObservableCollection<PerformanceTrend> trend)
    {
        if (ReferenceEquals(_boundPerformanceTrend, trend))
        {
            return;
        }

        if (_boundPerformanceTrend != null)
        {
            _boundPerformanceTrend.CollectionChanged -= OnPerformanceTrendChanged;
        }

        _boundPerformanceTrend = trend;
        _boundPerformanceTrend.CollectionChanged += OnPerformanceTrendChanged;
    }

    private void DetachCollectionSubscriptions()
    {
        if (_boundMessages != null)
        {
            _boundMessages.CollectionChanged -= OnMessagesCollectionChanged;
            _boundMessages = null;
        }

        if (_boundMaintenanceTasks != null)
        {
            _boundMaintenanceTasks.CollectionChanged -= OnMaintenanceTasksChanged;
            _boundMaintenanceTasks = null;
        }

        if (_boundRecentAnomalies != null)
        {
            _boundRecentAnomalies.CollectionChanged -= OnRecentAnomaliesChanged;
            _boundRecentAnomalies = null;
        }

        if (_boundPerformanceTrend != null)
        {
            _boundPerformanceTrend.CollectionChanged -= OnPerformanceTrendChanged;
            _boundPerformanceTrend = null;
        }
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshMessages();
            ScrollToBottom();
        });
    }

    private void OnMaintenanceTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(UpdateMaintenancePanel);
    }

    private void OnRecentAnomaliesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(UpdateAnomaliesPanel);
    }

    private void OnPerformanceTrendChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(UpdatePerformancePanel);
    }

    private void RefreshMessages()
    {
        if (_viewModel == null)
        {
            MessagesList.ItemsSource = null;
            return;
        }

        MessagesList.ItemsSource = _viewModel.Messages.Select(m => new MessageViewModel(m)).ToList();
    }
}

/// <summary>
/// Wrapper class for ChatMessage to expose UI-specific properties
/// </summary>
public class MessageViewModel
{
    private readonly ChatMessage _message;

    public MessageViewModel(ChatMessage message)
    {
        _message = message;
    }

    public string Content => _message.Content;
    public DateTime Timestamp => _message.Timestamp;
    public string TimestampText => _message.Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public ChatRole Role => _message.Role;
    public ChatUrgency Urgency => _message.Urgency;
    public bool IsUserMessage => _message.Role == ChatRole.User;
    public bool IsAssistantMessage => _message.Role == ChatRole.Assistant;

    // Urgency-specific background colors
    public Windows.UI.Color BackgroundColor => _message.Urgency switch
    {
        ChatUrgency.Warning => Windows.UI.Color.FromArgb(153, 69, 26, 3),    // amber-950/60
        ChatUrgency.Critical => Windows.UI.Color.FromArgb(153, 69, 10, 10),  // red-950/60
        ChatUrgency.Success => Windows.UI.Color.FromArgb(153, 5, 46, 22),    // emerald-950/60
        _ => Windows.UI.Color.FromArgb(153, 30, 41, 59)                      // slate-800/60
    };

    // Urgency-specific border colors
    public Windows.UI.Color BorderColor => _message.Urgency switch
    {
        ChatUrgency.Warning => Windows.UI.Color.FromArgb(255, 245, 158, 11),  // amber-500
        ChatUrgency.Critical => Windows.UI.Color.FromArgb(255, 220, 38, 38),  // red-500
        ChatUrgency.Success => Windows.UI.Color.FromArgb(255, 16, 185, 129),  // emerald-500
        _ => Windows.UI.Color.FromArgb(255, 100, 116, 139)                    // slate-500
    };

    // Urgency-specific text colors
    public Windows.UI.Color TextColor => _message.Urgency switch
    {
        ChatUrgency.Warning => Windows.UI.Color.FromArgb(255, 254, 243, 199),  // amber-100
        ChatUrgency.Critical => Windows.UI.Color.FromArgb(255, 254, 226, 226), // red-100
        ChatUrgency.Success => Windows.UI.Color.FromArgb(255, 209, 250, 229),  // emerald-100
        _ => Windows.UI.Color.FromArgb(255, 226, 232, 240)                     // slate-100
    };
}

public class MaintenanceTaskItemViewModel
{
    private readonly MaintenanceTask _task;

    public MaintenanceTaskItemViewModel(MaintenanceTask task)
    {
        _task = task;
    }

    public string Component => _task.Component;
    public string Summary => $"{_task.Type} | priority {_task.Priority}";
    public string DueText => $"Due {_task.DueDate.ToLocalTime():dd MMM yyyy HH:mm}";
}

public class PerformanceTrendItemViewModel
{
    private readonly PerformanceTrend _trend;

    public PerformanceTrendItemViewModel(PerformanceTrend trend)
    {
        _trend = trend;
    }

    public double BarHeight => Math.Clamp(_trend.Score * 0.6, 6, 60);
    public string Grade => _trend.Grade;
}
#endif
