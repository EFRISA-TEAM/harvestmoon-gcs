using HarvestmoonGCS.Core.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System;
using System.Threading.Tasks;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Globalization;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Services;

namespace HarvestmoonGCS.ViewModels;

public partial class LoRaViewModel : ViewModelBase
{
    private readonly IDispatcherService _dispatcherService;
    private readonly ILoRaService _loraService;
    private readonly Timer _onlineRefreshTimer;
    private static readonly Regex AnsiRegex = new(@"\x1B\[[\d;]*[a-zA-Z]|\[[\d;]*m", RegexOptions.Compiled);

    [ObservableProperty]
    private LoRaDevice? _selectedDevice;

    [ObservableProperty]
    private ObservableCollection<LoRaNodeData> _nodes = new();

    [ObservableProperty]
    private string? _selectedPort;

    public ObservableCollection<string> AvailablePorts { get; } = new();

    public bool Node1Status => Nodes.FirstOrDefault(n => n.NodeId == 1)?.IsOnline ?? false;
    public bool Node2Status => Nodes.FirstOrDefault(n => n.NodeId == 2)?.IsOnline ?? false;
    public bool Node3Status => Nodes.FirstOrDefault(n => n.NodeId == 3)?.IsOnline ?? false;
    public bool Node4Status => Nodes.FirstOrDefault(n => n.NodeId == 4)?.IsOnline ?? false;
    public bool Node5Status => Nodes.FirstOrDefault(n => n.NodeId == 5)?.IsOnline ?? false;
    public bool AreAllNodesOnline => Nodes.Count > 0 && Nodes.All(n => n.IsOnline);
    public string OnlineNodesText => $"{Nodes.Count(n => n.IsOnline)}/{Nodes.Count}";
    public string LoRaCoordinatesText
    {
        get
        {
            var coordinates = Nodes
                .Where(n => n.HasGpsFix)
                .Select(n => $"N{n.NodeId}: {n.Latitude:F6}, {n.Longitude:F6}");

            var text = string.Join(" | ", coordinates);
            return string.IsNullOrWhiteSpace(text) ? "---, ---" : text;
        }
    }

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _eventLog = "[Ready] LoRa Network initialized.\n[Info] Scan for devices to start.\n";

    [ObservableProperty]
    private int _packetCount;

    [ObservableProperty]
    private string _lastUpdateText = "--";

    [ObservableProperty]
    private int _rssi = -100;

    [ObservableProperty]
    private int _linkQuality;

    public ObservableCollection<LoRaDevice> DiscoveredDevices { get; } = new();

    public string StatusText => IsConnected ? "Connected" : "Disconnected";
    public string ConnectionButtonText => IsConnected ? "Disconnect" : "Connect";
    public bool HasGpsFix => Nodes.Any(n => n.HasGpsFix);
    public event EventHandler<LoRaNodeData>? NodeUpdated;

