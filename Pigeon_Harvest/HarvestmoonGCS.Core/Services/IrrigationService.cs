using System;
using System.Collections.Generic;
using System.Linq;
using HarvestmoonGCS.Core.Helpers;
using HarvestmoonGCS.Core.Models;
using Microsoft.Extensions.Logging;

namespace HarvestmoonGCS.Core.Services;

/// <summary>
/// Service untuk menganalisis data spasial irigasi dan defisit air
/// berdasarkan deteksi citra UAV dan data sensor tanah (Ground Truth).
/// Sesuai dengan roadmap riset PDP.
/// </summary>
public class IrrigationService : IIrrigationService
{
    private readonly ILogger<IrrigationService>? _logger;
    private readonly List<IrrigationDataPoint> _dataPoints = new();
    private readonly List<SoilSensorData> _soilSensors = new();
    private readonly List<IrrigationPlot> _plots = new();
    private readonly object _lock = new();

    public event EventHandler<IrrigationDataPoint>? DataPointAdded;
    public event EventHandler? DataCleared;
    public event EventHandler<SoilSensorData>? SoilSensorUpdated;
    public event EventHandler<IrrigationPlot>? PlotAnalyzed;
    public event EventHandler<DetectionLabelMode>? LabelModeChanged;

    private DetectionLabelMode _labelMode = DetectionLabelMode.Zone;
    public DetectionLabelMode LabelMode
    {
        get => _labelMode;
        set
        {
            if (_labelMode != value)
            {
                _labelMode = value;
                LabelModeChanged?.Invoke(this, value);
            }
        }
    }

    public string GetDisplayName(string rawClass)
    {
        var key = System.Text.RegularExpressions.Regex.Replace(
            (rawClass ?? string.Empty).ToLowerInvariant().Trim(), @"[\s/\-]+", "_");

        if (key.Contains("kanopi") || key.Contains("drought") || key.Contains("disease"))
            return _labelMode == DetectionLabelMode.Plant ? "Drought/Severe Stress" : "Defisit Air (Kanopi)";

        if (key.Contains("tanah") || key.Contains("bare") || key.Contains("soil") || key.Contains("pest"))
            return _labelMode == DetectionLabelMode.Plant ? "Bare Soil / Gap" : "Defisit Air (Tanah)";

        if (key.Contains("irigasi") || key.Contains("inconsistent") || key.Contains("stress"))
            return _labelMode == DetectionLabelMode.Plant ? "Inconsistent Growth" : "Ketidakmerataan Irigasi";

        return _labelMode == DetectionLabelMode.Plant ? "Lush Green" : "Zona Normal";
    }

    public (string Emoji, string Label, string Description) GetClassInfo(string rawClass)
    {
        var lower = (rawClass ?? string.Empty).ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
        if (lower.Contains("kanopi") || lower.Contains("drought") || lower.Contains("disease"))
        {
            return _labelMode == DetectionLabelMode.Plant
                ? ("🟠", "Drought / Severe Stress", "Severe water stress / canopy dehydration")
                : ("🟠", "Defisit Air (Kanopi)", "Defisit air kanopi / severe stress");
        }
        if (lower.Contains("tanah") || lower.Contains("bare") || lower.Contains("soil") || lower.Contains("pest"))
        {
            return _labelMode == DetectionLabelMode.Plant
                ? ("🟤", "Bare Soil / Gap", "Exposed dry soil / canopy gap")
                : ("🟤", "Defisit Air (Tanah)", "Defisit air tanah / tanah kering");
        }
        if (lower.Contains("irigasi") || lower.Contains("inconsistent") || lower.Contains("stress"))
        {
            return _labelMode == DetectionLabelMode.Plant
                ? ("🟡", "Inconsistent Growth", "Uneven irrigation / early growth stress")
                : ("🟡", "Ketidakmerataan Irigasi", "Stres awal / pertumbuhan tidak seragam");
        }
        return _labelMode == DetectionLabelMode.Plant
            ? ("🌿", "Lush Green", "Normal vegetative vigor & well-hydrated")
            : ("🌿", "Zona Normal", "Vegetasi normal & terhidrasi baik");
    }

