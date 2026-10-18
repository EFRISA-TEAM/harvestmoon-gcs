using System;
using System.Collections.Generic;
using System.Linq;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services;
using Xunit;

namespace HarvestmoonGCS.Tests.Services;

public class IrrigationServiceTests
{
    [Fact]
    public void AddDataPoint_ShouldAddPointAndTriggerEvent()
    {
        var service = new IrrigationService();
        bool eventFired = false;
        service.DataPointAdded += (s, e) =>
        {
            eventFired = true;
            Assert.Equal(IrrigationZoneType.Inconsistent, e.ZoneType);
        };

        service.AddDataPoint(-6.243, 107.365, 50, "1_ketidakmerataan_irigasi", 0.88f);

        Assert.True(eventFired);
        Assert.Single(service.DataPoints);
        Assert.Equal(IrrigationZoneType.Inconsistent, service.DataPoints[0].ZoneType);
    }

    [Fact]
    public void AnalyzePlot_WithDeficitSoil_ShouldCalculateHighWaterRequirement()
    {
        var service = new IrrigationService();

        var plot = new IrrigationPlot
        {
            Id = "PLOT-01",
            Name = "Petak Sawah Blok Timur",
            AreaHectares = 0.5, // 5,000 m2
            PolygonCoordinates = new List<GeoCoordinate>
            {
                new GeoCoordinate(-6.240, 107.360),
                new GeoCoordinate(-6.240, 107.362),
                new GeoCoordinate(-6.242, 107.362),
                new GeoCoordinate(-6.242, 107.360),
                new GeoCoordinate(-6.240, 107.360)
            }
        };

        service.AddOrUpdatePlot(plot);

        // Add 5 deficit soil detections inside the plot
        for (int i = 0; i < 5; i++)
        {
            service.AddDataPoint(-6.241, 107.361, 50, "3_defisit_air_tanah", 0.90f);
        }

        var analyzed = service.AnalyzePlot("PLOT-01");

        Assert.NotNull(analyzed);
        Assert.Equal(100.0, analyzed.DeficitSoilPercent);
        Assert.True(analyzed.RecommendedWaterLiters > 0, "Recommended water should be > 0 for soil water deficit.");
        Assert.True(analyzed.Priority >= IrrigationPriorityLevel.High);
    }

    [Fact]
    public void CalculateGroundTruthCorrelation_ShouldReturnHighCorrelationWhenSensorsMatch()
    {
        var service = new IrrigationService();

        // Dry soil detection
        service.AddDataPoint(-6.2410, 107.3610, 50, "3_defisit_air_tanah", 0.95f);
        // Ground sensor near that detection reports low moisture (dry)
        service.UpdateSoilSensor(new SoilSensorData
        {
            SensorId = "SENSOR-01",
            Latitude = -6.24105,
            Longitude = 107.36105,
            MoisturePercent = 14.5,
            BatteryVoltage = 4.1
        });

        // Healthy normal detection
        service.AddDataPoint(-6.2450, 107.3650, 50, "0_zona_normal", 0.92f);
        // Ground sensor near normal detection reports high moisture
        service.UpdateSoilSensor(new SoilSensorData
        {
            SensorId = "SENSOR-02",
            Latitude = -6.24502,
            Longitude = 107.36502,
            MoisturePercent = 55.0,
            BatteryVoltage = 4.0
        });

        var correlation = service.CalculateGroundTruthCorrelation();

        // Correlation should be positive
        Assert.True(correlation > 0.0, $"Expected correlation > 0, got {correlation}");
    }

    [Fact]
    public void LabelMode_SwitchingBetweenPlantAndZone_ShouldUpdateDisplayNamesAndRaiseEvent()
    {
        var service = new IrrigationService();
        DetectionLabelMode? receivedMode = null;
        service.LabelModeChanged += (s, m) => receivedMode = m;

        // Default is Zone mode
        Assert.Equal(DetectionLabelMode.Zone, service.LabelMode);
        Assert.Equal("Zona Normal", service.GetDisplayName("0_zona_normal"));
        Assert.Equal("Ketidakmerataan Irigasi", service.GetDisplayName("1_ketidakmerataan_irigasi"));
        Assert.Equal("Defisit Air (Kanopi)", service.GetDisplayName("2_defisit_air_kanopi"));
        Assert.Equal("Defisit Air (Tanah)", service.GetDisplayName("3_defisit_air_tanah"));

        // Switch to Plant mode
        service.LabelMode = DetectionLabelMode.Plant;
        Assert.Equal(DetectionLabelMode.Plant, service.LabelMode);
        Assert.Equal(DetectionLabelMode.Plant, receivedMode);
        Assert.Equal("Lush Green", service.GetDisplayName("0_zona_normal"));
        Assert.Equal("Inconsistent Growth", service.GetDisplayName("1_ketidakmerataan_irigasi"));
        Assert.Equal("Drought/Severe Stress", service.GetDisplayName("2_defisit_air_kanopi"));
        Assert.Equal("Bare Soil / Gap", service.GetDisplayName("3_defisit_air_tanah"));
    }

    [Fact]
    public void IrrigationDataPoint_GetZoneName_ShouldReturnCorrectModeLabel()
    {
        var dpNormal = new IrrigationDataPoint { ZoneType = IrrigationZoneType.Normal };
        var dpCanopy = new IrrigationDataPoint { ZoneType = IrrigationZoneType.DeficitCanopy };

        Assert.Equal("Zona Normal", dpNormal.GetZoneName(DetectionLabelMode.Zone));
        Assert.Equal("Lush Green", dpNormal.GetZoneName(DetectionLabelMode.Plant));

        Assert.Equal("Defisit Air (Kanopi)", dpCanopy.GetZoneName(DetectionLabelMode.Zone));
        Assert.Equal("Drought / Severe Stress", dpCanopy.GetZoneName(DetectionLabelMode.Plant));
    }
}
