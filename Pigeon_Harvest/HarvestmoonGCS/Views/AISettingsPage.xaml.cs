using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using HarvestmoonGCS.Core.Models.AI;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Core.Services.AI;
using HarvestmoonGCS.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace HarvestmoonGCS.Views;

public sealed partial class AISettingsPage : Page
{
    private const string OpenRouterProvider = "OpenRouter";
    private static readonly string[] SupportedProviders = { "OpenRouter", "Gemini", "OpenAI", "Grok" };

    private ISettingsService? _settingsService;
    private IApiKeyStore? _apiKeyStore;
    private AISettings? _aiSettings;
    private LLMServiceFactory? _llmServiceFactory;
    private HarvestFunctionalService? _harvestFunctionalService;
    private IFileService? _fileService;
    private IAnomalyDetectionService? _anomalyDetectionService;

    public AISettingsPage()
    {
        this.InitializeComponent();
        this.Loaded += AISettingsPage_Loaded;
        ProviderCombo.SelectionChanged += ProviderCombo_SelectionChanged;
        FallbackProviderCombo.SelectionChanged += FallbackProviderCombo_SelectionChanged;

        // Resolved in the constructor, not OnNavigatedTo: this app's sidebar navigation caches
        // pages and swaps Frame.Content directly (PageCacheManager.GetOrCreatePage) instead of
        // calling Frame.Navigate, so OnNavigatedTo never actually fires for cached pages. With
        // resolution only in OnNavigatedTo, every field here stayed null forever and clicking
        // Save Settings crashed with a NullReferenceException on _settingsService!.
        _settingsService = App.Current.Services.GetService(typeof(ISettingsService)) as ISettingsService;
        _apiKeyStore = App.Current.Services.GetService(typeof(IApiKeyStore)) as IApiKeyStore;
        _llmServiceFactory = App.Current.Services.GetService(typeof(LLMServiceFactory)) as LLMServiceFactory;
        _harvestFunctionalService = App.Current.Services.GetService(typeof(HarvestFunctionalService)) as HarvestFunctionalService;
        _fileService = App.Current.Services.GetService(typeof(IFileService)) as IFileService;
        _anomalyDetectionService = App.Current.Services.GetService(typeof(IAnomalyDetectionService)) as IAnomalyDetectionService;

        LoadSettings();
    }

    /// <summary>Reloads settings each time this cached page becomes the active tab.</summary>
    public void OnPageActivated()
    {
        LoadSettings();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
    }

