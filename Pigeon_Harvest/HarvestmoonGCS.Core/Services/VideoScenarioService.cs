using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarvestmoonGCS.Core.Models;

namespace HarvestmoonGCS.Core.Services;

public sealed class VideoScenarioService : IVideoScenarioService
{
    private readonly List<VideoScenarioProfile> _predefinedScenarios;
    private VideoScenarioProfile _currentScenario;

    public event EventHandler<VideoScenarioProfile>? ScenarioChanged;

    public IReadOnlyList<VideoScenarioProfile> PredefinedScenarios => _predefinedScenarios;
    public VideoScenarioProfile CurrentScenario => _currentScenario;

    public VideoScenarioService()
    {
        _predefinedScenarios = BuildPredefinedScenarios();
        _currentScenario = _predefinedScenarios[0];
    }

    public VideoScenarioProfile SelectScenarioByIndex(int index)
    {
        if (index >= 0 && index < _predefinedScenarios.Count)
        {
            SetCurrentScenario(_predefinedScenarios[index]);
            return _currentScenario;
        }
        return _currentScenario;
    }

    public void SetCurrentScenario(VideoScenarioProfile scenario)
    {
        if (scenario == null) return;
        _currentScenario = scenario;
        ScenarioChanged?.Invoke(this, scenario);
    }

