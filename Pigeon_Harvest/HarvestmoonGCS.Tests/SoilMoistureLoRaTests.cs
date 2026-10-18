using System;
using System.Linq;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Services;
using Xunit;

namespace HarvestmoonGCS.Tests;

public class SoilMoistureLoRaTests
{
    [Fact]
    public void ProcessRawPayload_WithJsonFormat_ShouldUpdateCorrectNode()
    {
        // Arrange
        var irrigationService = new IrrigationService();
        using var service = new SoilMoistureService(irrigationService);
        string json = "{\"node\":\"P-01\",\"moisture\":14.8,\"temp\":32.5,\"volt\":4.10,\"rssi\":-78}";

        // Act
        service.ProcessRawPayload(json);

        // Assert
        var p01 = service.ActiveSensors.FirstOrDefault(s => s.SensorId == "P-01");
        Assert.NotNull(p01);
        Assert.Equal(14.8, p01.MoisturePercent);
        Assert.Equal(32.5, p01.TemperatureCelsius);
        Assert.Equal(4.10, p01.BatteryVoltage);
        Assert.Equal(-78, p01.Rssi);
    }

    [Fact]
    public void ProcessRawPayload_WithCsvFormat_ShouldUpdateCorrectNode()
    {
        // Arrange
        var irrigationService = new IrrigationService();
        using var service = new SoilMoistureService(irrigationService);
        string csv = "+LORA:P-02,23.4,31.0,4.05,-82";

        // Act
        service.ProcessRawPayload(csv);

        // Assert
        var p02 = service.ActiveSensors.FirstOrDefault(s => s.SensorId == "P-02");
        Assert.NotNull(p02);
        Assert.Equal(23.4, p02.MoisturePercent);
        Assert.Equal(31.0, p02.TemperatureCelsius);
        Assert.Equal(4.05, p02.BatteryVoltage);
        Assert.Equal(-82, p02.Rssi);
    }

    [Fact]
    public void ProcessRawPayload_WithKeyValueFormat_ShouldUpdateCorrectNode()
    {
        // Arrange
        var irrigationService = new IrrigationService();
        using var service = new SoilMoistureService(irrigationService);
        string kv = "NODE=P-03;VWC=38.2;TEMP=29.4;VOLT=4.20;RSSI=-75";

        // Act
        service.ProcessRawPayload(kv);

        // Assert
        var p03 = service.ActiveSensors.FirstOrDefault(s => s.SensorId == "P-03");
        Assert.NotNull(p03);
        Assert.Equal(38.2, p03.MoisturePercent);
        Assert.Equal(29.4, p03.TemperatureCelsius);
    }

    [Fact]
    public void RunAutoCheck_WithCriticalDeficit_ShouldIdentifyAlertAndAction()
    {
        // Arrange
        var irrigationService = new IrrigationService();
        using var service = new SoilMoistureService(irrigationService);

        // Inject node P-01 as critical
        service.SimulateNodePacket("P-01", 14.8, 32.5, 4.10, -78);

        // Act
        var summary = service.RunAutoCheck();

        // Assert
        Assert.NotNull(summary);
        Assert.Equal("CRITICAL", summary.OverallStatus);
        Assert.True(summary.AlertNodes > 0);

        var p01Result = summary.NodeResults.FirstOrDefault(r => r.NodeId == "P-01");
        Assert.NotNull(p01Result);
        Assert.Equal(SoilMoistureLevel.CriticalDeficit, p01Result.Level);
        Assert.True(p01Result.HasFault);
        Assert.Contains("macak-macak", p01Result.AgronomicAction);
    }

    [Fact]
    public void RunAutoCheck_WithOptimalConditions_ShouldReportAllOk()
    {
        // Arrange
        var irrigationService = new IrrigationService();
        using var service = new SoilMoistureService(irrigationService);

        // Set all nodes to optimal range (35% - 70%)
        service.SimulateNodePacket("P-01", 55.0, 28.0, 4.10, -75);
        service.SimulateNodePacket("P-02", 52.0, 28.0, 4.10, -75);
        service.SimulateNodePacket("P-03", 58.0, 28.0, 4.10, -75);
        service.SimulateNodePacket("P-04", 65.0, 28.0, 4.10, -75);

        // Act
        var summary = service.RunAutoCheck();

        // Assert
        Assert.Equal("ALL_OK", summary.OverallStatus);
        Assert.Equal(0, summary.AlertNodes);
    }

    [Fact]
    public void RunAutoCheck_WithLowBattery_ShouldFlagHardwareWarning()
    {
        // Arrange
        var irrigationService = new IrrigationService();
        using var service = new SoilMoistureService(irrigationService);

        // Simulate P-02 with low battery (< 3.4V)
        service.SimulateNodePacket("P-02", 50.0, 28.0, 3.20, -75);

        // Act
        var summary = service.RunAutoCheck();

        // Assert
        var p02 = summary.NodeResults.FirstOrDefault(r => r.NodeId == "P-02");
        Assert.NotNull(p02);
        Assert.NotNull(p02.HardwareWarning);
        Assert.Contains("Baterai Menipis", p02.HardwareWarning);
    }
}
