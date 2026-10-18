using HarvestmoonGCS.Core.ViewModels;
using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Models;
using HarvestmoonGCS.Core.Helpers;
using Mapsui;
using Mapsui.Tiling;
using Mapsui.Layers;
using Mapsui.Styles;
using Mapsui.Projections;
using Mapsui.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;
using HarvestmoonGCS.Helpers;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Globalization;

namespace HarvestmoonGCS.ViewModels;

/// <summary>
/// ViewModel for Map page - handles waypoint management and map display
/// OPTIMIZED: Uses throttling for position updates and efficient marker rendering
/// </summary>
public partial class MapViewModel : ViewModelBase
{
    private readonly IGeofenceService _geofenceService;
    private readonly IMavLinkService _mavLinkService;
    private readonly IDialogService _dialogService;
    private readonly IFileService _fileService;
    
    // OPTIMIZATION: Throttle map updates to reduce redraws (20Hz = 50ms)
    private ThrottledUpdater? _mapUpdateThrottler;
    private bool _hasPendingMapUpdate = false;
    private readonly object _mapUpdateLock = new object();

    [ObservableProperty] private ObservableCollection<WaypointData> _waypoints = new();
    [ObservableProperty] private WaypointData? _vehiclePosition;
    [ObservableProperty] private WaypointData? _homePosition;
    [ObservableProperty] private WaypointData? _selectedWaypoint;
    [ObservableProperty] private string _mapProvider = "GoogleMap";
    [ObservableProperty] private bool _isFollowing = false;
    [ObservableProperty] private double _waypointRadius = 2.0;
    [ObservableProperty] private double _totalDistance = 0.0;
    
    // Geofence properties
    [ObservableProperty] private bool _isGeofenceActive;
    [ObservableProperty] private GeofenceType _geofenceType;
    [ObservableProperty] private double _geofenceRadius;
    [ObservableProperty] private double _geofenceCenterLat;
    [ObservableProperty] private double _geofenceCenterLon;
    [ObservableProperty] private double _geofenceMaxAltitude;
    [ObservableProperty] private ObservableCollection<GeofenceVertex> _geofenceVertices = new();
    [ObservableProperty] private GeofenceStatus _geofenceStatus;
    [ObservableProperty] private bool _isDrawingGeofence;
    [ObservableProperty] private double _distanceToBoundary;
    [ObservableProperty] private bool _showProximityWarning;

    public ICommand AddWaypointCommand { get; }
    public ICommand EditWaypointCommand { get; }
    public ICommand DeleteWaypointCommand { get; }
    public ICommand UploadMissionCommand { get; }
    public ICommand DownloadMissionCommand { get; }
    public ICommand SaveMissionCommand { get; }
    public ICommand LoadMissionCommand { get; }
    public ICommand ClearMissionCommand { get; }
    public ICommand ToggleFollowCommand { get; }
    
    // Geofence commands
    public ICommand ToggleGeofenceCommand { get; }
    public ICommand SetGeofenceCenterCommand { get; }
    public ICommand AddGeofenceVertexCommand { get; }
    public ICommand CompleteGeofenceCommand { get; }
    public ICommand ClearGeofenceCommand { get; }
    public ICommand SendGeofenceCommand { get; }

    public Map Map { get; } = new Map();

    private MemoryLayer _vehicleLayer = null!;
    private MemoryLayer _missionLayer = null!;
    private MemoryLayer _geofenceLayer = null!;

