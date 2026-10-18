using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services;
using Microsoft.Extensions.Logging;

namespace HarvestmoonGCS.Core.Services;

/// <summary>
/// Layanan penerima telemetri sensor tanah in-situ via LoRa (Serial USB dan UDP socket)
/// dilengkapi engine pengecekan otomatis (*Automated Health Check*) berbasis standar agronomi PDP.
/// </summary>
public class SoilMoistureService : ISoilMoistureService, IDisposable
{
    public event EventHandler<double>? MoistureUpdated;
    public event EventHandler<bool>? ConnectionStatusChanged;
    public event EventHandler<LoRaSoilPacket>? LoRaPacketReceived;
    public event EventHandler<LoRaAutoCheckSummary>? AutoCheckCompleted;

    private readonly IIrrigationService? _irrigationService;
    private readonly ILogger<SoilMoistureService>? _logger;
    private readonly object _lock = new();

    private SerialPort? _serialPort;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _udpCts;
    private Timer? _simulationTimer;
    private Timer? _autoCheckTimer;

    private bool _isConnected;
    private string _connectionSource = "NONE";
    private double _currentMoisture = 28.5;
    private LoRaAutoCheckSummary _latestAutoCheck = new();

    // Default 4 Node Skenario Riset PDP (Subang / Karawang)
    private readonly ConcurrentDictionary<string, SoilSensorData> _sensors = new();

