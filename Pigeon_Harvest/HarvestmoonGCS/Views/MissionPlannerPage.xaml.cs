using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using HarvestmoonGCS.Controls;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.ViewModels;
using HarvestmoonGCS.Core.Helpers;
using HarvestmoonGCS.Helpers;
using HarvestmoonGCS.Services;

namespace HarvestmoonGCS.Views;

public sealed partial class MissionPlannerPage : Page
{
    private sealed class MissionWaypointItem
    {
        public int Sequence { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Altitude { get; set; }
        public string Coordinates => $"{Latitude:F6}, {Longitude:F6}";
        public string AltitudeText => $"ALT {Altitude:F0} m";
    }

    private readonly IMissionService _missionService;
    private readonly IMavLinkService _mavLinkService;
    private readonly IWaypointService _waypointService;
    private readonly IGeofenceService? _geofenceService;
    private readonly IVideoScenarioService? _videoScenarioService;
    private readonly IFileService? _fileService;
    private readonly IIrrigationService? _irrigationService;
    private readonly MapViewModel? _mapViewModel;
    private readonly List<MissionWaypointItem> _waypoints = new();
    private DispatcherTimer? _demoSensorTimer;
    private readonly Random _rnd = new();
    private bool _initialized;
    private double _defaultLat = -6.24361;
    private double _defaultLon = 107.36556;

    public MissionPlannerPage()
    {
        this.InitializeComponent();

        _missionService = App.GetService<IMissionService>();
        _mavLinkService = App.GetService<IMavLinkService>();
        _waypointService = App.GetService<IWaypointService>();
        _geofenceService = App.Current.Services.GetService<IGeofenceService>();
        _videoScenarioService = App.Current.Services.GetService<IVideoScenarioService>();
        _fileService = App.Current.Services.GetService<IFileService>();
        _irrigationService = App.Current.Services.GetService<IIrrigationService>();
        _mapViewModel   = App.GetService<MapViewModel>();

        Loaded += MissionPlannerPage_Loaded;
        Unloaded += MissionPlannerPage_Unloaded;
    }

    public void OnPageActivated()
    {
        MissionMapControl?.SetActive(true);
        MissionMapControl?.InvalidateArrange();
        var scenario = _videoScenarioService?.CurrentScenario;
        if (scenario != null)
        {
            _defaultLat = scenario.FieldCenterLat;
            _defaultLon = scenario.FieldCenterLon;
        }
        _ = SafeSyncFromWaypointServiceAsync();
        ApplyGeofence();
    }

    /// <summary>
    /// Fire-and-forget wrapper that guarantees the awaited work can never raise an
    /// unobserved exception and tear down the process.
    /// </summary>
    private async Task SafeSyncFromWaypointServiceAsync()
    {
        try
        {
            await SyncFromWaypointServiceAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[MissionPlannerPage] SyncFromWaypointService failed");
        }
    }

    private async Task SyncFromWaypointServiceAsync()
    {
        var wps = await _waypointService.GetWaypointsAsync();
        if (wps == null || wps.Count == 0) return;

        _waypoints.Clear();
        foreach (var wp in wps.OrderBy(w => w.Sequence))
        {
            _waypoints.Add(new MissionWaypointItem
            {
                Sequence  = wp.Sequence,
                Latitude  = wp.Latitude,
                Longitude = wp.Longitude,
                Altitude  = wp.Altitude,
            });
        }

        RefreshWaypointList();
        RenderMap();

        double cLat = _waypoints.Average(w => w.Latitude);
        double cLon = _waypoints.Average(w => w.Longitude);
        MissionMapControl?.SetCenter(cLat, cLon, 14);
    }

    private void MissionPlannerPage_Loaded(object sender, RoutedEventArgs e)
    {
        MissionMapControl.WaypointMoved -= MissionMapControl_WaypointMoved;
        MissionMapControl.WaypointMoved += MissionMapControl_WaypointMoved;

        if (_mapViewModel != null)
        {
            _mapViewModel.PropertyChanged -= OnMapViewModelPropertyChanged;
            _mapViewModel.PropertyChanged += OnMapViewModelPropertyChanged;
        }

        if (!_initialized)
        {
            var scenario = _videoScenarioService?.CurrentScenario;
            if (scenario != null)
            {
                _defaultLat = scenario.FieldCenterLat;
                _defaultLon = scenario.FieldCenterLon;
            }
            MapProviderComboBox.SelectedIndex = 0;
            MissionMapControl.SetCenter(_defaultLat, _defaultLon, 14);
            _waypoints.Clear();
            _initialized = true;

            if (Environment.GetEnvironmentVariable("MOONHARVEST_LOAD_DEMO") == "1")
            {
                LoadDemoMission_Click(this, new RoutedEventArgs());
            }
        }

        if (_videoScenarioService != null)
        {
            _videoScenarioService.ScenarioChanged -= OnScenarioChanged;
            _videoScenarioService.ScenarioChanged += OnScenarioChanged;
        }

        // subscribe to waypoint changes
        _waypointService.WaypointsChanged -= WaypointService_WaypointsChanged;
        _waypointService.WaypointsChanged += WaypointService_WaypointsChanged;

        if (_irrigationService != null)
        {
            _irrigationService.DataPointAdded -= OnIrrigationDataPointAdded;
            _irrigationService.DataPointAdded += OnIrrigationDataPointAdded;
            _irrigationService.SoilSensorUpdated -= OnSoilSensorUpdated;
            _irrigationService.SoilSensorUpdated += OnSoilSensorUpdated;
            _irrigationService.DataCleared -= OnIrrigationDataCleared;
            _irrigationService.DataCleared += OnIrrigationDataCleared;

            if (_irrigationService.Plots.Count > 0)
                MissionMapControl.SetIrrigationPlots(_irrigationService.Plots);
            if (_irrigationService.SoilSensors.Count > 0)
                MissionMapControl.SetSoilSensors(_irrigationService.SoilSensors);
            if (_irrigationService.DataPoints.Count > 0)
                MissionMapControl.SetIrrigationPoints(_irrigationService.DataPoints);
        }

        _ = SafeSyncFromWaypointServiceAsync();
    }

