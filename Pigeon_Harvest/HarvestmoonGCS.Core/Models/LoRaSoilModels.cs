using System;
using System.Collections.Generic;

namespace HarvestmoonGCS.Core.Models;

/// <summary>
/// Tingkat kelembaban volumetrik tanah (Volumetric Water Content - VWC %)
/// sesuai standar agronomi padi sawah irigasi presisi.
/// </summary>
public enum SoilMoistureLevel
{
    CriticalDeficit, // VWC < 20%: Tanah kering/retak, butuh macak-macak segera
    ModerateStress,  // VWC 20% - 35%: Stres irigasi ringan hingga sedang
    Optimal,         // VWC 35% - 70%: Kondisi ideal padi (macak-macak)
    Waterlogged      // VWC > 70%: Tergenang berlebih / butuh drainase
}

/// <summary>
/// Paket telemetri sensor tanah yang dikirim oleh modul LoRa lapangan (Ground Truth Probe).
/// </summary>
public class LoRaSoilPacket
{
    public string NodeId { get; set; } = "P-01";
    public string NodeName { get; set; } = "Probe Petak A1";
    public double MoisturePercent { get; set; }
    public double TemperatureCelsius { get; set; }
    public double BatteryVoltage { get; set; } = 4.1;
    public int Rssi { get; set; } = -78;
    public double Snr { get; set; } = 8.5;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string RawPayload { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public SoilMoistureLevel HealthLevel => MoisturePercent switch
    {
        < 20.0 => SoilMoistureLevel.CriticalDeficit,
        < 35.0 => SoilMoistureLevel.ModerateStress,
        <= 70.0 => SoilMoistureLevel.Optimal,
        _ => SoilMoistureLevel.Waterlogged
    };

    public string StatusDescription => HealthLevel switch
    {
        SoilMoistureLevel.CriticalDeficit => "Kritis: Defisit Air Tanah (<20%)",
        SoilMoistureLevel.ModerateStress => "Waspada: Stres Irigasi (20–35%)",
        SoilMoistureLevel.Optimal => "Optimal: Macak-macak (35–70%)",
        _ => "Tergenang: Perlu Drainase (>70%)"
    };

    public string ColorHex => HealthLevel switch
    {
        SoilMoistureLevel.CriticalDeficit => "#EF4444", // Merah
        SoilMoistureLevel.ModerateStress => "#F59E0B",  // Oranye/Kuning
        SoilMoistureLevel.Optimal => "#10B981",         // Hijau
        _ => "#3B82F6"                                  // Biru
    };
}

/// <summary>
/// Hasil evaluasi diagnostik otomatis per-node LoRa.
/// </summary>
public class LoRaSoilAutoCheckResult
{
    public string NodeId { get; set; } = string.Empty;
    public string NodeName { get; set; } = string.Empty;
    public double MoisturePercent { get; set; }
    public double TemperatureCelsius { get; set; }
    public double BatteryVoltage { get; set; }
    public int Rssi { get; set; }
    public bool IsOnline { get; set; } = true;
    public SoilMoistureLevel Level { get; set; } = SoilMoistureLevel.Optimal;
    public string StatusText { get; set; } = "Normal";
    public string AgronomicAction { get; set; } = string.Empty;
    public string? HardwareWarning { get; set; }
    public bool HasFault { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Ringkasan status pemeriksaan otomatis (*Auto-Check Summary*) seluruh node LoRa lapangan.
/// </summary>
public class LoRaAutoCheckSummary
{
    public int TotalNodes { get; set; }
    public int OnlineNodes { get; set; }
    public int AlertNodes { get; set; }
    public double AverageMoisture { get; set; }
    public string OverallStatus { get; set; } = "ALL_OK"; // ALL_OK, WARNING, CRITICAL, OFFLINE
    public string SummaryMessage { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public List<LoRaSoilAutoCheckResult> NodeResults { get; set; } = new();
}