    private void AISettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
    }

    private void LoadSettings()
    {
        if (_settingsService == null) return;

        try
        {
            _aiSettings = _settingsService.Settings.AI;
            
            if (_aiSettings == null)
            {
                _aiSettings = new AISettings();
                _settingsService.Settings.AI = _aiSettings;
            }

            NormalizeProviderSettings(_aiSettings);

            SelectProvider(_aiSettings.Provider);
            SelectFallbackProvider(_aiSettings.FallbackProvider);

            AiEnabledToggle.IsOn = _aiSettings.Enabled;
            LoadProviderApiKeyToInput();
            LoadFallbackProviderApiKeyToInput();
            SiteUrlBox.Text = _aiSettings.SiteUrl ?? string.Empty;
            SiteNameBox.Text = _aiSettings.SiteName ?? string.Empty;

            VoiceEnabledToggle.IsOn = _aiSettings.VoiceCommand.Enabled;
            VoiceConfidenceBox.Text = _aiSettings.VoiceCommand.ConfidenceThreshold.ToString(CultureInfo.InvariantCulture);
            SelectLanguage(_aiSettings.VoiceCommand.Language);

            RuleBasedToggle.IsOn = _aiSettings.AnomalyDetection.RuleBasedEnabled;
            StatisticalToggle.IsOn = _aiSettings.AnomalyDetection.StatisticalEnabled;
            AiLayerToggle.IsOn = _aiSettings.AnomalyDetection.AIEnabled;
            AutoResponseToggle.IsOn = _aiSettings.AnomalyDetection.AutoResponseEnabled;

            AnalysisIntervalBox.Text = _aiSettings.Analysis.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
            AnalysisBufferBox.Text = _aiSettings.Analysis.BufferSeconds.ToString(CultureInfo.InvariantCulture);
            AnalysisMinConfidenceBox.Text = _aiSettings.Analysis.MinConfidence.ToString(CultureInfo.InvariantCulture);
            HistoryRetentionDaysBox.Text = _aiSettings.HistoryRetentionDays.ToString(CultureInfo.InvariantCulture);
            SamplingMinIntervalBox.Text = _aiSettings.TelemetrySampling.MinIntervalMs.ToString(CultureInfo.InvariantCulture);
            SamplingForceIntervalBox.Text = _aiSettings.TelemetrySampling.ForceIntervalMs.ToString(CultureInfo.InvariantCulture);
            SamplingBatteryDeltaBox.Text = _aiSettings.TelemetrySampling.BatteryDeltaPercent.ToString(CultureInfo.InvariantCulture);
            SamplingAltitudeDeltaBox.Text = _aiSettings.TelemetrySampling.AltitudeDeltaMeters.ToString(CultureInfo.InvariantCulture);
            SamplingSpeedDeltaBox.Text = _aiSettings.TelemetrySampling.SpeedDeltaMps.ToString(CultureInfo.InvariantCulture);
            SamplingHeadingDeltaBox.Text = _aiSettings.TelemetrySampling.HeadingDeltaDeg.ToString(CultureInfo.InvariantCulture);
            SamplingVerticalSpeedDeltaBox.Text = _aiSettings.TelemetrySampling.VerticalSpeedDeltaMps.ToString(CultureInfo.InvariantCulture);
            SamplingGpsDeltaBox.Text = _aiSettings.TelemetrySampling.GpsDistanceDeltaMeters.ToString(CultureInfo.InvariantCulture);
            SamplingGpsHdopDeltaBox.Text = _aiSettings.TelemetrySampling.GpsHdopDelta.ToString(CultureInfo.InvariantCulture);
            SamplingBatteryVoltageDeltaBox.Text = _aiSettings.TelemetrySampling.BatteryVoltageDeltaVolts.ToString(CultureInfo.InvariantCulture);
            SamplingBatteryCurrentDeltaBox.Text = _aiSettings.TelemetrySampling.BatteryCurrentDeltaAmp.ToString(CultureInfo.InvariantCulture);
            SamplingRollDeltaBox.Text = _aiSettings.TelemetrySampling.RollDeltaDeg.ToString(CultureInfo.InvariantCulture);
            SamplingPitchDeltaBox.Text = _aiSettings.TelemetrySampling.PitchDeltaDeg.ToString(CultureInfo.InvariantCulture);
            SamplingVibrationDeltaBox.Text = _aiSettings.TelemetrySampling.VibrationMagnitudeDelta.ToString(CultureInfo.InvariantCulture);
            SamplingLinkQualityDeltaBox.Text = _aiSettings.TelemetrySampling.LinkQualityDeltaPercent.ToString(CultureInfo.InvariantCulture);
            SamplingFlightModeToggle.IsOn = _aiSettings.TelemetrySampling.SampleOnFlightModeChange;
            SamplingArmedStateToggle.IsOn = _aiSettings.TelemetrySampling.SampleOnArmedStateChange;

            TelemetryModelBox.Text = _aiSettings.Models.TelemetryAnalysis;
            AnomalyModelBox.Text = _aiSettings.Models.AnomalyDetection;
            ChatModelBox.Text = _aiSettings.Models.NaturalLanguageChat;
            MaintenanceModelBox.Text = _aiSettings.Models.MaintenancePrediction;
            PerformanceModelBox.Text = _aiSettings.Models.PerformanceScoring;
            VoiceIntentModelBox.Text = _aiSettings.Models.VoiceIntent;
            FallbackModelBox.Text = _aiSettings.Models.Fallback;
            LoadVisionRuntimeSettings();
            UpdateProviderDiagnosticText("Diagnostic siap. Gunakan tombol test provider untuk verifikasi koneksi.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error loading AI settings: {ex.Message}");
        }
    }

    private async Task SaveCurrentSettingsAsync()
    {
        if (_settingsService == null || _aiSettings == null) return;

        try
        {
            _aiSettings.Enabled = AiEnabledToggle.IsOn;
            _aiSettings.Provider = GetSelectedProvider();
            _aiSettings.FallbackProvider = NormalizeFallbackProvider(_aiSettings.Provider, GetSelectedFallbackProvider());
            _aiSettings.BaseUrl = ResolveBaseUrl(_aiSettings.Provider);
            var apiKey = OpenRouterApiKeyBox.Password?.Trim() ?? string.Empty;
            _aiSettings.ApiKey = apiKey;
            if (_apiKeyStore != null)
            {
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    _apiKeyStore.RemoveApiKey(_aiSettings.Provider);
                }
                else
                {
                    _apiKeyStore.SaveApiKey(_aiSettings.Provider, apiKey);
                }
            }

            var fallbackApiKey = string.Equals(_aiSettings.FallbackProvider, _aiSettings.Provider, StringComparison.OrdinalIgnoreCase)
                ? apiKey
                : FallbackApiKeyBox.Password?.Trim() ?? string.Empty;
            if (_apiKeyStore != null)
            {
                if (string.Equals(_aiSettings.FallbackProvider, _aiSettings.Provider, StringComparison.OrdinalIgnoreCase))
                {
                    // Fallback provider sama dengan provider utama: gunakan key utama,
                    // dan jangan hapus key jika field fallback sengaja dikosongkan.
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        _apiKeyStore.SaveApiKey(_aiSettings.FallbackProvider, apiKey);
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(fallbackApiKey))
                    {
                        _apiKeyStore.RemoveApiKey(_aiSettings.FallbackProvider);
                    }
                    else
                    {
                        _apiKeyStore.SaveApiKey(_aiSettings.FallbackProvider, fallbackApiKey);
                    }
                }
            }

            _aiSettings.SiteUrl = SiteUrlBox.Text?.Trim() ?? _aiSettings.SiteUrl;
            _aiSettings.SiteName = SiteNameBox.Text?.Trim() ?? _aiSettings.SiteName;

            _aiSettings.VoiceCommand.Enabled = VoiceEnabledToggle.IsOn;
            _aiSettings.VoiceCommand.ConfidenceThreshold = Math.Clamp(
                ParseDoubleOrDefault(VoiceConfidenceBox.Text, _aiSettings.VoiceCommand.ConfidenceThreshold),
                0.3,
                1.0);
            _aiSettings.VoiceCommand.Language = GetSelectedLanguage();

            _aiSettings.AnomalyDetection.RuleBasedEnabled = RuleBasedToggle.IsOn;
            _aiSettings.AnomalyDetection.StatisticalEnabled = StatisticalToggle.IsOn;
            _aiSettings.AnomalyDetection.AIEnabled = AiLayerToggle.IsOn;
            _aiSettings.AnomalyDetection.AutoResponseEnabled = AutoResponseToggle.IsOn;

            _aiSettings.Analysis.IntervalSeconds = Math.Clamp(
                ParseIntOrDefault(AnalysisIntervalBox.Text, _aiSettings.Analysis.IntervalSeconds),
                5,
                300);
            _aiSettings.Analysis.BufferSeconds = Math.Clamp(
                ParseIntOrDefault(AnalysisBufferBox.Text, _aiSettings.Analysis.BufferSeconds),
                5,
                600);
            _aiSettings.Analysis.MinConfidence = Math.Clamp(
                ParseDoubleOrDefault(AnalysisMinConfidenceBox.Text, _aiSettings.Analysis.MinConfidence),
                0,
                1);
            _aiSettings.HistoryRetentionDays = Math.Clamp(
                ParseIntOrDefault(HistoryRetentionDaysBox.Text, _aiSettings.HistoryRetentionDays),
                1,
                3650);

            _aiSettings.TelemetrySampling.MinIntervalMs = Math.Clamp(
                ParseIntOrDefault(SamplingMinIntervalBox.Text, _aiSettings.TelemetrySampling.MinIntervalMs),
                50,
                10_000);
            _aiSettings.TelemetrySampling.ForceIntervalMs = Math.Clamp(
                ParseIntOrDefault(SamplingForceIntervalBox.Text, _aiSettings.TelemetrySampling.ForceIntervalMs),
                _aiSettings.TelemetrySampling.MinIntervalMs,
                60_000);
            _aiSettings.TelemetrySampling.BatteryDeltaPercent = Math.Clamp(
                ParseDoubleOrDefault(SamplingBatteryDeltaBox.Text, _aiSettings.TelemetrySampling.BatteryDeltaPercent),
                0.1,
                20);
            _aiSettings.TelemetrySampling.AltitudeDeltaMeters = Math.Clamp(
                ParseDoubleOrDefault(SamplingAltitudeDeltaBox.Text, _aiSettings.TelemetrySampling.AltitudeDeltaMeters),
                0.2,
                500);
            _aiSettings.TelemetrySampling.SpeedDeltaMps = Math.Clamp(
                ParseDoubleOrDefault(SamplingSpeedDeltaBox.Text, _aiSettings.TelemetrySampling.SpeedDeltaMps),
                0.1,
                50);
            _aiSettings.TelemetrySampling.HeadingDeltaDeg = Math.Clamp(
                ParseDoubleOrDefault(SamplingHeadingDeltaBox.Text, _aiSettings.TelemetrySampling.HeadingDeltaDeg),
                1,
                180);
            _aiSettings.TelemetrySampling.VerticalSpeedDeltaMps = Math.Clamp(
                ParseDoubleOrDefault(SamplingVerticalSpeedDeltaBox.Text, _aiSettings.TelemetrySampling.VerticalSpeedDeltaMps),
                0.1,
                50);
            _aiSettings.TelemetrySampling.GpsDistanceDeltaMeters = Math.Clamp(
                ParseDoubleOrDefault(SamplingGpsDeltaBox.Text, _aiSettings.TelemetrySampling.GpsDistanceDeltaMeters),
                0.5,
                1000);
            _aiSettings.TelemetrySampling.GpsHdopDelta = Math.Clamp(
                ParseDoubleOrDefault(SamplingGpsHdopDeltaBox.Text, _aiSettings.TelemetrySampling.GpsHdopDelta),
                0.01,
                10);
            _aiSettings.TelemetrySampling.BatteryVoltageDeltaVolts = Math.Clamp(
                ParseDoubleOrDefault(SamplingBatteryVoltageDeltaBox.Text, _aiSettings.TelemetrySampling.BatteryVoltageDeltaVolts),
                0.01,
                5);
            _aiSettings.TelemetrySampling.BatteryCurrentDeltaAmp = Math.Clamp(
                ParseDoubleOrDefault(SamplingBatteryCurrentDeltaBox.Text, _aiSettings.TelemetrySampling.BatteryCurrentDeltaAmp),
                0.05,
                50);
            _aiSettings.TelemetrySampling.RollDeltaDeg = Math.Clamp(
                ParseDoubleOrDefault(SamplingRollDeltaBox.Text, _aiSettings.TelemetrySampling.RollDeltaDeg),
                0.2,
                90);
            _aiSettings.TelemetrySampling.PitchDeltaDeg = Math.Clamp(
                ParseDoubleOrDefault(SamplingPitchDeltaBox.Text, _aiSettings.TelemetrySampling.PitchDeltaDeg),
                0.2,
                90);
            _aiSettings.TelemetrySampling.VibrationMagnitudeDelta = Math.Clamp(
                ParseDoubleOrDefault(SamplingVibrationDeltaBox.Text, _aiSettings.TelemetrySampling.VibrationMagnitudeDelta),
                0.5,
                500);
            _aiSettings.TelemetrySampling.LinkQualityDeltaPercent = Math.Clamp(
                ParseDoubleOrDefault(SamplingLinkQualityDeltaBox.Text, _aiSettings.TelemetrySampling.LinkQualityDeltaPercent),
                1,
                100);
            _aiSettings.TelemetrySampling.SampleOnFlightModeChange = SamplingFlightModeToggle.IsOn;
            _aiSettings.TelemetrySampling.SampleOnArmedStateChange = SamplingArmedStateToggle.IsOn;

            _aiSettings.Models.TelemetryAnalysis = TextOrFallback(TelemetryModelBox.Text, _aiSettings.Models.TelemetryAnalysis);
            _aiSettings.Models.AnomalyDetection = TextOrFallback(AnomalyModelBox.Text, _aiSettings.Models.AnomalyDetection);
            _aiSettings.Models.NaturalLanguageChat = TextOrFallback(ChatModelBox.Text, _aiSettings.Models.NaturalLanguageChat);
            _aiSettings.Models.MaintenancePrediction = TextOrFallback(MaintenanceModelBox.Text, _aiSettings.Models.MaintenancePrediction);
            _aiSettings.Models.PerformanceScoring = TextOrFallback(PerformanceModelBox.Text, _aiSettings.Models.PerformanceScoring);
            _aiSettings.Models.VoiceIntent = TextOrFallback(VoiceIntentModelBox.Text, _aiSettings.Models.VoiceIntent);
            _aiSettings.Models.Fallback = TextOrFallback(FallbackModelBox.Text, _aiSettings.Models.Fallback);

            ApplyVisionRuntimeSettings();
            await RestartAnomalyDetectionIfRunningAsync();

            _settingsService.Settings.AI = _aiSettings;
            await _settingsService.SetSettingAsync("AISettings", _aiSettings);
            UpdateProviderDiagnosticSummary();

            Debug.WriteLine("AI settings saved to model");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error saving AI settings: {ex.Message}");
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveCurrentSettingsAsync();
            
            var success = await _settingsService!.SaveSettingsAsync();
            
            if (success)
            {
                await ShowMessageDialog("Success", "AI settings have been saved successfully.");
            }
            else
            {
                await ShowMessageDialog("Error", "Failed to save AI settings.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error saving AI settings: {ex.Message}");
            await ShowMessageDialog("Error", $"Failed to save AI settings: {ex.Message}");
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadSettings();
            
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error cancelling: {ex.Message}");
        }
    }

    private void AdvancedToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var shouldShow = AdvancedSettingsPanel.Visibility != Visibility.Visible;
        AdvancedSettingsPanel.Visibility = shouldShow
            ? Visibility.Visible
            : Visibility.Collapsed;
        AdvancedToggleButton.Content = shouldShow
            ? "Sembunyikan Advanced Settings"
            : "Tampilkan Advanced Settings";
    }

    private void LoadVisionRuntimeSettings()
    {
        var baseDirectory = AppContext.BaseDirectory;
#if __ANDROID__
        var preferredModelName = "moonharvest-uav-det-int8.onnx";
        var preferredClassName = "classes-moonharvest-uav-det.txt";
#else
        var preferredModelName = "yolo11n_proposal_pdp.onnx";
        var preferredClassName = "classes-moonharvest-health.txt";
#endif
        var modelCandidates = new[]
        {
            Path.Combine(baseDirectory, "Assets", "models", preferredModelName),
            Path.Combine(baseDirectory, "Assets", "models", "moonharvest-health-cls.onnx"),
            Path.Combine(baseDirectory, "Assets", "models", "moonharvest-uav-det.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "models", preferredModelName),
            Path.Combine("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/models", preferredModelName),
            Path.Combine("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/models", "moonharvest-health-cls.onnx"),
            Path.Combine("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/models", "moonharvest-uav-det.onnx")
        };
        var defaultModelPath = modelCandidates.FirstOrDefault(File.Exists) ?? modelCandidates[0];

        var classCandidates = new[]
        {
            Path.Combine(baseDirectory, "Assets", "models", preferredClassName),
            Path.Combine(baseDirectory, "Assets", "models", "classes-moonharvest-health.txt"),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "models", preferredClassName),
            Path.Combine("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/models", preferredClassName)
        };
        var defaultClassPath = classCandidates.FirstOrDefault(File.Exists) ?? classCandidates[0];

        var currentModel = _harvestFunctionalService?.RuntimeModelPath;
        var currentClass = _harvestFunctionalService?.RuntimeClassPath;

        bool isModelValid = !string.IsNullOrWhiteSpace(currentModel) && File.Exists(Path.IsPathRooted(currentModel) ? currentModel : Path.Combine(baseDirectory, currentModel));
        bool isClassValid = !string.IsNullOrWhiteSpace(currentClass) && File.Exists(Path.IsPathRooted(currentClass) ? currentClass : Path.Combine(baseDirectory, currentClass));

        if (!isModelValid) currentModel = defaultModelPath;
        if (!isClassValid) currentClass = defaultClassPath;

        VisionModelPathBox.Text = currentModel;
        VisionClassPathBox.Text = currentClass;
        VisionConfidenceBox.Text = (_harvestFunctionalService?.RuntimeConfidenceThreshold ?? 0.4f)
            .ToString("0.00", CultureInfo.InvariantCulture);
        VisionNmsBox.Text = (_harvestFunctionalService?.RuntimeNmsThreshold ?? 0.4f)
            .ToString("0.00", CultureInfo.InvariantCulture);

        if (_harvestFunctionalService != null)
        {
            _harvestFunctionalService.ConfigureYoloRuntime(
                currentModel, 
                currentClass, 
                _harvestFunctionalService.RuntimeConfidenceThreshold, 
                _harvestFunctionalService.RuntimeNmsThreshold);
        }

        VisionRuntimeStatusText.Text = _harvestFunctionalService?.YoloStatusMessage ?? "Vision runtime standby.";
    }

    private void ApplyVisionRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyVisionRuntimeSettings();
    }

    private void ApplyVisionRuntimeSettings()
    {
        if (_harvestFunctionalService == null)
        {
            VisionRuntimeStatusText.Text = "Harvest vision service tidak tersedia.";
            return;
        }

        var modelPath = VisionModelPathBox.Text?.Trim();
        var classPath = VisionClassPathBox.Text?.Trim();
        var confidence = (float)Math.Clamp(ParseDoubleOrDefault(VisionConfidenceBox.Text, 0.4), 0.1, 1.0);
        var nms = (float)Math.Clamp(ParseDoubleOrDefault(VisionNmsBox.Text, 0.4), 0.1, 1.0);

        var ready = _harvestFunctionalService.ConfigureYoloRuntime(modelPath, classPath, confidence, nms);
        VisionRuntimeStatusText.Text = ready
            ? $"YOLO aktif: {Path.GetFileName(_harvestFunctionalService.RuntimeModelPath)} | conf={confidence:0.00} | nms={nms:0.00}"
            : _harvestFunctionalService.YoloStatusMessage;
    }

    /// <summary>
    /// The RuleBased/Statistical/AI anomaly-detection toggles above now write into the same
    /// <see cref="AnomalyDetectionConfig"/> instance the running <see cref="IAnomalyDetectionService"/>
    /// reads (see ServiceCollectionExtensions.AddPIAIntelligence). RuleBased is checked live on
    /// every telemetry snapshot, but the Statistical/AI timers are only created in StartAsync,
    /// so a stop/start is needed for those two toggles to take effect immediately.
    /// </summary>
    private async Task RestartAnomalyDetectionIfRunningAsync()
    {
        if (_anomalyDetectionService == null || !_anomalyDetectionService.IsRunning)
        {
            return;
        }

        await _anomalyDetectionService.StopAsync();
        await _anomalyDetectionService.StartAsync();
    }

    private async void BrowseVisionModelButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_fileService == null)
            {
                VisionRuntimeStatusText.Text = "File picker tidak tersedia.";
                return;
            }

            var path = await _fileService.PickFileAsync(new[] { ".onnx" });
            if (!string.IsNullOrWhiteSpace(path))
            {
                VisionModelPathBox.Text = path;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[AISettingsPage] Browse vision model failed");
            VisionRuntimeStatusText.Text = $"Gagal memilih model: {ex.Message}";
        }
    }

    private async void BrowseVisionClassButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_fileService == null)
            {
                VisionRuntimeStatusText.Text = "File picker tidak tersedia.";
                return;
            }

            var path = await _fileService.PickFileAsync(new[] { ".txt", ".names" });
            if (!string.IsNullOrWhiteSpace(path))
            {
                VisionClassPathBox.Text = path;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[AISettingsPage] Browse vision class failed");
            VisionRuntimeStatusText.Text = $"Gagal memilih file kelas: {ex.Message}";
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

    private static string TextOrFallback(string? value, string fallback)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static int ParseIntOrDefault(string? value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static double ParseDoubleOrDefault(string? value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private void SelectProvider(string? provider)
    {
        var target = NormalizeProviderName(provider);
        for (var i = 0; i < ProviderCombo.Items.Count; i++)
        {
            if (ProviderCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                ProviderCombo.SelectedIndex = i;
                return;
            }
        }

        ProviderCombo.SelectedIndex = 0;
    }

    private string GetSelectedProvider()
    {
        if (ProviderCombo.SelectedItem is ComboBoxItem item)
        {
            return NormalizeProviderName(item.Content?.ToString());
        }

        return OpenRouterProvider;
    }

    private void SelectFallbackProvider(string? provider)
    {
        var target = NormalizeProviderName(provider);
        for (var i = 0; i < FallbackProviderCombo.Items.Count; i++)
        {
            if (FallbackProviderCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                FallbackProviderCombo.SelectedIndex = i;
                return;
            }
        }

        FallbackProviderCombo.SelectedIndex = 1;
    }

    private string GetSelectedFallbackProvider()
    {
        if (FallbackProviderCombo.SelectedItem is ComboBoxItem item)
        {
            return NormalizeProviderName(item.Content?.ToString());
        }

        return "Gemini";
    }

    private static string ResolveBaseUrl(string? provider)
    {
        return NormalizeProviderName(provider) switch
        {
            "Gemini" => "https://generativelanguage.googleapis.com",
            "OpenAI" => "https://api.openai.com/v1",
            "Grok" => "https://api.x.ai/v1",
            _ => "https://openrouter.ai/api/v1"
        };
    }

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EnsureRecommendedFallbackSelection();
        LoadProviderApiKeyToInput();
        LoadFallbackProviderApiKeyToInput();
        UpdateProviderDiagnosticSummary();
    }

    private void FallbackProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EnsureRecommendedFallbackSelection();
        LoadFallbackProviderApiKeyToInput();
        UpdateProviderDiagnosticSummary();
    }

    private void LoadProviderApiKeyToInput()
    {
        if (_aiSettings == null)
        {
            return;
        }

        var provider = GetSelectedProvider();
        string? secureKey = null;
        try
        {
            secureKey = _apiKeyStore?.GetApiKey(provider);
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(secureKey))
        {
            _aiSettings.ApiKey = secureKey;
            OpenRouterApiKeyBox.Password = secureKey;
            UpdateProviderDiagnosticSummary();
            return;
        }

        if (string.Equals(_aiSettings.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(_aiSettings.ApiKey))
        {
            OpenRouterApiKeyBox.Password = _aiSettings.ApiKey;
            UpdateProviderDiagnosticSummary();
            return;
        }

        OpenRouterApiKeyBox.Password = string.Empty;
        UpdateProviderDiagnosticSummary();
    }

    private void LoadFallbackProviderApiKeyToInput()
    {
        if (_aiSettings == null)
        {
            return;
        }

        var provider = GetSelectedFallbackProvider();
        if (string.Equals(provider, GetSelectedProvider(), StringComparison.OrdinalIgnoreCase))
        {
            FallbackApiKeyBox.IsEnabled = false;
            FallbackApiKeyBox.Password = OpenRouterApiKeyBox.Password?.Trim() ?? string.Empty;
            UpdateProviderDiagnosticSummary();
            return;
        }

        FallbackApiKeyBox.IsEnabled = true;
        string? secureKey = null;
        try
        {
            secureKey = _apiKeyStore?.GetApiKey(provider);
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(secureKey))
        {
            FallbackApiKeyBox.Password = secureKey;
            UpdateProviderDiagnosticSummary();
            return;
        }

        FallbackApiKeyBox.Password = string.Empty;
        UpdateProviderDiagnosticSummary();
    }

    private void SelectLanguage(string? language)
    {
        var target = (language ?? "id-ID").Trim();
        for (var i = 0; i < VoiceLanguageCombo.Items.Count; i++)
        {
            if (VoiceLanguageCombo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                VoiceLanguageCombo.SelectedIndex = i;
                return;
            }
        }

        VoiceLanguageCombo.SelectedIndex = 0;
    }

    private string GetSelectedLanguage()
    {
        if (VoiceLanguageCombo.SelectedItem is ComboBoxItem item)
        {
            return item.Content?.ToString() ?? "id-ID";
        }

        return "id-ID";
    }

    private async void TestPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        await TestProviderAsync(
            provider: GetSelectedProvider(),
            apiKey: OpenRouterApiKeyBox.Password?.Trim(),
            isFallback: false);
    }

    private async void TestFallbackButton_Click(object sender, RoutedEventArgs e)
    {
        var fallbackProvider = GetSelectedFallbackProvider();
        var fallbackKey = string.Equals(fallbackProvider, GetSelectedProvider(), StringComparison.OrdinalIgnoreCase)
            ? OpenRouterApiKeyBox.Password?.Trim()
            : FallbackApiKeyBox.Password?.Trim();

        await TestProviderAsync(
            provider: fallbackProvider,
            apiKey: fallbackKey,
            isFallback: true);
    }

    private async Task TestProviderAsync(string provider, string? apiKey, bool isFallback)
    {
        var normalizedProvider = NormalizeProviderName(provider);
        if (string.IsNullOrWhiteSpace(normalizedProvider))
        {
            UpdateProviderDiagnosticText("Provider belum dipilih.");
            return;
        }

        var resolvedApiKey = ResolveApiKeyForProvider(normalizedProvider, apiKey);
        if (string.IsNullOrWhiteSpace(resolvedApiKey))
        {
            UpdateProviderDiagnosticText(
                $"{(isFallback ? "Fallback" : "Primary")} {normalizedProvider}: API key kosong.");
            return;
        }

        try
        {
            UpdateProviderDiagnosticText(
                $"{(isFallback ? "Testing fallback" : "Testing primary")} {normalizedProvider}...");

            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };

            var tempSettings = BuildTempSettings(normalizedProvider, resolvedApiKey);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var service = BuildProviderService(normalizedProvider, tempSettings, httpClient, resolvedApiKey);
            var ok = await service.TestConnectionAsync(cts.Token);
            var health = service.GetHealthStatus();

            if (ok)
            {
                UpdateProviderDiagnosticText(
                    $"{(isFallback ? "Fallback" : "Primary")} {normalizedProvider}: OK | model={health.ActiveModel} | latency={health.LastLatencyMs}ms | cache={(health.CacheHitRate * 100):0.0}%");
            }
            else
            {
                var error = string.IsNullOrWhiteSpace(health.LastError)
                    ? "koneksi gagal (cek model/quota/key)."
                    : health.LastError;
                UpdateProviderDiagnosticText(
                    $"{(isFallback ? "Fallback" : "Primary")} {normalizedProvider}: GAGAL | {error}");
            }
        }
        catch (Exception ex)
        {
            UpdateProviderDiagnosticText(
                $"{(isFallback ? "Fallback" : "Primary")} {normalizedProvider}: ERROR | {ex.Message}");
        }
    }

    private ILLMService BuildProviderService(string provider, AISettings settings, HttpClient httpClient, string apiKey)
    {
        var normalizedProvider = NormalizeProviderName(provider);
        settings.Provider = normalizedProvider;
        settings.FallbackProvider = normalizedProvider;
        settings.BaseUrl = ResolveBaseUrl(normalizedProvider);
        settings.ApiKey = apiKey;
        return normalizedProvider switch
        {
            "Gemini" => new GeminiFallbackService(httpClient, () => apiKey, ResolveDefaultModelForProvider(normalizedProvider)),
            "OpenAI" => new OpenAIService(httpClient, settings, () => apiKey),
            "Grok" => new GrokService(httpClient, settings, () => apiKey),
            _ => new OpenRouterService(settings, () => apiKey, ResolveBaseUrl(normalizedProvider))
        };
    }

    private AISettings BuildTempSettings(string provider, string apiKey)
    {
        var normalizedProvider = NormalizeProviderName(provider);
        var baseSettings = _aiSettings ?? new AISettings();
        var models = baseSettings.Models ?? new AIModelsConfig();

        return new AISettings
        {
            Enabled = true,
            Provider = normalizedProvider,
            FallbackProvider = normalizedProvider,
            ApiKey = apiKey,
            BaseUrl = ResolveBaseUrl(normalizedProvider),
            SiteUrl = SiteUrlBox.Text?.Trim() ?? baseSettings.SiteUrl,
            SiteName = SiteNameBox.Text?.Trim() ?? baseSettings.SiteName,
            Models = new AIModelsConfig
            {
                TelemetryAnalysis = TextOrFallback(TelemetryModelBox.Text, models.TelemetryAnalysis),
                AnomalyDetection = TextOrFallback(AnomalyModelBox.Text, models.AnomalyDetection),
                NaturalLanguageChat = TextOrFallback(ChatModelBox.Text, models.NaturalLanguageChat),
                MaintenancePrediction = TextOrFallback(MaintenanceModelBox.Text, models.MaintenancePrediction),
                PerformanceScoring = TextOrFallback(PerformanceModelBox.Text, models.PerformanceScoring),
                BatteryPrediction = TextOrFallback(models.BatteryPrediction, ResolveDefaultModelForProvider(normalizedProvider)),
                FlightSessionSummary = TextOrFallback(models.FlightSessionSummary, ResolveDefaultModelForProvider(normalizedProvider)),
                VoiceIntent = TextOrFallback(VoiceIntentModelBox.Text, models.VoiceIntent),
                Fallback = TextOrFallback(FallbackModelBox.Text, models.Fallback)
            }
        };
    }

    private string ResolveApiKeyForProvider(string provider, string? inputApiKey)
    {
        if (!string.IsNullOrWhiteSpace(inputApiKey))
        {
            return inputApiKey.Trim();
        }

        var normalizedProvider = string.IsNullOrWhiteSpace(provider)
            ? OpenRouterProvider
            : NormalizeProviderName(provider);

        try
        {
            var secure = _apiKeyStore?.GetApiKey(normalizedProvider);
            if (!string.IsNullOrWhiteSpace(secure))
            {
                return secure;
            }
        }
        catch
        {
        }

        return ResolveEnvironmentApiKey(normalizedProvider);
    }

    private void UpdateProviderDiagnosticSummary()
    {
        var primary = GetSelectedProvider();
        var fallback = GetSelectedFallbackProvider();
        var primaryKeySet = !string.IsNullOrWhiteSpace(ResolveApiKeyForProvider(primary, OpenRouterApiKeyBox.Password));
        var fallbackInput = string.Equals(primary, fallback, StringComparison.OrdinalIgnoreCase)
            ? OpenRouterApiKeyBox.Password
            : FallbackApiKeyBox.Password;
        var fallbackKeySet = !string.IsNullOrWhiteSpace(ResolveApiKeyForProvider(fallback, fallbackInput));
        var baseText =
            $"Primary={primary} (key {(primaryKeySet ? "OK" : "kosong")}) | " +
            $"Fallback={fallback} (key {(fallbackKeySet ? "OK" : "kosong")})";

        try
        {
            var runtimeHealth = _llmServiceFactory?.GetService()?.GetHealthStatus();
            var circuit = _llmServiceFactory?.GetCircuitStatus();
            if (runtimeHealth != null)
            {
                var cachePercent = runtimeHealth.CacheHitRate * 100.0;
                var circuitText = circuit == null
                    ? "n/a"
                    : $"{circuit.CircuitState} fail={circuit.ConsecutiveFailures}";
                baseText +=
                    $" | Active={runtimeHealth.ActiveProvider} ({runtimeHealth.ActiveModel}) | " +
                    $"Cache={cachePercent:0.0}% ({runtimeHealth.TotalRequests} req) | Circuit={circuitText}";

                if (!string.IsNullOrWhiteSpace(runtimeHealth.LastError))
                {
                    baseText += $" | LastError={runtimeHealth.LastError}";
                }
            }
        }
        catch
        {
        }

        UpdateProviderDiagnosticText(baseText);
    }

    private void UpdateProviderDiagnosticText(string text)
    {
        ProviderDiagnosticText.Text = string.IsNullOrWhiteSpace(text)
            ? "Belum ada diagnostic."
            : text.Trim();
    }

    private static void NormalizeProviderSettings(AISettings settings)
    {
        settings.Provider = NormalizeProviderName(settings.Provider);
        settings.FallbackProvider = NormalizeFallbackProvider(settings.Provider, settings.FallbackProvider);
        settings.BaseUrl = ResolveBaseUrl(settings.Provider);
    }

    private void EnsureRecommendedFallbackSelection()
    {
        if (string.Equals(GetSelectedProvider(), OpenRouterProvider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(GetSelectedFallbackProvider(), OpenRouterProvider, StringComparison.OrdinalIgnoreCase))
        {
            SelectFallbackProvider("Gemini");
        }
    }

    private static string NormalizeFallbackProvider(string primaryProvider, string? fallbackProvider)
    {
        var fallback = NormalizeProviderName(fallbackProvider);
        if (string.Equals(primaryProvider, OpenRouterProvider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(fallback, OpenRouterProvider, StringComparison.OrdinalIgnoreCase))
        {
            return "Gemini";
        }

        return fallback;
    }

    private static string NormalizeProviderName(string? provider)
    {
        var normalized = (provider ?? string.Empty).Trim();
        foreach (var supportedProvider in SupportedProviders)
        {
            if (string.Equals(supportedProvider, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return supportedProvider;
            }
        }

        return OpenRouterProvider;
    }

    private static string ResolveDefaultModelForProvider(string? provider)
    {
        return NormalizeProviderName(provider) switch
        {
            "Gemini" => "gemini-2.0-flash",
            "OpenAI" => "gpt-4o-mini",
            "Grok" => "grok-3-mini",
            _ => "openrouter/free"
        };
    }

    private static string ResolveEnvironmentApiKey(string? provider)
    {
        var envName = NormalizeProviderName(provider) switch
        {
            "Gemini" => "GEMINI_API_KEY",
            "OpenAI" => "OPENAI_API_KEY",
            "Grok" => "XAI_API_KEY",
            _ => "OPENROUTER_API_KEY"
        };

        return Environment.GetEnvironmentVariable(envName) ?? string.Empty;
    }
}
