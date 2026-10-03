using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenCvSharp;
using HarvestmoonGCS.Core.Models;

namespace HarvestmoonGCS.Core.Helpers
{
    /// <summary>
    /// Unified Vegetation + YOLO Analyzer for drought and irrigation deficit detection
    /// Combines YOLO object detection with HSV color analysis for comprehensive vegetation analysis
    /// </summary>
    public class VegetationYoloAnalyzer : IDisposable
    {
        #region Enums and Classes

        /// <summary>
        /// Drought severity levels based on visual indicators
        /// </summary>
        public enum DroughtSeverity
        {
            None,       // Healthy vegetation
            Mild,       // Early stress signs (yellow tint)
            Moderate,   // Visible stress (brown patches)
            Severe,     // Heavy drought (mostly brown)
            Critical    // Soil cracks visible
        }

        /// <summary>
        /// Vegetation class detected by YOLO (4 classes custom model)
        /// </summary>
        public enum VegetationClass
        {
            GreenHealthy = 0,   // Class 0: green_healthy - Hijau tua, daun segar
            YellowStress = 1,   // Class 1: yellow_stress - Kuning/hijau pucat
            BrownDrought = 2,   // Class 2: brown_drought - Coklat kering
            SoilCrack = 3,      // Class 3: soil_crack - Retakan tanah
            Unknown = -1
        }

        /// <summary>
        /// Zone analysis result combining YOLO + HSV
        /// </summary>
        public class ZoneResult
        {
            public int Row { get; set; }
            public int Col { get; set; }
            public Rect BoundingBox { get; set; }
            
            // HSV Analysis Results
            public double HealthyPercentage { get; set; }
            public double StressedPercentage { get; set; }
            public double DroughtPercentage { get; set; }
            public double BareSoilPercentage { get; set; }
            
            // YOLO Detection Results
            public VegetationClass DominantClass { get; set; }
            public List<DetectionResult> Detections { get; set; } = new List<DetectionResult>();
            
            // Combined Analysis
            public DroughtSeverity Severity { get; set; }
            public int IrrigationPriority { get; set; } // 1 = highest priority
            public string Recommendation { get; set; }
        }

        /// <summary>
        /// Overall statistics for the analyzed frame
        /// </summary>
        public class AnalysisStatistics
        {
            public DateTime Timestamp { get; set; }
            public int TotalZones { get; set; }
            
            // Percentages
            public double TotalHealthyPercentage { get; set; }
            public double TotalStressedPercentage { get; set; }
            public double TotalDroughtPercentage { get; set; }
            public double TotalBareSoilPercentage { get; set; }
            
            // Zone counts by severity
            public int HealthyZoneCount { get; set; }
            public int MildStressCount { get; set; }
            public int ModerateStressCount { get; set; }
            public int SevereStressCount { get; set; }
            public int CriticalZoneCount { get; set; }
            
            // YOLO detections
            public int YoloDetectionCount { get; set; }
            public int SoilCrackDetections { get; set; }
            
            // Overall severity
            public DroughtSeverity OverallSeverity { get; set; }
        }

        /// <summary>
        /// Irrigation priority recommendation
        /// </summary>
        public class IrrigationPriority
        {
            public int Priority { get; set; } // 1 = most urgent
            public int ZoneRow { get; set; }
            public int ZoneCol { get; set; }
            public DroughtSeverity Severity { get; set; }
            public string Reason { get; set; }
            public double Latitude { get; set; }
            public double Longitude { get; set; }
            public DateTime Timestamp { get; set; }
        }

        #endregion

        #region Private Fields

        // YOLO Detector
        private YoloDetector _yoloDetector;
        private bool _isYoloInitialized = false;
        private bool _useYolo = true;

        // Grid configuration
        private int _gridRows = 4;
        private int _gridCols = 4;

        // HSV ranges for vegetation classification
        private Scalar _healthyLower = new Scalar(35, 40, 40);
        private Scalar _healthyUpper = new Scalar(85, 255, 255);
        private Scalar _stressedLower = new Scalar(15, 30, 40);   // Yellow/pale green
        private Scalar _stressedUpper = new Scalar(35, 255, 255);
        private Scalar _droughtLower = new Scalar(5, 30, 40);      // Brown/tan
        private Scalar _droughtUpper = new Scalar(20, 255, 200);
        private Scalar _soilLower = new Scalar(0, 10, 30);         // Bare soil
        private Scalar _soilUpper = new Scalar(20, 100, 200);