    public MapViewModel(
        IGeofenceService geofenceService, 
        IMavLinkService mavLinkService, 
        IDialogService dialogService, 
        IFileService fileService,
        IOfflineMapService? offlineMapService = null,
        ITileDownloadService? tileDownloadService = null)
    {
        _geofenceService = geofenceService;
        _mavLinkService = mavLinkService;
        _dialogService = dialogService;
        _fileService = fileService;
        _offlineMapService = offlineMapService;
        _tileDownloadService = tileDownloadService;
        
        // OPTIMIZATION: Initialize map update throttler (20Hz = 50ms interval)
        _mapUpdateThrottler = new ThrottledUpdater(50);
        
        InitializeMap();
        InitializeGeofence();
        InitializeOfflineMaps();

        AddWaypointCommand = new RelayCommand<(double Lat, double Lon)?>(AddWaypoint);
        EditWaypointCommand = new RelayCommand<WaypointData>(EditWaypoint);
        DeleteWaypointCommand = new RelayCommand<WaypointData>(DeleteWaypoint);
        UploadMissionCommand = new RelayCommand(async () => await UploadMissionAsync());
        DownloadMissionCommand = new RelayCommand(async () => await DownloadMissionAsync());
        SaveMissionCommand = new RelayCommand(async () => await SaveMissionAsync());
        LoadMissionCommand = new RelayCommand(async () => await LoadMissionAsync());
        ClearMissionCommand = new RelayCommand(ClearMission);
        ToggleFollowCommand = new RelayCommand(ToggleFollow);
        
        // Geofence commands
        ToggleGeofenceCommand = new RelayCommand<bool>(ToggleGeofence);
        SetGeofenceCenterCommand = new RelayCommand<(double Lat, double Lon)>(SetGeofenceCenter);
        AddGeofenceVertexCommand = new RelayCommand<(double Lat, double Lon)>(AddGeofenceVertex);
        CompleteGeofenceCommand = new RelayCommand(CompleteGeofence);
        ClearGeofenceCommand = new RelayCommand(ClearGeofence);
        SendGeofenceCommand = new RelayCommand(SendGeofence);
        
        // Offline maps commands
        ToggleOfflineModeCommand = new RelayCommand(ToggleOfflineMode);
        DownloadJawaCommand = new RelayCommand(async () => await DownloadRegionAsync(JawaRegion));
        DownloadKalimantanCommand = new RelayCommand(async () => await DownloadRegionAsync(KalimantanRegion));
        CancelDownloadCommand = new RelayCommand(CancelDownload);
        ClearCacheCommand = new RelayCommand(async () => await ClearCacheAsync());
        RefreshStorageInfoCommand = new RelayCommand(async () => await RefreshStorageInfoAsync());

        // Wire real-time MAVLink telemetry for live vehicle tracking on map
        if (_mavLinkService != null)
        {
            _mavLinkService.TelemetryReceived += OnMavLinkTelemetryReceived;
        }
    }

    private void OnMavLinkTelemetryReceived(object? sender, FlightData data)
    {
        if (data?.GPS != null && (data.GPS.Latitude != 0 || data.GPS.Longitude != 0))
        {
            double lat = data.GPS.Latitude / 1e7;
            double lon = data.GPS.Longitude / 1e7;
            if (Math.Abs(lat) > 0.0001 && Math.Abs(lon) > 0.0001)
            {
                UpdateVehiclePosition(lat, lon, data.AltitudeFloat, data.IMU?.Yaw ?? 0f);
            }
        }
    }

    private void InitializeMap()
    {
        // Add default OpenStreetMap layer
        Map.Layers.Add(OpenStreetMap.CreateTileLayer());

        // Create mission layer for waypoints
        _missionLayer = new MemoryLayer
        {
            Name = "Mission",
            Style = new SymbolStyle 
            { 
                Fill = new Brush(Color.Red), 
                Outline = new Pen(Color.White, 2), 
                SymbolScale = 0.8 
            }
        };
        Map.Layers.Add(_missionLayer);

        // Create vehicle layer
        _vehicleLayer = new MemoryLayer
        {
            Name = "Vehicle",
            Style = new SymbolStyle 
            { 
                Fill = new Brush(Color.Blue), 
                Outline = new Pen(Color.White, 2), 
                SymbolScale = 1.0 
            }
        };
        Map.Layers.Add(_vehicleLayer);

        // Create geofence layer
        _geofenceLayer = new MemoryLayer
        {
            Name = "Geofence",
            Style = new VectorStyle
            {
                Fill = new Brush(Color.FromArgb(50, 255, 0, 0)), // Semi-transparent red
                Outline = new Pen(Color.FromArgb(200, 255, 0, 0), 3), // Red border
                Line = new Pen(Color.FromArgb(200, 255, 0, 0), 3)
            }
        };
        Map.Layers.Add(_geofenceLayer);

        // Set initial map center (can be changed to user's location or last known position)
        Map.Navigator?.CenterOn(0, 0);
        Map.Navigator?.ZoomTo(2);
    }