    public LoRaViewModel(IDispatcherService dispatcherService, ILoRaService loraService)
    {
        _dispatcherService = dispatcherService;
        _loraService = loraService;

        // Subscribe to LoRa service events
        _loraService.DeviceDiscovered += OnDeviceDiscovered;
        _loraService.ConnectionStatusChanged += OnConnectionStatusChanged;
        _loraService.DataReceived += OnDataReceived;

        Nodes.Add(new LoRaNodeData { NodeId = 1, NodeName = "Node 1 (Gateway)", RSSI = -100 });
        Nodes.Add(new LoRaNodeData { NodeId = 2, NodeName = "Node 2 (Relay)", RSSI = -100 });
        Nodes.Add(new LoRaNodeData { NodeId = 3, NodeName = "Node 3 (End)", RSSI = -100 });
        Nodes.Add(new LoRaNodeData { NodeId = 4, NodeName = "Node 4 (End)", RSSI = -100 });
        Nodes.Add(new LoRaNodeData { NodeId = 5, NodeName = "Node 5 (End)", RSSI = -100 });

        AppendLog("[Info] LoRa Network Control initialized.");
        _onlineRefreshTimer = new Timer(_ => _dispatcherService.Enqueue(RefreshOnlineStatus), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    [RelayCommand]
    private async Task ScanDevicesAsync()
    {
        if (IsScanning)
            return;

            try
            {
                IsScanning = true;
                DiscoveredDevices.Clear();
                AvailablePorts.Clear();
                SelectedDevice = null;
                SelectedPort = null;
                AppendLog("[Info] Scanning for LoRa devices...");

            var devices = await _loraService.ScanDevicesAsync();
            
            foreach (var device in devices)
            {
                AddDiscoveredDevice(device);
            }

            AppendLog($"[Info] Found {devices.Count} LoRa device(s).");
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Scan failed: {ex.Message}");
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task ConnectToDeviceAsync(LoRaDevice? device)
    {
        if (device == null)
        {
            AppendLog("[Error] No device selected.");
            return;
        }

        if (IsConnected)
        {
            await DisconnectAsync();
        }

        try
        {
            AppendLog($"[Info] Connecting to {device.Name}...");
            
            var success = await _loraService.ConnectAsync(device);
            
            if (success)
            {
                SelectedDevice = device;
                IsConnected = true;
                Rssi = device.RSSI;
                LinkQuality = device.LinkQuality;
                AppendLog($"[Connected] {device.Name} on {device.PortName}");
                AppendLog(device.SupportsAtCommands
                    ? $"[Info] Frequency: {device.Frequency} MHz, SF: {device.SpreadingFactor}, BW: {device.Bandwidth} kHz"
                    : "[Info] Raw serial telemetry mode enabled.");
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ConnectionButtonText));
            }
            else
            {
                AppendLog($"[Error] Failed to connect to {device.Name}");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Connection failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        try
        {
            await _loraService.DisconnectAsync();
            IsConnected = false;
            SelectedDevice = null;
            AppendLog("[Disconnected]");
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ConnectionButtonText));
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Disconnect failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
        }
        else
        {
            // If there's a selected device, connect to it
            if (SelectedDevice != null)
            {
                await ConnectToDeviceAsync(SelectedDevice);
            }
            else if (DiscoveredDevices.Count > 0)
            {
                // Connect to first discovered device
                await ConnectToDeviceAsync(DiscoveredDevices[0]);
            }
            else
            {
                AppendLog("[Error] No device available. Please scan for devices first.");
            }
        }
    }

    [RelayCommand]
    private async Task SendTestDataAsync()
    {
        if (!IsConnected)
        {
            AppendLog("[Error] Not connected to any device.");
            return;
        }

        try
        {
            var testData = Encoding.UTF8.GetBytes("Hello LoRa!\n");
            var success = await _loraService.SendDataAsync(testData);
            
            if (success)
            {
                AppendLog($"[Sent] Test data: {testData.Length} bytes");
            }
            else
            {
                AppendLog("[Error] Failed to send test data");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Send failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ConfigureDeviceAsync()
    {
        if (!IsConnected)
        {
            AppendLog("[Error] Not connected to any device.");
            return;
        }

        try
        {
            var config = new LoRaConfig
            {
                Frequency = 915.0f,
                Bandwidth = 125,
                SpreadingFactor = 7,
                CodingRate = 5,
                TxPower = 17
            };

            AppendLog("[Info] Configuring device...");
            var success = await _loraService.ConfigureAsync(config);
            
            if (success)
            {
                AppendLog($"[Success] Device configured: {config.Frequency} MHz, SF{config.SpreadingFactor}, BW{config.Bandwidth}");
            }
            else
            {
                AppendLog("[Error] Failed to configure device");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Configuration failed: {ex.Message}");
        }
    }

    private void OnDeviceDiscovered(object? sender, LoRaDevice device)
    {
        _dispatcherService.Enqueue(() =>
        {
            AddDiscoveredDevice(device);
        });
    }

    private void OnConnectionStatusChanged(object? sender, bool isConnected)
    {
        _dispatcherService.Enqueue(() =>
        {
            IsConnected = isConnected;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ConnectionButtonText));
        });
    }

    private void OnDataReceived(object? sender, byte[] data)
    {
        _dispatcherService.Enqueue(() =>
        {
            var text = DecodePayload(data);
            if (ProcessReceivedText(text))
            {
                LastUpdateText = DateTime.Now.ToString("HH:mm:ss");
            }
            else
            {
                if (SelectedDevice?.SupportsAtCommands == false)
                {
                    return;
                }

                PacketCount++;
                LastUpdateText = DateTime.Now.ToString("HH:mm:ss");
                AppendLog($"[Received] {data.Length} bytes: {text}");
            }

            // Update RSSI if device is available
            if (SelectedDevice != null)
            {
                Rssi = SelectedDevice.RSSI;
                LinkQuality = SelectedDevice.LinkQuality;
            }
        });
    }

    private void AddDiscoveredDevice(LoRaDevice device)
    {
        if (DiscoveredDevices.Any(d => d.PortName == device.PortName))
        {
            return;
        }

        DiscoveredDevices.Add(device);
        if (!AvailablePorts.Contains(device.PortName))
        {
            AvailablePorts.Add(device.PortName);
        }
        SelectedDevice ??= device;
        SelectedPort ??= device.PortName;
        AppendLog($"[Discovered] {device.Name} on {device.PortName}");
    }

    private bool ProcessReceivedText(string text)
    {
        var cleanText = StripAnsiCodes(text);
        var json = ExtractJson(cleanText);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var type = GetString(root, "type");
            if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            {
                ProcessTextMessage(root);
                PacketCount++;
                RaiseNetworkSummaryChanged();
                return true;
            }

            ProcessTelemetryMessage(root);
            PacketCount++;
            RaiseNetworkSummaryChanged();
            return true;
        }
        catch (JsonException)
        {
            AppendLog($"[Warning] Invalid LoRa JSON skipped: {cleanText}");
            return false;
        }
    }

    private void ProcessTelemetryMessage(JsonElement root)
    {
        var nodeId = ResolveNodeId(
            GetString(root, "id") ??
            GetString(root, "node") ??
            GetString(root, "nodeId") ??
            GetString(root, "from"));
        var node = Nodes.FirstOrDefault(n => n.NodeId == nodeId);
        if (node == null)
        {
            return;
        }

        if (TryGetFloatAny(root, out var temp, "temp", "temperature", "t"))
            node.Temperature = temp;

        if (TryGetFloatAny(root, out var hum, "hum", "humidity", "h"))
            node.Humidity = hum;

        if (TryGetIntAny(root, out var rssi, "rssi", "RSSI"))
            node.RSSI = Math.Clamp(rssi, sbyte.MinValue, sbyte.MaxValue);

        if (TryGetDoubleAny(root, out var snr, "snr", "SNR"))
            node.SNR = snr;

        if (TryGetDoubleAny(root, out var lat, "lat", "latitude"))
            node.Latitude = lat;

        if (TryGetDoubleAny(root, out var lon, "lng", "lon", "long", "longitude"))
            node.Longitude = lon;

        if (TryGetFloatAny(root, out var alt, "alt", "altitude"))
            node.Altitude = alt;

        if (TryGetIntAny(root, out var sat, "sat", "sats", "satellites"))
            node.Satellites = sat;

        if (TryGetDoubleAny(root, out var speed, "speed", "spd"))
            node.Speed = speed;

        if (TryGetIntAny(root, out var vibration, "vibration", "vib"))
            node.Vibration = vibration;

        if (TryGetFloatAny(root, out var batteryVoltage, "batv", "batteryVoltage", "voltage"))
            node.BatteryVoltage = batteryVoltage;

        if (TryGetIntAny(root, out var batteryPercent, "bat", "battery", "batteryPercent"))
            node.BatteryPercent = Math.Clamp(batteryPercent, 0, 100);

        if (TryGetLongAny(root, out var packet, "packet", "pkt", "seq"))
            node.PacketNumber = packet;

        node.LastSeen = DateTime.Now;

        var nodeLabel = $"TX{node.NodeId}";
        AppendLog($"[{nodeLabel}] T={node.Temperature:F1}C H={node.Humidity:F1}% V={node.Vibration} GPS={node.Latitude:F5},{node.Longitude:F5} RSSI={node.RSSI}dBm Pkt#{node.PacketNumber}");
        NodeUpdated?.Invoke(this, node);
    }

    private void ProcessTextMessage(JsonElement root)
    {
        var from = GetString(root, "from") ?? GetString(root, "id") ?? "Unknown";
        var text = GetString(root, "text") ?? string.Empty;
        var nodeId = ResolveNodeId(from, 0);

        var rssi = GetIntAny(root, -100, "rssi", "RSSI");
        var snr = GetDoubleAny(root, 0, "snr", "SNR");

        if (nodeId > 0)
        {
            var node = Nodes.FirstOrDefault(n => n.NodeId == nodeId);
            if (node != null)
            {
                node.RSSI = Math.Clamp(rssi, sbyte.MinValue, sbyte.MaxValue);
                node.SNR = snr;
                node.LastSeen = DateTime.Now;
                NodeUpdated?.Invoke(this, node);
            }
        }

        AppendLog($"[{from}] {text} (RSSI:{rssi}dBm, SNR:{snr:F1}dB)");
    }

    private void RaiseNetworkSummaryChanged()
    {
        OnPropertyChanged(nameof(Node1Status));
        OnPropertyChanged(nameof(Node2Status));
        OnPropertyChanged(nameof(Node3Status));
        OnPropertyChanged(nameof(Node4Status));
        OnPropertyChanged(nameof(Node5Status));
        OnPropertyChanged(nameof(AreAllNodesOnline));
        OnPropertyChanged(nameof(OnlineNodesText));
        OnPropertyChanged(nameof(LoRaCoordinatesText));
        OnPropertyChanged(nameof(HasGpsFix));
    }

    private void RefreshOnlineStatus()
    {
        var statusChanged = false;

        foreach (var node in Nodes)
        {
            var wasOnline = node.IsOnline;
            node.RefreshOnlineStatus();

            if (wasOnline != node.IsOnline)
            {
                statusChanged = true;
                NodeUpdated?.Invoke(this, node);
            }
        }

        if (statusChanged)
        {
            RaiseNetworkSummaryChanged();
        }
    }

    partial void OnSelectedDeviceChanged(LoRaDevice? value)
    {
        if (value != null && SelectedPort != value.PortName)
        {
            SelectedPort = value.PortName;
        }
    }

    partial void OnSelectedPortChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var device = DiscoveredDevices.FirstOrDefault(d => d.PortName == value);
        if (device != null && SelectedDevice != device)
        {
            SelectedDevice = device;
        }
    }

    private static string DecodePayload(byte[] data)
    {
        try
        {
            return Encoding.UTF8.GetString(data).Trim();
        }
        catch
        {
            return BitConverter.ToString(data);
        }
    }

    private static string StripAnsiCodes(string input)
    {
        return string.IsNullOrWhiteSpace(input) ? input : AnsiRegex.Replace(input, string.Empty).Trim();
    }

    private static string? ExtractJson(string input)
    {
        var jsonStart = input.IndexOf('{');
        var jsonEnd = input.LastIndexOf('}');
        return jsonStart >= 0 && jsonEnd > jsonStart
            ? input.Substring(jsonStart, jsonEnd - jsonStart + 1)
            : null;
    }

    private static int ResolveNodeId(string? id, int defaultNodeId = 1)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return defaultNodeId;
        }

        if (id.Equals("TX1", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (id.Equals("TX2", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (id.Equals("TX3", StringComparison.OrdinalIgnoreCase))
            return 3;

        if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericId))
            return numericId;

        var match = Regex.Match(id, @"\d+");
        if (match.Success && int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out numericId))
            return numericId;

        return defaultNodeId;
    }

    private static string? GetString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;
    }

