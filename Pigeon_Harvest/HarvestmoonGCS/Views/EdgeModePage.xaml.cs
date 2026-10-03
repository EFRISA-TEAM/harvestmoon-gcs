using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Services;
using Serilog;

namespace HarvestmoonGCS.Views;

public sealed partial class EdgeModePage : Page
{
    private readonly HarvestFunctionalService? _harvestService;
    private readonly IFileService? _fileService;
    private readonly IMavLinkService? _mavLinkService;
    private DispatcherTimer? _refreshTimer;
    private HarvestFunctionalService.YoloBenchmarkResult? _lastBenchmark;
    private bool _isBenchmarking;

    public EdgeModePage()
    {
        this.InitializeComponent();
        _harvestService = App.Current.Services.GetService<HarvestFunctionalService>();
        _fileService = App.Current.Services.GetService<IFileService>();
        _mavLinkService = App.Current.Services.GetService<IMavLinkService>();
        this.Loaded += EdgeModePage_Loaded;
        this.Unloaded += EdgeModePage_Unloaded;
    }

    private void EdgeModePage_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshRuntimeInfo();
        SyncTogglesFromService();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshPerformanceStats();
            RefreshRuntimeInfo();
        };
        _refreshTimer.Start();
    }

    public void OnPageActivated()
    {
        RefreshRuntimeInfo();
        SyncTogglesFromService();
        RefreshPerformanceStats();
        if (_refreshTimer == null)
        {
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _refreshTimer.Tick += (_, _) =>
            {
                RefreshPerformanceStats();
                RefreshRuntimeInfo();
            };
            _refreshTimer.Start();
        }
    }

    private void EdgeModePage_Unloaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
    }

    // -------------------------------------------------------------------------
    // Runtime environment info
    // -------------------------------------------------------------------------
    private void RefreshRuntimeInfo()
    {
        // Platform
        string platform;
#if ANDROID
        platform = "Android";
#elif __IOS__
        platform = "iOS";
#elif WINDOWS
        platform = "Windows";
#else
        platform = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" :
                   RuntimeInformation.IsOSPlatform(OSPlatform.OSX)   ? "macOS" : "Desktop";
#endif
        PlatformText.Text = platform;

        // Execution provider (dynamic detection)
#if ANDROID
        ExecutionProviderText.Text = "NNAPI / CPU";
#else
        var useCuda = Environment.GetEnvironmentVariable("PIGEON_YOLO_USE_CUDA") == "1";
        ExecutionProviderText.Text = useCuda ? "CUDA / GPU (TensorRT)" : "CPU (Optimized Runtime)";
#endif

        // YOLO status — saat demo aktif, tampilkan status demo yang jujur
        // (sebelumnya halaman ini tidak pernah tahu demo sedang berjalan dan
        // bisa menampilkan "Fallback / Idle" padahal sistem sedang demo).
        bool demoActive = _harvestService?.IsDemoModeActive == true;
        bool yoloReady  = _harvestService?.IsYoloRuntimeReady == true;
        bool edgeOk     = yoloReady || demoActive;
        EdgeStatusDot.Fill = new SolidColorBrush(
            edgeOk ? Windows.UI.Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E)
                   : Windows.UI.Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
        EdgeStatusText.Text = demoActive
            ? (yoloReady ? "Edge AI: Active (Demo)" : "Edge AI: Demo Mode")
            : (yoloReady ? "Edge AI: Active" : "Edge AI: Fallback / Idle");
        EdgeStatusText.Foreground = new SolidColorBrush(
            edgeOk ? Windows.UI.Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E)
                   : Windows.UI.Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));

        // Real transport status — was previously a hardcoded "UDP · 14550 · Listening" regardless
        // of how the vehicle is actually connected, which would visibly contradict a Serial/USB
        // connection (e.g. the static demo unit at a booth).
        if (_mavLinkService != null && _mavLinkService.IsConnected)
        {
            var connStr = _mavLinkService.ConnectionString;
            TransportStatusText.Text = string.IsNullOrWhiteSpace(connStr)
                ? $"{_mavLinkService.ConnectionType} · Connected"
                : $"{connStr} · Connected";
        }
        else
        {
            TransportStatusText.Text = "Not connected";
        }
    }

    private void RefreshPerformanceStats()
    {
        if (_isBenchmarking)
        {
            return;
        }

        if (_lastBenchmark != null)
        {
            FpsValueText.Text = _lastBenchmark.FramesPerSecond.ToString("F1");
            LatencyValueText.Text = $"{_lastBenchmark.AverageLatencyMs:F0} ms";
            DetectionsValueText.Text = _lastBenchmark.AverageDetections.ToString("F1");
            var pct = Math.Clamp(_lastBenchmark.FramesPerSecond / 15.0 * 100.0, 0, 100);
            FpsTargetText.Text = $"{pct:F0}%";
            FpsProgressBar.Width = 280 * (pct / 100.0);
        }
        else
        {
            FpsValueText.Text = "—";
            LatencyValueText.Text = "—";
            DetectionsValueText.Text = "—";
            FpsTargetText.Text = "0%";
            FpsProgressBar.Width = 0;
        }
    }

    /// <summary>
    /// Runs a real on-device YOLO benchmark against the last captured camera/YOLO frame and
    /// shows the genuinely measured FPS/latency/detection count — replaces the previous
    /// hardcoded per-device estimate strings.
    /// </summary>
    private async void RunBenchmarkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_harvestService == null || _isBenchmarking)
        {
            return;
        }

        var samplePath = _harvestService.LatestYoloScreenshotPath;
        if (string.IsNullOrWhiteSpace(samplePath) || !System.IO.File.Exists(samplePath))
        {
            BenchmarkStatusText.Text = "No captured frame available yet — open Camera and start the feed first, then come back here.";
            return;
        }

        try
        {
            _isBenchmarking = true;
            RunBenchmarkButton.IsEnabled = false;
            BenchmarkStatusText.Text = "Benchmarking on this device…";

            var result = await _harvestService.BenchmarkImageAsync(samplePath);

            if (result == null)
            {
                BenchmarkStatusText.Text = "Benchmark failed — YOLO runtime may not be initialized.";
                return;
            }

            _lastBenchmark = result;
            RefreshPerformanceStats();
            BenchmarkStatusText.Text = $"Measured on {result.Device} · {result.Iterations} iterations · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[EdgeModePage] Benchmark failed");
            BenchmarkStatusText.Text = "Benchmark failed — see logs for details.";
        }
        finally
        {
            _isBenchmarking = false;
            RunBenchmarkButton.IsEnabled = true;
        }
    }

    private void SyncTogglesFromService()
    {
        if (_harvestService == null) return;
        YoloToggle.IsOn = _harvestService.IsYoloOptionEnabled;
        VegToggle.IsOn = _harvestService.IsVegOverlayEnabled;
        ImuToggle.IsOn = _harvestService.IsImuOverlayEnabled;
        Int8Toggle.IsOn = _harvestService.IsInt8QuantizationEnabled;
    }

    // -------------------------------------------------------------------------
    // Toggle handlers
    // -------------------------------------------------------------------------
    private void YoloToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_harvestService == null) return;
        _harvestService.SetYoloOptionEnabled(YoloToggle.IsOn);
        Log.Information("EdgeModePage: YOLO toggled {State}", YoloToggle.IsOn);
    }

    private void VegToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_harvestService == null) return;
        _harvestService.IsVegOverlayEnabled = VegToggle.IsOn;
        Log.Information("EdgeModePage: Vegetation overlay toggled {State}", VegToggle.IsOn);
    }

    private void ImuToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_harvestService == null) return;
        _harvestService.IsImuOverlayEnabled = ImuToggle.IsOn;
        Log.Information("EdgeModePage: IMU overlay toggled {State}", ImuToggle.IsOn);
    }

    private void Int8Toggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_harvestService == null) return;
        _harvestService.IsInt8QuantizationEnabled = Int8Toggle.IsOn;
        Log.Information("EdgeModePage: INT8 quantization toggled {State}", Int8Toggle.IsOn);
    }

    // -------------------------------------------------------------------------
    // Slider handlers
    // -------------------------------------------------------------------------
    private void ConfThreshold_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ConfThresholdLabel == null) return;
        int val = (int)e.NewValue;
        ConfThresholdLabel.Text = $"{val}%";
    }

    private void NmsThreshold_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (NmsThresholdLabel == null) return;
        int val = (int)e.NewValue;
        NmsThresholdLabel.Text = $"{val}%";
    }

    // -------------------------------------------------------------------------
    // Button handlers
    // -------------------------------------------------------------------------
    private void ApplyModel_Click(object sender, RoutedEventArgs e)
    {
        var selected = (ModelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        ModelNameText.Text = $"Model: {selected}";
        Log.Information("EdgeModePage: Applied model {Model}", selected);

        if (_harvestService != null)
        {
            var baseDirectory = AppContext.BaseDirectory;
            var defaultModelPath = System.IO.Path.Combine(baseDirectory, "Assets", "models", selected);

            string classFileName = selected switch
            {
                // The int8 detector is the older 416px crop/weed model — it needs its own labels,
                // not the 4-class agronomy list used by moonharvest-uav-det.onnx.
                var s when s.Contains("uav-det-int8", StringComparison.OrdinalIgnoreCase)
                    => "classes-moonharvest-uav-det-legacy.txt",
                var s when s.Contains("uav", StringComparison.OrdinalIgnoreCase)
                    => "classes-moonharvest-uav-det.txt",
                var s when s.Contains("weed", StringComparison.OrdinalIgnoreCase)
                    => "classes-crop-weed.txt",
                var s when s.Contains("320", StringComparison.OrdinalIgnoreCase)
                        || s.Contains("coco", StringComparison.OrdinalIgnoreCase)
                    => "classes-yolov8n-coco.txt",
                _ => "classes-moonharvest-health.txt",
            };

            var defaultClassPath = System.IO.Path.Combine(baseDirectory, "Assets", "models", classFileName);
            
            float conf = (float)(ConfThresholdSlider.Value / 100.0);
            float nms = (float)(NmsThresholdSlider.Value / 100.0);
            
            bool success = _harvestService.ConfigureYoloRuntime(defaultModelPath, defaultClassPath, conf, nms);
            if (success)
            {
                Log.Information("EdgeModePage: Successfully applied model {ModelName}", selected);
                RefreshRuntimeInfo();
            }
            else
            {
                Log.Error("EdgeModePage: Failed to apply model {ModelName}", selected);
            }
        }
    }

    private async void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        if (_fileService == null)
        {
            Log.Warning("EdgeModePage: File picker service not available");
            return;
        }

        try
        {
            var path = await _fileService.PickFileAsync(new[] { ".onnx" });
            if (!string.IsNullOrWhiteSpace(path))
            {
                ModelNameText.Text = $"Model: {System.IO.Path.GetFileName(path)}";
                Log.Information("EdgeModePage: Custom model picked: {Path}", path);
                
                if (_harvestService != null)
                {
                    float conf = (float)(ConfThresholdSlider.Value / 100.0);
                    float nms = (float)(NmsThresholdSlider.Value / 100.0);

                    var classPath = _harvestService.RuntimeClassPath 
                        ?? System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "models", "classes-moonharvest-health.txt");

                    bool success = _harvestService.ConfigureYoloRuntime(path, classPath, conf, nms);
                    if (success)
                    {
                        Log.Information("EdgeModePage: Successfully loaded custom model {Path}", path);
                        RefreshRuntimeInfo();
                    }
                    else
                    {
                        Log.Error("EdgeModePage: Failed to configure YOLO with custom model {Path}", path);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "EdgeModePage: Error browsing model file");
        }
    }

    private void RefreshStatus_Click(object sender, RoutedEventArgs e)
    {
        RefreshRuntimeInfo();
        RefreshPerformanceStats();
    }
}
