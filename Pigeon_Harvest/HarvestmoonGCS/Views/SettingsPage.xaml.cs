using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Core.ViewModels;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using CoreThemeService = HarvestmoonGCS.Core.Services.IThemeService;

namespace HarvestmoonGCS.Views;

public sealed partial class SettingsPage : Page
{
    private SettingsViewModel? _viewModel;
    private readonly IMavLinkService? _mavLinkService;
    private DispatcherTimer? _connectionStatusTimer;

    public SettingsPage()
    {
        this.InitializeComponent();

        // Resolved here rather than only in OnNavigatedTo: this app's sidebar navigation caches
        // pages and swaps Frame.Content directly instead of calling Frame.Navigate, so
        // OnNavigatedTo never actually fires for a cached page — the view model stayed null
        // forever and Save/Reset silently did nothing while still showing a "Success" dialog.
        _mavLinkService = App.Current.Services.GetService<IMavLinkService>();
        _viewModel = new SettingsViewModel();
        this.DataContext = _viewModel;
        LoadSettings();

        this.Loaded += SettingsPage_Loaded;
        this.Unloaded += SettingsPage_Unloaded;
    }

    /// <summary>Reloads settings each time this cached page becomes the active tab.</summary>
    public void OnPageActivated()
    {
        LoadSettings();
        RefreshConnectionStatus();
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("SettingsPage loaded");
        RefreshConnectionStatus();
        _connectionStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _connectionStatusTimer.Tick += (_, _) => RefreshConnectionStatus();
        _connectionStatusTimer.Start();
    }

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _connectionStatusTimer?.Stop();
        _connectionStatusTimer = null;
    }

    /// <summary>
    /// Reflects the real MAVLink connection state. Previously this pill was a hardcoded
    /// "Connected" badge in XAML with no binding at all — it showed green even when nothing
    /// was connected.
    /// </summary>
    private void RefreshConnectionStatus()
    {
        bool connected = _mavLinkService?.IsConnected == true;
        var color = connected
            ? Windows.UI.Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E)
            : Windows.UI.Color.FromArgb(0xFF, 0x9C, 0xA3, 0xAF);
        ConnPillDot.Fill = new SolidColorBrush(color);
        ConnPillText.Foreground = new SolidColorBrush(color);
        ConnPillText.Text = connected
            ? $"Connected · {_mavLinkService!.ConnectionString}"
            : "Disconnected";
    }

    private void LoadSettings()
    {
        if (_viewModel == null) return;

        try
        {
            ProtocolCombo.SelectedIndex = _viewModel.ConnectionTypeIndex;
            ThemeCombo.SelectedIndex = _viewModel.ThemeIndex;
            LanguageCombo.SelectedIndex = _viewModel.LanguageIndex;
            UnitCombo.SelectedIndex = _viewModel.UnitSystemIndex;
            HighContrastCheck.IsChecked = _viewModel.HighContrastMode;
            ShowTooltipsCheck.IsChecked = _viewModel.ShowTooltips;
            AnimationsCheck.IsChecked = _viewModel.EnableAnimations;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading settings: {ex.Message}");
        }
    }

    private void SaveCurrentSettings()
    {
        if (_viewModel == null) return;

        try
        {
            _viewModel.ConnectionTypeIndex = ProtocolCombo.SelectedIndex;
            _viewModel.ThemeIndex = ThemeCombo.SelectedIndex;
            _viewModel.LanguageIndex = LanguageCombo.SelectedIndex;
            _viewModel.UnitSystemIndex = UnitCombo.SelectedIndex;
            _viewModel.HighContrastMode = HighContrastCheck.IsChecked ?? false;
            _viewModel.ShowTooltips = ShowTooltipsCheck.IsChecked ?? true;
            _viewModel.EnableAnimations = AnimationsCheck.IsChecked ?? true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error saving settings: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the same real Connect dialog used by the top bar (Serial auto-detect/baud probing,
    /// UDP/TCP, RunCam WiFi Link 2). Previously this button faked success after a 200ms delay
    /// without attempting any real connection at all.
    /// </summary>
    private async void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ConnectDialog(_mavLinkService) { XamlRoot = this.XamlRoot };
            await dialog.ShowAsync();
            RefreshConnectionStatus();
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("Connection Error", $"Failed to open Connect dialog: {ex.Message}");
        }
    }

    private async void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveCurrentSettings();
            
            if (_viewModel != null)
            {
                await _viewModel.SaveSettings();
            }
            
            await ShowMessageDialog("Success", "Pengaturan disimpan");
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("Error", $"Failed to save: {ex.Message}");
        }
    }

    private async void ResetBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var confirm = await ShowConfirmDialog("Reset", "Apakah Anda yakin ingin mereset ke default?");
            
            if (confirm)
            {
                _viewModel?.ResetToDefaults();
                LoadSettings();
                await ShowMessageDialog("Success", "Reset ke default");
            }
        }
        catch (Exception ex)
        {
            await ShowMessageDialog("Error", $"Failed to reset: {ex.Message}");
        }
    }

    private async void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            var themeService = App.Current.Services.GetService(typeof(CoreThemeService)) as CoreThemeService;
            
            if (themeService == null)
            {
                Debug.WriteLine("ThemeService not available");
                return;
            }

            var selectedTheme = ThemeCombo.SelectedIndex switch
            {
                0 => ThemeMode.Light,
                1 => ThemeMode.Dark,
                2 => ThemeMode.System,
                _ => ThemeMode.Light
            };

            await themeService.SetThemeAsync(selectedTheme);
            Debug.WriteLine($"Theme changed to: {selectedTheme}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error changing theme: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task ShowMessageDialog(string title, string message)
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

    private async System.Threading.Tasks.Task<bool> ShowConfirmDialog(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "Yes",
            CloseButtonText = "No",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
