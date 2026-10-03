using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Dispatching;
using SkiaSharp;
using SkiaSharp.Views.Windows;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Core.Services.Optimization;

namespace HarvestmoonGCS.Controls
{
    public sealed partial class SkiaMapControl : UserControl
    {
        private double _centerLat = -6.24361; // Karawang rice field default
        private double _centerLon = 107.36556;
        private int _zoomLevel = 15;
        
        private double _vehicleLat = -6.24361;
        private double _vehicleLon = 107.36556;
        private bool _showVehicle = false;
        private bool _followVehicle = false;
        private double _lastRenderedVehicleLat = double.NaN;
        private double _lastRenderedVehicleLon = double.NaN;
        private const double VehicleRenderThresholdMeters = 1.5;
        
        // Tracker position (for Tracker page)
        private double _trackerLat = -6.200000;
        private double _trackerLon = 106.816666;
        private bool _showTracker = false;

        // LoRa node tracking overlay.
        private readonly Dictionary<int, LoRaNodeOverlay> _loRaNodes = new Dictionary<int, LoRaNodeOverlay>();
        private readonly Dictionary<int, List<(double Lat, double Lon)>> _loRaTrails = new Dictionary<int, List<(double Lat, double Lon)>>();
        private bool _loRaFirstFix = true;
        
        // Pan and zoom state
        private bool _isPanning = false;
        private Windows.Foundation.Point _lastPanPoint;
        private double _offsetX = 0;
        private double _offsetY = 0;
        
        // Tile cache
        private Dictionary<string, SKBitmap> _tileCache = new Dictionary<string, SKBitmap>();
        private const int MAX_TILE_CACHE_SIZE = 180; // Keep more nearby tiles to reduce placeholder redraws during pan
        private Queue<string> _tileCacheOrder = new Queue<string>(); // Track insertion order for LRU
        private HttpClient _httpClient = new HttpClient();
        private string _currentTileServer = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
        private MapTileProvider _currentProvider = MapTileProvider.OpenStreetMap;
        
        // Offline map service
        private IOfflineMapService? _offlineMapService;
        private bool _isOfflineMode = false;
        
        // Dispatcher for UI thread operations
        private DispatcherQueue? _dispatcherQueue;
        
        // Rendering throttle
        private DateTime _lastRenderTimeUtc = DateTime.MinValue;
        private const int INTERACTION_RENDER_INTERVAL_MS = 33; // ~30 FPS while user is interacting
        private const int BACKGROUND_RENDER_INTERVAL_MS = 150; // ~7 FPS for tile/background updates
        private int _interactionRenderIntervalMs = INTERACTION_RENDER_INTERVAL_MS;
        private int _backgroundRenderIntervalMs = BACKGROUND_RENDER_INTERVAL_MS;
        private bool _renderPending = false;
        private bool _isActive = true;  // false = page hidden, skip rendering
        private readonly HashSet<string> _tileLoadsInProgress = new HashSet<string>();
        private readonly SemaphoreSlim _tileLoadLimiter = new SemaphoreSlim(6, 6);
        private IOptimizedRenderer? _optimizedRenderer;
        private DateTime _lastOptimizationSyncUtc = DateTime.MinValue;
        private bool _reduceOverlayDetails;
        
        // Vehicle icon cache and current icon
        private static readonly Dictionary<string, SKBitmap> _vehicleIconCache = new Dictionary<string, SKBitmap>();
        private SKBitmap? _currentVehicleIcon;
        private int _currentVehicleType = 1; // Default to FixedWing

        private bool IsUserInteracting =>
            _isPanning ||
            _isDraggingWaypoint ||
            _isDraggingCanvasMarker ||
            _isDraggingGeofenceCenter ||
            _draggedGeofenceVertexIndex >= 0;
        
        // Map tile providers
        public enum MapTileProvider
        {
            OpenStreetMap,
            ArcGISTopographic,
            ArcGISImagery,
            ArcGISStreetMap,
            GoogleMap,
            GoogleSatellite,
            GoogleTerrain,
            GoogleHybrid
        }
        
        public SkiaMapControl()
        {
            this.InitializeComponent();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "PigeonGCS/1.0");
            // Acquire DispatcherQueue early as fallback; Loaded event will update it
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            this.Loaded += SkiaMapControl_Loaded;
            InitializeRuntimeOptimization();
            
            Serilog.Log.Debug("[SkiaMapControl] Constructor called");
        }

        /// <summary>
        /// Sets the offline map service for tile retrieval
        /// </summary>
        public void SetOfflineMapService(IOfflineMapService offlineService)
        {
            _offlineMapService = offlineService;
            if (_offlineMapService != null)
            {
                _offlineMapService.OfflineModeChanged += OnOfflineModeChanged;
                _isOfflineMode = _offlineMapService.IsOfflineMode;
            }
        }

        private void OnOfflineModeChanged(object? sender, bool isOffline)
        {
            _isOfflineMode = isOffline;
            _dispatcherQueue?.TryEnqueue(() =>
            {
                RequestRender();
            });
        }

        /// <summary>
        /// Toggles between online and offline mode
        /// </summary>
        public void SetOfflineMode(bool isOffline)
        {
            _offlineMapService?.SetOfflineMode(isOffline);
        }

        /// <summary>
        /// Gets current offline mode status
        /// </summary>
        public bool IsOfflineMode => _isOfflineMode;

        private void SkiaMapControl_Loaded(object sender, RoutedEventArgs e)
        {
            // Acquire DispatcherQueue here when UI context is guaranteed
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            SyncOptimizationSettings(force: true);
            
            Serilog.Log.Debug("[SkiaMapControl] Loaded event fired - Canvas ActualWidth: {Width}, ActualHeight: {Height}", 
                mapCanvas.ActualWidth, mapCanvas.ActualHeight);
            
            // Initial render
            mapCanvas.Invalidate();
            
            Serilog.Log.Debug("[SkiaMapControl] Canvas invalidated");
        }

        private void InitializeRuntimeOptimization()
        {
            try
            {
                _optimizedRenderer = App.Current?.Services?.GetService(typeof(IOptimizedRenderer)) as IOptimizedRenderer;
                SyncOptimizationSettings(force: true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "[SkiaMapControl] Runtime optimization service unavailable");
            }
        }