        // Heatmap colors (BGR format)
        private readonly Scalar _colorHealthy = new Scalar(0, 200, 0);       // Green
        private readonly Scalar _colorMild = new Scalar(0, 255, 255);        // Yellow
        private readonly Scalar _colorModerate = new Scalar(0, 165, 255);    // Orange
        private readonly Scalar _colorSevere = new Scalar(0, 0, 255);        // Red
        private readonly Scalar _colorCritical = new Scalar(128, 0, 128);    // Purple (soil crack)
        private readonly Scalar _colorSoil = new Scalar(42, 42, 128);        // Brown

        // Frame skip for performance
        private int _frameSkipCount = 0;
        private int _frameSkipInterval = 3;

        // Waypoint storage
        private List<IrrigationPriority> _irrigationWaypoints = new List<IrrigationPriority>();

        // Cache for last analysis
        private List<ZoneResult> _lastZoneResults;
        private AnalysisStatistics _lastStatistics;

        #endregion

        #region Properties

        public int GridRows
        {
            get => _gridRows;
            set => _gridRows = Math.Max(2, Math.Min(12, value));
        }

        public int GridCols
        {
            get => _gridCols;
            set => _gridCols = Math.Max(2, Math.Min(12, value));
        }

        public bool UseYolo
        {
            get => _useYolo && _isYoloInitialized;
            set => _useYolo = value;
        }

        public bool IsYoloInitialized => _isYoloInitialized;

        /// <summary>
        /// Direct access to the underlying YOLO detector so callers can run
        /// ad-hoc inferences (e.g. for a live preview overlay) without going
        /// through the full zone analysis pipeline.
        /// </summary>
        public YoloDetector Detector => _yoloDetector;

        public List<IrrigationPriority> IrrigationWaypoints => _irrigationWaypoints;
        public int WaypointCount => _irrigationWaypoints.Count;

        public List<ZoneResult> LastZoneResults => _lastZoneResults;
        public AnalysisStatistics LastStatistics => _lastStatistics;

        #endregion

        #region Initialization

        /// <summary>
        /// Initialize the analyzer with optional YOLO model
        /// </summary>
        /// <param name="modelPath">Path to YOLO .onnx model (optional)</param>
        /// <param name="classNamesPath">Path to class names file (optional)</param>
        public bool Initialize(string modelPath = null, string classNamesPath = null)
        {
            try
            {
                ConfigureOpenCvRuntime();

                // Reset existing state so re-initialisation after a failure actually retries.
                if (_yoloDetector != null)
                {
                    try { _yoloDetector.Dispose(); } catch { }
                    _yoloDetector = null;
                }
                _isYoloInitialized = false;

                // Try to initialize YOLO if paths provided
                if (!string.IsNullOrEmpty(modelPath) && !string.IsNullOrEmpty(classNamesPath))
                {
                    if (File.Exists(modelPath) && File.Exists(classNamesPath))
                    {
                        _yoloDetector = new YoloDetector();
                        _isYoloInitialized = _yoloDetector.Initialize(modelPath, classNamesPath, useCuda: true);

                        if (_isYoloInitialized)
                        {
                            Serilog.Log.Information("[VegetationYoloAnalyzer] YOLO initialized successfully from {Model}", modelPath);
                        }
                        else
                        {
                            Serilog.Log.Warning("[VegetationYoloAnalyzer] YoloDetector.Initialize returned false for {Model}", modelPath);
                        }
                    }
                    else
                    {
                        Serilog.Log.Warning("[VegetationYoloAnalyzer] Model or class file missing: model={Model} classes={Classes}", modelPath, classNamesPath);
                    }
                }

                Serilog.Log.Information("[VegetationYoloAnalyzer] Initialized. YOLO ready: {Ready}", _isYoloInitialized);
                return true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[VegetationYoloAnalyzer] Init error");
                return false;
            }
        }

        private static void ConfigureOpenCvRuntime()
        {
            try
            {
                Cv2.SetUseOptimized(true);
#if __ANDROID__
                Cv2.SetNumThreads(Math.Max(1, Math.Min(2, Environment.ProcessorCount / 2)));
#else
                Cv2.SetNumThreads(Math.Max(1, Environment.ProcessorCount));
#endif
            }
            catch (Exception ex)
            {
                Serilog.Log.Information("[VegetationYoloAnalyzer] Native OpenCVSharp runtime skipped ({Message}); vision stream driven by Python .venv-yolo.", ex.Message);
            }
        }