    // Koordinat acuan petak sawah penelitian
    private const double RefLat = -6.24361;
    private const double RefLon = 107.36556;

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (_isConnected != value)
            {
                _isConnected = value;
                ConnectionStatusChanged?.Invoke(this, _isConnected);
            }
        }
    }

    public string ConnectionSource => _connectionSource;
    public double CurrentMoisture => _currentMoisture;

    public IReadOnlyList<SoilSensorData> ActiveSensors
    {
        get
        {
            lock (_lock)
            {
                return _sensors.Values.OrderBy(s => s.SensorId).ToList();
            }
        }
    }

    public LoRaAutoCheckSummary LatestAutoCheck => _latestAutoCheck;

    public SoilMoistureService(IIrrigationService? irrigationService = null, ILogger<SoilMoistureService>? logger = null)
    {
        _irrigationService = irrigationService;
        _logger = logger;

        InitializeDefaultNodes();

        // Mulai timer evaluasi otomatis latar belakang (setiap 3 detik)
        _autoCheckTimer = new Timer(_ =>
        {
            try { RunAutoCheck(); }
            catch (Exception ex) { Debug.WriteLine($"[SoilMoistureService] AutoCheck timer error: {ex.Message}"); }
        }, null, 3000, 3000);

        // Auto-listen UDP port 14552 secara default agar siap menerima streaming paket LoRa
        _ = StartUdpListenerAsync(14552);
    }

    private void InitializeDefaultNodes()
    {
        RegisterOrUpdateNode("P-01", "Probe Blok A1 (Kritis)", RefLat - 0.0003, RefLon - 0.0003, 14.8, 32.5, 4.10, -78);
        RegisterOrUpdateNode("P-02", "Probe Blok B2 (Stres)", RefLat - 0.0003, RefLon + 0.0003, 23.4, 31.0, 4.05, -82);
        RegisterOrUpdateNode("P-03", "Probe Blok C3 (Variatif)", RefLat + 0.0003, RefLon - 0.0003, 38.2, 29.4, 4.20, -75);
        RegisterOrUpdateNode("P-04", "Probe Blok D4 (Optimal)", RefLat + 0.0003, RefLon + 0.0003, 68.1, 27.6, 4.22, -71);
    }

    private SoilSensorData RegisterOrUpdateNode(string nodeId, string nodeName, double lat, double lon, double moisture, double temp, double volt, int rssi)
    {
        var sensor = _sensors.GetOrAdd(nodeId, id => new SoilSensorData
        {
            SensorId = id,
            NodeName = nodeName,
            Latitude = lat,
            Longitude = lon,
            MoisturePercent = moisture,
            TemperatureCelsius = temp,
            BatteryVoltage = volt,
            Rssi = rssi,
            LastUpdated = DateTime.UtcNow
        });

        sensor.MoisturePercent = Math.Round(moisture, 1);
        sensor.TemperatureCelsius = Math.Round(temp, 1);
        sensor.BatteryVoltage = Math.Round(volt, 2);
        sensor.Rssi = rssi;
        sensor.LastUpdated = DateTime.UtcNow;

        _irrigationService?.UpdateSoilSensor(sensor);
        return sensor;
    }

    public Task ConnectAsync(string portName)
    {
        return ConnectAsync(portName, 115200);
    }

    public Task ConnectAsync(string portName, int baudRate)
    {
        return Task.Run(() =>
        {
            try
            {
                if (string.Equals(portName, "SIMULATOR", StringComparison.OrdinalIgnoreCase))
                {
                    StartSimulation();
                    _connectionSource = "SIMULATOR";
                    IsConnected = true;
                    return;
                }

                _serialPort?.Dispose();
                _serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 1000,
                    WriteTimeout = 1000,
                    DtrEnable = true,
                    RtsEnable = true
                };

                _serialPort.DataReceived += SerialPort_DataReceived;
                _serialPort.Open();

                _connectionSource = "SERIAL";
                IsConnected = true;
                _simulationTimer?.Dispose();

                Debug.WriteLine($"[SoilMoistureService] Terhubung ke port LoRa Serial: {portName} @ {baudRate}bps");
                _logger?.LogInformation("Terhubung ke port LoRa Serial: {Port} @ {Baud}bps", portName, baudRate);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SoilMoistureService] Gagal buka port serial {portName}: {ex.Message}. Beralih ke SIMULATOR...");
                _logger?.LogWarning(ex, "Gagal terhubung port serial LoRa {Port}, fallback ke SIMULATOR", portName);

                // Fallback otomatis ke mode simulasi jika port belum dicolok
                StartSimulation();
                _connectionSource = "SIMULATOR";
                IsConnected = true;
            }
        });
    }

    public Task StartUdpListenerAsync(int port = 14552)
    {
        return Task.Run(() =>
        {
            try
            {
                _udpCts?.Cancel();
                _udpCts = new CancellationTokenSource();
                _udpClient?.Dispose();

                _udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                _connectionSource = _connectionSource == "SERIAL" ? "SERIAL+UDP" : "UDP";
                IsConnected = true;

                Debug.WriteLine($"[SoilMoistureService] LoRa UDP Listener aktif pada port {port}");
                _logger?.LogInformation("LoRa UDP Listener aktif pada port {Port}", port);

                var token = _udpCts.Token;
                _ = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested && _udpClient != null)
                    {
                        try
                        {
                            var result = await _udpClient.ReceiveAsync(token);
                            var rawStr = Encoding.UTF8.GetString(result.Buffer);
                            ProcessRawPayload(rawStr, -72);
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SoilMoistureService] UDP receive error: {ex.Message}");
                        }
                    }
                }, token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SoilMoistureService] Gagal mulai UDP listener pada port {port}: {ex.Message}");
            }
        });
    }

    private void SerialPort_DataReceived(object sender, System.IO.Ports.SerialDataReceivedEventArgs e)
    {
        try
        {
            if (_serialPort == null || !_serialPort.IsOpen) return;
            while (_serialPort.BytesToRead > 0)
            {
                string line = _serialPort.ReadLine();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    ProcessRawPayload(line.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SoilMoistureService] Serial read error: {ex.Message}");
        }
    }

    /// <summary>
    /// Memproses string payload LoRa mentah (format JSON, CSV, prefix +LORA:, atau key-value).
    /// </summary>
    public void ProcessRawPayload(string rawData, int rssi = -75)
    {
        if (string.IsNullOrWhiteSpace(rawData)) return;

        try
        {
            string clean = rawData.Trim();

            // 1. Coba Parser JSON
            if (clean.StartsWith("{") && clean.EndsWith("}"))
            {
                if (TryParseJsonPacket(clean, rssi, out var jsonPkt))
                {
                    ApplyPacket(jsonPkt);
                    return;
                }
            }

            // 2. Format AT / LoRa Prefix: +LORA:P-01,14.8,32.5,4.1,-78 atau LORA:P-01,14.8,32.5,4.1,-78
            if (clean.StartsWith("+LORA:", StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith("LORA:", StringComparison.OrdinalIgnoreCase))
            {
                var body = clean.Substring(clean.IndexOf(':') + 1).Trim();
                if (TryParseCsvPacket(body, rssi, out var csvPkt))
                {
                    ApplyPacket(csvPkt);
                    return;
                }
            }

            // 3. Format CSV Langsung: P-01,14.8,32.5,4.1,-78
            if (clean.Contains(",") && TryParseCsvPacket(clean, rssi, out var directCsvPkt))
            {
                ApplyPacket(directCsvPkt);
                return;
            }

            // 4. Format Key-Value: NODE=P-01;MOIST=14.8;TEMP=32.5;VOLT=4.1;RSSI=-78
            if (clean.Contains(";") || clean.Contains("="))
            {
                if (TryParseKeyValuePacket(clean, rssi, out var kvPkt))
                {
                    ApplyPacket(kvPkt);
                    return;
                }
            }

            // 5. Format Legacy: "MOISTURE:14.8" atau sekadar angka "14.8"
            if (clean.Contains(":"))
            {
                var parts = clean.Split(':');
                if (parts.Length == 2 && double.TryParse(parts[1], out double val))
                {
                    var pkt = new LoRaSoilPacket
                    {
                        NodeId = "P-01",
                        NodeName = "Probe Blok A1",
                        MoisturePercent = val,
                        TemperatureCelsius = 30.0,
                        BatteryVoltage = 4.1,
                        Rssi = rssi,
                        RawPayload = clean
                    };
                    ApplyPacket(pkt);
                    return;
                }
            }
            else if (double.TryParse(clean, out double val2))
            {
                var pkt = new LoRaSoilPacket
                {
                    NodeId = "P-01",
                    NodeName = "Probe Blok A1",
                    MoisturePercent = val2,
                    TemperatureCelsius = 30.0,
                    BatteryVoltage = 4.1,
                    Rssi = rssi,
                    RawPayload = clean
                };
                ApplyPacket(pkt);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SoilMoistureService] Gagal parsing payload LoRa '{rawData}': {ex.Message}");
        }
    }

    private bool TryParseJsonPacket(string json, int defaultRssi, out LoRaSoilPacket packet)
    {
        packet = new LoRaSoilPacket();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string id = "P-01";
            if (root.TryGetProperty("node", out var pNode)) id = pNode.GetString() ?? "P-01";
            else if (root.TryGetProperty("id", out var pId)) id = pId.GetString() ?? "P-01";
            else if (root.TryGetProperty("nodeId", out var pNId)) id = pNId.GetString() ?? "P-01";

            double moisture = 0;
            if (root.TryGetProperty("moisture", out var pM)) moisture = pM.GetDouble();
            else if (root.TryGetProperty("vwc", out var pV)) moisture = pV.GetDouble();
            else if (root.TryGetProperty("soil", out var pS)) moisture = pS.GetDouble();
            else if (root.TryGetProperty("val", out var pVal)) moisture = pVal.GetDouble();

            double temp = 28.0;
            if (root.TryGetProperty("temp", out var pT)) temp = pT.GetDouble();
            else if (root.TryGetProperty("temperature", out var pTemp)) temp = pTemp.GetDouble();

            double volt = 4.1;
            if (root.TryGetProperty("volt", out var pVolt)) volt = pVolt.GetDouble();
            else if (root.TryGetProperty("bat", out var pBat)) volt = pBat.GetDouble();
            else if (root.TryGetProperty("voltage", out var pVlt)) volt = pVlt.GetDouble();

            int pktRssi = defaultRssi;
            if (root.TryGetProperty("rssi", out var pRssi)) pktRssi = pRssi.GetInt32();

            double snr = 8.5;
            if (root.TryGetProperty("snr", out var pSnr)) snr = pSnr.GetDouble();

            packet.NodeId = id.ToUpperInvariant();
            packet.NodeName = GetDefaultNodeName(packet.NodeId);
            packet.MoisturePercent = moisture;
            packet.TemperatureCelsius = temp;
            packet.BatteryVoltage = volt;
            packet.Rssi = pktRssi;
            packet.Snr = snr;
            packet.RawPayload = json;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryParseCsvPacket(string csv, int defaultRssi, out LoRaSoilPacket packet)
    {
        packet = new LoRaSoilPacket();
        try
        {
            var parts = csv.Split(',');
            if (parts.Length < 2) return false;

            packet.NodeId = parts[0].Trim().ToUpperInvariant();
            if (!double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double m))
                return false;

            packet.MoisturePercent = m;
            packet.NodeName = GetDefaultNodeName(packet.NodeId);

            if (parts.Length > 2 && double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double t))
                packet.TemperatureCelsius = t;
            else packet.TemperatureCelsius = 29.5;

            if (parts.Length > 3 && double.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double v))
                packet.BatteryVoltage = v;
            else packet.BatteryVoltage = 4.1;

            if (parts.Length > 4 && int.TryParse(parts[4].Trim(), out int r))
                packet.Rssi = r;
            else packet.Rssi = defaultRssi;

            packet.RawPayload = csv;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryParseKeyValuePacket(string kv, int defaultRssi, out LoRaSoilPacket packet)
    {
        packet = new LoRaSoilPacket { Rssi = defaultRssi };
        try
        {
            var pairs = kv.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var kvParts = pair.Split(new[] { '=', ':' }, 2);
                if (kvParts.Length != 2) continue;
                var key = kvParts[0].Trim().ToUpperInvariant();
                var val = kvParts[1].Trim();

                if (key.Contains("NODE") || key.Contains("ID")) packet.NodeId = val.ToUpperInvariant();
                else if (key.Contains("MOIST") || key.Contains("VWC") || key.Contains("SOIL"))
                    _ = double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var m) && (packet.MoisturePercent = m) > -999;
                else if (key.Contains("TEMP"))
                    _ = double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var t) && (packet.TemperatureCelsius = t) > -999;
                else if (key.Contains("VOLT") || key.Contains("BAT"))
                    _ = double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) && (packet.BatteryVoltage = v) > -999;
                else if (key.Contains("RSSI"))
                    _ = int.TryParse(val, out var r) && (packet.Rssi = r) > -999;
            }

            if (string.IsNullOrEmpty(packet.NodeId)) packet.NodeId = "P-01";
            packet.NodeName = GetDefaultNodeName(packet.NodeId);
            packet.RawPayload = kv;
            return packet.MoisturePercent > 0;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyPacket(LoRaSoilPacket packet)
    {
        // Tetapkan koordinat sesuai pemetaan petak penelitian
        var (lat, lon) = GetNodeCoordinates(packet.NodeId);
        packet.Latitude = lat;
        packet.Longitude = lon;

        RegisterOrUpdateNode(packet.NodeId, packet.NodeName, lat, lon, packet.MoisturePercent, packet.TemperatureCelsius, packet.BatteryVoltage, packet.Rssi);

        _currentMoisture = Math.Round(packet.MoisturePercent, 1);
        MoistureUpdated?.Invoke(this, _currentMoisture);
        LoRaPacketReceived?.Invoke(this, packet);

        // Langsung jalankan evaluasi pengecekan otomatis
        RunAutoCheck();
    }

    public void SimulateNodePacket(string nodeId, double moisture, double temp, double volt, int rssi)
    {
        var pkt = new LoRaSoilPacket
        {
            NodeId = nodeId.ToUpperInvariant(),
            NodeName = GetDefaultNodeName(nodeId),
            MoisturePercent = moisture,
            TemperatureCelsius = temp,
            BatteryVoltage = volt,
            Rssi = rssi,
            RawPayload = $"SIM:{nodeId},{moisture},{temp},{volt},{rssi}"
        };
        ApplyPacket(pkt);
    }

    /// <summary>
    /// Mengeksekusi pengecekan otomatis (*Automated Health Check*) seluruh node LoRa.
    /// Memeriksa ambang batas kelembaban tanah (VWC %), status link/timeout, dan voltase baterai.
    /// </summary>
    public LoRaAutoCheckSummary RunAutoCheck()
    {
        var summary = new LoRaAutoCheckSummary();
        var results = new List<LoRaSoilAutoCheckResult>();
        int alertCount = 0;
        int onlineCount = 0;
        double totalMoisture = 0;

        var allSensors = ActiveSensors;
        summary.TotalNodes = allSensors.Count;

        foreach (var sensor in allSensors)
        {
            var res = new LoRaSoilAutoCheckResult
            {
                NodeId = sensor.SensorId,
                NodeName = sensor.NodeName,
                MoisturePercent = sensor.MoisturePercent,
                TemperatureCelsius = sensor.TemperatureCelsius,
                BatteryVoltage = sensor.BatteryVoltage,
                Rssi = sensor.Rssi,
                LastSeen = sensor.LastUpdated,
                IsOnline = (DateTime.UtcNow - sensor.LastUpdated).TotalSeconds < 45
            };

            if (res.IsOnline)
            {
                onlineCount++;
                totalMoisture += sensor.MoisturePercent;

                // 1. Cek Ambang Batas Agronomi
                if (sensor.MoisturePercent < 20.0)
                {
                    res.Level = SoilMoistureLevel.CriticalDeficit;
                    res.StatusText = "Kritis: Defisit Air Tanah";
                    res.AgronomicAction = "Penggenangan macak-macak 3–5 cm segera.";
                    res.HasFault = true;
                    alertCount++;
                }
                else if (sensor.MoisturePercent < 35.0)
                {
                    res.Level = SoilMoistureLevel.ModerateStress;
                    res.StatusText = "Waspada: Stres Irigasi";
                    res.AgronomicAction = "Normalisasi bukaan pintu tersier 30%.";
                    res.HasFault = true;
                    alertCount++;
                }
                else if (sensor.MoisturePercent <= 70.0)
                {
                    res.Level = SoilMoistureLevel.Optimal;
                    res.StatusText = "Optimal: Macak-macak";
                    res.AgronomicAction = "Pertahankan kondisi air macak-macak.";
                    res.HasFault = false;
                }
                else
                {
                    res.Level = SoilMoistureLevel.Waterlogged;
                    res.StatusText = "Tergenang Berlebih";
                    res.AgronomicAction = "Buka saluran pembuangan/drainase.";
                    res.HasFault = false;
                }

                // 2. Cek Baterai Node
                if (sensor.BatteryVoltage < 3.4)
                {
                    res.HardwareWarning = $"Baterai Menipis ({sensor.BatteryVoltage:F2}V < 3.4V)";
                    res.HasFault = true;
                    alertCount++;
                }
                else if (sensor.Rssi < -115)
                {
                    res.HardwareWarning = $"Sinyal Lemah ({sensor.Rssi} dBm)";
                }
            }
            else
            {
                res.StatusText = "OFFLINE (Timeout >45s)";
                res.AgronomicAction = "Periksa transmiter LoRa atau baterai node.";
                res.HasFault = true;
                alertCount++;
            }

            results.Add(res);
        }

        summary.OnlineNodes = onlineCount;
        summary.AlertNodes = alertCount;
        summary.AverageMoisture = onlineCount > 0 ? Math.Round(totalMoisture / onlineCount, 1) : 0;
        summary.NodeResults = results;
        summary.Timestamp = DateTime.UtcNow;

        if (onlineCount == 0)
        {
            summary.OverallStatus = "OFFLINE";
            summary.SummaryMessage = "Seluruh sensor tanah LoRa offline atau belum terhubung.";
        }
        else if (results.Any(r => r.Level == SoilMoistureLevel.CriticalDeficit))
        {
            summary.OverallStatus = "CRITICAL";
            var critNodes = string.Join(", ", results.Where(r => r.Level == SoilMoistureLevel.CriticalDeficit).Select(r => r.NodeId));
            summary.SummaryMessage = $"Peringatan Agronomi: Terdeteksi defisit kritis pada Node [{critNodes}]. Segera alirkan irigasi macak-macak.";
        }
        else if (alertCount > 0)
        {
            summary.OverallStatus = "WARNING";
            summary.SummaryMessage = $"Perhatian: {alertCount} node mendeteksi anomali stres irigasi / hardware warning.";
        }
        else
        {
            summary.OverallStatus = "ALL_OK";
            summary.SummaryMessage = "Semua sensor tanah LoRa normal, terhidrasi optimal, dan sinyal stabil.";
        }

        _latestAutoCheck = summary;
        AutoCheckCompleted?.Invoke(this, summary);
        return summary;
    }

    private void StartSimulation()
    {
        Debug.WriteLine("[SoilMoistureService] Mode Simulasi Multi-Node LoRa aktif...");
        _simulationTimer?.Dispose();

        var rand = new Random();
        int step = 0;

        _simulationTimer = new Timer(_ =>
        {
            try
            {
                step = (step + 1) % 4;
                string nodeId = step switch
                {
                    0 => "P-01",
                    1 => "P-02",
                    2 => "P-03",
                    _ => "P-04"
                };

                double baseMoist = step switch
                {
                    0 => 14.8, // Blok A1 Kritis
                    1 => 23.4, // Blok B2 Stres
                    2 => 38.2, // Blok C3 Variatif
                    _ => 68.1  // Blok D4 Optimal
                };

                double jitter = (rand.NextDouble() * 0.4) - 0.2;
                double moist = Math.Round(baseMoist + jitter, 1);
                double temp = 27.5 + (rand.NextDouble() * 5.0);
                double volt = 4.05 + (rand.NextDouble() * 0.15);
                int rssi = -75 - rand.Next(0, 10);

                SimulateNodePacket(nodeId, moist, temp, volt, rssi);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SoilMoistureService] Sim error: {ex.Message}");
            }
        }, null, 0, 1500);
    }

    private static string GetDefaultNodeName(string nodeId) => nodeId.ToUpperInvariant() switch
    {
        "P-01" => "Probe Blok A1 (Kritis)",
        "P-02" => "Probe Blok B2 (Stres)",
        "P-03" => "Probe Blok C3 (Variatif)",
        "P-04" => "Probe Blok D4 (Optimal)",
        _ => $"Sensor Node {nodeId}"
    };

    private static (double Lat, double Lon) GetNodeCoordinates(string nodeId) => nodeId.ToUpperInvariant() switch
    {
        "P-01" => (RefLat - 0.0003, RefLon - 0.0003),
        "P-02" => (RefLat - 0.0003, RefLon + 0.0003),
        "P-03" => (RefLat + 0.0003, RefLon - 0.0003),
        "P-04" => (RefLat + 0.0003, RefLon + 0.0003),
        _ => (RefLat, RefLon)
    };

    public void Disconnect()
    {
        try
        {
            _simulationTimer?.Dispose();
            _simulationTimer = null;

            _udpCts?.Cancel();
            _udpClient?.Dispose();
            _udpClient = null;

            if (_serialPort != null)
            {
                if (_serialPort.IsOpen)
                {
                    _serialPort.DataReceived -= SerialPort_DataReceived;
                    _serialPort.Close();
                }
                _serialPort.Dispose();
                _serialPort = null;
            }

            _connectionSource = "NONE";
            IsConnected = false;
            Debug.WriteLine("[SoilMoistureService] Layanan LoRa diputus.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SoilMoistureService] Disconnect error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _autoCheckTimer?.Dispose();
        Disconnect();
    }
}
