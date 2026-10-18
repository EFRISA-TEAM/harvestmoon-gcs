using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Models;

namespace HarvestmoonGCS.Core.Services;

public interface IIrrigationService
{
    event EventHandler<IrrigationDataPoint>? DataPointAdded;
    event EventHandler? DataCleared;
    event EventHandler<SoilSensorData>? SoilSensorUpdated;
    event EventHandler<IrrigationPlot>? PlotAnalyzed;
    event EventHandler<DetectionLabelMode>? LabelModeChanged;

    DetectionLabelMode LabelMode { get; set; }
    string GetDisplayName(string rawClass);
    (string Emoji, string Label, string Description) GetClassInfo(string rawClass);
    string GetDominantLabel(double healthyPct, double stressPct, double droughtPct, double bareSoilPct);

    IReadOnlyList<IrrigationDataPoint> DataPoints { get; }
    IReadOnlyList<SoilSensorData> SoilSensors { get; }
    IReadOnlyList<IrrigationPlot> Plots { get; }

    void AddDataPoint(double lat, double lon, double alt, string className, float confidence, double? moisture = null);
    void ClearDataPoints();

    void UpdateSoilSensor(SoilSensorData sensorData);
    void RemoveSoilSensor(string sensorId);

    void AddOrUpdatePlot(IrrigationPlot plot);
    void RemovePlot(string plotId);
    IrrigationPlot? AnalyzePlot(string plotId);
    List<IrrigationPlot> AnalyzeAllPlots();

    double CalculateGroundTruthCorrelation();
}