        #endregion

        #region Main Analysis Methods

        /// <summary>
        /// Analyze frame using hybrid YOLO + HSV approach
        /// </summary>
        public List<ZoneResult> AnalyzeFrame(Mat frame, bool forceAnalyze = false)
        {
            _frameSkipCount++;

            // Skip frames for performance unless forced
            if (!forceAnalyze && _frameSkipCount % _frameSkipInterval != 0)
                return _lastZoneResults;

            return PerformAnalysis(frame);
        }

        /// <summary>
        /// Perform complete analysis on frame
        /// </summary>
        private List<ZoneResult> PerformAnalysis(Mat frame)
        {
            var results = new List<ZoneResult>();

            if (frame == null || frame.Empty())
                return results;

            try
            {
                // Adjust HSV ranges based on illumination
                AdjustForIllumination(frame);

                // Run YOLO detection if available
                List<DetectionResult> yoloDetections = null;
                if (_useYolo && _isYoloInitialized && _yoloDetector != null)
                {
                    yoloDetections = _yoloDetector.Detect(frame);
                }

                int zoneWidth = frame.Width / _gridCols;
                int zoneHeight = frame.Height / _gridRows;

                for (int row = 0; row < _gridRows; row++)
                {
                    for (int col = 0; col < _gridCols; col++)
                    {
                        int x = col * zoneWidth;
                        int y = row * zoneHeight;
                        int w = (col == _gridCols - 1) ? frame.Width - x : zoneWidth;
                        int h = (row == _gridRows - 1) ? frame.Height - y : zoneHeight;

                        var roi = new Rect(x, y, w, h);

                        using (var zoneFrame = new Mat(frame, roi))
                        {
                            var zoneResult = AnalyzeZone(zoneFrame, row, col, roi, yoloDetections);
                            results.Add(zoneResult);
                        }
                    }
                }

                // Calculate irrigation priorities
                AssignIrrigationPriorities(results);

                _lastZoneResults = results;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VegetationYoloAnalyzer] Analysis error: {ex.Message}");
            }

            return results;
        }