    public VideoScenarioProfile LoadScenarioForVideo(string videoPath, double durationSeconds = -1)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            return _currentScenario;
        }

        var fileName = Path.GetFileName(videoPath);
        var lower = fileName.ToLowerInvariant();

        // 1. Cek apakah cocok dengan salah satu skenario bawaan
        if (lower.Contains("60s") || lower.Contains("multisector"))
        {
            var p = CloneProfile(_predefinedScenarios[0]);
            p.ResolvedVideoPath = videoPath;
            if (durationSeconds > 0) p.VideoDurationSeconds = durationSeconds;
            SetCurrentScenario(p);
            return p;
        }
        if (lower.Contains("15d") || lower.Contains("v7c"))
        {
            var p = CloneProfile(_predefinedScenarios[1]);
            p.ResolvedVideoPath = videoPath;
            if (durationSeconds > 0) p.VideoDurationSeconds = durationSeconds;
            SetCurrentScenario(p);
            return p;
        }
        if (lower.Contains("ydxj") || lower.Contains("irigasi"))
        {
            var p = CloneProfile(_predefinedScenarios[2]);
            p.ResolvedVideoPath = videoPath;
            if (durationSeconds > 0) p.VideoDurationSeconds = durationSeconds;
            SetCurrentScenario(p);
            return p;
        }
        if (lower.Contains("burned") || lower.Contains("kering") || lower.Contains("drought"))
        {
            var p = CloneProfile(_predefinedScenarios[3]);
            p.ResolvedVideoPath = videoPath;
            if (durationSeconds > 0) p.VideoDurationSeconds = durationSeconds;
            SetCurrentScenario(p);
            return p;
        }

        // 2. Video kustom: buat profil unik secara deterministik agar setiap video kustom berbeda
        var customProfile = CreateDeterministicCustomProfile(videoPath, durationSeconds);
        SetCurrentScenario(customProfile);
        return customProfile;
    }

    public string? ResolveVideoFilePath(VideoScenarioProfile profile)
    {
        if (profile == null) return null;
        if (!string.IsNullOrWhiteSpace(profile.ResolvedVideoPath) && File.Exists(profile.ResolvedVideoPath))
        {
            return profile.ResolvedVideoPath;
        }

        foreach (var candidate in profile.VideoPathCandidates)
        {
            if (File.Exists(candidate) && new FileInfo(candidate).Length > 10_000)
            {
                profile.ResolvedVideoPath = candidate;
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Builds the candidate list for a demo video: the app-bundled asset first (works on any
    /// machine/OS), then the developer's local checkout as a convenience fallback. Previously
    /// the absolute /home/... entries came first, so on Windows every candidate missed and the
    /// demo silently fell back to whatever else was found.
    /// </summary>
    private static string[] DemoVideoCandidates(string fileName, params string[] extraAbsolute)
    {
        var list = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "demo_videos", fileName),
        };

        var repoRoot = ResolveRepoRoot();
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            list.Add(Path.Combine(repoRoot, "Pigeon_Harvest", "HarvestmoonGCS", "Assets", "demo_videos", fileName));
            list.Add(Path.Combine(repoRoot, "TEKNOFEST_SIAP", "demo_video", fileName));
            list.Add(Path.Combine(repoRoot, "vidio", fileName));
            list.Add(Path.Combine(repoRoot, "vid", fileName));
            list.Add(Path.Combine(repoRoot, "demo_videos", fileName));
        }

        // Environment override, then any explicitly requested absolute paths last.
        var envOverride = Environment.GetEnvironmentVariable("MOONHARVEST_DEMO_DIR");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            list.Add(Path.Combine(envOverride, fileName));
        }
        list.AddRange(extraAbsolute);
        return list.ToArray();
    }

    /// <summary>
    /// Walks up from the executable looking for the Harvestmoon repo root (identified by a
    /// sibling Pigeon_Harvest/TEKNOFEST_SIAP layout). Returns null when not running from a
    /// source checkout — e.g. a packaged Windows install.
    /// </summary>
    private static string? ResolveRepoRoot()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "Pigeon_Harvest")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "TEKNOFEST_SIAP")))
                {
                    return dir.FullName;
                }
            }
        }
        catch
        {
            // Ignore probing failures — the bundled asset path still works.
        }
        return null;
    }

    private static List<VideoScenarioProfile> BuildPredefinedScenarios()
    {
        var list = new List<VideoScenarioProfile>();

        // ── Skenario 1: Multi-Sektor 60s (Subang - Riset PDP) ───────────────
        var sc1 = new VideoScenarioProfile
        {
            Id = "scenario-multisector",
            Name = "★ Riset PDP 4-Sektor 1080p (Subang · 14 WP)",
            ShortDescription = "Riset PDP 4-Sektor 1080p",
            VideoFileName = "pdp_sawah_4sektor_1080p.mp4",
            VideoPathCandidates = DemoVideoCandidates(
                "pdp_sawah_4sektor_1080p.mp4",
                "stream_multisector_60s_detected.mp4",
                "stream_multisector_60s.mp4",
                "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/pdp_sawah_4sektor_1080p.mp4",
                "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/stream_multisector_60s.mp4"),
            VideoDurationSeconds = 60.0,
            MissionId = "PDP-SUBANG-14WP-01",
            AreaName = "Pabuaran Rice Estate, Subang",
            RegionName = "Subang, Jawa Barat",
            FieldCenterLat = -6.42580,
            FieldCenterLon = 107.61890,
            GeofenceRadiusMeters = 550.0,
            MapZoomLevel = 17,
            BearingDeg = 90.0, // Barat ke Timur
            CruiseAltitudeMeters = 50.0,
            CruiseSpeedMps = 10.0,
            InitialBatteryPct = 96.0,
            SatelliteCount = 16,
            TargetFhi = 85.4,
            LushGreenPct = 52.3,
            InconsistentGrowthPct = 32.1,
            DroughtPct = 3.5,
            BareSoilPct = 12.1,
            DominantStatus = "Lush Green (Vegetatif Optimal)",
            PriorityLevel = "Optimal",
            FarmerStatusText = "Field Health Index: Excellent (85%). Tutupan kanopi subur dan stabil di seluruh sektor A-D.",
            FarmerRecommendation = "Kondisi vegetasi sangat baik. Lanjutkan pemantauan rutin dan rotasi pengairan terjadwal.",
            SectorNames = new List<string>
            {
                "Sektor A (Vegetatif Subur)",
                "Sektor B (Padi Muda 15 HST)",
                "Sektor C (Variasi Stres)",
                "Sektor D (Pemulihan Irigasi)",
                "Sektor E (Batas Pembalikan)"
            },
            TimelineSectorEvents = new List<string>
            {
                "WP 1 → Takeoff & Pendakian Menuju 50m AGL (Koridor Hulu)",
                "WP 2 → Entry Sektor A · Inspeksi Kanopi Vegetatif Subur (FHI 96.5%)",
                "WP 3 → Transek Leg 1 Sektor A · Pemindaian Ortomosaik Subang",
                "WP 4 → Transek Leg 1 Sektor B · Deteksi Kanopi Padi Muda 15 HST",
                "WP 5 → Prosedur Turnaround Timur · Banking Roll +15°",
                "WP 6 → Entry Sektor C · Pemindaian Saluran Irigasi Cacing",
                "WP 7 → Deteksi Stres Air Saluran Tersier Sektor C (FHI 64.0%)",
                "WP 8 → Sweeping Transek Leg 2 Barat · Inspeksi Pematang Sawah",
                "WP 9 → Turnaround Barat Menuju Blok Hilir Sektor D",
                "WP 10 → Entry Sektor D · Deteksi Defisit Kering & Rekahan Tanah",
                "WP 11 → Deteksi Kritis AWD Sektor D (FHI 32.1% · LoRa P-01 VWC 14.8%)",
                "WP 12 → Titik Akhir Fotogrametri Grid Selesai (Total 1.850 m)",
                "WP 13 → Inbound Corridor Approach Menuju Home Base",
                "WP 14 → Touchdown & Landing Prosedur RTL Selesai"
            },
            DetectionFrames = new[]
            {
                new[] { ("Zona Normal", 0.95), ("Zona Normal", 0.92), ("Ketidakmerataan Irigasi", 0.89), ("Defisit Air (Kanopi)", 0.88), ("Defisit Air (Tanah)", 0.94) },
                new[] { ("Zona Normal", 0.94), ("Ketidakmerataan Irigasi", 0.91), ("Defisit Air (Kanopi)", 0.86), ("Defisit Air (Tanah)", 0.90) },
                new[] { ("Zona Normal", 0.96), ("Zona Normal", 0.91), ("Ketidakmerataan Irigasi", 0.88), ("Defisit Air (Kanopi)", 0.89), ("Defisit Air (Tanah)", 0.87) },
                new[] { ("Zona Normal", 0.93), ("Ketidakmerataan Irigasi", 0.87), ("Defisit Air (Kanopi)", 0.85), ("Defisit Air (Tanah)", 0.92) },
            }
        };
        sc1.Waypoints = new List<WaypointData>
        {
            new WaypointData { Sequence = 1,  Latitude = -6.42580, Longitude = 107.61710, Altitude = 20.0, Command = WaypointCommand.TakeOff },
            new WaypointData { Sequence = 2,  Latitude = -6.42530, Longitude = 107.61780, Altitude = 50.0, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 3,  Latitude = -6.42530, Longitude = 107.61890, Altitude = 50.0, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 4,  Latitude = -6.42530, Longitude = 107.62000, Altitude = 50.0, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 5,  Latitude = -6.42555, Longitude = 107.62015, Altitude = 50.0, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 6,  Latitude = -6.42565, Longitude = 107.61890, Altitude = 50.0, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 7,  Latitude = -6.42565, Longitude = 107.61780, Altitude = 50.0, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 8,  Latitude = -6.42595, Longitude = 107.61765, Altitude = 50.0, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 9,  Latitude = -6.42610, Longitude = 107.61890, Altitude = 50.0, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 10, Latitude = -6.42610, Longitude = 107.62000, Altitude = 50.0, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 11, Latitude = -6.42635, Longitude = 107.62015, Altitude = 50.0, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 12, Latitude = -6.42640, Longitude = 107.61780, Altitude = 50.0, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 13, Latitude = -6.42580, Longitude = 107.61730, Altitude = 35.0, Command = WaypointCommand.Waypoint },
            new WaypointData { Sequence = 14, Latitude = -6.42580, Longitude = 107.61710, Altitude = 15.0, Command = WaypointCommand.Land }
        };
        list.Add(sc1);

        // ── Skenario 2: Sawah 15 HST (Karawang) ────────────────────────────────
        var sc2 = new VideoScenarioProfile
        {
            Id = "scenario-15hst",
            Name = "Sawah 15 HST (Karawang · 4 WP)",
            ShortDescription = "Sawah 15 HST",
            VideoFileName = "stream_v7c_final_detected.mp4",
            VideoPathCandidates = DemoVideoCandidates(
                "stream_v7c_final_detected.mp4",
                "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/15d_detected.mp4",
                "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/stream_v7c_final.mp4"),
            VideoDurationSeconds = 15.5,
            MissionId = "HST15-RAWAMERTA-02",
            AreaName = "Sukamerta Rice Field, Karawang",
            RegionName = "Karawang, Jawa Barat",
            FieldCenterLat = -6.24361,
            FieldCenterLon = 107.36556,
            GeofenceRadiusMeters = 350.0,
            MapZoomLevel = 17,
            BearingDeg = 180.0, // Utara ke Selatan melintasi petak sawah
            CruiseAltitudeMeters = 45.0, // Rendah untuk kanopi anakan padi muda
            CruiseSpeedMps = 7.5, // Pelan untuk inspeksi presisi
            InitialBatteryPct = 88.0,
            SatelliteCount = 14,
            TargetFhi = 81.6, // PPT Slide 751 Controlled Validation target
            LushGreenPct = 6.8,
            InconsistentGrowthPct = 69.3,
            DroughtPct = 0.0,
            BareSoilPct = 23.9,
            DominantStatus = "Inconsistent Growth (Fase Bertunas 15 HST)",
            PriorityLevel = "Good",
            FarmerStatusText = "Field Health Index: Good (82%). Fase bertunas 15 HST normal, pertahankan genangan dangkal.",
            FarmerRecommendation = "Fase vegetatif awal normal (vitalitas anakan 77%). Pertahankan genangan air 2-3 cm untuk mendukung pembentukan anakan produktif.",
            SectorNames = new List<string>
            {
                "Petak Sukamerta 1",
                "Petak Sukamerta 2",
                "Petak Sukamerta 3",
                "Petak Sukamerta 4"
            },
            TimelineSectorEvents = new List<string>
            {
                "WP 1 → Memasuki Petak Sukamerta 1 · Deteksi anakan padi 15 HST",
                "WP 2 → Petak 2 · Genangan air dangkal terdeteksi normal",
                "WP 3 → Petak 3 · Vitalitas kanopi 77% (anakan normal)",
                "WP 4 → Batas Petak 4 · Putar balik inspeksi pematang"
            },
            DetectionFrames = new[]
            {
                new[] { ("Inconsistent Growth", 0.86), ("Bare Soil / Gap", 0.78), ("Lush Green", 0.88) },
                new[] { ("Inconsistent Growth", 0.84), ("Bare Soil / Gap", 0.81), ("Inconsistent Growth", 0.79) },
                new[] { ("Inconsistent Growth", 0.89), ("Lush Green", 0.91), ("Bare Soil / Gap", 0.74) },
                new[] { ("Inconsistent Growth", 0.82), ("Bare Soil / Gap", 0.76), ("Inconsistent Growth", 0.85) },
            }
        };
        sc2.Waypoints = VideoScenarioProfile.BuildLinearWaypoints(
            sc2.FieldCenterLat, sc2.FieldCenterLon, count: 4, totalMeters: 400, bearingDeg: sc2.BearingDeg, altitude: sc2.CruiseAltitudeMeters);
        list.Add(sc2);

        // ── Skenario 3: Lahan Irigasi Subur (Majalengka) ───────────────────────
        var sc3 = new VideoScenarioProfile
        {
            Id = "scenario-irrigated",
            Name = "Lahan Irigasi (Majalengka · 5 WP)",
            ShortDescription = "Lahan Irigasi (YDXJ)",
            VideoFileName = "YDXJ_fused_only_detected.mp4",
            VideoPathCandidates = DemoVideoCandidates(
                "YDXJ_fused_only_detected.mp4",
                "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/YDXJ_irigasi_detected.mp4",
                "/home/fawwazfa/Program/Harvestmoon/vidio/YDXJ0012_demo(1).mp4"),
            VideoDurationSeconds = 60.0,
            MissionId = "IRR-MAJALENGKA-03",
            AreaName = "Rentang Irrigation Zone, Majalengka",
            RegionName = "Majalengka, Jawa Barat",
            FieldCenterLat = -6.61240,
            FieldCenterLon = 108.21450,
            GeofenceRadiusMeters = 480.0,
            MapZoomLevel = 17,
            BearingDeg = 45.0, // Diagonal Barat Daya ke Timur Laut
            CruiseAltitudeMeters = 55.0,
            CruiseSpeedMps = 9.0,
            InitialBatteryPct = 92.0,
            SatelliteCount = 18,
            TargetFhi = 94.2,
            LushGreenPct = 82.4,
            InconsistentGrowthPct = 11.2,
            DroughtPct = 1.1,
            BareSoilPct = 5.3,
            DominantStatus = "Lush Green (Vegetasi Sangat Prima)",
            PriorityLevel = "Optimal",
            FarmerStatusText = "Field Health Index: Prime (94%). Kanopi sangat subur dan pasokan air saluran irigasi melimpah.",
            FarmerRecommendation = "Status kanopi sangat subur dan seragam. Persiapkan aplikasi pemupukan susulan urea/NPK menjelang fase bunting.",
            SectorNames = new List<string>
            {
                "Saluran Primer Barat",
                "Hamparan Padi Masif A",
                "Sektor Irigasi Tengah",
                "Hamparan Padi Masif B",
                "Saluran Sekunder Timur"
            },
            TimelineSectorEvents = new List<string>
            {
                "WP 1 → Jalur Irigasi Rentang Barat aktif · Pasokan air penuh",
                "WP 2 → Hamparan Padi A · Tutupan kanopi 88% seragam",
                "WP 3 → Sektor Irigasi Tengah · FHI 95.1% terverifikasi",
                "WP 4 → Hamparan Padi B · Tidak ditemukan gejala klorosis",
                "WP 5 → Ujung saluran sekunder · Belok menuju jalur sapuan"
            },
            DetectionFrames = new[]
            {
                new[] { ("Lush Green", 0.96), ("Lush Green", 0.94), ("Lush Green", 0.90) },
                new[] { ("Lush Green", 0.95), ("Lush Green", 0.92), ("Inconsistent Growth", 0.76) },
                new[] { ("Lush Green", 0.97), ("Lush Green", 0.93), ("Bare Soil / Gap", 0.71) },
                new[] { ("Lush Green", 0.94), ("Lush Green", 0.91), ("Lush Green", 0.88) },
            }
        };
        sc3.Waypoints = VideoScenarioProfile.BuildLinearWaypoints(
            sc3.FieldCenterLat, sc3.FieldCenterLon, count: 5, totalMeters: 550, bearingDeg: sc3.BearingDeg, altitude: sc3.CruiseAltitudeMeters);
        list.Add(sc3);

        // ── Skenario 4: Lahan Kering & Stres (Bogor) ───────────────────────────
        var sc4 = new VideoScenarioProfile
        {
            Id = "scenario-drought",
            Name = "Lahan Kering (Bogor · 4 WP)",
            ShortDescription = "Lahan Kering & Stres",
            VideoFileName = "YDX_burned_detected.mp4",
            VideoPathCandidates = DemoVideoCandidates(
                "YDX_burned_detected.mp4",
                "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/YDX_burned.mp4",
                "/home/fawwazfa/Program/Harvestmoon/vidio/YDX_burned.mp4"),
            VideoDurationSeconds = 35.0,
            MissionId = "DRY-SINDUR-04",
            AreaName = "Gunung Sindur Rainfed Field, Bogor",
            RegionName = "Bogor, Jawa Barat",
            FieldCenterLat = -6.41980,
            FieldCenterLon = 106.71120,
            GeofenceRadiusMeters = 420.0,
            MapZoomLevel = 17,
            BearingDeg = 135.0, // Diagonal Barat Laut ke Tenggara
            CruiseAltitudeMeters = 75.0, // Tinggi untuk survei sebaran retakan luas
            CruiseSpeedMps = 11.5,
            InitialBatteryPct = 81.0,
            SatelliteCount = 13,
            TargetFhi = 42.5,
            LushGreenPct = 3.2,
            InconsistentGrowthPct = 21.5,
            DroughtPct = 51.8,
            BareSoilPct = 23.5,
            DominantStatus = "Drought / Severe Stress (Kekeringan Akut)",
            PriorityLevel = "Action Needed",
            FarmerStatusText = "Field Health Index: Critical (43%). Kekeringan parah terdeteksi, butuh irigasi segera!",
            FarmerRecommendation = "PERINGATAN: Klorosis dan cekaman kekeringan akut > 50%. Segera aktifkan pompa air tanah atau irigasi suplesi darurat untuk mencegah kematian tanaman!",
            SectorNames = new List<string>
            {
                "Zona Kering Utara",
                "Episentrum Kekeringan A",
                "Episentrum Kekeringan B",
                "Batas Lahan Kering Selatan"
            },
            TimelineSectorEvents = new List<string>
            {
                "WP 1 → Memasuki Zona Kering Utara · Kelembaban tanah < 15%",
                "WP 2 → PERINGATAN: Anomali kekeringan parah terdeteksi (Drought 58%)",
                "WP 3 → Retakan tanah & klorosis daun luas di Sektor C",
                "WP 4 → Rekomendasi: Irigasi suplesi darurat segera!"
            },
            DetectionFrames = new[]
            {
                new[] { ("Drought/Severe Stress", 0.91), ("Bare Soil / Gap", 0.82), ("Inconsistent Growth", 0.74) },
                new[] { ("Drought/Severe Stress", 0.88), ("Drought/Severe Stress", 0.85), ("Bare Soil / Gap", 0.79) },
                new[] { ("Drought/Severe Stress", 0.93), ("Bare Soil / Gap", 0.84), ("Lush Green", 0.71) },
                new[] { ("Drought/Severe Stress", 0.89), ("Inconsistent Growth", 0.78), ("Bare Soil / Gap", 0.83) },
            }
        };
        sc4.Waypoints = VideoScenarioProfile.BuildLinearWaypoints(
            sc4.FieldCenterLat, sc4.FieldCenterLon, count: 4, totalMeters: 450, bearingDeg: sc4.BearingDeg, altitude: sc4.CruiseAltitudeMeters);
        list.Add(sc4);

        return list;
    }

    private static (double Lat, double Lon, string Area, string Region)[] AgriculturalHubs = new[]
    {
        (-6.81820, 107.13950, "Cianjur Pandanwangi Estate", "Cianjur, Jawa Barat"),
        (-6.32640, 108.32000, "Haurgeulis Granary Fields", "Indramayu, Jawa Barat"),
        (-7.70560, 110.60140, "Delanggu Premium Rice Zone", "Klaten, Jawa Tengah"),
        (-7.40420, 111.44490, "Bengawan Rice Basin", "Ngawi, Jawa Timur"),
        (-8.21920, 114.36920, "Rogojampi Fertile Plain", "Banyuwangi, Jawa Timur"),
        (-3.92670, 119.80000, "Sidenreng Rappang Rice Belt", "Sidrap, Sulawesi Selatan"),
        (-3.79170, 119.64720, "Paleteang Agricultural Zone", "Pinrang, Sulawesi Selatan"),
        (-8.54110, 115.12500, "Jatiluwih Terraced Agrofield", "Tabanan, Bali"),
    };

    private static VideoScenarioProfile CreateDeterministicCustomProfile(string videoPath, double durationSeconds)
    {
        var fn = Path.GetFileName(videoPath);
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        long fileSize = 1000;
        try
        {
            if (File.Exists(videoPath)) fileSize = new FileInfo(videoPath).Length;
        }
        catch { }

        int hash = Math.Abs((fn + fileSize.ToString()).GetHashCode());
        var hub = AgriculturalHubs[hash % AgriculturalHubs.Length];

        // Offset mikro unik dari hash sehingga setiap video beda koordinat
        double dLatOffset = ((hash % 100) - 50) * 0.0004;
        double dLonOffset = (((hash / 100) % 100) - 50) * 0.0004;
        double centerLat = Math.Round(hub.Lat + dLatOffset, 6);
        double centerLon = Math.Round(hub.Lon + dLonOffset, 6);

        double bearing = (hash * 37) % 360;
        double dur = durationSeconds > 10 ? durationSeconds : 45.0;
        int wpCount = Math.Clamp((int)Math.Round(dur / 12.0) + 1, 4, 8);
        double alt = 45.0 + (hash % 30);
        double speed = 8.0 + (hash % 35) / 10.0;
        double totalMeters = speed * (dur / (wpCount - 1)) * (wpCount - 1);
        double geofenceRadius = Math.Max(320.0, (totalMeters / 2.0) + 100.0);

        var sanitizedId = new string(baseName.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (sanitizedId.Length > 10) sanitizedId = sanitizedId.Substring(0, 10);
        if (string.IsNullOrEmpty(sanitizedId)) sanitizedId = "CUST";

        var sectors = new List<string>();
        var timelineEvents = new List<string>();
        for (int i = 0; i < wpCount; i++)
        {
            char secChar = (char)('A' + i);
            sectors.Add($"Sektor {secChar} ({baseName})");
            timelineEvents.Add($"WP {i + 1} → Melintasi Sektor {secChar} ({hub.Area}) · Transek aktif");
        }

        double fhi = 65.0 + (hash % 28);
        double lush = Math.Clamp(fhi * 0.7, 10, 80);
        double stress = Math.Clamp((100 - fhi) * 0.7, 10, 60);
        double drought = Math.Clamp((100 - fhi) * 0.15, 0, 30);
        double soil = Math.Max(0, 100 - (lush + stress + drought));

        var profile = new VideoScenarioProfile
        {
            Id = $"custom-{hash:X4}",
            Name = $"📁 {fn} ({dur:F0}s · {wpCount} WP)",
            ShortDescription = $"Kustom: {baseName}",
            VideoFileName = fn,
            ResolvedVideoPath = videoPath,
            VideoDurationSeconds = dur,
            MissionId = $"CUST-{hash % 1000:D3}-{sanitizedId}",
            AreaName = $"Lahan {baseName}, {hub.Area}",
            RegionName = hub.Region,
            FieldCenterLat = centerLat,
            FieldCenterLon = centerLon,
            GeofenceRadiusMeters = geofenceRadius,
            MapZoomLevel = 17,
            BearingDeg = bearing,
            CruiseAltitudeMeters = alt,
            CruiseSpeedMps = speed,
            InitialBatteryPct = 90.0 + (hash % 8),
            SatelliteCount = 14 + (hash % 5),
            TargetFhi = Math.Round(fhi, 1),
            LushGreenPct = Math.Round(lush, 1),
            InconsistentGrowthPct = Math.Round(stress, 1),
            DroughtPct = Math.Round(drought, 1),
            BareSoilPct = Math.Round(soil, 1),
            DominantStatus = fhi >= 80 ? "Lush Green" : (drought > 20 ? "Drought Stress" : "Inconsistent Growth"),
            PriorityLevel = fhi >= 80 ? "Optimal" : (fhi >= 60 ? "Good" : "Caution"),
            FarmerStatusText = $"Field Health Index: {fhi:F0}%. Survei UAV terverifikasi di {hub.Area}.",
            FarmerRecommendation = $"Analisis video {fn} selesai. Pantau distribusi kelembaban kanopi di petak terdampak.",
            SectorNames = sectors,
            TimelineSectorEvents = timelineEvents,
            DetectionFrames = new[]
            {
                new[] { ("Lush Green", 0.90), ("Inconsistent Growth", 0.81), ("Bare Soil / Gap", 0.75) },
                new[] { ("Inconsistent Growth", 0.83), ("Lush Green", 0.88), ("Drought/Severe Stress", 0.70) },
            }
        };

        profile.Waypoints = VideoScenarioProfile.BuildLinearWaypoints(
            centerLat, centerLon, wpCount, totalMeters, bearing, alt);

        return profile;
    }

    private static VideoScenarioProfile CloneProfile(VideoScenarioProfile source)
    {
        return new VideoScenarioProfile
        {
            Id = source.Id,
            Name = source.Name,
            ShortDescription = source.ShortDescription,
            VideoFileName = source.VideoFileName,
            VideoPathCandidates = source.VideoPathCandidates.ToArray(),
            ResolvedVideoPath = source.ResolvedVideoPath,
            VideoDurationSeconds = source.VideoDurationSeconds,
            MissionId = source.MissionId,
            AreaName = source.AreaName,
            RegionName = source.RegionName,
            FieldCenterLat = source.FieldCenterLat,
            FieldCenterLon = source.FieldCenterLon,
            GeofenceRadiusMeters = source.GeofenceRadiusMeters,
            MapZoomLevel = source.MapZoomLevel,
            BearingDeg = source.BearingDeg,
            CruiseAltitudeMeters = source.CruiseAltitudeMeters,
            CruiseSpeedMps = source.CruiseSpeedMps,
            InitialBatteryPct = source.InitialBatteryPct,
            SatelliteCount = source.SatelliteCount,
            Waypoints = source.Waypoints.Select(w => new WaypointData
            {
                Sequence = w.Sequence,
                Latitude = w.Latitude,
                Longitude = w.Longitude,
                Altitude = w.Altitude,
                Command = w.Command
            }).ToList(),
            SectorNames = source.SectorNames.ToList(),
            TargetFhi = source.TargetFhi,
            LushGreenPct = source.LushGreenPct,
            InconsistentGrowthPct = source.InconsistentGrowthPct,
            DroughtPct = source.DroughtPct,
            BareSoilPct = source.BareSoilPct,
            DominantStatus = source.DominantStatus,
            PriorityLevel = source.PriorityLevel,
            FarmerStatusText = source.FarmerStatusText,
            FarmerRecommendation = source.FarmerRecommendation,
            TimelineSectorEvents = source.TimelineSectorEvents.ToList(),
            DetectionFrames = source.DetectionFrames
        };
    }
}