    private async void InitializeGeofence()
    {
        if (_geofenceService == null)
        {
            return;
        }

        // Load saved geofence parameters
        await _geofenceService.LoadGeofenceParametersAsync();
        
        // Sync ViewModel properties with service
        var geofence = _geofenceService.CurrentGeofence;
        if (geofence == null)
        {
            return;
        }

        IsGeofenceActive = geofence.IsActive;
        GeofenceType = geofence.Type;
        GeofenceRadius = geofence.Radius;
        GeofenceCenterLat = geofence.CenterLatitude;
        GeofenceCenterLon = geofence.CenterLongitude;
        GeofenceMaxAltitude = geofence.MaxAltitude;
        GeofenceStatus = geofence.Status;
        
        // Load vertices if polygon
        if (geofence.Type == GeofenceType.Polygon)
        {
            GeofenceVertices.Clear();
            foreach (var vertex in geofence.Vertices)
            {
                GeofenceVertices.Add(vertex);
            }
        }
        
        // Render geofence if active
        if (geofence.IsActive)
        {
            UpdateGeofenceLayer();
        }
    }

    partial void OnVehiclePositionChanged(WaypointData? value)
    {
        if (value == null) return;

        // OPTIMIZATION: Throttle vehicle position updates to reduce map redraws
        lock (_mapUpdateLock)
        {
            _hasPendingMapUpdate = true;
        }
        
        _mapUpdateThrottler?.Schedule(() =>
        {
            bool shouldUpdate = false;
            lock (_mapUpdateLock)
            {
                if (_hasPendingMapUpdate)
                {
                    _hasPendingMapUpdate = false;
                    shouldUpdate = true;
                }
            }
            
            if (shouldUpdate && value != null)
            {
                var (x, y) = SphericalMercator.FromLonLat(value.Longitude, value.Latitude);
                var point = new MPoint(x, y);
                _vehicleLayer.Features = new List<IFeature> { new PointFeature(point) };
                _vehicleLayer.DataHasChanged();

                // Auto-center if following (throttled to prevent excessive panning)
                if (IsFollowing && Map.Navigator != null)
                {
                    Map.Navigator.CenterOn(point);
                }
            }
        });
    }

    private void AddWaypoint((double Lat, double Lon)? position)
    {
        var resolvedPosition = position
            ?? (VehiclePosition != null
                ? (VehiclePosition.Latitude, VehiclePosition.Longitude)
                : Waypoints.Count > 0
                    ? (Waypoints[^1].Latitude + 0.0001, Waypoints[^1].Longitude + 0.0001)
                    : (-6.2, 106.8));

        var waypoint = new WaypointData
        {
            Latitude = resolvedPosition.Lat,
            Longitude = resolvedPosition.Lon,
            Altitude = 100,
            Command = WaypointCommand.Waypoint,
            Sequence = Waypoints.Count + 1,
            Param1 = WaypointRadius // Set radius from current setting
        };
        Waypoints.Add(waypoint);
        UpdateMissionLayer();
        CalculateTotalDistance();
    }

    private void UpdateMissionLayer()
    {
        // OPTIMIZATION: Batch feature updates to reduce layer refresh overhead
        var features = new List<IFeature>();
        
        // Add waypoint markers
        foreach (var wp in Waypoints)
        {
            var (x, y) = SphericalMercator.FromLonLat(wp.Longitude, wp.Latitude);
            var point = new MPoint(x, y);
            var feature = new PointFeature(point);
            feature["Sequence"] = wp.Sequence;
            features.Add(feature);
        }
        
        // Add line connecting waypoints
        if (Waypoints.Count > 1)
        {
            var linePoints = Waypoints.Select(wp => 
            {
                var (x, y) = SphericalMercator.FromLonLat(wp.Longitude, wp.Latitude);
                return new MPoint(x, y);
            }).ToList();
            // TODO: Add line feature for route visualization
        }
        
        // OPTIMIZATION: Single update to layer reduces rendering overhead
        _missionLayer.Features = features;
        _missionLayer.DataHasChanged();
    }

    private void EditWaypoint(WaypointData? waypoint) 
    {
        SelectedWaypoint = waypoint;
    }

    private void DeleteWaypoint(WaypointData? waypoint)
    {
        if (waypoint != null)
        {
            Waypoints.Remove(waypoint);
            
            // Renumber remaining waypoints
            for (int i = 0; i < Waypoints.Count; i++)
            {
                Waypoints[i].Sequence = i + 1;
            }
            
            UpdateMissionLayer();
            CalculateTotalDistance();
        }
    }