    private void OnScenarioChanged(object? sender, VideoScenarioProfile scenario)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (scenario != null)
            {
                _defaultLat = scenario.FieldCenterLat;
                _defaultLon = scenario.FieldCenterLon;
                // Saat demo berjalan, waypoint sudah diisi oleh Dashboard dengan
                // penomoran offset — jangan ditimpa dengan waypoint skenario mentah.
                if (scenario.Waypoints.Count > 0 && !IsDemoRunning())
                {
                    await _waypointService.ReplaceWaypointsAsync(scenario.Waypoints);
                }
                ApplyGeofence();
                await SyncFromWaypointServiceAsync();
            }
        });
    }

    /// <summary>Cek apakah demo sedang berjalan (waypoint dikelola oleh Dashboard).</summary>
    private bool IsDemoRunning()
    {
        var hfs = App.Current.Services.GetService<HarvestFunctionalService>();
        return hfs?.IsDemoModeActive == true;
    }

    private void MissionPlannerPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _demoSensorTimer?.Stop();
        MissionMapControl.SetActive(false);
        MissionMapControl.WaypointMoved -= MissionMapControl_WaypointMoved;
        if (_mapViewModel != null)
            _mapViewModel.PropertyChanged -= OnMapViewModelPropertyChanged;

        if (_videoScenarioService != null)
            _videoScenarioService.ScenarioChanged -= OnScenarioChanged;

        if (_irrigationService != null)
        {
            _irrigationService.DataPointAdded -= OnIrrigationDataPointAdded;
            _irrigationService.SoilSensorUpdated -= OnSoilSensorUpdated;
            _irrigationService.DataCleared -= OnIrrigationDataCleared;
        }

        _waypointService.WaypointsChanged -= WaypointService_WaypointsChanged;
    }

    private void WaypointService_WaypointsChanged(object? sender, EventArgs e)
    {
        // Refresh local cache and UI
        DispatcherQueue.TryEnqueue(async () =>
        {
            await SyncFromWaypointServiceAsync();
        });
    }

    private void OnMapViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(_mapViewModel.VehiclePosition))
            return;
        var vp = _mapViewModel?.VehiclePosition;
        if (vp == null) return;
        var lat = vp.Latitude;
        var lon = vp.Longitude;
        DispatcherQueue.TryEnqueue(() =>
        {
            MissionMapControl?.UpdateVehiclePosition(lat, lon);
        });
    }

    private void SeedInitialWaypoints()
    {
        if (_waypoints.Count > 0)
        {
            return;
        }

        if (_mapViewModel != null && _mapViewModel.Waypoints.Count > 0)
        {
            foreach (var wp in _mapViewModel.Waypoints.OrderBy(w => w.Sequence))
            {
                _waypoints.Add(new MissionWaypointItem
                {
                    Sequence = wp.Sequence,
                    Latitude = wp.Latitude,
                    Longitude = wp.Longitude,
                    Altitude = wp.Altitude > 0 ? wp.Altitude : 60,
                });
            }
            RefreshWaypointList();
            RenderMap();
            return;
        }

        var scenario = _videoScenarioService?.CurrentScenario;
        if (scenario != null && scenario.Waypoints.Count > 0)
        {
            foreach (var wp in scenario.Waypoints.OrderBy(w => w.Sequence))
            {
                _waypoints.Add(new MissionWaypointItem
                {
                    Sequence = wp.Sequence,
                    Latitude = wp.Latitude,
                    Longitude = wp.Longitude,
                    Altitude = wp.Altitude > 0 ? wp.Altitude : 60,
                });
            }
            RefreshWaypointList();
            RenderMap();
            return;
        }

        // Fallback transect 14 points
        double centerLat = scenario?.FieldCenterLat ?? -6.42580;
        double centerLon = scenario?.FieldCenterLon ?? 107.61890;
        double mPerDegLon = 111320.0 * Math.Cos(centerLat * Math.PI / 180.0);
        double spacingLon = (1200.0 / 13.0) / mPerDegLon;
        double startLon = centerLon - 6.5 * spacingLon;

        for (int i = 0; i < 14; i++)
        {
            _waypoints.Add(new MissionWaypointItem
            {
                Sequence = 1 + i,
                Latitude = centerLat + Math.Sin(i * 0.8) * 0.0008,
                Longitude = startLon + i * spacingLon,
                Altitude = i == 0 ? 25 : (i == 13 ? 15 : 50)
            });
        }
    }

    private void MapProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MapProviderComboBox.SelectedIndex < 0)
        {
            return;
        }

        var provider = MapProviderComboBox.SelectedIndex switch
        {
            0 => SkiaMapControl.MapTileProvider.GoogleMap,
            1 => SkiaMapControl.MapTileProvider.GoogleSatellite,
            2 => SkiaMapControl.MapTileProvider.GoogleHybrid,
            3 => SkiaMapControl.MapTileProvider.GoogleTerrain,
            4 => SkiaMapControl.MapTileProvider.OpenStreetMap,
            5 => SkiaMapControl.MapTileProvider.ArcGISTopographic,
            6 => SkiaMapControl.MapTileProvider.ArcGISImagery,
            7 => SkiaMapControl.MapTileProvider.ArcGISStreetMap,
            _ => SkiaMapControl.MapTileProvider.GoogleMap
        };

        MissionMapControl.SetTileProvider(provider);
        RenderMap();
    }

    private async void MissionMapControl_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        try
        {
            var tapPoint = e.GetPosition(MissionMapControl);
            var geo = MissionMapControl.GetLatLonFromClick(tapPoint);

            await _waypointService.AddWaypointAsync(new WaypointData { Latitude = geo.Lat, Longitude = geo.Lon, Altitude = 150 });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[MissionPlannerPage] Add waypoint by double-tap failed");
            WaypointSummaryText.Text = $"Gagal menambah waypoint: {ex.Message}";
        }
    }

    private async void AddWaypointButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((await _waypointService.GetWaypointsAsync()).Count == 0)
            {
                await _waypointService.AddWaypointAsync(new WaypointData { Latitude = _defaultLat, Longitude = _defaultLon, Altitude = 150 });
                return;
            }

            var last = (await _waypointService.GetWaypointsAsync()).OrderBy(w => w.Sequence).Last();
            await _waypointService.AddWaypointAsync(new WaypointData { Latitude = last.Latitude + 0.0008, Longitude = last.Longitude + 0.0008, Altitude = last.Altitude });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[MissionPlannerPage] Add waypoint failed");
            WaypointSummaryText.Text = $"Gagal menambah waypoint: {ex.Message}";
        }
    }

    private async void ClearWaypointButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _demoSensorTimer?.Stop();
            await _waypointService.ClearWaypointsAsync();
            _irrigationService?.ClearDataPoints();
            MissionMapControl.ClearIrrigationPoints();
            MissionMapControl.ClearSoilSensors();
            MissionMapControl.ClearIrrigationPlots();
            MissionMapControl.ClearWaypoints();
            MissionMapControl.ClearGeofence();
            MissionMapControl.ShowWaypointGeofence = false;
            if (GeofenceEnableCheckBox != null) GeofenceEnableCheckBox.IsChecked = false;
            _waypoints.Clear();
            RefreshWaypointList();
            WaypointSummaryText.Text = "0 waypoints (Flight plan kosong)";
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[MissionPlannerPage] Clear waypoints failed");
            WaypointSummaryText.Text = $"Gagal menghapus waypoint: {ex.Message}";
        }
    }

    private async void UploadMissionButton_Click(object sender, RoutedEventArgs e)
    {
        var wps = await _waypointService.GetWaypointsAsync();
        if (wps.Count == 0)
        {
            WaypointSummaryText.Text = "No waypoints to upload";
            return;
        }

        bool demoMode = IsDemoRunning();
        if (!_mavLinkService.IsConnected && !demoMode)
        {
            WaypointSummaryText.Text = "Vehicle not connected · Hubungkan UAV atau aktifkan Demo Mode";
            return;
        }

        UploadMissionButton.IsEnabled = false;
        WaypointSummaryText.Text = "Validating mission...";

        try
        {
            var missionWaypoints = WaypointMapper.ToMissionWaypoints(wps);

            // Enforce validation before pushing to the vehicle. A mission that violates the
            // limits would be rejected (or worse, flown) silently otherwise.
            var validation = await _missionService.ValidateMissionAsync(missionWaypoints);
            if (!validation.IsValid)
            {
                WaypointSummaryText.Text = "Mission invalid: " + string.Join("; ", validation.Errors);
                Serilog.Log.Warning("[MissionPlannerPage] Mission upload blocked: {Errors}",
                    string.Join("; ", validation.Errors));
                return;
            }

            if (validation.Warnings.Count > 0)
            {
                Serilog.Log.Information("[MissionPlannerPage] Mission warnings: {Warnings}",
                    string.Join("; ", validation.Warnings));
            }

            WaypointSummaryText.Text = "Uploading mission...";
            if (demoMode && !_mavLinkService.IsConnected)
            {
                await Task.Delay(400);
                var warningSuffix = validation.Warnings.Count > 0
                    ? $" ({validation.Warnings.Count} warning)"
                    : string.Empty;
                WaypointSummaryText.Text = $"Uploaded {missionWaypoints.Count} waypoints to Simulator{warningSuffix}";
            }
            else
            {
                var success = await _missionService.UploadMissionAsync(missionWaypoints);
                if (success)
                {
                    var warningSuffix = validation.Warnings.Count > 0
                        ? $" ({validation.Warnings.Count} warning)"
                        : string.Empty;
                    WaypointSummaryText.Text = $"Uploaded {missionWaypoints.Count} waypoints{warningSuffix}";
                }
                else
                {
                    WaypointSummaryText.Text = "Mission upload failed";
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[MissionPlannerPage] Mission upload failed");
            WaypointSummaryText.Text = $"Upload error: {ex.Message}";
        }
        finally
        {
            UploadMissionButton.IsEnabled = true;
        }
    }

    private async void RemoveWaypointButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button button) return;

            var sequence = button.Tag switch
            {
                int value => value,
                string text when int.TryParse(text, out var parsed) => parsed,
                _ => -1
            };

            if (sequence < 0) return;

            await _waypointService.RemoveWaypointAsync(sequence);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[MissionPlannerPage] Remove waypoint failed");
            WaypointSummaryText.Text = $"Gagal menghapus waypoint: {ex.Message}";
        }
    }

    private void GeofenceRadiusSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        GeofenceLabel.Text = $"{e.NewValue:F0} m";
        ApplyGeofence();
    }

    private async void MissionMapControl_WaypointMoved(object? sender, SkiaMapControl.WaypointMovedEventArgs e)
    {
        try
        {
            // Preserve the existing altitude and command — the drag event only carries X/Y,
            // so rebuilding from scratch would silently zero the waypoint's altitude.
            var existing = await _waypointService.GetWaypointAsync(e.Sequence);
            var moved = new WaypointData
            {
                Sequence = e.Sequence,
                Latitude = e.NewLat,
                Longitude = e.NewLon,
                Altitude = existing?.Altitude ?? 0,
                Command = existing?.Command ?? WaypointCommand.Waypoint,
                Param1 = existing?.Param1 ?? 0,
                Param2 = existing?.Param2 ?? 0,
                Param3 = existing?.Param3 ?? 0,
                Param4 = existing?.Param4 ?? 0
            };

            await _waypointService.UpdateWaypointAsync(moved);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[MissionPlannerPage] Waypoint move failed");
            WaypointSummaryText.Text = $"Gagal memindahkan waypoint: {ex.Message}";
        }
    }

    private void ResequenceWaypoints()
    {
        // WaypointService handles sequencing
    }

    private void RenderMap()
    {
        MissionMapControl.ClearWaypoints();

        foreach (var wp in _waypoints.OrderBy(w => w.Sequence))
        {
            MissionMapControl.AddWaypointMarker(wp.Sequence, wp.Latitude, wp.Longitude, wp.Altitude, "WP");
        }

        if (_waypoints.Count > 0)
        {
            var first = _waypoints[0];
            MissionMapControl.SetCenter(first.Latitude, first.Longitude, 16);
        }

        ApplyGeofence();
        RefreshWaypointList();
    }

    private void GeofenceEnableCheckBox_Click(object sender, RoutedEventArgs e)
    {
        bool isEnabled = GeofenceEnableCheckBox?.IsChecked == true;
        if (GeofenceRadiusSlider != null) GeofenceRadiusSlider.IsEnabled = isEnabled;
        if (SendGeofenceButton != null) SendGeofenceButton.IsEnabled = isEnabled && _waypoints.Count > 0;
        ApplyGeofence();
    }

    private void ToggleIrrigationBtn_Click(object sender, RoutedEventArgs e)
    {
        MissionMapControl.ToggleIrrigationOverlay(ToggleIrrigationBtn?.IsChecked == true);
    }

    private void ToggleSensorsBtn_Click(object sender, RoutedEventArgs e)
    {
        MissionMapControl.ToggleSoilSensors(ToggleSensorsBtn?.IsChecked == true);
    }

    private void OnIrrigationDataPointAdded(object? sender, IrrigationDataPoint point)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            MissionMapControl?.AddIrrigationPoint(point);
        });
    }

    private void OnSoilSensorUpdated(object? sender, SoilSensorData sensor)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            MissionMapControl?.UpdateSoilSensor(sensor);
        });
    }

    private void OnIrrigationDataCleared(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            MissionMapControl?.ClearIrrigationPoints();
        });
    }

    private async void LoadDemoMission_Click(object sender, RoutedEventArgs e)
    {
        var scenario = _videoScenarioService?.CurrentScenario;
        double baseLat = scenario?.FieldCenterLat ?? -6.42580;
        double baseLon = scenario?.FieldCenterLon ?? 107.61890;

        // 1. Waypoint Demo (14 titik fotogrametri lawnmower grid survei PDP multi-sektor)
        var demoWps = new List<WaypointData>
        {
            new WaypointData { Sequence = 1,  Latitude = baseLat - 0.0010, Longitude = baseLon - 0.0037, Altitude = 25, Command = WaypointCommand.TakeOff },
            new WaypointData { Sequence = 2,  Latitude = baseLat - 0.0006, Longitude = baseLon - 0.0031, Altitude = 50, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 3,  Latitude = baseLat - 0.0006, Longitude = baseLon - 0.0004, Altitude = 50, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 4,  Latitude = baseLat - 0.0006, Longitude = baseLon + 0.0026, Altitude = 50, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 5,  Latitude = baseLat + 0.0000, Longitude = baseLon + 0.0031, Altitude = 50, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 6,  Latitude = baseLat + 0.0002, Longitude = baseLon + 0.0023, Altitude = 50, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 7,  Latitude = baseLat + 0.0002, Longitude = baseLon - 0.0009, Altitude = 50, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 8,  Latitude = baseLat + 0.0002, Longitude = baseLon - 0.0034, Altitude = 50, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 9,  Latitude = baseLat + 0.0009, Longitude = baseLon - 0.0037, Altitude = 50, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 10, Latitude = baseLat + 0.0011, Longitude = baseLon - 0.0014, Altitude = 50, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 11, Latitude = baseLat + 0.0013, Longitude = baseLon + 0.0016, Altitude = 50, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 12, Latitude = baseLat + 0.0017, Longitude = baseLon + 0.0023, Altitude = 50, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 13, Latitude = baseLat - 0.0003, Longitude = baseLon - 0.0019, Altitude = 35, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 14, Latitude = baseLat - 0.0010, Longitude = baseLon - 0.0037, Altitude = 15, Command = WaypointCommand.Land }
        };
        await _waypointService.ReplaceWaypointsAsync(demoWps);

        // 2. Petak Sawah Tematik Proposal (4 Petak Lengkap mencakup 4 Kelas PDP)
        var plots = new List<IrrigationPlot>
        {
            new IrrigationPlot
            {
                Id = "PLOT-A1",
                Name = "Petak A1 (Defisit Tanah)",
                AreaHectares = 0.65,
                DeficitSoilPercent = 70,
                DeficitCanopyPercent = 20,
                NormalPercent = 10,
                Priority = IrrigationPriorityLevel.Emergency,
                RecommendedWaterLiters = 4800,
                ActionRecommendation = "Buka pintu intake tersier Blok Barat; lakukan penggenangan macak-macak 3-5 cm segera.",
                PolygonCoordinates = new List<GeoCoordinate>
                {
                    new GeoCoordinate(baseLat - 0.0009, baseLon - 0.0011),
                    new GeoCoordinate(baseLat - 0.0009, baseLon + 0.0000),
                    new GeoCoordinate(baseLat - 0.0002, baseLon + 0.0000),
                    new GeoCoordinate(baseLat - 0.0002, baseLon - 0.0011),
                    new GeoCoordinate(baseLat - 0.0009, baseLon - 0.0011)
                }
            },
            new IrrigationPlot
            {
                Id = "PLOT-B2",
                Name = "Petak B2 (Defisit Kanopi)",
                AreaHectares = 0.58,
                DeficitCanopyPercent = 62,
                InconsistentPercent = 26,
                NormalPercent = 12,
                Priority = IrrigationPriorityLevel.High,
                RecommendedWaterLiters = 3400,
                ActionRecommendation = "Stres air pada kanopi daun; jadwalkan irigasi berkala dan semprot nutrisi foliar.",
                PolygonCoordinates = new List<GeoCoordinate>
                {
                    new GeoCoordinate(baseLat - 0.0001, baseLon - 0.0011),
                    new GeoCoordinate(baseLat - 0.0001, baseLon + 0.0000),
                    new GeoCoordinate(baseLat + 0.0006, baseLon + 0.0000),
                    new GeoCoordinate(baseLat + 0.0006, baseLon - 0.0011),
                    new GeoCoordinate(baseLat - 0.0001, baseLon - 0.0011)
                }
            },
            new IrrigationPlot
            {
                Id = "PLOT-C3",
                Name = "Petak C3 (Ketidakmerataan Irigasi)",
                AreaHectares = 0.70,
                InconsistentPercent = 65,
                NormalPercent = 28,
                DeficitSoilPercent = 7,
                Priority = IrrigationPriorityLevel.Medium,
                RecommendedWaterLiters = 2100,
                ActionRecommendation = "Saluran cacing tersumbat sedimentasi; lakukan normalisasi bedengan dan parit tersier.",
                PolygonCoordinates = new List<GeoCoordinate>
                {
                    new GeoCoordinate(baseLat - 0.0001, baseLon + 0.0001),
                    new GeoCoordinate(baseLat - 0.0001, baseLon + 0.0013),
                    new GeoCoordinate(baseLat + 0.0006, baseLon + 0.0013),
                    new GeoCoordinate(baseLat + 0.0006, baseLon + 0.0001),
                    new GeoCoordinate(baseLat - 0.0001, baseLon + 0.0001)
                }
            },
            new IrrigationPlot
            {
                Id = "PLOT-D4",
                Name = "Petak D4 (Zona Normal - Optimal)",
                AreaHectares = 0.84,
                NormalPercent = 94,
                InconsistentPercent = 6,
                Priority = IrrigationPriorityLevel.Low,
                RecommendedWaterLiters = 450,
                ActionRecommendation = "Kelembapan tanah dan klorofil kanopi optimal; pertahankan suplai air terjadwal.",
                PolygonCoordinates = new List<GeoCoordinate>
                {
                    new GeoCoordinate(baseLat - 0.0009, baseLon + 0.0001),
                    new GeoCoordinate(baseLat - 0.0009, baseLon + 0.0013),
                    new GeoCoordinate(baseLat - 0.0002, baseLon + 0.0013),
                    new GeoCoordinate(baseLat - 0.0002, baseLon + 0.0001),
                    new GeoCoordinate(baseLat - 0.0009, baseLon + 0.0001)
                }
            }
        };

        // 3. Sensor Tanah Ground-Truth (4 Sensor Terkalibrasi pada Masing-Masing Petak)
        var sensors = new List<SoilSensorData>
        {
            new SoilSensorData { SensorId = "SOIL-01", NodeName = "Sensor Sektor A1 (Kritis)", Latitude = baseLat - 0.0005, Longitude = baseLon - 0.0005, MoisturePercent = 14.8, TemperatureCelsius = 32.5, BatteryVoltage = 4.1, LastUpdated = DateTime.UtcNow },
            new SoilSensorData { SensorId = "SOIL-02", NodeName = "Sensor Sektor B2 (Stres Kanopi)", Latitude = baseLat + 0.0002, Longitude = baseLon - 0.0005, MoisturePercent = 23.4, TemperatureCelsius = 31.0, BatteryVoltage = 4.0, LastUpdated = DateTime.UtcNow },
            new SoilSensorData { SensorId = "SOIL-03", NodeName = "Sensor Sektor C3 (Fluktuatif)", Latitude = baseLat + 0.0002, Longitude = baseLon + 0.0007, MoisturePercent = 38.2, TemperatureCelsius = 29.4, BatteryVoltage = 4.2, LastUpdated = DateTime.UtcNow },
            new SoilSensorData { SensorId = "SOIL-04", NodeName = "Sensor Sektor D4 (Optimal)", Latitude = baseLat - 0.0005, Longitude = baseLon + 0.0007, MoisturePercent = 68.1, TemperatureCelsius = 27.6, BatteryVoltage = 4.2, LastUpdated = DateTime.UtcNow }
        };

        // 4. Titik Deteksi Spasial YOLO (24 Titik Representatif Realistis Sesuai 4 Kelas PDP)
        var points = new List<IrrigationDataPoint>
        {
            // Cluster Petak A1 - Defisit Tanah
            new IrrigationDataPoint { Latitude = baseLat - 0.00055, Longitude = baseLon - 0.00055, ZoneType = IrrigationZoneType.DeficitSoil, Confidence = 0.95f },
            new IrrigationDataPoint { Latitude = baseLat - 0.00045, Longitude = baseLon - 0.00050, ZoneType = IrrigationZoneType.DeficitSoil, Confidence = 0.93f },
            new IrrigationDataPoint { Latitude = baseLat - 0.00060, Longitude = baseLon - 0.00040, ZoneType = IrrigationZoneType.DeficitSoil, Confidence = 0.91f },
            new IrrigationDataPoint { Latitude = baseLat - 0.00040, Longitude = baseLon - 0.00060, ZoneType = IrrigationZoneType.DeficitCanopy, Confidence = 0.89f },
            new IrrigationDataPoint { Latitude = baseLat - 0.00070, Longitude = baseLon - 0.00070, ZoneType = IrrigationZoneType.DeficitSoil, Confidence = 0.94f },
            new IrrigationDataPoint { Latitude = baseLat - 0.00030, Longitude = baseLon - 0.00030, ZoneType = IrrigationZoneType.DeficitCanopy, Confidence = 0.88f },

            // Cluster Petak B2 - Defisit Kanopi
            new IrrigationDataPoint { Latitude = baseLat + 0.00025, Longitude = baseLon - 0.00055, ZoneType = IrrigationZoneType.DeficitCanopy, Confidence = 0.94f },
            new IrrigationDataPoint { Latitude = baseLat + 0.00015, Longitude = baseLon - 0.00045, ZoneType = IrrigationZoneType.DeficitCanopy, Confidence = 0.92f },
            new IrrigationDataPoint { Latitude = baseLat + 0.00035, Longitude = baseLon - 0.00040, ZoneType = IrrigationZoneType.DeficitCanopy, Confidence = 0.90f },
            new IrrigationDataPoint { Latitude = baseLat + 0.00010, Longitude = baseLon - 0.00065, ZoneType = IrrigationZoneType.Inconsistent, Confidence = 0.86f },
            new IrrigationDataPoint { Latitude = baseLat + 0.00040, Longitude = baseLon - 0.00070, ZoneType = IrrigationZoneType.DeficitCanopy, Confidence = 0.93f },
            new IrrigationDataPoint { Latitude = baseLat + 0.00020, Longitude = baseLon - 0.00030, ZoneType = IrrigationZoneType.Inconsistent, Confidence = 0.85f },

            // Cluster Petak C3 - Ketidakmerataan Irigasi
            new IrrigationDataPoint { Latitude = baseLat + 0.00025, Longitude = baseLon + 0.00065, ZoneType = IrrigationZoneType.Inconsistent, Confidence = 0.91f },
            new IrrigationDataPoint { Latitude = baseLat + 0.00015, Longitude = baseLon + 0.00075, ZoneType = IrrigationZoneType.Inconsistent, Confidence = 0.89f },
            new IrrigationDataPoint { Latitude = baseLat + 0.00035, Longitude = baseLon + 0.00060, ZoneType = IrrigationZoneType.Inconsistent, Confidence = 0.87f },
            new IrrigationDataPoint { Latitude = baseLat + 0.00010, Longitude = baseLon + 0.00080, ZoneType = IrrigationZoneType.Normal, Confidence = 0.93f },
            new IrrigationDataPoint { Latitude = baseLat + 0.00040, Longitude = baseLon + 0.00090, ZoneType = IrrigationZoneType.Inconsistent, Confidence = 0.88f },
            new IrrigationDataPoint { Latitude = baseLat + 0.00020, Longitude = baseLon + 0.00050, ZoneType = IrrigationZoneType.Normal, Confidence = 0.92f },

            // Cluster Petak D4 - Zona Normal
            new IrrigationDataPoint { Latitude = baseLat - 0.00045, Longitude = baseLon + 0.00065, ZoneType = IrrigationZoneType.Normal, Confidence = 0.97f },
            new IrrigationDataPoint { Latitude = baseLat - 0.00055, Longitude = baseLon + 0.00075, ZoneType = IrrigationZoneType.Normal, Confidence = 0.96f },
            new IrrigationDataPoint { Latitude = baseLat - 0.00035, Longitude = baseLon + 0.00080, ZoneType = IrrigationZoneType.Normal, Confidence = 0.95f },
            new IrrigationDataPoint { Latitude = baseLat - 0.00065, Longitude = baseLon + 0.00060, ZoneType = IrrigationZoneType.Normal, Confidence = 0.98f },
            new IrrigationDataPoint { Latitude = baseLat - 0.00040, Longitude = baseLon + 0.00090, ZoneType = IrrigationZoneType.Normal, Confidence = 0.94f },
            new IrrigationDataPoint { Latitude = baseLat - 0.00060, Longitude = baseLon + 0.00050, ZoneType = IrrigationZoneType.Normal, Confidence = 0.96f }
        };

        MissionMapControl.SetIrrigationPlots(plots);
        MissionMapControl.SetSoilSensors(sensors);
        MissionMapControl.SetIrrigationPoints(points);

        if (_irrigationService != null)
        {
            _irrigationService.ClearDataPoints();
            foreach (var p in plots) _irrigationService.AddOrUpdatePlot(p);
            foreach (var s in sensors) _irrigationService.UpdateSoilSensor(s);
            foreach (var pt in points) _irrigationService.AddDataPoint(pt.Latitude, pt.Longitude, 50, pt.ZoneName, pt.Confidence);
        }

        // Jalankan simulasi sensor tanah live IoT
        StartDemoSensorSimulation(sensors);

        // Aktifkan Geofencing Eagle & Center Peta
        if (GeofenceEnableCheckBox != null) GeofenceEnableCheckBox.IsChecked = true;
        MissionMapControl.ShowWaypointGeofence = true;
        if (GeofenceRadiusSlider != null) GeofenceRadiusSlider.Value = 650;
        ApplyGeofence();

        MissionMapControl.SetCenter(baseLat, baseLon, 16);
        WaypointSummaryText.Text = "Skenario Riset PDP: 6 WP (Eagle Geofence), 4 Sektor Lahan, 4 Probe In-situ, 24 Titik Analisis Kanopi";
    }

    private void StartDemoSensorSimulation(List<SoilSensorData> sensors)
    {
        _demoSensorTimer?.Stop();
        _demoSensorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _demoSensorTimer.Tick += (s, e) =>
        {
            if (sensors == null || sensors.Count == 0 || _irrigationService == null) return;
            foreach (var sensor in sensors)
            {
                double delta = (_rnd.NextDouble() - 0.5) * 0.4;
                sensor.MoisturePercent = Math.Clamp(sensor.MoisturePercent + delta, 5.0, 95.0);
                sensor.TemperatureCelsius = Math.Clamp(sensor.TemperatureCelsius + ((_rnd.NextDouble() - 0.5) * 0.1), 20.0, 42.0);
                sensor.LastUpdated = DateTime.UtcNow;
                _irrigationService.UpdateSoilSensor(sensor);
            }
        };
        _demoSensorTimer.Start();
    }

    private void ApplyGeofence()
    {
        bool isEnabled = GeofenceEnableCheckBox?.IsChecked == true;
        MissionMapControl.ShowWaypointGeofence = isEnabled;
        if (_waypoints.Count == 0)
        {
            MissionMapControl.ClearGeofence();
            _geofenceService?.SetGeofenceActive(false);
            return;
        }

        double radius = GeofenceRadiusSlider?.Value ?? 650;
        MissionMapControl.WaypointGeofenceRadiusMeters = Math.Min(200.0, Math.Max(100.0, radius / 3.0));

        if (!isEnabled)
        {
            MissionMapControl.ClearGeofence();
            _geofenceService?.SetGeofenceActive(false);
            return;
        }

        double centerLat = _waypoints.Average(w => w.Latitude);
        double centerLon = _waypoints.Average(w => w.Longitude);
        MissionMapControl.SetGeofence(true, centerLat, centerLon, radius);

        // Keep the shared geofence model in sync
        _geofenceService?.SetGeofenceType(GeofenceType.Circular);
        _geofenceService?.SetGeofenceCenter(centerLat, centerLon);
        _geofenceService?.SetGeofenceRadius(radius);
        _geofenceService?.SetGeofenceActive(true);
    }

    /// <summary>
    /// Actually pushes the geofence configured above to the connected vehicle as real
    /// FENCE_ENABLE/FENCE_TYPE/FENCE_RADIUS/FENCE_ACTION MAVLink parameters — previously this
    /// slider only drew a circle on the map with no vehicle-side enforcement.
    /// </summary>
    private async void SendGeofenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_geofenceService == null)
        {
            WaypointSummaryText.Text = "Geofence service not available";
            return;
        }

        if (_waypoints.Count == 0)
        {
            WaypointSummaryText.Text = "Add a waypoint first to anchor the geofence";
            return;
        }

        bool demoMode = IsDemoRunning();
        if (!_mavLinkService.IsConnected && !demoMode)
        {
            WaypointSummaryText.Text = "Vehicle not connected · Hubungkan UAV atau aktifkan Demo Mode";
            return;
        }

        SendGeofenceButton.IsEnabled = false;
        WaypointSummaryText.Text = "Sending geofence to vehicle...";
        try
        {
            ApplyGeofence();
            if (demoMode && !_mavLinkService.IsConnected)
            {
                await Task.Delay(300);
                WaypointSummaryText.Text = $"Geofence active in Simulator: {GeofenceRadiusSlider.Value:F0} m radius";
            }
            else
            {
                await _geofenceService.SendGeofenceToVehicleAsync();
                WaypointSummaryText.Text = $"Geofence sent: {GeofenceRadiusSlider.Value:F0} m radius";
            }
        }
        catch (Exception ex)
        {
            WaypointSummaryText.Text = $"Geofence send failed: {ex.Message}";
        }
        finally
        {
            SendGeofenceButton.IsEnabled = true;
        }
    }

    private async void DownloadMissionButton_Click(object sender, RoutedEventArgs e)
    {
        bool demoMode = IsDemoRunning();
        if (!_mavLinkService.IsConnected && !demoMode)
        {
            WaypointSummaryText.Text = "Vehicle not connected · Hubungkan UAV atau aktifkan Demo Mode";
            return;
        }

        DownloadMissionButton.IsEnabled = false;
        WaypointSummaryText.Text = "Downloading mission from UAV...";
        try
        {
            if (demoMode && !_mavLinkService.IsConnected)
            {
                await Task.Delay(400);
                var scenarioWps = _videoScenarioService?.CurrentScenario?.Waypoints;
                if (scenarioWps != null && scenarioWps.Count > 0)
                {
                    await _waypointService.ReplaceWaypointsAsync(scenarioWps);
                    WaypointSummaryText.Text = $"Downloaded {scenarioWps.Count} waypoints from Simulator";
                }
                else
                {
                    WaypointSummaryText.Text = "No mission waypoints in Simulator";
                }
            }
            else
            {
                var downloaded = await _missionService.DownloadMissionAsync();
                if (downloaded.Count > 0)
                {
                    var wps = WaypointMapper.FromMissionWaypoints(downloaded);
                    await _waypointService.ReplaceWaypointsAsync(wps);
                    WaypointSummaryText.Text = $"Downloaded {wps.Count} waypoints from UAV";
                }
                else
                {
                    WaypointSummaryText.Text = "No mission waypoints on UAV";
                }
            }
        }
        catch (Exception ex)
        {
            WaypointSummaryText.Text = $"Download error: {ex.Message}";
        }
        finally
        {
            DownloadMissionButton.IsEnabled = true;
        }
    }

    private async void ExportMissionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var wps = await _waypointService.GetWaypointsAsync();
            if (wps.Count == 0)
            {
                WaypointSummaryText.Text = "No waypoints to export";
                return;
            }

            var missionWps = WaypointMapper.ToMissionWaypoints(wps);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var filename = $"mission_{timestamp}.waypoints";

            // Resolve a directory that is actually writable on the current platform.
            // MyDocuments is empty on Android, so a raw Path.Combine would build a path
            // rooted at an empty string and fail.
            var exportDir = ResolveMissionExportDirectory();
            if (string.IsNullOrWhiteSpace(exportDir))
            {
                WaypointSummaryText.Text = "Export failed: no writable directory";
                return;
            }

            Directory.CreateDirectory(exportDir);
            var filePath = Path.Combine(exportDir, filename);

            var ok = await _missionService.ExportMissionAsync(missionWps, filePath);
            WaypointSummaryText.Text = ok ? $"Exported: {filename}" : "Export failed";
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[MissionPlannerPage] Mission export failed");
            WaypointSummaryText.Text = $"Export error: {ex.Message}";
        }
    }

    /// <summary>
    /// Resolves a directory that is actually writable on the current platform. MyDocuments is
    /// empty on Android, so fall back to the app data directory rather than building a path
    /// rooted at an empty string.
    /// </summary>
    private static string ResolveMissionExportDirectory()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var root = string.IsNullOrWhiteSpace(docs)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : docs;

        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        return string.IsNullOrWhiteSpace(root) ? string.Empty : Path.Combine(root, "HarvestMissions");
    }

    private async void ImportMissionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? filePath = null;
            if (_fileService != null)
            {
                filePath = await _fileService.PickFileAsync(new[] { ".waypoints", ".txt", ".csv", ".plan" });
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                // Fallback: check the export directory for the latest mission file
                var exportDir = ResolveMissionExportDirectory();
                if (!string.IsNullOrWhiteSpace(exportDir) && Directory.Exists(exportDir))
                {
                    var latest = Directory.GetFiles(exportDir, "*.waypoints")
                        .OrderByDescending(File.GetLastWriteTime)
                        .FirstOrDefault();
                    if (latest != null) filePath = latest;
                }
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                WaypointSummaryText.Text = "No mission file selected";
                return;
            }

            var imported = await _missionService.ImportMissionAsync(filePath);
            if (imported.Count > 0)
            {
                var wps = WaypointMapper.FromMissionWaypoints(imported);
                await _waypointService.ReplaceWaypointsAsync(wps);
                WaypointSummaryText.Text = $"Imported {wps.Count} WPs from {Path.GetFileName(filePath)}";
            }
            else
            {
                WaypointSummaryText.Text = "No valid waypoints in file";
            }
        }
        catch (Exception ex)
        {
            WaypointSummaryText.Text = $"Import error: {ex.Message}";
        }
    }

    private async void EditWaypointButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button button) return;
            var sequence = button.Tag switch
            {
                int value => value,
                string text when int.TryParse(text, out var parsed) => parsed,
                _ => -1
            };
            if (sequence < 0) return;

            var existingWps = await _waypointService.GetWaypointsAsync();
            var targetWp = existingWps.FirstOrDefault(w => w.Sequence == sequence);
            if (targetWp == null) return;

            var dlg = new WaypointEditDialog(targetWp)
            {
                XamlRoot = this.XamlRoot
            };

            // ShowAsync throws if another ContentDialog is already open on this XamlRoot.
            var result = await dlg.ShowAsync();
            if (result == ContentDialogResult.Primary && dlg.ResultWaypoint != null)
            {
                await _waypointService.UpdateWaypointAsync(dlg.ResultWaypoint);
                WaypointSummaryText.Text = $"Updated WP {dlg.ResultWaypoint.Sequence} (Alt: {dlg.ResultWaypoint.Altitude:F0}m)";
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[MissionPlannerPage] Edit waypoint failed");
            WaypointSummaryText.Text = $"Gagal mengedit waypoint: {ex.Message}";
        }
    }

    private void RefreshWaypointList()
    {
        WaypointListView.ItemsSource = null;
        var ordered = _waypoints.OrderBy(w => w.Sequence).ToList();
        WaypointListView.ItemsSource = ordered;

        if (ordered.Count == 0)
        {
            WaypointSummaryText.Text = "0 waypoints";
            return;
        }

        double totalDistM = 0;
        for (int i = 1; i < ordered.Count; i++)
        {
            totalDistM += GeoMath.CalculateDistance(
                ordered[i - 1].Latitude, ordered[i - 1].Longitude,
                ordered[i].Latitude, ordered[i].Longitude);
        }

        var speedMps = _videoScenarioService?.CurrentScenario?.CruiseSpeedMps ?? 10.0;
        if (speedMps <= 0) speedMps = 10.0;
        int totalSecs = (int)Math.Round(totalDistM / speedMps);
        int mins = totalSecs / 60;
        int secs = totalSecs % 60;

        if (totalDistM >= 1000)
        {
            WaypointSummaryText.Text = $"{ordered.Count} WPs · {totalDistM / 1000.0:F2} km · Est. {mins:D2}:{secs:D2} @ {speedMps:F0}m/s";
        }
        else
        {
            WaypointSummaryText.Text = $"{ordered.Count} WPs · {totalDistM:F0} m · Est. {mins:D2}:{secs:D2} @ {speedMps:F0}m/s";
        }
    }

    private static bool IsValidCoordinate(double lat, double lon)
    {
        if (double.IsNaN(lat) || double.IsNaN(lon))
        {
            return false;
        }

        if (lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            return false;
        }

        return Math.Abs(lat) > 0.000001 || Math.Abs(lon) > 0.000001;
    }

    private void SwitchToMapPage_Click(object sender, RoutedEventArgs e)
    {
        Frame?.Navigate(typeof(MapPage));
    }
}