    public string GetDominantLabel(double healthyPct, double stressPct, double droughtPct, double bareSoilPct)
    {
        var max = Math.Max(Math.Max(healthyPct, stressPct), Math.Max(droughtPct, bareSoilPct));
        if (Math.Abs(max - droughtPct) < 0.001)
            return _labelMode == DetectionLabelMode.Plant ? $"Drought/Severe Stress {droughtPct:F0}%" : $"Defisit Air (Kanopi) {droughtPct:F0}%";
        if (Math.Abs(max - bareSoilPct) < 0.001)
            return _labelMode == DetectionLabelMode.Plant ? $"Bare Soil / Gap {bareSoilPct:F0}%" : $"Defisit Air (Tanah) {bareSoilPct:F0}%";
        if (Math.Abs(max - stressPct) < 0.001)
            return _labelMode == DetectionLabelMode.Plant ? $"Inconsistent Growth {stressPct:F0}%" : $"Ketidakmerataan Irigasi {stressPct:F0}%";
        return _labelMode == DetectionLabelMode.Plant ? $"Lush Green {healthyPct:F0}%" : $"Zona Normal {healthyPct:F0}%";
    }

    public IReadOnlyList<IrrigationDataPoint> DataPoints
    {
        get { lock (_lock) { return _dataPoints.ToList(); } }
    }

    public IReadOnlyList<SoilSensorData> SoilSensors
    {
        get { lock (_lock) { return _soilSensors.ToList(); } }
    }

    public IReadOnlyList<IrrigationPlot> Plots
    {
        get { lock (_lock) { return _plots.ToList(); } }
    }

    public IrrigationService(ILogger<IrrigationService>? logger = null)
    {
        _logger = logger;
    }

    public void AddDataPoint(double lat, double lon, double alt, string className, float confidence, double? moisture = null)
    {
        var zoneType = ParseZoneType(className);
        var point = new IrrigationDataPoint
        {
            Latitude = lat,
            Longitude = lon,
            Altitude = alt,
            ZoneType = zoneType,
            Confidence = confidence,
            MeasuredMoisturePercent = moisture,
            Timestamp = DateTime.UtcNow
        };

        lock (_lock)
        {
            _dataPoints.Add(point);
        }

        DataPointAdded?.Invoke(this, point);
    }

