using System;
using System.Collections.Generic;
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
    private readonly MapViewModel? _mapViewModel;
    private readonly List<MissionWaypointItem> _waypoints = new();
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
        _mapViewModel   = App.GetService<MapViewModel>();

        Loaded += MissionPlannerPage_Loaded;
        Unloaded += MissionPlannerPage_Unloaded;
    }

    public void OnPageActivated()
    {
        MissionMapControl?.SetActive(true);
        MissionMapControl?.InvalidateArrange();
        _ = SyncFromWaypointServiceAsync();
        ApplyGeofence();
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
            MapProviderComboBox.SelectedIndex = 0;
            MissionMapControl.SetCenter(_defaultLat, _defaultLon, 14);
            SeedInitialWaypoints();
            _initialized = true;
        }

        // subscribe to waypoint changes
        _waypointService.WaypointsChanged -= WaypointService_WaypointsChanged;
        _waypointService.WaypointsChanged += WaypointService_WaypointsChanged;

        _ = SyncFromWaypointServiceAsync();
    }

    private void MissionPlannerPage_Unloaded(object sender, RoutedEventArgs e)
    {
        MissionMapControl.SetActive(false);
        MissionMapControl.WaypointMoved -= MissionMapControl_WaypointMoved;
        if (_mapViewModel != null)
            _mapViewModel.PropertyChanged -= OnMapViewModelPropertyChanged;

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

        // Demo rice field transect in Sukamerta, Rawamerta, Karawang (600m E-W, 5 WPs matching 4 survey sectors)
        const double centerLat = -6.24361;
        const double centerLon = 107.36556;
        const double mPerDegLon = 111320.0 * 0.99407; // cos(-6.24361 deg)
        double spacingLon = (600.0 / 4.0) / mPerDegLon;
        double startLon = centerLon - 2.0 * spacingLon;

        for (int i = 0; i < 5; i++)
        {
            _waypoints.Add(new MissionWaypointItem
            {
                Sequence = 1 + i,
                Latitude = centerLat,
                Longitude = startLon + i * spacingLon,
                Altitude = 60
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
            0 => SkiaMapControl.MapTileProvider.ArcGISTopographic,
            1 => SkiaMapControl.MapTileProvider.ArcGISImagery,
            2 => SkiaMapControl.MapTileProvider.ArcGISStreetMap,
            3 => SkiaMapControl.MapTileProvider.GoogleMap,
            4 => SkiaMapControl.MapTileProvider.GoogleSatellite,
            5 => SkiaMapControl.MapTileProvider.GoogleTerrain,
            6 => SkiaMapControl.MapTileProvider.GoogleHybrid,
            _ => SkiaMapControl.MapTileProvider.OpenStreetMap
        };

        MissionMapControl.SetTileProvider(provider);
        RenderMap();
    }

    private void MissionMapControl_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var tapPoint = e.GetPosition(MissionMapControl);
        var geo = MissionMapControl.GetLatLonFromClick(tapPoint);

        _ = _waypointService.AddWaypointAsync(new WaypointData { Latitude = geo.Lat, Longitude = geo.Lon, Altitude = 150 });
    }

    private async void AddWaypointButton_Click(object sender, RoutedEventArgs e)
    {
        if ((await _waypointService.GetWaypointsAsync()).Count == 0)
        {
            await _waypointService.AddWaypointAsync(new WaypointData { Latitude = _defaultLat, Longitude = _defaultLon, Altitude = 150 });
            return;
        }

        var last = (await _waypointService.GetWaypointsAsync()).OrderBy(w => w.Sequence).Last();
        await _waypointService.AddWaypointAsync(new WaypointData { Latitude = last.Latitude + 0.0008, Longitude = last.Longitude + 0.0008, Altitude = last.Altitude });
    }

    private async void ClearWaypointButton_Click(object sender, RoutedEventArgs e)
    {
        await _waypointService.ClearWaypointsAsync();
    }

    private async void UploadMissionButton_Click(object sender, RoutedEventArgs e)
    {
        var wps = await _waypointService.GetWaypointsAsync();
        if (wps.Count == 0)
        {
            WaypointSummaryText.Text = "No waypoints to upload";
            return;
        }

        if (!_mavLinkService.IsConnected)
        {
            WaypointSummaryText.Text = "Vehicle not connected";
            return;
        }

        UploadMissionButton.IsEnabled = false;
        WaypointSummaryText.Text = "Uploading mission...";

        try
        {
            var missionWaypoints = WaypointMapper.ToMissionWaypoints(wps);
            var success = await _missionService.UploadMissionAsync(missionWaypoints);
            WaypointSummaryText.Text = success
                ? $"Uploaded {missionWaypoints.Count} waypoints"
                : "Mission upload failed";
        }
        catch (Exception ex)
        {
            WaypointSummaryText.Text = $"Upload error: {ex.Message}";
        }
        finally
        {
            UploadMissionButton.IsEnabled = true;
        }
    }

    private async void RemoveWaypointButton_Click(object sender, RoutedEventArgs e)
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

    private void GeofenceRadiusSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        GeofenceLabel.Text = $"{e.NewValue:F0} m";
        ApplyGeofence();
    }

    private void MissionMapControl_WaypointMoved(object? sender, SkiaMapControl.WaypointMovedEventArgs e)
    {
        _ = _waypointService.UpdateWaypointAsync(new WaypointData { Sequence = e.Sequence, Latitude = e.NewLat, Longitude = e.NewLon });
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

    private void ApplyGeofence()
    {
        if (_waypoints.Count == 0)
        {
            MissionMapControl.ClearGeofence();
            _geofenceService?.SetGeofenceActive(false);
            return;
        }

        double centerLat = _waypoints.Average(w => w.Latitude);
        double centerLon = _waypoints.Average(w => w.Longitude);
        double radius = GeofenceRadiusSlider?.Value ?? 650;
        MissionMapControl.SetGeofence(true, centerLat, centerLon, radius);

        // Keep the shared geofence model (used by IGeofenceService.SendGeofenceToVehicleAsync and
        // the Dashboard's boundary-distance alerting) in sync with what's drawn here.
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

        if (!_mavLinkService.IsConnected)
        {
            WaypointSummaryText.Text = "Vehicle not connected";
            return;
        }

        SendGeofenceButton.IsEnabled = false;
        WaypointSummaryText.Text = "Sending geofence to vehicle...";
        try
        {
            ApplyGeofence();
            await _geofenceService.SendGeofenceToVehicleAsync();
            WaypointSummaryText.Text = $"Geofence sent: {GeofenceRadiusSlider.Value:F0} m radius";
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

    private void RefreshWaypointList()
    {
        WaypointListView.ItemsSource = null;
        WaypointListView.ItemsSource = _waypoints.OrderBy(w => w.Sequence).ToList();
        WaypointSummaryText.Text = $"{_waypoints.Count} waypoints";
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
}
