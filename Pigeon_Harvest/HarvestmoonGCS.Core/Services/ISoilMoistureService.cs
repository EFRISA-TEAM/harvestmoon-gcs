using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Models;

namespace HarvestmoonGCS.Core.Services;

/// <summary>
/// Layanan penerima data sensor tanah in-situ melalui LoRa (Serial USB / UDP)
/// dan mesin evaluasi diagnostik agronomi otomatis.
/// </summary>
public interface ISoilMoistureService
{
    event EventHandler<double>? MoistureUpdated;
    event EventHandler<bool>? ConnectionStatusChanged;
    event EventHandler<LoRaSoilPacket>? LoRaPacketReceived;
    event EventHandler<LoRaAutoCheckSummary>? AutoCheckCompleted;
    
    bool IsConnected { get; }
    string ConnectionSource { get; }
    double CurrentMoisture { get; }
    IReadOnlyList<SoilSensorData> ActiveSensors { get; }
    LoRaAutoCheckSummary LatestAutoCheck { get; }
    
    Task ConnectAsync(string portName);
    Task ConnectAsync(string portName, int baudRate);
    Task StartUdpListenerAsync(int port = 14552);
    void Disconnect();
    
    void ProcessRawPayload(string rawData, int rssi = -75);
    LoRaAutoCheckSummary RunAutoCheck();
    void SimulateNodePacket(string nodeId, double moisture, double temp, double volt, int rssi);
}
