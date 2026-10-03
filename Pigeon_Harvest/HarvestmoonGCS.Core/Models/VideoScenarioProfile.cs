using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HarvestmoonGCS.Core.Models;

/// <summary>
/// Profil skenario video yang mengikat tampilan video spesifik dengan:
/// - Titik koordinat & pola rute Waypoint unik di persawahan Indonesia
/// - Karakteristik telemetri terbang (Altitude, Speed, Heading, Geofence)
/// - Parameter agronomi & deteksi kesehatan tanaman (FHI, Rasio Kelas, Status Rekomendasi)
/// - Identitas misi (Mission ID, Area Name, Region)
/// </summary>
public sealed class VideoScenarioProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ShortDescription { get; set; } = string.Empty;
    public string VideoFileName { get; set; } = string.Empty;
    public string[] VideoPathCandidates { get; set; } = Array.Empty<string>();
    public string ResolvedVideoPath { get; set; } = string.Empty;
    public double VideoDurationSeconds { get; set; } = 60.0;

    // Identitas Lokasi & Misi
    public string MissionId { get; set; } = string.Empty;
    public string AreaName { get; set; } = string.Empty;
    public string RegionName { get; set; } = string.Empty;
    public double FieldCenterLat { get; set; }
    public double FieldCenterLon { get; set; }
    public double GeofenceRadiusMeters { get; set; } = 500.0;
    public int MapZoomLevel { get; set; } = 17;

    // Profil Penerbangan & Waypoint
    public double BearingDeg { get; set; } = 90.0;
    public double CruiseAltitudeMeters { get; set; } = 60.0;
    public double CruiseSpeedMps { get; set; } = 10.0;
    public double InitialBatteryPct { get; set; } = 95.0;
    public int SatelliteCount { get; set; } = 16;
    public List<WaypointData> Waypoints { get; set; } = new();
    public List<string> SectorNames { get; set; } = new();

    // Metrik Agronomi & Deteksi
    public double TargetFhi { get; set; } = 85.0;
    public double LushGreenPct { get; set; } = 50.0;
    public double InconsistentGrowthPct { get; set; } = 30.0;
    public double DroughtPct { get; set; } = 5.0;
    public double BareSoilPct { get; set; } = 15.0;
    public string DominantStatus { get; set; } = "Lush Green";
    public string PriorityLevel { get; set; } = "Optimal"; // Optimal, Good, Caution, Action Needed
    public string FarmerStatusText { get; set; } = string.Empty;
    public string FarmerRecommendation { get; set; } = string.Empty;
    public List<string> TimelineSectorEvents { get; set; } = new();
    public (string Class, double Conf)[][] DetectionFrames { get; set; } = Array.Empty<(string, double)[]>();

    /// <summary>
    /// Menghasilkan koordinat waypoint berjarak linear sepanjang arah bearing.
    /// </summary>
    public static List<WaypointData> BuildLinearWaypoints(
        double centerLat, double centerLon, int count, double totalMeters, double bearingDeg, double altitude)
    {
        const double mPerDegLat = 111320.0;
        double mPerDegLon = mPerDegLat * Math.Cos(centerLat * Math.PI / 180.0);
        double spacingMeters = totalMeters / Math.Max(1, count - 1);
        double bearingRad = bearingDeg * Math.PI / 180.0;
        double dLat = spacingMeters * Math.Cos(bearingRad) / mPerDegLat;
        double dLon = spacingMeters * Math.Sin(bearingRad) / mPerDegLon;

        double startLat = centerLat - dLat * (count - 1) / 2.0;
        double startLon = centerLon - dLon * (count - 1) / 2.0;

        var list = new List<WaypointData>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new WaypointData
            {
                Sequence  = i + 1,
                Latitude  = Math.Round(startLat + i * dLat, 6),
                Longitude = Math.Round(startLon + i * dLon, 6),
                Altitude  = altitude,
                Command   = WaypointCommand.Waypoint,
            });
        }
        return list;
    }
}