    private static int GetInt(JsonElement root, string name, int fallback)
    {
        return TryGetInt(root, name, out var value) ? value : fallback;
    }

    private static int GetIntAny(JsonElement root, int fallback, params string[] names)
    {
        return TryGetIntAny(root, out var value, names) ? value : fallback;
    }

    private static double GetDouble(JsonElement root, string name, double fallback)
    {
        return TryGetDouble(root, name, out var value) ? value : fallback;
    }

    private static double GetDoubleAny(JsonElement root, double fallback, params string[] names)
    {
        return TryGetDoubleAny(root, out var value, names) ? value : fallback;
    }

    private static bool TryGetInt(JsonElement root, string name, out int value)
    {
        value = default;
        if (!root.TryGetProperty(name, out var element))
            return false;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private static bool TryGetLong(JsonElement root, string name, out long value)
    {
        value = default;
        if (!root.TryGetProperty(name, out var element))
            return false;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private static bool TryGetLongAny(JsonElement root, out long value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetLong(root, name, out value))
                return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetFloat(JsonElement root, string name, out float value)
    {
        value = default;
        if (!TryGetDouble(root, name, out var doubleValue))
            return false;

        value = (float)doubleValue;
        return true;
    }

    private static bool TryGetDouble(JsonElement root, string name, out double value)
    {
        value = default;
        if (!root.TryGetProperty(name, out var element))
            return false;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private static bool TryGetIntAny(JsonElement root, out int value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetInt(root, name, out value))
                return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetFloatAny(JsonElement root, out float value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetFloat(root, name, out value))
                return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetDoubleAny(JsonElement root, out double value, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetDouble(root, name, out value))
                return true;
        }

        value = default;
        return false;
    }

    private void AppendLog(string text)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var line = text.StartsWith("[") ? $"[{timestamp}] {text}" : $"[{timestamp}] {text}";
        EventLog += line.EndsWith("\n") ? line : line + "\n";

        var lines = EventLog.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 300)
        {
            EventLog = string.Join('\n', lines.Skip(lines.Length - 300)) + "\n";
        }
    }

    public void SendRawCommand(string command)
    {
        _ = SendRawCommandAsync(command);
    }

    private async Task SendRawCommandAsync(string command)
    {
        if (!IsConnected)
        {
            AppendLog("[Error] Not connected to any device.");
            return;
        }

        try
        {
            var payload = Encoding.UTF8.GetBytes(command + "\n");
            var success = await _loraService.SendDataAsync(payload);
            if (success)
            {
                AppendLog($"[Sent] {command}");
            }
            else
            {
                AppendLog("[Error] Failed to send command");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Send failed: {ex.Message}");
        }
    }
}