        private void SyncOptimizationSettings(bool force = false)
        {
            if (_optimizedRenderer == null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (!force && (now - _lastOptimizationSyncUtc).TotalSeconds < 2)
            {
                return;
            }

            _lastOptimizationSyncUtc = now;
            var metrics = _optimizedRenderer.GetMetrics();
            var targetFps = Math.Clamp(metrics.TargetFPS > 0 ? metrics.TargetFPS : 30, 12, 60);
            var interactionInterval = Math.Clamp((int)Math.Round(1000.0 / targetFps), 16, 120);

            _interactionRenderIntervalMs = interactionInterval;
            _backgroundRenderIntervalMs = Math.Clamp(interactionInterval * 3, 50, 240);
            _reduceOverlayDetails = metrics.QualityLevel <= (int)RenderingQuality.Low || targetFps <= 24;
        }

        /// <summary>
        /// Load vehicle icon from Assets/icons/ folder
        /// </summary>
        public async Task LoadVehicleIconAsync(int vehicleType)
        {
            string iconFilename = vehicleType switch
            {
                1 => "ikon-wahana-pesawat-1.png", // FixedWing
                2 => "ikon-quadcopter.png", // Quadrotor
                13 => "ikon-quadcopter.png", // Hexarotor
                14 => "ikon-quadcopter.png", // Octorotor
                15 => "ikon-quadcopter.png", // Tricopter
                29 => "ikon-quadcopter.png", // Dodecarotor
                43 => "ikon-quadcopter.png", // GenericMultirotor
                _ => "ikon-wahana-pesawat-1.png" // Default to plane
            };

            string cacheKey = $"{vehicleType}:{iconFilename}";
            
            // Check cache first
            if (_vehicleIconCache.TryGetValue(cacheKey, out var cachedBitmap) && cachedBitmap != null)
            {
                _currentVehicleIcon = cachedBitmap;
                _currentVehicleType = vehicleType;
                Serilog.Log.Debug($"[SkiaMapControl] Using cached vehicle icon: {iconFilename}");
                return;
            }

            // Load from file
            try
            {
                var uri = new Uri($"ms-appx:///Assets/icons/{iconFilename}");
                var file = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(uri);
                
                using (var stream = await file.OpenStreamForReadAsync())
                {
                    var bitmap = SKBitmap.Decode(stream);
                    if (bitmap != null)
                    {
                        _vehicleIconCache[cacheKey] = bitmap;
                        _currentVehicleIcon = bitmap;
                        _currentVehicleType = vehicleType;
                        Serilog.Log.Debug($"[SkiaMapControl] Loaded vehicle icon: {iconFilename}");
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Error($"[SkiaMapControl] Failed to load vehicle icon {iconFilename}: {ex.Message}");
            }
        }

        private void OnPaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            var frameStartTicks = Stopwatch.GetTimestamp();
            SyncOptimizationSettings();

            var canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.LightGray);
            
            var info = e.Info;
            int width = info.Width;
            int height = info.Height;
            
            // Draw map tiles
            DrawMapTiles(canvas, width, height);
            
            // Draw geofence (behind waypoints)
            DrawGeofence(canvas, width, height);
            
            // Draw waypoint trail
            DrawWaypointTrail(canvas, width, height);
            
            // Draw waypoint markers
            DrawWaypointMarkers(canvas, width, height);
            
            // Draw vehicle marker
            if (_showVehicle)
            {
                DrawVehicleMarker(canvas, width, height);
            }
            
            // Draw tracker marker
            if (_showTracker)
            {
                DrawTrackerMarker(canvas, width, height);
            }

            DrawLoRaOverlay(canvas, width, height);
            
            // Grid labels/decoration are expensive and not critical while dragging.
            if (!IsUserInteracting)
            {
                DrawGridLines(canvas, width, height);
            }
            
            // Draw scale only when idle to keep drag/pan smooth.
            if (!IsUserInteracting)
            {
                DrawScale(canvas, width, height);
            }

            if (_optimizedRenderer != null)
            {
                var elapsedMs = (Stopwatch.GetTimestamp() - frameStartTicks) * 1000.0 / Stopwatch.Frequency;
                _optimizedRenderer.RecordFrameTime(elapsedMs);
            }
        }

        /// <summary>
        /// Renders the current map state (tiles, geofence, waypoints, vehicle marker) to an
        /// off-screen surface and returns it as PNG bytes — a real snapshot of what the map
        /// control is showing right now, for evidence/report attachments.
        /// </summary>
        public byte[]? CaptureSnapshotPng(int width = 960, int height = 640)
        {
            try
            {
                var info = new SKImageInfo(Math.Max(1, width), Math.Max(1, height));
                using var surface = SKSurface.Create(info);
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.LightGray);

                DrawMapTiles(canvas, width, height);
                DrawGeofence(canvas, width, height);
                DrawWaypointTrail(canvas, width, height);
                DrawWaypointMarkers(canvas, width, height);
                if (_showVehicle)
                {
                    DrawVehicleMarker(canvas, width, height);
                }
                if (_showTracker)
                {
                    DrawTrackerMarker(canvas, width, height);
                }
                DrawGridLines(canvas, width, height);
                DrawScale(canvas, width, height);

                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 90);
                return data.ToArray();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[SkiaMapControl] CaptureSnapshotPng failed");
                return null;
            }
        }

        private void DrawWaypointTrail(SKCanvas canvas, int width, int height)
        {
            if (_waypointOverlayItems.Count < 2)
                return;
                
            using (var paint = new SKPaint())
            {
                paint.Color = new SKColor(255, 165, 0); // Orange
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 3;
                paint.IsAntialias = true;
                
                // Draw lines connecting waypoints
                for (int i = 1; i < _waypointOverlayItems.Count; i++)
                {
                    var prev = _waypointOverlayItems[i - 1];
                    var curr = _waypointOverlayItems[i];
                    
                    var prevScreen = LatLonToScreen(prev.Lat, prev.Lon, width, height);
                    var currScreen = LatLonToScreen(curr.Lat, curr.Lon, width, height);
                    
                    canvas.DrawLine(prevScreen.X, prevScreen.Y, currScreen.X, currScreen.Y, paint);
                }
            }
        }

        private void DrawMapTiles(SKCanvas canvas, int width, int height)
        {
            // Calculate which tiles to draw based on center position and zoom
            int tileSize = 256;
            double scale = Math.Pow(2, _zoomLevel);
            
            // Convert lat/lon to tile coordinates
            double centerX = ((_centerLon + 180.0) / 360.0) * scale;
            double centerY = (1.0 - Math.Log(Math.Tan(_centerLat * Math.PI / 180.0) + 
                             1.0 / Math.Cos(_centerLat * Math.PI / 180.0)) / Math.PI) / 2.0 * scale;
            
            // Calculate tile range to draw
            int tilesX = (width / tileSize) + 2;
            int tilesY = (height / tileSize) + 2;
            
            int centerTileX = (int)Math.Floor(centerX);
            int centerTileY = (int)Math.Floor(centerY);
            
            // Pixel offset within center tile
            double pixelOffsetX = (centerX - centerTileX) * tileSize;
            double pixelOffsetY = (centerY - centerTileY) * tileSize;
            
            // Draw tiles
            for (int ty = -tilesY / 2; ty <= tilesY / 2; ty++)
            {
                for (int tx = -tilesX / 2; tx <= tilesX / 2; tx++)
                {
                    int tileX = centerTileX + tx;
                    int tileY = centerTileY + ty;
                    
                    // Check tile bounds
                    int maxTile = (int)scale;
                    if (tileX < 0 || tileX >= maxTile || tileY < 0 || tileY >= maxTile)
                        continue;
                    
                    // Calculate screen position
                    float screenX = (float)(width / 2 + (tx * tileSize) - pixelOffsetX + _offsetX);
                    float screenY = (float)(height / 2 + (ty * tileSize) - pixelOffsetY + _offsetY);

                    // Skip tiles completely outside the viewport.
                    if (screenX + tileSize < 0 || screenX > width || screenY + tileSize < 0 || screenY > height)
                    {
                        continue;
                    }
                    
                    // Try to load real tile from cache or draw placeholder
                    string tileKey = $"{_zoomLevel}/{tileX}/{tileY}";
                    if (_tileCache.TryGetValue(tileKey, out var cachedTile) && cachedTile != null)
                    {
                        // Draw cached tile
                        canvas.DrawBitmap(cachedTile, screenX, screenY);
                    }
                    else
                    {
                        // Draw placeholder while tile loads
                        DrawTilePlaceholder(canvas, screenX, screenY, tileSize, tileX, tileY, _zoomLevel);
                        
                        // Start loading tile asynchronously (fire and forget)
                        _ = LoadTileAsync(tileKey, tileX, tileY, _zoomLevel);
                    }
                }
            }
        }
        
        private async Task LoadTileAsync(string tileKey, int tileX, int tileY, int zoom)
        {
            // Check if already loading or loaded
            lock (_tileCache)
            {
                if (_tileCache.ContainsKey(tileKey) || _tileLoadsInProgress.Contains(tileKey))
                {
                    return;
                }

                _tileLoadsInProgress.Add(tileKey);
            }
            
            try
            {
                await _tileLoadLimiter.WaitAsync().ConfigureAwait(false);

                // Try offline database first if in offline mode or hybrid mode
                if (_offlineMapService != null)
                {
                    var offlineTile = await _offlineMapService.GetTileAsync(zoom, tileX, tileY);
                    if (offlineTile != null)
                    {
                        using (var stream = new System.IO.MemoryStream(offlineTile))
                        {
                            var bitmap = SKBitmap.Decode(stream);
                            if (bitmap != null)
                            {
                                AddTileToCache(tileKey, bitmap);
                                return;
                            }
                        }
                    }
                    
                    // If offline mode is enabled and no offline tile found, skip online download
                    if (_isOfflineMode)
                    {
                        return;
                    }
                }

                // Construct tile URL based on current provider
                string tileUrl = GetTileUrl(tileX, tileY, zoom);
                
                // Download tile from internet
                var response = await _httpClient.GetAsync(tileUrl);
                if (response.IsSuccessStatusCode)
                {
                    var imageData = await response.Content.ReadAsByteArrayAsync();
                    
                    // Decode image to SKBitmap
                    using (var stream = new System.IO.MemoryStream(imageData))
                    {
                        var bitmap = SKBitmap.Decode(stream);
                        if (bitmap != null)
                        {
                            AddTileToCache(tileKey, bitmap);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[SkiaMapControl] Failed to load tile: {TileKey}", tileKey);
            }
            finally
            {
                _tileLoadLimiter.Release();
                lock (_tileCache)
                {
                    _tileLoadsInProgress.Remove(tileKey);
                }
            }
        }

        private void AddTileToCache(string tileKey, SKBitmap bitmap)
        {
            lock (_tileCache)
            {
                // Evict oldest tile if cache is full
                if (_tileCache.Count >= MAX_TILE_CACHE_SIZE && _tileCacheOrder.Count > 0)
                {
                    var oldestKey = _tileCacheOrder.Dequeue();
                    if (_tileCache.TryGetValue(oldestKey, out var oldBitmap))
                    {
                        oldBitmap?.Dispose();
                        _tileCache.Remove(oldestKey);
                    }
                }
                
                _tileCache[tileKey] = bitmap;
                _tileCacheOrder.Enqueue(tileKey);
                _tileLoadsInProgress.Remove(tileKey);
            }
            
            // Trigger throttled redraw on UI thread
            _dispatcherQueue?.TryEnqueue(() =>
            {
                RequestRender();
            });
        }

        private string GetTileUrl(int tileX, int tileY, int zoom)
        {
            // Replace placeholders in tile server URL
            return _currentTileServer
                .Replace("{z}", zoom.ToString())
                .Replace("{x}", tileX.ToString())
                .Replace("{y}", tileY.ToString())
                .Replace("{s}", GetSubdomain(tileX, tileY)); // For servers that use subdomains
        }

        private string GetSubdomain(int tileX, int tileY)
        {
            // Cycle through subdomains a, b, c for load balancing
            string[] subdomains = { "a", "b", "c" };
            int index = (tileX + tileY) % subdomains.Length;
            return subdomains[index];
        }

        public void SetTileProvider(MapTileProvider provider)
        {
            if (_currentProvider == provider)
                return;

            _currentProvider = provider;
            
            // Set tile server URL based on provider
            switch (provider)
            {
                case MapTileProvider.OpenStreetMap:
                    _currentTileServer = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
                    break;
                    
                case MapTileProvider.ArcGISTopographic:
                    _currentTileServer = "https://services.arcgisonline.com/ArcGIS/rest/services/World_Topo_Map/MapServer/tile/{z}/{y}/{x}";
                    break;
                    
                case MapTileProvider.ArcGISImagery:
                    _currentTileServer = "https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}";
                    break;
                    
                case MapTileProvider.ArcGISStreetMap:
                    _currentTileServer = "https://services.arcgisonline.com/ArcGIS/rest/services/World_Street_Map/MapServer/tile/{z}/{y}/{x}";
                    break;
                    
                case MapTileProvider.GoogleMap:
                    _currentTileServer = "https://mt{s}.google.com/vt/lyrs=m&x={x}&y={y}&z={z}";
                    break;
                    
                case MapTileProvider.GoogleSatellite:
                    _currentTileServer = "https://mt{s}.google.com/vt/lyrs=s&x={x}&y={y}&z={z}";
                    break;
                    
                case MapTileProvider.GoogleTerrain:
                    _currentTileServer = "https://mt{s}.google.com/vt/lyrs=p&x={x}&y={y}&z={z}";
                    break;
                    
                case MapTileProvider.GoogleHybrid:
                    _currentTileServer = "https://mt{s}.google.com/vt/lyrs=y&x={x}&y={y}&z={z}";
                    break;
            }
            
            // Clear tile cache when switching providers
            lock (_tileCache)
            {
                foreach (var tile in _tileCache.Values)
                {
                    tile?.Dispose();
                }
                _tileCache.Clear();
                _tileCacheOrder.Clear();
            }
            
            // Trigger redraw
            RequestRender();
            
            Serilog.Log.Information("[SkiaMapControl] Tile provider changed to: {Provider}", provider);
        }

        private void DrawTilePlaceholder(SKCanvas canvas, float x, float y, int size, int tileX, int tileY, int zoom)
        {
            using (var paint = new SKPaint())
            {
                // Draw ocean/land background based on tile position
                // Simple heuristic: tiles near equator and certain longitudes are land
                bool isLand = IsLandTile(tileX, tileY, zoom);
                
                if (isLand)
                {
                    // Land color - light green/beige
                    paint.Color = new SKColor(220, 230, 200);
                }
                else
                {
                    // Ocean color - light blue
                    paint.Color = new SKColor(170, 211, 223);
                }
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawRect(x, y, size, size, paint);

                // Draw grid lines
                paint.Color = new SKColor(200, 200, 200, 100);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 1;
                canvas.DrawRect(x, y, size, size, paint);

                // Lightweight label at far zoom only.
                if (!IsUserInteracting && zoom <= 4)
                {
                    paint.Color = new SKColor(100, 100, 100, 150);
                    paint.Style = SKPaintStyle.Fill;
                    paint.TextSize = 10;
                    paint.IsAntialias = true;
                    
                    string tileInfo = $"Z{zoom}";
                    canvas.DrawText(tileInfo, x + 5, y + 15, paint);
                }
            }
        }

        private bool IsLandTile(int tileX, int tileY, int zoom)
        {
            // Simple heuristic to determine if tile is land or ocean
            // This is a rough approximation - real implementation would use actual map data
            
            double scale = Math.Pow(2, zoom);
            double lon = (tileX / scale) * 360.0 - 180.0;
            double lat = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * tileY / scale))) * 180.0 / Math.PI;
            
            // Very rough land detection based on known land masses
            // Indonesia region
            if (lat >= -11 && lat <= 6 && lon >= 95 && lon <= 141)
                return true;
            
            // Asia
            if (lat >= 0 && lat <= 70 && lon >= 60 && lon <= 150)
                return true;
            
            // Europe
            if (lat >= 35 && lat <= 71 && lon >= -10 && lon <= 40)
                return true;
            
            // Africa
            if (lat >= -35 && lat <= 37 && lon >= -18 && lon <= 52)
                return true;
            
            // North America
            if (lat >= 15 && lat <= 72 && lon >= -170 && lon <= -50)
                return true;
            
            // South America
            if (lat >= -56 && lat <= 13 && lon >= -82 && lon <= -34)
                return true;
            
            // Australia
            if (lat >= -44 && lat <= -10 && lon >= 113 && lon <= 154)
                return true;
            
            return false;
        }

        private void DrawTerrainFeatures(SKCanvas canvas, float x, float y, int size, int tileX, int tileY)
        {
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                
                // Draw some random terrain features (hills, forests)
                var random = new Random((tileX * 1000 + tileY) * 31); // Deterministic random
                
                // Draw a few terrain patches
                for (int i = 0; i < 3; i++)
                {
                    float px = x + (float)(random.NextDouble() * size);
                    float py = y + (float)(random.NextDouble() * size);
                    float radius = (float)(random.NextDouble() * 30 + 10);
                    
                    // Darker green for forests/vegetation
                    paint.Color = new SKColor(180, 200, 160, 100);
                    paint.Style = SKPaintStyle.Fill;
                    canvas.DrawCircle(px, py, radius, paint);
                }
            }
        }

        private void DrawRoads(SKCanvas canvas, float x, float y, int size, int tileX, int tileY)
        {
            using (var paint = new SKPaint())
            {
                paint.Color = new SKColor(255, 255, 255, 180);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 2;
                paint.IsAntialias = true;
                
                var random = new Random((tileX * 1000 + tileY) * 17); // Deterministic random
                
                // Draw a simple road pattern
                if (random.NextDouble() > 0.5)
                {
                    // Horizontal road
                    float roadY = y + size / 2;
                    canvas.DrawLine(x, roadY, x + size, roadY, paint);
                }
                
                if (random.NextDouble() > 0.5)
                {
                    // Vertical road
                    float roadX = x + size / 2;
                    canvas.DrawLine(roadX, y, roadX, y + size, paint);
                }
            }
        }

        private void DrawVehicleMarker(SKCanvas canvas, int width, int height)
        {
            // Convert vehicle lat/lon to screen coordinates
            var screenPos = LatLonToScreen(_vehicleLat, _vehicleLon, width, height);
            
            // Draw vehicle icon if loaded, otherwise fallback to circle
            if (_currentVehicleIcon != null)
            {
                // Calculate icon size (scale down if needed)
                float iconSize = 40;
                float halfSize = iconSize / 2;
                
                // Create destination rectangle centered on vehicle position
                var destRect = new SKRect(
                    screenPos.X - halfSize,
                    screenPos.Y - halfSize,
                    screenPos.X + halfSize,
                    screenPos.Y + halfSize
                );
                
                // Draw the icon
                canvas.DrawBitmap(_currentVehicleIcon, destRect);
            }
            else
            {
                // Fallback: draw red circle with white border and "UAV" text (like PIGEON)
                using (var paint = new SKPaint())
                {
                    // Red circle background
                    paint.Color = SKColors.Red;
                    paint.Style = SKPaintStyle.Fill;
                    paint.IsAntialias = true;
                    canvas.DrawCircle(screenPos.X, screenPos.Y, 16, paint);
                    
                    // White border
                    paint.Color = SKColors.White;
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = 2;
                    canvas.DrawCircle(screenPos.X, screenPos.Y, 16, paint);
                    
                    // UAV text
                    paint.Color = SKColors.White;
                    paint.Style = SKPaintStyle.Fill;
                    paint.TextSize = 10;
                    paint.TextAlign = SKTextAlign.Center;
                    canvas.DrawText("UAV", screenPos.X, screenPos.Y + 4, paint);
                }
            }
        }

        private void DrawTrackerMarker(SKCanvas canvas, int width, int height)
        {
            // Convert tracker lat/lon to screen coordinates
            var screenPos = LatLonToScreen(_trackerLat, _trackerLon, width, height);
            
            // Draw blue circle with "TRK" label (like PIGEON)
            using (var paint = new SKPaint())
            {
                // Blue circle background
                paint.Color = new SKColor(59, 130, 246); // Blue-500
                paint.Style = SKPaintStyle.Fill;
                paint.IsAntialias = true;
                canvas.DrawCircle(screenPos.X, screenPos.Y, 16, paint);
                
                // White border
                paint.Color = SKColors.White;
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 2;
                canvas.DrawCircle(screenPos.X, screenPos.Y, 16, paint);
                
                // TRK text
                paint.Color = SKColors.White;
                paint.Style = SKPaintStyle.Fill;
                paint.TextSize = 10;
                paint.TextAlign = SKTextAlign.Center;
                canvas.DrawText("TRK", screenPos.X, screenPos.Y + 4, paint);
            }
        }

        private void DrawGridLines(SKCanvas canvas, int width, int height)
        {
            using (var paint = new SKPaint())
            {
                paint.Color = new SKColor(100, 100, 100, 30);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 1;
                paint.PathEffect = SKPathEffect.CreateDash(new float[] { 5, 5 }, 0);
                
                // Draw latitude/longitude grid lines
                int gridSpacing = 100;
                
                // Draw vertical lines (longitude)
                for (int x = 0; x < width; x += gridSpacing)
                {
                    canvas.DrawLine(x, 0, x, height, paint);
                }
                
                // Draw horizontal lines (latitude)
                for (int y = 0; y < height; y += gridSpacing)
                {
                    canvas.DrawLine(0, y, width, y, paint);
                }
            }
            
            // Draw city labels only when full visual detail is enabled.
            if (!_reduceOverlayDetails)
            {
                DrawCityLabels(canvas, width, height);
            }
        }

        private void DrawCityLabels(SKCanvas canvas, int width, int height)
        {
            // Major cities in Indonesia and surrounding regions
            var cities = new[]
            {
                new { Name = "Jakarta", Lat = -6.2088, Lon = 106.8456 },
                new { Name = "Surabaya", Lat = -7.2575, Lon = 112.7521 },
                new { Name = "Bandung", Lat = -6.9175, Lon = 107.6191 },
                new { Name = "Medan", Lat = 3.5952, Lon = 98.6722 },
                new { Name = "Semarang", Lat = -6.9667, Lon = 110.4167 },
                new { Name = "Makassar", Lat = -5.1477, Lon = 119.4327 },
                new { Name = "Palembang", Lat = -2.9761, Lon = 104.7754 },
                new { Name = "Denpasar", Lat = -8.6705, Lon = 115.2126 },
                new { Name = "Singapore", Lat = 1.3521, Lon = 103.8198 },
                new { Name = "Kuala Lumpur", Lat = 3.1390, Lon = 101.6869 },
            };
            
            using (var paint = new SKPaint())
            {
                paint.Color = new SKColor(50, 50, 50);
                paint.Style = SKPaintStyle.Fill;
                paint.TextSize = 12;
                paint.IsAntialias = true;
                paint.Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
                
                foreach (var city in cities)
                {
                    var screenPos = LatLonToScreen(city.Lat, city.Lon, width, height);
                    
                    // Only draw if city is visible on screen
                    if (screenPos.X >= 0 && screenPos.X <= width && 
                        screenPos.Y >= 0 && screenPos.Y <= height)
                    {
                        // Draw city marker (small circle)
                        paint.Color = new SKColor(200, 50, 50);
                        canvas.DrawCircle(screenPos.X, screenPos.Y, 4, paint);
                        
                        // Draw city name
                        paint.Color = new SKColor(50, 50, 50);
                        canvas.DrawText(city.Name, screenPos.X + 8, screenPos.Y + 4, paint);
                    }
                }
            }
        }

        private void DrawScale(SKCanvas canvas, int width, int height)
        {
            // Draw scale bar in bottom right
            float scaleWidth = 100;
            float scaleHeight = 5;
            float margin = 120;
            
            float x = width - scaleWidth - margin;
            float y = height - 30;
            
            using (var paint = new SKPaint())
            {
                // Draw scale bar
                paint.Color = SKColors.Black;
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawRect(x, y, scaleWidth, scaleHeight, paint);
                
                paint.Color = SKColors.White;
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 1;
                canvas.DrawRect(x, y, scaleWidth, scaleHeight, paint);
                
                // Draw scale text
                paint.Style = SKPaintStyle.Fill;
                paint.TextSize = 10;
                paint.IsAntialias = true;
                
                // Calculate scale distance (approximate)
                double metersPerPixel = 156543.03392 * Math.Cos(_centerLat * Math.PI / 180) / Math.Pow(2, _zoomLevel);
                double scaleMeters = metersPerPixel * scaleWidth;
                
                string scaleText = scaleMeters > 1000 
                    ? $"{scaleMeters / 1000:F1} km" 
                    : $"{scaleMeters:F0} m";
                
                canvas.DrawText(scaleText, x + scaleWidth / 2 - 20, y - 5, paint);
            }
        }

        private SKPoint LatLonToScreen(double lat, double lon, int width, int height)
        {
            double scale = Math.Pow(2, _zoomLevel);
            
            // Convert lat/lon to tile coordinates
            double worldX = ((lon + 180.0) / 360.0) * scale;
            double worldY = (1.0 - Math.Log(Math.Tan(lat * Math.PI / 180.0) + 
                           1.0 / Math.Cos(lat * Math.PI / 180.0)) / Math.PI) / 2.0 * scale;
            
            double centerX = ((_centerLon + 180.0) / 360.0) * scale;
            double centerY = (1.0 - Math.Log(Math.Tan(_centerLat * Math.PI / 180.0) + 
                            1.0 / Math.Cos(_centerLat * Math.PI / 180.0)) / Math.PI) / 2.0 * scale;
            
            // Convert to screen coordinates
            int tileSize = 256;
            float screenX = (float)(width / 2 + (worldX - centerX) * tileSize + _offsetX);
            float screenY = (float)(height / 2 + (worldY - centerY) * tileSize + _offsetY);
            
            return new SKPoint(screenX, screenY);
        }

        /// <summary>
        /// Convert screen coordinates to latitude/longitude
        /// </summary>
        public (double Lat, double Lon) ScreenToLatLon(double screenX, double screenY, int width, int height)
        {
            double scale = Math.Pow(2, _zoomLevel);
            int tileSize = 256;
            
            // Calculate center in tile coordinates
            double centerX = ((_centerLon + 180.0) / 360.0) * scale;
            double centerY = (1.0 - Math.Log(Math.Tan(_centerLat * Math.PI / 180.0) + 
                            1.0 / Math.Cos(_centerLat * Math.PI / 180.0)) / Math.PI) / 2.0 * scale;
            
            // Convert screen coordinates to tile coordinates
            double worldX = centerX + (screenX - width / 2 - _offsetX) / tileSize;
            double worldY = centerY + (screenY - height / 2 - _offsetY) / tileSize;
            
            // Convert tile coordinates to lat/lon
            double lon = worldX / scale * 360.0 - 180.0;
            double n = Math.PI - 2.0 * Math.PI * worldY / scale;
            double lat = 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n)));
            
            return (lat, lon);
        }

        /// <summary>
        /// Get latitude/longitude from click position
        /// </summary>
        public (double Lat, double Lon) GetLatLonFromClick(Windows.Foundation.Point clickPosition)
        {
            return ScreenToLatLon(clickPosition.X, clickPosition.Y, (int)ActualWidth, (int)ActualHeight);
        }

        // Public methods for external control
        public void SetCenter(double latitude, double longitude, int? zoom = null)
        {
            _centerLat = latitude;
            _centerLon = longitude;
            if (zoom.HasValue)
                _zoomLevel = Math.Max(1, Math.Min(18, zoom.Value));

            txtMapCoordinates.Text = $"Lat: {latitude:F4}, Lon: {longitude:F4}";
            txtMapZoom.Text = $"Zoom: {_zoomLevel}";

            _offsetX = 0;
            _offsetY = 0;
            
            Serilog.Log.Debug("[SkiaMapControl] SetCenter called - Lat: {Lat}, Lon: {Lon}, Zoom: {Zoom}", 
                latitude, longitude, _zoomLevel);
            
            RequestRender();
        }

        public void UpdateVehiclePosition(double latitude, double longitude)
        {
            bool shouldRender = !_showVehicle ||
                                double.IsNaN(_lastRenderedVehicleLat) ||
                                CalculateDistanceMeters(_lastRenderedVehicleLat, _lastRenderedVehicleLon, latitude, longitude) >= VehicleRenderThresholdMeters;

            _vehicleLat = latitude;
            _vehicleLon = longitude;
            _showVehicle = true;

            if (!shouldRender)
            {
                return;
            }

            _lastRenderedVehicleLat = latitude;
            _lastRenderedVehicleLon = longitude;

            txtMapCoordinates.Text = $"Lat: {latitude:F4}, Lon: {longitude:F4}";

            // Follow vehicle if enabled
            if (_followVehicle)
            {
                SetCenter(latitude, longitude, _zoomLevel);
            }
            else
            {
                RequestRender();
            }
        }

        public void UpdateTrackerPosition(double latitude, double longitude)
        {
            _trackerLat = latitude;
            _trackerLon = longitude;
            _showTracker = true;
            RequestRender();
        }

        public void SetShowTracker(bool show)
        {
            _showTracker = show;
            RequestRender();
        }

        public void UpdateLoRaNode(int nodeId, double latitude, double longitude, bool isOnline)
        {
            if (nodeId <= 0 || (Math.Abs(latitude) < double.Epsilon && Math.Abs(longitude) < double.Epsilon))
            {
                return;
            }

            _loRaNodes[nodeId] = new LoRaNodeOverlay(nodeId, latitude, longitude, isOnline);

            if (!_loRaTrails.TryGetValue(nodeId, out var trail))
            {
                trail = new List<(double Lat, double Lon)>();
                _loRaTrails[nodeId] = trail;
            }

            if (trail.Count == 0 || CalculateDistanceMeters(trail[^1].Lat, trail[^1].Lon, latitude, longitude) >= 0.5)
            {
                trail.Add((latitude, longitude));
                if (trail.Count > 500)
                {
                    trail.RemoveAt(0);
                }
            }

            if (_loRaFirstFix)
            {
                SetCenter(latitude, longitude, 17);
                _loRaFirstFix = false;
                return;
            }

            RequestRender();
        }

        public void ClearLoRaNodes()
        {
            _loRaNodes.Clear();
            _loRaTrails.Clear();
            _loRaFirstFix = true;
            RequestRender();
        }

        public void SetMapControlsVisible(bool visible)
        {
            if (MapControlsPanel != null)
            {
                MapControlsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static double CalculateDistanceMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double EarthRadiusMeters = 6371000;
            var lat1Rad = lat1 * Math.PI / 180.0;
            var lat2Rad = lat2 * Math.PI / 180.0;
            var dLat = (lat2 - lat1) * Math.PI / 180.0;
            var dLon = (lon2 - lon1) * Math.PI / 180.0;

            var sinLat = Math.Sin(dLat / 2.0);
            var sinLon = Math.Sin(dLon / 2.0);
            var a = sinLat * sinLat + Math.Cos(lat1Rad) * Math.Cos(lat2Rad) * sinLon * sinLon;
            var c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));

            return EarthRadiusMeters * c;
        }

        private void DrawLoRaOverlay(SKCanvas canvas, int width, int height)
        {
            if (_loRaNodes.Count == 0)
            {
                return;
            }

            using var trailPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3,
                StrokeCap = SKStrokeCap.Round
            };

            foreach (var pair in _loRaTrails)
            {
                var trail = pair.Value;
                if (trail.Count < 2)
                {
                    continue;
                }

                trailPaint.Color = GetLoRaTrailColor(pair.Key).WithAlpha(180);
                for (var i = 1; i < trail.Count; i++)
                {
                    var prev = LatLonToScreen(trail[i - 1].Lat, trail[i - 1].Lon, width, height);
                    var current = LatLonToScreen(trail[i].Lat, trail[i].Lon, width, height);
                    canvas.DrawLine(prev.X, prev.Y, current.X, current.Y, trailPaint);
                }
            }

            using var fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            using var outlinePaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3,
                Color = SKColors.White
            };
            using var textPaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.White,
                TextSize = 18,
                FakeBoldText = true,
                TextAlign = SKTextAlign.Center
            };

            foreach (var node in _loRaNodes.Values.OrderBy(n => n.NodeId))
            {
                var point = LatLonToScreen(node.Latitude, node.Longitude, width, height);
                fillPaint.Color = node.IsOnline ? GetLoRaNodeColor(node.NodeId) : new SKColor(128, 128, 128);

                canvas.DrawCircle(point.X, point.Y, 11, fillPaint);
                canvas.DrawCircle(point.X, point.Y, 11, outlinePaint);
                canvas.DrawText($"N{node.NodeId}", point.X, point.Y - 16, textPaint);
            }
        }

        private static SKColor GetLoRaNodeColor(int nodeId)
        {
            return nodeId switch
            {
                1 => new SKColor(255, 80, 80),
                2 => new SKColor(80, 140, 255),
                3 => new SKColor(0, 220, 100),
                _ => new SKColor(0, 206, 209)
            };
        }

        private static SKColor GetLoRaTrailColor(int nodeId)
        {
            return nodeId switch
            {
                1 => new SKColor(0, 206, 209),
                2 => new SKColor(255, 165, 0),
                3 => new SKColor(0, 255, 100),
                _ => new SKColor(0, 206, 209)
            };
        }

        private sealed record LoRaNodeOverlay(int NodeId, double Latitude, double Longitude, bool IsOnline);

        /// <summary>
        /// Enable/disable follow vehicle mode
        /// </summary>
        public void SetFollowVehicle(bool follow)
        {
            _followVehicle = follow;
            
            if (_followVehicle && _showVehicle)
            {
                SetCenter(_vehicleLat, _vehicleLon, _zoomLevel);
            }
            
            Serilog.Log.Information("[SkiaMapControl] Follow vehicle mode: {Follow}", follow);
        }

        /// <summary>
        /// Get current follow vehicle state
        /// </summary>
        public bool IsFollowingVehicle => _followVehicle;
        
        /// <summary>
        /// Request a render with throttling to prevent excessive redraws
        /// </summary>
        public void SetActive(bool active) { _isActive = active; }

        private void RequestRender()
        {
            if (!_isActive) return;
            SyncOptimizationSettings();
            var now = DateTime.UtcNow;
            var minRenderIntervalMs = IsUserInteracting ? _interactionRenderIntervalMs : _backgroundRenderIntervalMs;
            var timeSinceLastRender = (now - _lastRenderTimeUtc).TotalMilliseconds;

            if (timeSinceLastRender >= minRenderIntervalMs)
            {
                // Enough time has passed, render immediately
                _lastRenderTimeUtc = now;
                _renderPending = false;
                mapCanvas.Invalidate();
                
                // Update waypoint marker positions
                if (_waypointOverlayItems.Count > 0)
                {
                    UpdateWaypointMarkerPositions();
                }
            }
            else if (!_renderPending)
            {
                // Schedule a delayed render
                _renderPending = true;
                var delay = Math.Max(1, (int)(minRenderIntervalMs - timeSinceLastRender));
                _ = Task.Delay(delay).ContinueWith(_ =>
                {
                    _dispatcherQueue?.TryEnqueue(() =>
                    {
                        _lastRenderTimeUtc = DateTime.UtcNow;
                        _renderPending = false;
                        mapCanvas.Invalidate();
                        
                        // Update waypoint marker positions
                        if (_waypointOverlayItems.Count > 0)
                        {
                            UpdateWaypointMarkerPositions();
                        }
                    });
                });
            }
        }

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
        {
            if (_zoomLevel < 18)
            {
                _zoomLevel++;
                txtMapZoom.Text = $"Zoom: {_zoomLevel}";
                
                // Don't clear waypoints on zoom
                RequestRender();
                
                Serilog.Log.Information("[SkiaMapControl] Zoomed in to level {Zoom}", _zoomLevel);
            }
        }

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
        {
            if (_zoomLevel > 1)
            {
                _zoomLevel--;
                txtMapZoom.Text = $"Zoom: {_zoomLevel}";
                
                // Don't clear waypoints on zoom
                RequestRender();
                
                Serilog.Log.Information("[SkiaMapControl] Zoomed out to level {Zoom}", _zoomLevel);
            }
        }

        private void BtnCenterMap_Click(object sender, RoutedEventArgs e)
        {
            if (_showVehicle)
            {
                SetCenter(_vehicleLat, _vehicleLon, _zoomLevel);
                Serilog.Log.Information("[SkiaMapControl] Centered map on vehicle position: {Lat}, {Lon}", _vehicleLat, _vehicleLon);
            }
            else
            {
                // Center on default location if no vehicle
                SetCenter(-6.24361, 107.36556, _zoomLevel);
                Serilog.Log.Information("[SkiaMapControl] Centered map on default location");
            }
        }

        // Pan and zoom handlers
        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var currentPoint = e.GetCurrentPoint(mapCanvas).Position;
            int width = (int)mapCanvas.ActualWidth;
            int height = (int)mapCanvas.ActualHeight;
            
            Serilog.Log.Debug("[SkiaMapControl] OnPointerPressed at ({X}, {Y})", currentPoint.X, currentPoint.Y);
            
            // Check if clicking on a waypoint marker
            var clickedWaypoint = GetWaypointAtPosition(currentPoint.X, currentPoint.Y, width, height);
            if (clickedWaypoint != null)
            {
                _isDraggingCanvasMarker = true;
                _draggedCanvasWaypoint = clickedWaypoint;
                _lastPanPoint = currentPoint;
                mapCanvas.CapturePointer(e.Pointer);
                e.Handled = true;
                Serilog.Log.Information("[SkiaMapControl] Started dragging waypoint {Seq}", clickedWaypoint.Sequence);
                return;
            }
            
            // Check if clicking on geofence center (circular)
            if (_geofence != null && _geofence.IsCircular)
            {
                var centerScreen = LatLonToScreen(_geofence.CenterLat, _geofence.CenterLon, width, height);
                double distance = Math.Sqrt(Math.Pow(currentPoint.X - centerScreen.X, 2) + Math.Pow(currentPoint.Y - centerScreen.Y, 2));
                Serilog.Log.Debug("[SkiaMapControl] Distance to geofence center: {Distance}px", distance);
                if (distance <= 20) // 20px hit radius
                {
                    _isDraggingGeofenceCenter = true;
                    _lastPanPoint = currentPoint;
                    mapCanvas.CapturePointer(e.Pointer);
                    e.Handled = true;
                    Serilog.Log.Information("[SkiaMapControl] Started dragging geofence center");
                    return;
                }
            }
            
            // Check if clicking on geofence vertex (polygon)
            if (_geofence != null && !_geofence.IsCircular && _geofence.Vertices.Count > 0)
            {
                for (int i = 0; i < _geofence.Vertices.Count; i++)
                {
                    var vertex = _geofence.Vertices[i];
                    var vertexScreen = LatLonToScreen(vertex.Lat, vertex.Lon, width, height);
                    double distance = Math.Sqrt(Math.Pow(currentPoint.X - vertexScreen.X, 2) + Math.Pow(currentPoint.Y - vertexScreen.Y, 2));
                    if (distance <= 15) // 15px hit radius
                    {
                        _isDraggingCanvasMarker = true;
                        _draggedGeofenceVertexIndex = i;
                        _lastPanPoint = currentPoint;
                        mapCanvas.CapturePointer(e.Pointer);
                        e.Handled = true;
                        Serilog.Log.Information("[SkiaMapControl] Started dragging geofence vertex {Index}", i);
                        return;
                    }
                }
            }
            
            // Normal pan
            _isPanning = true;
            _lastPanPoint = currentPoint;
            mapCanvas.CapturePointer(e.Pointer);
            Serilog.Log.Debug("[SkiaMapControl] Started panning");
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var currentPoint = e.GetCurrentPoint(mapCanvas).Position;
            
            // Dragging waypoint marker
            if (_isDraggingCanvasMarker && _draggedCanvasWaypoint != null)
            {
                var (newLat, newLon) = ScreenToLatLon(currentPoint.X, currentPoint.Y);
                _draggedCanvasWaypoint.Lat = newLat;
                _draggedCanvasWaypoint.Lon = newLon;
                RequestRender();
                return;
            }
            
            // Dragging geofence center
            if (_isDraggingGeofenceCenter && _geofence != null)
            {
                var (newLat, newLon) = ScreenToLatLon(currentPoint.X, currentPoint.Y);
                _geofence.CenterLat = newLat;
                _geofence.CenterLon = newLon;
                RequestRender();
                return;
            }
            
            // Dragging geofence vertex
            if (_isDraggingCanvasMarker && _draggedGeofenceVertexIndex >= 0 && _geofence != null)
            {
                var (newLat, newLon) = ScreenToLatLon(currentPoint.X, currentPoint.Y);
                _geofence.Vertices[_draggedGeofenceVertexIndex] = (newLat, newLon);
                RequestRender();
                return;
            }
            
            // Normal panning
            if (_isPanning)
            {
                double dx = currentPoint.X - _lastPanPoint.X;
                double dy = currentPoint.Y - _lastPanPoint.Y;
                
                _offsetX += dx;
                _offsetY += dy;
                
                _lastPanPoint = currentPoint;
                RequestRender();
            }
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            // Finish dragging waypoint
            if (_isDraggingCanvasMarker && _draggedCanvasWaypoint != null)
            {
                _isDraggingCanvasMarker = false;
                mapCanvas.ReleasePointerCapture(e.Pointer);
                
                // Notify about waypoint move
                WaypointMoved?.Invoke(this, new WaypointMovedEventArgs
                {
                    Sequence = _draggedCanvasWaypoint.Sequence,
                    NewLat = _draggedCanvasWaypoint.Lat,
                    NewLon = _draggedCanvasWaypoint.Lon
                });
                
                Serilog.Log.Information("[SkiaMapControl] Waypoint {Seq} moved to {Lat}, {Lon}", 
                    _draggedCanvasWaypoint.Sequence, _draggedCanvasWaypoint.Lat, _draggedCanvasWaypoint.Lon);
                
                _draggedCanvasWaypoint = null;
                return;
            }
            
            // Finish dragging geofence center
            if (_isDraggingGeofenceCenter && _geofence != null)
            {
                _isDraggingGeofenceCenter = false;
                mapCanvas.ReleasePointerCapture(e.Pointer);
                
                // Notify about geofence center move
                GeofenceCenterMoved?.Invoke(this, new GeofenceCenterMovedEventArgs
                {
                    NewLat = _geofence.CenterLat,
                    NewLon = _geofence.CenterLon
                });
                
                Serilog.Log.Information("[SkiaMapControl] Geofence center moved to {Lat}, {Lon}", 
                    _geofence.CenterLat, _geofence.CenterLon);
                return;
            }
            
            // Finish dragging geofence vertex
            if (_isDraggingCanvasMarker && _draggedGeofenceVertexIndex >= 0 && _geofence != null)
            {
                _isDraggingCanvasMarker = false;
                mapCanvas.ReleasePointerCapture(e.Pointer);
                
                var vertex = _geofence.Vertices[_draggedGeofenceVertexIndex];
                
                // Notify about vertex move
                GeofenceVertexMoved?.Invoke(this, new GeofenceVertexMovedEventArgs
                {
                    VertexIndex = _draggedGeofenceVertexIndex,
                    NewLat = vertex.Lat,
                    NewLon = vertex.Lon
                });
                
                Serilog.Log.Information("[SkiaMapControl] Geofence vertex {Index} moved to {Lat}, {Lon}", 
                    _draggedGeofenceVertexIndex, vertex.Lat, vertex.Lon);
                
                _draggedGeofenceVertexIndex = -1;
                return;
            }
            
            // Normal pan finish
            if (_isPanning)
            {
                _isPanning = false;
                mapCanvas.ReleasePointerCapture(e.Pointer);
                
                // Update center position based on offset
                UpdateCenterFromOffset();
            }
        }

        private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var delta = e.GetCurrentPoint(mapCanvas).Properties.MouseWheelDelta;
            
            if (delta > 0)
            {
                BtnZoomIn_Click(sender, new RoutedEventArgs());
            }
            else if (delta < 0)
            {
                BtnZoomOut_Click(sender, new RoutedEventArgs());
            }
        }

        private void UpdateCenterFromOffset()
        {
            int width = (int)mapCanvas.ActualWidth;
            int height = (int)mapCanvas.ActualHeight;
            if (width <= 0 || height <= 0)
            {
                _offsetX = 0;
                _offsetY = 0;
                return;
            }

            // Derive the new map center from the current rendered viewport to avoid jumpy release behavior.
            var (newLat, newLon) = ScreenToLatLon(width / 2.0, height / 2.0);
            _centerLat = Math.Max(-85, Math.Min(85, newLat));
            _centerLon = ((newLon + 180) % 360 + 360) % 360 - 180;

            _offsetX = 0;
            _offsetY = 0;
            
            txtMapCoordinates.Text = $"Lat: {_centerLat:F4}, Lon: {_centerLon:F4}";
            RequestRender();
        }

        // Properties for binding
        public double Latitude
        {
            get => _centerLat;
            set => SetCenter(value, _centerLon);
        }

        public double Longitude
        {
            get => _centerLon;
            set => SetCenter(_centerLat, value);
        }

        public int ZoomLevel
        {
            get => _zoomLevel;
            set
            {
                _zoomLevel = Math.Max(1, Math.Min(18, value));
                SetCenter(_centerLat, _centerLon, _zoomLevel);
            }
        }

        /// <summary>
        /// Convert screen coordinates to latitude/longitude
        /// </summary>
        public (double Lat, double Lon) ScreenToLatLon(double screenX, double screenY)
        {
            int width = (int)mapCanvas.ActualWidth;
            int height = (int)mapCanvas.ActualHeight;
            
            if (width == 0 || height == 0)
            {
                return (_centerLat, _centerLon);
            }
            
            int tileSize = 256;
            double scale = Math.Pow(2, _zoomLevel);
            
            // Convert center lat/lon to tile coordinates
            double centerX = ((_centerLon + 180.0) / 360.0) * scale;
            double centerY = (1.0 - Math.Log(Math.Tan(_centerLat * Math.PI / 180.0) + 
                            1.0 / Math.Cos(_centerLat * Math.PI / 180.0)) / Math.PI) / 2.0 * scale;
            
            // Calculate world coordinates from screen position
            double worldX = centerX + (screenX - width / 2 - _offsetX) / tileSize;
            double worldY = centerY + (screenY - height / 2 - _offsetY) / tileSize;
            
            // Convert world coordinates to lat/lon
            double lon = worldX / scale * 360.0 - 180.0;
            double n = Math.PI - 2.0 * Math.PI * worldY / scale;
            double lat = 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n)));
            
            return (lat, lon);
        }

        /// <summary>
        /// Convert latitude/longitude to screen coordinates (public version)
        /// </summary>
        public (double X, double Y) LatLonToScreenPublic(double lat, double lon)
        {
            int width = (int)mapCanvas.ActualWidth;
            int height = (int)mapCanvas.ActualHeight;
            
            if (width == 0 || height == 0)
            {
                return (width / 2, height / 2);
            }
            
            var point = LatLonToScreen(lat, lon, width, height);
            return (point.X, point.Y);
        }

        // Waypoint management
        private List<WaypointOverlayItem> _waypointOverlayItems = new();
        
        public class WaypointOverlayItem
        {
            public int Sequence { get; set; }
            public double Lat { get; set; }
            public double Lon { get; set; }
            public Border MarkerElement { get; set; } = null!;
        }

        /// <summary>
        /// Add waypoint marker to overlay
        /// </summary>
        public void AddWaypointMarker(int sequence, double lat, double lon)
        {
            // Create marker visual
            var marker = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(20),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                BorderThickness = new Thickness(2),
                Tag = $"waypoint_{sequence}"
            };
            
            var textBlock = new TextBlock
            {
                Text = (sequence + 1).ToString(),
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false // Let parent handle events
            };
            
            marker.Child = textBlock;
            
            // Add drag events
            marker.PointerPressed += WaypointMarker_PointerPressed;
            marker.PointerMoved += WaypointMarker_PointerMoved;
            marker.PointerReleased += WaypointMarker_PointerReleased;
            
            // Position marker
            var (x, y) = LatLonToScreenPublic(lat, lon);
            Canvas.SetLeft(marker, x - 20); // Center the 40px marker
            Canvas.SetTop(marker, y - 20);
            
            // Add to overlay
            waypointOverlay.Children.Add(marker);
            
            // Track in list
            var overlayItem = new WaypointOverlayItem
            {
                Sequence = sequence,
                Lat = lat,
                Lon = lon,
                MarkerElement = marker
            };
            _waypointOverlayItems.Add(overlayItem);
            
            Serilog.Log.Information("[SkiaMapControl] Added waypoint marker {Sequence} at {Lat}, {Lon}", 
                sequence + 1, lat, lon);
        }

        // Drag & Drop support
        private bool _isDraggingWaypoint = false;
        private WaypointOverlayItem? _draggedWaypoint;
        private Windows.Foundation.Point _dragStartPoint;

        public event EventHandler<WaypointDraggedEventArgs>? WaypointDragged;

        public class WaypointDraggedEventArgs : EventArgs
        {
            public int Sequence { get; set; }
            public double NewLat { get; set; }
            public double NewLon { get; set; }
        }

        private void WaypointMarker_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border marker)
            {
                _isDraggingWaypoint = true;
                _dragStartPoint = e.GetCurrentPoint(waypointOverlay).Position;
                _draggedWaypoint = _waypointOverlayItems.FirstOrDefault(w => w.MarkerElement == marker);
                
                marker.CapturePointer(e.Pointer);
                e.Handled = true;
                
                Serilog.Log.Debug("[SkiaMapControl] Started dragging waypoint {Sequence}", 
                    _draggedWaypoint?.Sequence + 1);
            }
        }

        private void WaypointMarker_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_isDraggingWaypoint && _draggedWaypoint != null && sender is Border marker)
            {
                var currentPoint = e.GetCurrentPoint(waypointOverlay).Position;
                
                // Update marker position
                Canvas.SetLeft(marker, currentPoint.X - 20);
                Canvas.SetTop(marker, currentPoint.Y - 20);
                
                // Convert to lat/lon and update data
                var (newLat, newLon) = ScreenToLatLon(currentPoint.X, currentPoint.Y);
                _draggedWaypoint.Lat = newLat;
                _draggedWaypoint.Lon = newLon;
                
                // Trigger redraw for trail update
                RequestRender();
                
                e.Handled = true;
            }
        }

        private void WaypointMarker_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isDraggingWaypoint && _draggedWaypoint != null && sender is Border marker)
            {
                marker.ReleasePointerCapture(e.Pointer);
                
                // Notify about the drag completion
                WaypointDragged?.Invoke(this, new WaypointDraggedEventArgs
                {
                    Sequence = _draggedWaypoint.Sequence,
                    NewLat = _draggedWaypoint.Lat,
                    NewLon = _draggedWaypoint.Lon
                });
                
                Serilog.Log.Information("[SkiaMapControl] Finished dragging waypoint {Sequence} to {Lat}, {Lon}", 
                    _draggedWaypoint.Sequence + 1, _draggedWaypoint.Lat, _draggedWaypoint.Lon);
                
                _isDraggingWaypoint = false;
                _draggedWaypoint = null;
                e.Handled = true;
            }
        }

        /// <summary>
        /// Remove waypoint marker from overlay
        /// </summary>
        public void RemoveWaypointMarker(int sequence)
        {
            var item = _waypointOverlayItems.FirstOrDefault(w => w.Sequence == sequence);
            if (item != null)
            {
                waypointOverlay.Children.Remove(item.MarkerElement);
                _waypointOverlayItems.Remove(item);
            }
        }

        /// <summary>
        /// Clear all waypoint markers
        /// </summary>
        public void ClearWaypointMarkers()
        {
            foreach (var item in _waypointOverlayItems)
            {
                waypointOverlay.Children.Remove(item.MarkerElement);
            }
            _waypointOverlayItems.Clear();
        }

        /// <summary>
        /// Update waypoint marker positions when map moves
        /// </summary>
        public void UpdateWaypointMarkerPositions()
        {
            foreach (var item in _waypointOverlayItems)
            {
                var (x, y) = LatLonToScreenPublic(item.Lat, item.Lon);
                Canvas.SetLeft(item.MarkerElement, x - 20);
                Canvas.SetTop(item.MarkerElement, y - 20);
            }
        }

        // ========== WAYPOINT AND GEOFENCE RENDERING ==========
        
        private List<WaypointMarker> _waypoints = new List<WaypointMarker>();
        private GeofenceData? _geofence;
        
        // Drag and drop state for canvas markers
        private bool _isDraggingCanvasMarker = false;
        private WaypointMarker? _draggedCanvasWaypoint;
        private int _draggedGeofenceVertexIndex = -1;
        private bool _isDraggingGeofenceCenter = false;

        public class WaypointMarker
        {
            public int Sequence { get; set; }
            public double Lat { get; set; }
            public double Lon { get; set; }
            public double Alt { get; set; }
            public string Command { get; set; } = "WP";
        }

        public class GeofenceData
        {
            public bool IsCircular { get; set; }
            public double CenterLat { get; set; }
            public double CenterLon { get; set; }
            public double Radius { get; set; } // meters
            public List<(double Lat, double Lon)> Vertices { get; set; } = new List<(double, double)>();
        }
        
        // Events for drag notifications
        public event EventHandler<WaypointMovedEventArgs>? WaypointMoved;
        public event EventHandler<GeofenceCenterMovedEventArgs>? GeofenceCenterMoved;
        public event EventHandler<GeofenceVertexMovedEventArgs>? GeofenceVertexMoved;
        
        public class WaypointMovedEventArgs : EventArgs
        {
            public int Sequence { get; set; }
            public double NewLat { get; set; }
            public double NewLon { get; set; }
        }
        
        public class GeofenceCenterMovedEventArgs : EventArgs
        {
            public double NewLat { get; set; }
            public double NewLon { get; set; }
        }
        
        public class GeofenceVertexMovedEventArgs : EventArgs
        {
            public int VertexIndex { get; set; }
            public double NewLat { get; set; }
            public double NewLon { get; set; }
        }

        /// <summary>
        /// Get waypoint at screen position (for drag detection)
        /// </summary>
        private WaypointMarker? GetWaypointAtPosition(double screenX, double screenY, int width, int height)
        {
            foreach (var wp in _waypoints)
            {
                var pt = LatLonToScreen(wp.Lat, wp.Lon, width, height);
                double distance = Math.Sqrt(Math.Pow(screenX - pt.X, 2) + Math.Pow(screenY - pt.Y, 2));
                if (distance <= 20) // 20px hit radius (marker is 15px radius + 5px margin)
                {
                    return wp;
                }
            }
            return null;
        }

        /// <summary>
        /// Add waypoint marker to map
        /// </summary>
        public void AddWaypointMarker(int sequence, double lat, double lon, double alt, string command = "WP")
        {
            _waypoints.Add(new WaypointMarker
            {
                Sequence = sequence,
                Lat = lat,
                Lon = lon,
                Alt = alt,
                Command = command
            });
            RequestRender();
        }

        /// <summary>
        /// Clear all waypoint markers
        /// </summary>
        public void ClearWaypoints()
        {
            _waypoints.Clear();
            RequestRender();
        }

        /// <summary>
        /// Set geofence to display
        /// </summary>
        public void SetGeofence(bool isCircular, double centerLat, double centerLon, double radius, List<(double, double)>? vertices = null)
        {
            _geofence = new GeofenceData
            {
                IsCircular = isCircular,
                CenterLat = centerLat,
                CenterLon = centerLon,
                Radius = radius,
                Vertices = vertices ?? new List<(double, double)>()
            };
            RequestRender();
        }

        /// <summary>
        /// Clear geofence
        /// </summary>
        public void ClearGeofence()
        {
            _geofence = null;
            RequestRender();
        }

        /// <summary>
        /// Draw waypoint markers on map
        /// </summary>
        private void DrawWaypointMarkers(SKCanvas canvas, int width, int height)
        {
            if (_waypoints.Count == 0) return;

            // Gambar lingkaran geofence 200m di setiap waypoint
            const double waypointGeofenceMeters = 200.0;
            using (var gfPaint = new SKPaint
            {
                IsAntialias = true,
                Style       = SKPaintStyle.Fill,
                Color       = new SKColor(255, 80, 80, 40),   // merah transparan
            })
            using (var gfStroke = new SKPaint
            {
                IsAntialias  = true,
                Style        = SKPaintStyle.Stroke,
                StrokeWidth  = 1.5f,
                Color        = new SKColor(220, 60, 60, 120),
                PathEffect   = SKPathEffect.CreateDash(new float[] { 6, 4 }, 0),
            })
            {
                foreach (var wp in _waypoints)
                {
                    var pt = LatLonToScreen(wp.Lat, wp.Lon, width, height);
                    double cosLat = Math.Cos(wp.Lat * Math.PI / 180.0);
                    double metersPerPixel = (40075016.0 * cosLat) / (256.0 * Math.Pow(2, _zoomLevel));
                    float radiusPx = (float)(waypointGeofenceMeters / metersPerPixel);
                    canvas.DrawCircle(pt.X, pt.Y, radiusPx, gfPaint);
                    canvas.DrawCircle(pt.X, pt.Y, radiusPx, gfStroke);
                }
            }

            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };

            using var strokePaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2,
                Color = SKColors.White
            };

            using var textPaint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.White,
                TextSize = 14,
                TextAlign = SKTextAlign.Center,
                Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
            };

            // Draw lines connecting waypoints
            if (_waypoints.Count > 1)
            {
                using var linePaint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 3,
                    Color = new SKColor(0, 150, 255, 200), // Blue with transparency
                    PathEffect = SKPathEffect.CreateDash(new float[] { 10, 5 }, 0)
                };

                for (int i = 0; i < _waypoints.Count - 1; i++)
                {
                    var wp1 = _waypoints[i];
                    var wp2 = _waypoints[i + 1];
                    
                    var pt1 = LatLonToScreen(wp1.Lat, wp1.Lon, width, height);
                    var pt2 = LatLonToScreen(wp2.Lat, wp2.Lon, width, height);
                    
                    canvas.DrawLine(pt1, pt2, linePaint);
                }
            }

            // Draw waypoint markers
            foreach (var wp in _waypoints)
            {
                var pt = LatLonToScreen(wp.Lat, wp.Lon, width, height);
                
                // Skip if off-screen
                if (pt.X < -50 || pt.X > width + 50 || pt.Y < -50 || pt.Y > height + 50)
                    continue;

                // Check if this waypoint is being dragged
                bool isDragging = _draggedCanvasWaypoint == wp;
                float markerRadius = isDragging ? 18 : 15; // Larger when dragging

                // Draw marker circle
                if (isDragging)
                {
                    // Highlight when dragging
                    paint.Color = new SKColor(255, 255, 0); // Yellow
                }
                else
                {
                    paint.Color = wp.Command == "LAND" ? new SKColor(255, 0, 0) : 
                                 wp.Command == "TAKEOFF" ? new SKColor(0, 255, 0) :
                                 new SKColor(255, 165, 0); // Orange for waypoints
                }
                
                canvas.DrawCircle(pt.X, pt.Y, markerRadius, paint);
                
                // Draw thicker border when dragging
                strokePaint.StrokeWidth = isDragging ? 3 : 2;
                canvas.DrawCircle(pt.X, pt.Y, markerRadius, strokePaint);
                
                // Draw sequence number
                canvas.DrawText(wp.Sequence.ToString(), pt.X, pt.Y + 5, textPaint);
            }
        }

        /// <summary>
        /// Draw geofence on map
        /// </summary>
        private void DrawGeofence(SKCanvas canvas, int width, int height)
        {
            if (_geofence == null) return;

            using var fillPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = new SKColor(255, 0, 0, 50) // Red with low transparency
            };

            using var strokePaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3,
                Color = new SKColor(255, 0, 0, 200) // Red with transparency
            };

            if (_geofence.IsCircular)
            {
                // Draw circular geofence
                var center = LatLonToScreen(_geofence.CenterLat, _geofence.CenterLon, width, height);
                
                // Calculate radius in pixels (approximate)
                double metersPerPixel = 156543.03392 * Math.Cos(_geofence.CenterLat * Math.PI / 180) / Math.Pow(2, _zoomLevel);
                float radiusPixels = (float)(_geofence.Radius / metersPerPixel);
                
                canvas.DrawCircle(center.X, center.Y, radiusPixels, fillPaint);
                canvas.DrawCircle(center.X, center.Y, radiusPixels, strokePaint);
                
                // Draw center marker (draggable)
                using var centerPaint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill,
                    Color = _isDraggingGeofenceCenter ? new SKColor(255, 255, 0) : new SKColor(255, 0, 0) // Yellow when dragging
                };
                
                using var centerStrokePaint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = _isDraggingGeofenceCenter ? 3 : 2,
                    Color = SKColors.White
                };
                
                float centerRadius = _isDraggingGeofenceCenter ? 12 : 10;
                canvas.DrawCircle(center.X, center.Y, centerRadius, centerPaint);
                canvas.DrawCircle(center.X, center.Y, centerRadius, centerStrokePaint);
                
                // Draw crosshair on center
                using var crosshairPaint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2,
                    Color = SKColors.White
                };
                canvas.DrawLine(center.X - 6, center.Y, center.X + 6, center.Y, crosshairPaint);
                canvas.DrawLine(center.X, center.Y - 6, center.X, center.Y + 6, crosshairPaint);
            }
            else if (_geofence.Vertices.Count >= 3)
            {
                // Draw polygon geofence
                using var path = new SKPath();
                
                var firstPt = LatLonToScreen(_geofence.Vertices[0].Lat, _geofence.Vertices[0].Lon, width, height);
                path.MoveTo(firstPt);
                
                for (int i = 1; i < _geofence.Vertices.Count; i++)
                {
                    var pt = LatLonToScreen(_geofence.Vertices[i].Lat, _geofence.Vertices[i].Lon, width, height);
                    path.LineTo(pt);
                }
                
                path.Close();
                
                canvas.DrawPath(path, fillPaint);
                canvas.DrawPath(path, strokePaint);
                
                // Draw vertex markers (draggable)
                using var vertexPaint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Fill
                };
                
                using var vertexStrokePaint = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2,
                    Color = SKColors.White
                };
                
                for (int i = 0; i < _geofence.Vertices.Count; i++)
                {
                    var vertex = _geofence.Vertices[i];
                    var pt = LatLonToScreen(vertex.Lat, vertex.Lon, width, height);
                    
                    bool isDragging = _draggedGeofenceVertexIndex == i;
                    float vertexRadius = isDragging ? 10 : 8;
                    
                    vertexPaint.Color = isDragging ? new SKColor(255, 255, 0) : new SKColor(255, 0, 0);
                    vertexStrokePaint.StrokeWidth = isDragging ? 3 : 2;
                    
                    canvas.DrawCircle(pt.X, pt.Y, vertexRadius, vertexPaint);
                    canvas.DrawCircle(pt.X, pt.Y, vertexRadius, vertexStrokePaint);
                }
            }
        }
    }
}