    private async Task UploadMissionAsync() 
    {
        if (Waypoints.Count == 0)
        {
            await _dialogService.ShowAlertAsync("No waypoints to upload. Please add waypoints first.", "Upload Mission");
            return;
        }

        if (!_mavLinkService.IsConnected)
        {
            await _dialogService.ShowAlertAsync("Vehicle not connected. Please connect to the vehicle first.", "Upload Mission");
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync(
            $"Upload {Waypoints.Count} waypoints to the vehicle?",
            "Confirm Upload");

        if (!confirmed) return;

        var waypointDataList = Waypoints.Select(wp => new WaypointData
        {
            Sequence = wp.Sequence,
            Latitude = wp.Latitude,
            Longitude = wp.Longitude,
            Altitude = wp.Altitude,
            Command = wp.Command,
            Param1 = wp.Param1,
            Param2 = wp.Param2,
            Param3 = wp.Param3,
            Param4 = wp.Param4
        }).ToList();

        var success = await _mavLinkService.UploadMissionAsync(waypointDataList);

        if (success)
        {
            await _dialogService.ShowAlertAsync($"Successfully uploaded {Waypoints.Count} waypoints to the vehicle.", "Upload Complete");
        }
        else
        {
            await _dialogService.ShowAlertAsync("Failed to upload mission. Please check the connection and try again.", "Upload Failed");
        }
    }

    private async Task DownloadMissionAsync() 
    {
        if (!_mavLinkService.IsConnected)
        {
            await _dialogService.ShowAlertAsync("Vehicle not connected. Please connect to the vehicle first.", "Download Mission");
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync(
            "Download mission from the vehicle? This will replace current waypoints.",
            "Confirm Download");

        if (!confirmed) return;

        var waypoints = await _mavLinkService.DownloadMissionAsync();

        if (waypoints != null && waypoints.Count > 0)
        {
            Waypoints.Clear();
            foreach (var wp in waypoints)
            {
                Waypoints.Add(wp);
            }
            UpdateMissionLayer();
            CalculateTotalDistance();
            await _dialogService.ShowAlertAsync($"Successfully downloaded {waypoints.Count} waypoints from the vehicle.", "Download Complete");
        }
        else
        {
            await _dialogService.ShowAlertAsync("No waypoints found on the vehicle.", "Download Complete");
        }
    }

    private async Task SaveMissionAsync() 
    {
        if (Waypoints.Count == 0)
        {
            await _dialogService.ShowAlertAsync("No waypoints to save.", "Save Mission");
            return;
        }

        var filename = $"mission_{DateTime.Now:yyyyMMdd_HHmmss}.waypoints";
        var content = GenerateMissionPlannerFormat();
        
        var savedPath = await _fileService.SaveMissionFileAsync(filename, content);
        await _dialogService.ShowAlertAsync($"Mission saved to: {savedPath}", "Save Complete");
    }

    private async Task LoadMissionAsync() 
    {
        var filePath = await _fileService.PickFileAsync(new[] { ".waypoints", ".txt", ".csv" });
        
        if (string.IsNullOrEmpty(filePath))
        {
            return;
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(filePath);
            bool isMissionPlannerFormat = lines.Length > 0 && lines[0].StartsWith("QGC WPL");
            
            var importedWaypoints = new List<WaypointData>();
            
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith("QGC"))
                    continue;
                
                var parts = line.Split(new char[] { '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                
                if (isMissionPlannerFormat && parts.Length >= 11)
                {
                    if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seq) &&
                        double.TryParse(parts[8], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double lat) &&
                        double.TryParse(parts[9], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double lon) &&
                        double.TryParse(parts[10], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double alt))
                    {
                        var waypoint = new WaypointData
                        {
                            Sequence = seq,
                            Latitude = lat,
                            Longitude = lon,
                            Altitude = alt,
                            Command = WaypointCommand.Waypoint
                        };
                        
                        if (int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cmdInt))
                        {
                            waypoint.Command = (WaypointCommand)cmdInt;
                        }
                        
                        importedWaypoints.Add(waypoint);
                    }
                }
                else if (!isMissionPlannerFormat && parts.Length >= 3)
                {
                    if (double.TryParse(parts[0], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double lat) &&
                        double.TryParse(parts[1], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double lon) &&
                        double.TryParse(parts[2], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double alt))
                    {
                        var waypoint = new WaypointData
                        {
                            Sequence = importedWaypoints.Count,
                            Latitude = lat,
                            Longitude = lon,
                            Altitude = alt,
                            Command = WaypointCommand.Waypoint
                        };
                        importedWaypoints.Add(waypoint);
                    }
                }
            }
            
            for (int i = 0; i < importedWaypoints.Count; i++)
            {
                importedWaypoints[i].Sequence = i + 1;
            }

            if (importedWaypoints.Count > 0)
            {
                Waypoints.Clear();
                foreach (var wp in importedWaypoints)
                {
                    Waypoints.Add(wp);
                }
                UpdateMissionLayer();
                CalculateTotalDistance();
                await _dialogService.ShowAlertAsync($"Successfully imported {importedWaypoints.Count} waypoints.", "Import Complete");
            }
            else
            {
                await _dialogService.ShowAlertAsync("No valid waypoints found in the file.", "Import Failed");
            }
        }
        catch (Exception ex)
        {
            await _dialogService.ShowAlertAsync($"Error importing waypoints: {ex.Message}", "Import Error");
        }
    }

    private string GenerateMissionPlannerFormat()
    {
        var lines = new List<string>();
        lines.Add("QGC WPL 110");
        
        for (int i = 0; i < Waypoints.Count; i++)
        {
            var wp = Waypoints[i];
            var line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8}\t{9}\t{10}\t{11}",
                i,
                i == 0 ? 1 : 0,
                3,
                (int)wp.Command,
                wp.Param1,
                wp.Param2,
                wp.Param3,
                wp.Param4,
                wp.Latitude,
                wp.Longitude,
                wp.Altitude,
                1);
            lines.Add(line);
        }
        
        return string.Join("\n", lines);
    }

    private void ClearMission()
    {
        Waypoints.Clear();
        UpdateMissionLayer();
        TotalDistance = 0;
    }

    private void ToggleFollow()
    {
        IsFollowing = !IsFollowing;
        
        if (IsFollowing && VehiclePosition != null)
        {
            var (x, y) = SphericalMercator.FromLonLat(
                VehiclePosition.Longitude, 
                VehiclePosition.Latitude);
            Map.Navigator?.CenterOn(new MPoint(x, y));
        }
    }

    private void CalculateTotalDistance()
    {
        if (Waypoints.Count < 2)
        {
            TotalDistance = 0;
            return;
        }

        double total = 0;
        for (int i = 0; i < Waypoints.Count - 1; i++)
        {
            var wp1 = Waypoints[i];
            var wp2 = Waypoints[i + 1];
            total += GeoMath.CalculateDistance(
                wp1.Latitude, wp1.Longitude,
                wp2.Latitude, wp2.Longitude);
        }

        TotalDistance = total / 1000.0; // Convert to kilometers
    }

    public double CalculateMissionDistance() => TotalDistance;
    
    public double CalculateMissionDuration() 
    {
        const double defaultCruiseSpeedMetersPerSecond = 8.0;
        if (TotalDistance <= 0)
        {
            CalculateTotalDistance();
        }

        var distanceMeters = TotalDistance * 1000.0;
        if (distanceMeters <= 0)
        {
            return 0;
        }

        var loiterSeconds = Waypoints.Count(waypoint =>
            waypoint.Command is WaypointCommand.Loiter or
                WaypointCommand.LoiterTime or
                WaypointCommand.LoiterTurns or
                WaypointCommand.LoiterUnlimited) * 10.0;

        var takeoffLandingSeconds = Waypoints.Count(waypoint =>
            waypoint.Command is WaypointCommand.TakeOff or
                WaypointCommand.Land or
                WaypointCommand.NavVtolTakeoff or
                WaypointCommand.NavVtolLand) * 20.0;

        return (distanceMeters / defaultCruiseSpeedMetersPerSecond) + loiterSeconds + takeoffLandingSeconds;
    }

    /// <summary>
    /// Update vehicle position from telemetry data
    /// </summary>
    public void UpdateVehiclePosition(double latitude, double longitude, double altitude, float heading)
    {
        VehiclePosition = new WaypointData
        {
            Latitude = latitude,
            Longitude = longitude,
            Altitude = altitude,
            Param4 = heading // Store heading in param4
        };
        
        // Check geofence proximity if active
        if (IsGeofenceActive)
        {
            CheckGeofenceProximity(latitude, longitude, altitude);
        }
    }

    /// <summary>
    /// Set home position
    /// </summary>
    public void SetHomePosition(double latitude, double longitude, double altitude)
    {
        HomePosition = new WaypointData
        {
            Latitude = latitude,
            Longitude = longitude,
            Altitude = altitude,
            Command = WaypointCommand.SetHome
        };
    }

    // ========== Geofence Methods ==========

    private void ToggleGeofence(bool isActive)
    {
        _geofenceService.SetGeofenceActive(isActive);
        IsGeofenceActive = isActive;
        GeofenceStatus = _geofenceService.CurrentGeofence.Status;
        
        if (isActive)
        {
            UpdateGeofenceLayer();
        }
        else
        {
            ClearGeofenceLayer();
        }
        
        // Save parameters
        _ = _geofenceService.SaveGeofenceParametersAsync();
    }

    private void SetGeofenceCenter((double Lat, double Lon) center)
    {
        _geofenceService.SetGeofenceCenter(center.Lat, center.Lon);
        GeofenceCenterLat = center.Lat;
        GeofenceCenterLon = center.Lon;
        
        if (IsGeofenceActive && GeofenceType == GeofenceType.Circular)
        {
            UpdateGeofenceLayer();
        }
    }

    private void AddGeofenceVertex((double Lat, double Lon) position)
    {
        if (!IsDrawingGeofence || GeofenceType != GeofenceType.Polygon)
        {
            return;
        }
        
        _geofenceService.AddPolygonVertex(position.Lat, position.Lon);
        
        // Update vertices collection
        GeofenceVertices.Clear();
        foreach (var vertex in _geofenceService.CurrentGeofence.Vertices)
        {
            GeofenceVertices.Add(vertex);
        }
        
        GeofenceStatus = _geofenceService.CurrentGeofence.Status;
        UpdateGeofenceLayer();
    }

    private void CompleteGeofence()
    {
        _geofenceService.CompletePolygon();
        IsDrawingGeofence = false;
        IsGeofenceActive = true;
        GeofenceStatus = _geofenceService.CurrentGeofence.Status;
        UpdateGeofenceLayer();
        
        // Save parameters
        _ = _geofenceService.SaveGeofenceParametersAsync();
    }

    private void ClearGeofence()
    {
        _geofenceService.ClearPolygonVertices();
        GeofenceVertices.Clear();
        IsGeofenceActive = false;
        IsDrawingGeofence = false;
        GeofenceStatus = GeofenceStatus.Inactive;
        ClearGeofenceLayer();
        
        // Save parameters
        _ = _geofenceService.SaveGeofenceParametersAsync();
    }

    private async void SendGeofence()
    {
        await _geofenceService.SendGeofenceToVehicleAsync();
    }

    public void SetGeofenceType(GeofenceType type)
    {
        _geofenceService.SetGeofenceType(type);
        GeofenceType = type;
        
        if (type == GeofenceType.Circular)
        {
            GeofenceVertices.Clear();
        }
        
        UpdateGeofenceLayer();
    }

    public void SetGeofenceRadius(double radius)
    {
        _geofenceService.SetGeofenceRadius(radius);
        GeofenceRadius = radius;
        
        if (IsGeofenceActive && GeofenceType == GeofenceType.Circular)
        {
            UpdateGeofenceLayer();
        }
    }

    public void SetGeofenceMaxAltitude(double maxAltitude)
    {
        _geofenceService.SetMaxAltitude(maxAltitude);
        GeofenceMaxAltitude = maxAltitude;
    }

    public void StartDrawingGeofence()
    {
        IsDrawingGeofence = true;
        GeofenceStatus = GeofenceStatus.Drawing;
    }

    private void CheckGeofenceProximity(double latitude, double longitude, double altitude)
    {
        double distance = _geofenceService.CalculateDistanceToBoundary(latitude, longitude, altitude);
        DistanceToBoundary = distance;
        
        // Show warning if within 50 meters of boundary
        const double warningThreshold = 50.0;
        ShowProximityWarning = distance < warningThreshold && distance > 0;
    }

    private void UpdateGeofenceLayer()
    {
        var features = new List<IFeature>();
        
        if (GeofenceType == GeofenceType.Circular)
        {
            // Create circle polygon
            if (GeofenceCenterLat != 0 || GeofenceCenterLon != 0)
            {
                var circlePoints = CreateCirclePoints(
                    GeofenceCenterLat, 
                    GeofenceCenterLon, 
                    GeofenceRadius, 
                    64); // 64 points for smooth circle
                
                var polygon = new Mapsui.Nts.GeometryFeature
                {
                    Geometry = new NetTopologySuite.Geometries.Polygon(
                        new NetTopologySuite.Geometries.LinearRing(
                            circlePoints.Select(p =>
                            {
                                var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
                                return new NetTopologySuite.Geometries.Coordinate(x, y);
                            }).ToArray()
                        )
                    )
                };
                features.Add(polygon);
            }
        }
        else if (GeofenceType == GeofenceType.Polygon && GeofenceVertices.Count >= 3)
        {
            // Create polygon from vertices
            var polygonPoints = GeofenceVertices.Select(v =>
            {
                var (x, y) = SphericalMercator.FromLonLat(v.Lon, v.Lat);
                return new NetTopologySuite.Geometries.Coordinate(x, y);
            }).ToList();
            
            // Close the polygon
            polygonPoints.Add(polygonPoints[0]);
            
            var polygon = new Mapsui.Nts.GeometryFeature
            {
                Geometry = new NetTopologySuite.Geometries.Polygon(
                    new NetTopologySuite.Geometries.LinearRing(polygonPoints.ToArray())
                )
            };
            features.Add(polygon);
        }
        
        _geofenceLayer.Features = features;
        _geofenceLayer.DataHasChanged();
    }

    private List<(double Lat, double Lon)> CreateCirclePoints(double centerLat, double centerLon, double radius, int numPoints)
    {
        var points = new List<(double Lat, double Lon)>();
        
        for (int i = 0; i < numPoints; i++)
        {
            double angle = (360.0 / numPoints) * i;
            var (lat, lon) = GeoMath.CalculateDestination(centerLat, centerLon, angle, radius);
            points.Add((lat, lon));
        }
        
        // Close the circle
        points.Add(points[0]);
        
        return points;
    }

    private void ClearGeofenceLayer()
    {
        _geofenceLayer.Features = new List<IFeature>();
        _geofenceLayer.DataHasChanged();
    }

    // Offline Maps Properties and Commands
    private readonly IOfflineMapService? _offlineMapService;
    private readonly ITileDownloadService? _tileDownloadService;
    
    [ObservableProperty] private bool _isOfflineMode;
    [ObservableProperty] private MapRegion _jawaRegion = MapRegion.Jawa;
    [ObservableProperty] private MapRegion _kalimantanRegion = MapRegion.Kalimantan;
    [ObservableProperty] private DownloadProgress? _currentDownloadProgress;

    public string DownloadProgressText => CurrentDownloadProgress?.GetFormattedProgress() ?? "Not downloading";
    public double DownloadProgressValue => CurrentDownloadProgress?.ProgressPercentage ?? 0;
    [ObservableProperty] private string _storageSizeText = "0 MB";
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private int _totalTileCount;
    
    public ICommand ToggleOfflineModeCommand { get; }
    public ICommand DownloadJawaCommand { get; }
    public ICommand DownloadKalimantanCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand RefreshStorageInfoCommand { get; }

    private void InitializeOfflineMaps()
    {
        if (_offlineMapService != null)
        {
            _ = RefreshStorageInfoAsync();
        }
    }

    private void ToggleOfflineMode()
    {
        _offlineMapService?.ToggleOfflineMode();
        IsOfflineMode = _offlineMapService?.IsOfflineMode ?? false;
    }

    private async Task DownloadRegionAsync(MapRegion region)
    {
        if (_tileDownloadService == null || IsDownloading) return;
        
        try
        {
            IsDownloading = true;
            CurrentDownloadProgress = new DownloadProgress { CurrentRegion = region.Name };
            
            var progress = new Progress<DownloadProgress>(p =>
            {
                CurrentDownloadProgress = p;
            });
            
            await _tileDownloadService.DownloadRegionAsync(region, progress);
            await RefreshStorageInfoAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Download failed: {ex.Message}");
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private void CancelDownload()
    {
        _tileDownloadService?.CancelDownload();
    }

    private async Task ClearCacheAsync()
    {
        if (_offlineMapService == null) return;
        
        var count = await _offlineMapService.ClearCacheAsync();
        await RefreshStorageInfoAsync();
        TotalTileCount = 0;
    }

    private async Task RefreshStorageInfoAsync()
    {
        if (_offlineMapService == null) return;
        
        StorageSizeText = await _offlineMapService.GetStorageSizeFormattedAsync();
        TotalTileCount = await _offlineMapService.GetTileCountAsync();
    }
}