    public void ClearDataPoints()
    {
        lock (_lock)
        {
            _dataPoints.Clear();
        }
        DataCleared?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateSoilSensor(SoilSensorData sensorData)
    {
        lock (_lock)
        {
            var existing = _soilSensors.FirstOrDefault(s => s.SensorId == sensorData.SensorId);
            if (existing != null)
            {
                existing.Latitude = sensorData.Latitude;
                existing.Longitude = sensorData.Longitude;
                existing.MoisturePercent = sensorData.MoisturePercent;
                existing.TemperatureCelsius = sensorData.TemperatureCelsius;
                existing.BatteryVoltage = sensorData.BatteryVoltage;
                existing.Rssi = sensorData.Rssi;
                existing.LastUpdated = DateTime.UtcNow;
            }
            else
            {
                sensorData.LastUpdated = DateTime.UtcNow;
                _soilSensors.Add(sensorData);
            }
        }
        SoilSensorUpdated?.Invoke(this, sensorData);
    }

    public void RemoveSoilSensor(string sensorId)
    {
        lock (_lock)
        {
            _soilSensors.RemoveAll(s => s.SensorId == sensorId);
        }
    }

    public void AddOrUpdatePlot(IrrigationPlot plot)
    {
        lock (_lock)
        {
            var idx = _plots.FindIndex(p => p.Id == plot.Id);
            if (idx >= 0)
            {
                _plots[idx] = plot;
            }
            else
            {
                _plots.Add(plot);
            }
        }
    }

    public void RemovePlot(string plotId)
    {
        lock (_lock)
        {
            _plots.RemoveAll(p => p.Id == plotId);
        }
    }

    /// <summary>
    /// Analisis distribusi kelembaban & defisit air pada suatu petak (polygon)
    /// </summary>
    public IrrigationPlot? AnalyzePlot(string plotId)
    {
        IrrigationPlot? plot;
        List<IrrigationDataPoint> points;
        lock (_lock)
        {
            plot = _plots.FirstOrDefault(p => p.Id == plotId);
            points = _dataPoints.ToList();
        }

        if (plot == null || plot.PolygonCoordinates == null || plot.PolygonCoordinates.Count < 3)
            return plot;

        // Ambil titik-titik yang berada di dalam poligon
        var pointsInPlot = points.Where(pt => 
            GeometryHelper.IsPointInPolygon(new GeoCoordinate(pt.Latitude, pt.Longitude), plot.PolygonCoordinates)).ToList();

        if (pointsInPlot.Count == 0)
        {
            return plot;
        }

        int normalCount = pointsInPlot.Count(p => p.ZoneType == IrrigationZoneType.Normal);
        int inconsistentCount = pointsInPlot.Count(p => p.ZoneType == IrrigationZoneType.Inconsistent);
        int deficitCanopyCount = pointsInPlot.Count(p => p.ZoneType == IrrigationZoneType.DeficitCanopy);
        int deficitSoilCount = pointsInPlot.Count(p => p.ZoneType == IrrigationZoneType.DeficitSoil);
        int total = pointsInPlot.Count;

        plot.NormalPercent = (double)normalCount / total * 100.0;
        plot.InconsistentPercent = (double)inconsistentCount / total * 100.0;
        plot.DeficitCanopyPercent = (double)deficitCanopyCount / total * 100.0;
        plot.DeficitSoilPercent = (double)deficitSoilCount / total * 100.0;

        // Hitung estimasi luas area jika belum dihitung (derajat ke meter persegi pendekatan)
        if (plot.AreaHectares <= 0)
        {
            // perkiraan kasar: luas koordinat poligon ke meter persegi -> hektar
            double degArea = GeometryHelper.CalculatePolygonArea(plot.PolygonCoordinates);
            // 1 deg lat ~ 111km, 1 deg lon ~ 111km * cos(lat)
            double latRad = plot.PolygonCoordinates[0].Latitude * Math.PI / 180.0;
            double m2 = degArea * 111000.0 * 111000.0 * Math.Cos(latRad);
            plot.AreaHectares = Math.Max(0.01, m2 / 10000.0);
        }

        // Penentuan Level Prioritas
        double totalDeficit = plot.TotalDeficitPercent;
        if (totalDeficit >= 40.0 || plot.DeficitSoilPercent >= 20.0)
        {
            plot.Priority = IrrigationPriorityLevel.Emergency;
            plot.ActionRecommendation = "TINDAKAN SEGERA: Defisit air parah/tanah pecah terdeteksi. Buka saluran irigasi utama.";
            plot.RecommendedWaterLiters = plot.AreaHectares * (totalDeficit / 100.0) * 30000; // ~30mm = 300m3/ha
        }
        else if (totalDeficit >= 20.0 || plot.InconsistentPercent >= 35.0)
        {
            plot.Priority = IrrigationPriorityLevel.High;
            plot.ActionRecommendation = "PERINGATAN: Muncul gejala stres air pada kanopi tanaman. Jadwalkan pengairan dalam 24 jam.";
            plot.RecommendedWaterLiters = plot.AreaHectares * (totalDeficit / 100.0) * 20000;
        }
        else if (plot.InconsistentPercent >= 15.0)
        {
            plot.Priority = IrrigationPriorityLevel.Medium;
            plot.ActionRecommendation = "WASPADA: Pertumbuhan tidak seragam, periksa kelembaban tanah dan sebaran debit air.";
            plot.RecommendedWaterLiters = plot.AreaHectares * (plot.InconsistentPercent / 100.0) * 10000;
        }
        else
        {
            plot.Priority = IrrigationPriorityLevel.Low;
            plot.ActionRecommendation = "KONDISI OPTIMAL: Suplai air cukup, pertahankan level air saat ini.";
            plot.RecommendedWaterLiters = 0;
        }

        PlotAnalyzed?.Invoke(this, plot);
        return plot;
    }

    public List<IrrigationPlot> AnalyzeAllPlots()
    {
        List<IrrigationPlot> results = new();
        List<string> ids;
        lock (_lock)
        {
            ids = _plots.Select(p => p.Id).ToList();
        }
        foreach (var id in ids)
        {
            var res = AnalyzePlot(id);
            if (res != null) results.Add(res);
        }
        return results;
    }

    /// <summary>
    /// Validasi komparasi antara deteksi citra UAV vs data sensor tanah
    /// Menghitung persentase kecocokan / korelasi.
    /// </summary>
    public double CalculateGroundTruthCorrelation()
    {
        lock (_lock)
        {
            if (_soilSensors.Count == 0 || _dataPoints.Count == 0)
                return 0.0;

            int matches = 0;
            int totalChecked = 0;

            foreach (var sensor in _soilSensors)
            {
                // Cari data points dalam radius 15 meter dari sensor
                var sensorCoord = new GeoCoordinate(sensor.Latitude, sensor.Longitude);
                var nearbyPoints = _dataPoints.Where(pt =>
                    GeometryHelper.CalculateDistance(sensorCoord, new GeoCoordinate(pt.Latitude, pt.Longitude)) <= 15.0
                ).ToList();

                if (nearbyPoints.Count > 0)
                {
                    totalChecked++;
                    // Klasifikasi berdasarkan sensor: < 30% kelembaban = defisit
                    bool sensorSaysDeficit = sensor.MoisturePercent < 30.0;

                    // Mayoritas deteksi kamera
                    int deficitDetections = nearbyPoints.Count(p => p.ZoneType == IrrigationZoneType.DeficitCanopy || p.ZoneType == IrrigationZoneType.DeficitSoil);
                    bool aiSaysDeficit = ((double)deficitDetections / nearbyPoints.Count) >= 0.4;

                    if (sensorSaysDeficit == aiSaysDeficit)
                    {
                        matches++;
                    }
                }
            }

            return totalChecked > 0 ? (double)matches / totalChecked * 100.0 : 0.0;
        }
    }

    public Task SaveSessionAsync(string filePath)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                var exportObj = new
                {
                    DataPoints = _dataPoints,
                    SoilSensors = _soilSensors,
                    Plots = _plots
                };
                var json = System.Text.Json.JsonSerializer.Serialize(exportObj, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(filePath, json);
            }
        });
    }

    public Task LoadSessionAsync(string filePath)
    {
        return Task.Run(() =>
        {
            if (!System.IO.File.Exists(filePath)) return;
            var json = System.IO.File.ReadAllText(filePath);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            
            // Re-populate data safely
            // (Implemented as needed for sessions)
        });
    }

    private static IrrigationZoneType ParseZoneType(string className)
    {
        var lower = className.ToLowerInvariant().Trim();
        if (lower.Contains("normal") || lower.Contains("lush") || lower.Contains("healthy") || lower.Contains("0_"))
            return IrrigationZoneType.Normal;
        if (lower.Contains("inconsistent") || lower.Contains("stress") || lower.Contains("1_"))
            return IrrigationZoneType.Inconsistent;
        if (lower.Contains("kanopi") || lower.Contains("drought") || lower.Contains("2_"))
            return IrrigationZoneType.DeficitCanopy;
        if (lower.Contains("tanah") || lower.Contains("soil") || lower.Contains("bare") || lower.Contains("3_"))
            return IrrigationZoneType.DeficitSoil;
        return IrrigationZoneType.Unknown;
    }
}