        /// <summary>
        /// Analyze individual zone with HSV + YOLO
        /// </summary>
        private ZoneResult AnalyzeZone(Mat zoneFrame, int row, int col, Rect boundingBox, List<DetectionResult> allDetections)
        {
            var result = new ZoneResult
            {
                Row = row,
                Col = col,
                BoundingBox = boundingBox,
                DominantClass = VegetationClass.Unknown,
                Severity = DroughtSeverity.None
            };

            if (zoneFrame == null || zoneFrame.Empty())
                return result;

            try
            {
                // === HSV Color Analysis ===
                using (var hsv = new Mat())
                using (var healthyMask = new Mat())
                using (var stressedMask = new Mat())
                using (var droughtMask = new Mat())
                using (var soilMask = new Mat())
                {
                    Cv2.CvtColor(zoneFrame, hsv, ColorConversionCodes.BGR2HSV);

                    Cv2.InRange(hsv, _healthyLower, _healthyUpper, healthyMask);
                    Cv2.InRange(hsv, _stressedLower, _stressedUpper, stressedMask);
                    Cv2.InRange(hsv, _droughtLower, _droughtUpper, droughtMask);
                    Cv2.InRange(hsv, _soilLower, _soilUpper, soilMask);

                    int totalPixels = zoneFrame.Width * zoneFrame.Height;
                    int healthyPixels = Cv2.CountNonZero(healthyMask);
                    int stressedPixels = Cv2.CountNonZero(stressedMask);
                    int droughtPixels = Cv2.CountNonZero(droughtMask);
                    int soilPixels = Cv2.CountNonZero(soilMask);

                    result.HealthyPercentage = (healthyPixels * 100.0) / totalPixels;
                    result.StressedPercentage = (stressedPixels * 100.0) / totalPixels;
                    result.DroughtPercentage = (droughtPixels * 100.0) / totalPixels;
                    result.BareSoilPercentage = (soilPixels * 100.0) / totalPixels;
                }

                // === YOLO Detections in this zone ===
                if (allDetections != null)
                {
                    foreach (var det in allDetections)
                    {
                        // Check if detection overlaps with this zone
                        var intersection = boundingBox & det.BoundingBox;
                        if (intersection.Width > 0 && intersection.Height > 0)
                        {
                            result.Detections.Add(det);
                        }
                    }

                    // Determine dominant YOLO class in zone
                    if (result.Detections.Count > 0)
                    {
                        var dominantDet = result.Detections
                            .GroupBy(d => d.ClassId)
                            .OrderByDescending(g => g.Count())
                            .First();
                        
                        result.DominantClass = MapYoloClass(dominantDet.Key);
                    }
                }

                // === Determine Severity (combine HSV + YOLO) ===
                result.Severity = CalculateSeverity(result);
                result.Recommendation = GetZoneRecommendation(result);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VegetationYoloAnalyzer] Zone analysis error: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Calculate drought severity from combined analysis
        /// </summary>
        private DroughtSeverity CalculateSeverity(ZoneResult zone)
        {
            // If YOLO detected soil cracks, it's critical
            if (zone.DominantClass == VegetationClass.SoilCrack || 
                zone.Detections.Any(d => d.ClassId == (int)VegetationClass.SoilCrack))
            {
                return DroughtSeverity.Critical;
            }

            // If YOLO detected brown drought
            if (zone.DominantClass == VegetationClass.BrownDrought)
            {
                return DroughtSeverity.Severe;
            }

            // If YOLO detected yellow stress
            if (zone.DominantClass == VegetationClass.YellowStress)
            {
                return zone.DroughtPercentage > 20 ? DroughtSeverity.Moderate : DroughtSeverity.Mild;
            }

            // Fallback to HSV analysis
            double stressScore = zone.StressedPercentage + zone.DroughtPercentage + zone.BareSoilPercentage;

            if (zone.BareSoilPercentage > 50)
                return DroughtSeverity.Critical;
            else if (zone.DroughtPercentage > 40 || stressScore > 60)
                return DroughtSeverity.Severe;
            else if (zone.DroughtPercentage > 20 || stressScore > 40)
                return DroughtSeverity.Moderate;
            else if (zone.StressedPercentage > 20 || stressScore > 20)
                return DroughtSeverity.Mild;

            return DroughtSeverity.None;
        }

        /// <summary>
        /// Get recommendation for zone
        /// </summary>
        private string GetZoneRecommendation(ZoneResult zone)
        {
            switch (zone.Severity)
            {
                case DroughtSeverity.Critical:
                    return "🚨 KRITIS: Irigasi segera! Retakan tanah terdeteksi.";
                case DroughtSeverity.Severe:
                    return "🔴 PARAH: Irigasi dalam 24 jam!";
                case DroughtSeverity.Moderate:
                    return "🟠 SEDANG: Jadwalkan irigasi 1-2 hari.";
                case DroughtSeverity.Mild:
                    return "🟡 RINGAN: Pantau dan irigasi 3-5 hari.";
                default:
                    return "🟢 SEHAT: Irigasi normal.";
            }
        }

        /// <summary>
        /// Map YOLO class ID to VegetationClass enum
        /// </summary>
        private VegetationClass MapYoloClass(int classId)
        {
            switch (classId)
            {
                case 0: return VegetationClass.GreenHealthy;
                case 1: return VegetationClass.YellowStress;
                case 2: return VegetationClass.BrownDrought;
                case 3: return VegetationClass.SoilCrack;
                default: return VegetationClass.Unknown;
            }
        }

        /// <summary>
        /// Assign irrigation priorities to zones
        /// </summary>
        private void AssignIrrigationPriorities(List<ZoneResult> zones)
        {
            // Sort by severity (Critical first) then by stress percentage
            var sortedZones = zones
                .OrderByDescending(z => (int)z.Severity)
                .ThenByDescending(z => z.DroughtPercentage + z.StressedPercentage)
                .ToList();

            int priority = 1;
            foreach (var zone in sortedZones)
            {
                if (zone.Severity != DroughtSeverity.None)
                {
                    zone.IrrigationPriority = priority++;
                }
                else
                {
                    zone.IrrigationPriority = 0; // No irrigation needed
                }
            }
        }

        #endregion

        #region Statistics

        /// <summary>
        /// Calculate overall statistics from zone results
        /// </summary>
        public AnalysisStatistics CalculateStatistics(List<ZoneResult> zones)
        {
            var stats = new AnalysisStatistics { Timestamp = DateTime.Now };

            if (zones == null || zones.Count == 0)
                return stats;

            stats.TotalZones = zones.Count;

            double totalHealthy = 0, totalStressed = 0, totalDrought = 0, totalSoil = 0;

            foreach (var zone in zones)
            {
                totalHealthy += zone.HealthyPercentage;
                totalStressed += zone.StressedPercentage;
                totalDrought += zone.DroughtPercentage;
                totalSoil += zone.BareSoilPercentage;

                switch (zone.Severity)
                {
                    case DroughtSeverity.None: stats.HealthyZoneCount++; break;
                    case DroughtSeverity.Mild: stats.MildStressCount++; break;
                    case DroughtSeverity.Moderate: stats.ModerateStressCount++; break;
                    case DroughtSeverity.Severe: stats.SevereStressCount++; break;
                    case DroughtSeverity.Critical: stats.CriticalZoneCount++; break;
                }

                stats.YoloDetectionCount += zone.Detections.Count;
                stats.SoilCrackDetections += zone.Detections.Count(d => d.ClassId == (int)VegetationClass.SoilCrack);
            }

            stats.TotalHealthyPercentage = totalHealthy / zones.Count;
            stats.TotalStressedPercentage = totalStressed / zones.Count;
            stats.TotalDroughtPercentage = totalDrought / zones.Count;
            stats.TotalBareSoilPercentage = totalSoil / zones.Count;

            // Determine overall severity
            if (stats.CriticalZoneCount > 0)
                stats.OverallSeverity = DroughtSeverity.Critical;
            else if (stats.SevereStressCount > zones.Count / 4)
                stats.OverallSeverity = DroughtSeverity.Severe;
            else if (stats.ModerateStressCount > zones.Count / 3)
                stats.OverallSeverity = DroughtSeverity.Moderate;
            else if (stats.MildStressCount > zones.Count / 2)
                stats.OverallSeverity = DroughtSeverity.Mild;
            else
                stats.OverallSeverity = DroughtSeverity.None;

            _lastStatistics = stats;
            return stats;
        }

        #endregion

        #region Visualization

        /// <summary>
        /// Generate heatmap overlay with legend
        /// </summary>
        public Mat GenerateHeatmapOverlay(Mat frame, List<ZoneResult> zones, double opacity = 0.4)
        {
            if (frame == null || frame.Empty() || zones == null || zones.Count == 0)
                return frame?.Clone();

            var overlay = frame.Clone();

            using (var heatmap = new Mat(frame.Size(), frame.Type(), Scalar.All(0)))
            {
                foreach (var zone in zones)
                {
                    Scalar color = GetSeverityColor(zone.Severity);
                    Cv2.Rectangle(heatmap, zone.BoundingBox, color, -1);
                }

                Cv2.AddWeighted(frame, 1 - opacity, heatmap, opacity, 0, overlay);
            }

            // Draw grid lines, labels, and priorities
            foreach (var zone in zones)
            {
                // White border
                Cv2.Rectangle(overlay, zone.BoundingBox, new Scalar(255, 255, 255), 1);

                // Severity label
                string label = GetSeverityLabel(zone.Severity, zone.IrrigationPriority);
                var textPos = new Point(zone.BoundingBox.X + 3, zone.BoundingBox.Y + 15);

                // Text shadow
                Cv2.PutText(overlay, label, new Point(textPos.X + 1, textPos.Y + 1),
                    HersheyFonts.HersheySimplex, 0.35, new Scalar(0, 0, 0), 1);
                Cv2.PutText(overlay, label, textPos,
                    HersheyFonts.HersheySimplex, 0.35, new Scalar(255, 255, 255), 1);

                // Draw YOLO bounding boxes
                foreach (var det in zone.Detections)
                {
                    Scalar detColor = GetVegetationClassColor(det.ClassId);
                    Cv2.Rectangle(overlay, det.BoundingBox, detColor, 2);
                }
            }

            // Draw legend
            DrawLegend(overlay);

            return overlay;
        }

        /// <summary>
        /// Get color for severity level
        /// </summary>
        private Scalar GetSeverityColor(DroughtSeverity severity)
        {
            switch (severity)
            {
                case DroughtSeverity.None: return _colorHealthy;
                case DroughtSeverity.Mild: return _colorMild;
                case DroughtSeverity.Moderate: return _colorModerate;
                case DroughtSeverity.Severe: return _colorSevere;
                case DroughtSeverity.Critical: return _colorCritical;
                default: return _colorSoil;
            }
        }

        /// <summary>
        /// Get label for zone
        /// </summary>
        private string GetSeverityLabel(DroughtSeverity severity, int priority)
        {
            string severityChar;
            switch (severity)
            {
                case DroughtSeverity.None:
                    severityChar = "OK";
                    break;
                case DroughtSeverity.Mild:
                    severityChar = "M";
                    break;
                case DroughtSeverity.Moderate:
                    severityChar = "MD";
                    break;
                case DroughtSeverity.Severe:
                    severityChar = "S";
                    break;
                case DroughtSeverity.Critical:
                    severityChar = "!";
                    break;
                default:
                    severityChar = "?";
                    break;
            }

            return priority > 0 ? $"P{priority}:{severityChar}" : severityChar;
        }

        /// <summary>
        /// Get color for vegetation class
        /// </summary>
        private Scalar GetVegetationClassColor(int classId)
        {
            switch (classId)
            {
                case 0: return new Scalar(0, 255, 0);     // Green - healthy
                case 1: return new Scalar(0, 255, 255);   // Yellow - stress
                case 2: return new Scalar(0, 128, 255);   // Orange - drought
                case 3: return new Scalar(128, 0, 255);   // Purple - soil crack
                default: return new Scalar(128, 128, 128);
            }
        }

        /// <summary>
        /// Draw legend on frame
        /// </summary>
        private void DrawLegend(Mat frame)
        {
            int legendX = 10;
            int legendY = frame.Height - 100;
            int boxSize = 12;
            int lineHeight = 16;

            // Background
            Cv2.Rectangle(frame, new Rect(legendX - 5, legendY - 5, 130, 95), 
                new Scalar(0, 0, 0), -1);
            Cv2.Rectangle(frame, new Rect(legendX - 5, legendY - 5, 130, 95), 
                new Scalar(255, 255, 255), 1);

            // Legend items
            DrawLegendItem(frame, legendX, legendY, boxSize, _colorHealthy, "Sehat");
            DrawLegendItem(frame, legendX, legendY + lineHeight, boxSize, _colorMild, "Ringan");
            DrawLegendItem(frame, legendX, legendY + lineHeight * 2, boxSize, _colorModerate, "Sedang");
            DrawLegendItem(frame, legendX, legendY + lineHeight * 3, boxSize, _colorSevere, "Parah");
            DrawLegendItem(frame, legendX, legendY + lineHeight * 4, boxSize, _colorCritical, "Kritis");
        }

        private void DrawLegendItem(Mat frame, int x, int y, int size, Scalar color, string text)
        {
            Cv2.Rectangle(frame, new Rect(x, y, size, size), color, -1);
            Cv2.Rectangle(frame, new Rect(x, y, size, size), new Scalar(255, 255, 255), 1);
            Cv2.PutText(frame, text, new Point(x + size + 5, y + size - 2),
                HersheyFonts.HersheySimplex, 0.4, new Scalar(255, 255, 255), 1);
        }

        #endregion

        #region GPS Tagging

        /// <summary>
        /// Tag zones needing irrigation with GPS coordinates
        /// </summary>
        public int TagIrrigationZones(List<ZoneResult> zones, double latitude, double longitude, double altitude)
        {
            if (zones == null || latitude == 0 || longitude == 0) return 0;

            int taggedCount = 0;

            foreach (var zone in zones)
            {
                if (zone.Severity >= DroughtSeverity.Moderate)
                {
                    // Check if similar waypoint already exists
                    bool exists = _irrigationWaypoints.Any(w =>
                        w.ZoneRow == zone.Row &&
                        w.ZoneCol == zone.Col &&
                        Math.Abs(w.Latitude - latitude) < 0.00001 &&
                        Math.Abs(w.Longitude - longitude) < 0.00001);

                    if (!exists)
                    {
                        _irrigationWaypoints.Add(new IrrigationPriority
                        {
                            Priority = zone.IrrigationPriority,
                            ZoneRow = zone.Row,
                            ZoneCol = zone.Col,
                            Severity = zone.Severity,
                            Reason = zone.Recommendation,
                            Latitude = latitude,
                            Longitude = longitude,
                            Timestamp = DateTime.Now
                        });
                        taggedCount++;
                    }
                }
            }

            // Re-sort by priority
            _irrigationWaypoints = _irrigationWaypoints
                .OrderBy(w => w.Priority)
                .ThenByDescending(w => (int)w.Severity)
                .ToList();

            return taggedCount;
        }

        /// <summary>
        /// Clear all waypoints
        /// </summary>
        public void ClearWaypoints()
        {
            _irrigationWaypoints.Clear();
        }

        /// <summary>
        /// Export irrigation waypoints to CSV
        /// </summary>
        public bool ExportWaypointsToCSV(string filePath)
        {
            try
            {
                using (var writer = new StreamWriter(filePath))
                {
                    writer.WriteLine("Priority,Timestamp,Latitude,Longitude,ZoneRow,ZoneCol,Severity,Reason");

                    foreach (var wp in _irrigationWaypoints)
                    {
                        writer.WriteLine($"{wp.Priority},{wp.Timestamp:yyyy-MM-dd HH:mm:ss}," +
                            $"{wp.Latitude:F8},{wp.Longitude:F8}," +
                            $"{wp.ZoneRow},{wp.ZoneCol},{wp.Severity},\"{wp.Reason}\"");
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[VegetationYoloAnalyzer] Exported {_irrigationWaypoints.Count} waypoints");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VegetationYoloAnalyzer] Export error: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Recommendations

        /// <summary>
        /// Get overall irrigation recommendations
        /// </summary>
        public List<string> GetIrrigationRecommendations(AnalysisStatistics stats)
        {
            var recommendations = new List<string>();

            if (stats == null) return recommendations;

            // Overall status
            switch (stats.OverallSeverity)
            {
                case DroughtSeverity.Critical:
                    recommendations.Add("🚨 DARURAT: Terdeteksi retakan tanah! Irigasi segera diperlukan.");
                    break;
                case DroughtSeverity.Severe:
                    recommendations.Add("🔴 KRITIS: >25% area mengalami kekeringan parah. Prioritaskan irigasi!");
                    break;
                case DroughtSeverity.Moderate:
                    recommendations.Add("🟠 PERINGATAN: Area stress signifikan. Jadwalkan irigasi 1-2 hari.");
                    break;
                case DroughtSeverity.Mild:
                    recommendations.Add("🟡 PERHATIAN: Tanda awal stress terdeteksi. Pantau dan persiapkan irigasi.");
                    break;
                default:
                    recommendations.Add("🟢 NORMAL: Vegetasi mayoritas sehat.");
                    break;
            }

            // Specific stats
            if (stats.CriticalZoneCount > 0)
                recommendations.Add($"📍 {stats.CriticalZoneCount} zona kritis dengan retakan tanah");
            
            if (stats.SevereStressCount > 0)
                recommendations.Add($"🔴 {stats.SevereStressCount} zona kekeringan parah");

            if (stats.SoilCrackDetections > 0)
                recommendations.Add($"⚠️ {stats.SoilCrackDetections} deteksi retakan tanah (YOLO)");

            if (_irrigationWaypoints.Count > 0)
                recommendations.Add($"📍 {_irrigationWaypoints.Count} titik irigasi di-tag GPS");

            return recommendations;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Adjust HSV ranges based on illumination
        /// </summary>
        private void AdjustForIllumination(Mat frame)
        {
            try
            {
                using (var hsv = new Mat())
                {
                    Cv2.CvtColor(frame, hsv, ColorConversionCodes.BGR2HSV);
                    var channels = Cv2.Split(hsv);
                    double vMean = Cv2.Mean(channels[2]).Val0;
                    foreach (var ch in channels) ch.Dispose();

                    if (vMean < 60) // Dark
                    {
                        _healthyLower = new Scalar(30, 50, 30);
                        _healthyUpper = new Scalar(90, 255, 200);
                    }
                    else if (vMean > 200) // Bright
                    {
                        _healthyLower = new Scalar(35, 30, 100);
                        _healthyUpper = new Scalar(85, 255, 255);
                    }
                    else // Normal
                    {
                        _healthyLower = new Scalar(35, 40, 40);
                        _healthyUpper = new Scalar(85, 255, 255);
                    }
                }
            }
            catch { /* Use default values */ }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            _yoloDetector?.Dispose();
            _irrigationWaypoints.Clear();
        }

        #endregion
    }
}
