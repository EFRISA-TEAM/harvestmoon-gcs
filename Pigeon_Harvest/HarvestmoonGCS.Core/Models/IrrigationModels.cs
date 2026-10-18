using System;
using System.Collections.Generic;

namespace HarvestmoonGCS.Core.Models;

/// <summary>
/// Klasifikasi kondisi irigasi dan vegetasi sesuai proposal penelitian
/// </summary>
public enum IrrigationZoneType
{
    Normal = 0,                // 0_zona_normal: Tanaman sehat & air tercukupi
    Inconsistent = 1,          // 1_ketidakmerataan_irigasi: Anomali/stres ringan
    DeficitCanopy = 2,         // 2_defisit_air_kanopi: Daun menguning/kering
    DeficitSoil = 3,           // 3_defisit_air_tanah: Tanah pecah/kering kerontang
    Unknown = 99
}

public enum IrrigationPriorityLevel
{
    Low,       // Normal / Tidak butuh tindakan segera
    Medium,    // Perlu observasi & irigasi terjadwal
    High,      // Kritis: Perlu irigasi segera
    Emergency  // Sangat Kritis: Kerusakan permanen jika tidak diairi
}

/// <summary>
/// Mode analisis deteksi MoonHarvest GCS:
/// - Plant: Mode 1 - Plant Level (Lush Green, Inconsistent Growth, Drought / Severe Stress, Bare Soil / Gap)
/// - Zone: Mode 2 - Zone Level (Zona Normal, Ketidakmerataan Irigasi, Defisit Air (Kanopi), Defisit Air (Tanah))
/// </summary>
public enum DetectionLabelMode
{
    Plant = 0,
    Zone = 1,
    // Alias kompatibilitas
    MoonHarvest = 0,
    ProposalPdp = 1
}

/// <summary>
/// Titik data observasi spasial hasil inferensi AI / sensor
/// </summary>
public class IrrigationDataPoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }
    public IrrigationZoneType ZoneType { get; set; }
    public float Confidence { get; set; }
    public double? MeasuredMoisturePercent { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string GetZoneName(DetectionLabelMode mode = DetectionLabelMode.Zone) => ZoneType switch
    {
        IrrigationZoneType.Normal => mode == DetectionLabelMode.Plant ? "Lush Green" : "Zona Normal",
        IrrigationZoneType.Inconsistent => mode == DetectionLabelMode.Plant ? "Inconsistent Growth" : "Ketidakmerataan Irigasi",
        IrrigationZoneType.DeficitCanopy => mode == DetectionLabelMode.Plant ? "Drought / Severe Stress" : "Defisit Air (Kanopi)",
        IrrigationZoneType.DeficitSoil => mode == DetectionLabelMode.Plant ? "Bare Soil / Gap" : "Defisit Air (Tanah)",
        _ => mode == DetectionLabelMode.Plant ? "Unknown" : "Tidak Diketahui"
    };

    public string ZoneName => GetZoneName(DetectionLabelMode.Zone);

    public string ColorHex => ZoneType switch
    {
        IrrigationZoneType.Normal => "#2ECC71",        // Hijau
        IrrigationZoneType.Inconsistent => "#F1C40F",  // Kuning
        IrrigationZoneType.DeficitCanopy => "#E67E22",  // Oranye
        IrrigationZoneType.DeficitSoil => "#E74C3C",    // Merah
        _ => "#95A5A6"
    };
}

/// <summary>
/// Data bacaan dari Ground Truth Sensor (Soil Moisture Sensor)
/// </summary>
public class SoilSensorData
{
    public string SensorId { get; set; } = "SOIL-01";
    public string NodeName { get; set; } = "Sensor Node 1";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double MoisturePercent { get; set; }
    public double TemperatureCelsius { get; set; }
    public double BatteryVoltage { get; set; }
    public int Rssi { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public bool IsOnline => (DateTime.UtcNow - LastUpdated).TotalSeconds < 30;
}

/// <summary>
/// Analisis petak lahan pertanian
/// </summary>
public class IrrigationPlot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Petak Sawah";
    public List<GeoCoordinate> PolygonCoordinates { get; set; } = new();
    public double AreaHectares { get; set; }
    
    // Hasil Analisis
    public double NormalPercent { get; set; }
    public double InconsistentPercent { get; set; }
    public double DeficitCanopyPercent { get; set; }
    public double DeficitSoilPercent { get; set; }
    public double TotalDeficitPercent => DeficitCanopyPercent + DeficitSoilPercent;

    public IrrigationPriorityLevel Priority { get; set; } = IrrigationPriorityLevel.Low;
    public double RecommendedWaterLiters { get; set; }
    public string ActionRecommendation { get; set; } = string.Empty;
}
