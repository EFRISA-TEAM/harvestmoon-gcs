using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MavLinkNet;
using HarvestmoonGCS.Core.Diagnostics;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services.Optimization;
using HarvestmoonGCS.Core.Transport;
using HarvestmoonGCS.Models;

namespace HarvestmoonGCS.Core.Services;

/// <summary>
/// MAVLink communication service implementation
/// Handles connection management, message parsing, and command encoding
/// </summary>
public class MavLinkService : IMavLinkService
{
    // Transport layer
    private ITransport? _transport;
    private ConnectionConfig? _currentConfig;
    
    // MAVLink parser
    private MavLinkAsyncWalker? _parser;
    
    // State
    private bool _isConnected;
    private bool _isInPlaybackMode;
    private CancellationTokenSource? _receiveCts;
    private Timer? _streamRequestTimer;
    private Timer? _heartbeatWatchdogTimer;
    private DateTime _lastHeartbeatUtc = DateTime.MinValue;
    private bool _hasReceivedHeartbeatSinceConnect;
    private readonly SemaphoreSlim _connectionGate = new SemaphoreSlim(1, 1);
    private int _unexpectedDisconnectHandling;
    private volatile bool _manualDisconnectRequested;
    
    // Telemetry data
    private FlightData _currentFlightData;
    
    // Command acknowledgment tracking
    private readonly Dictionary<MavCmd, TaskCompletionSource<MavResult>> _pendingCommands;
    private readonly object _commandLock = new object();
    
    // Mission upload/download state
    private TaskCompletionSource<bool>? _missionOperationTcs;
    private List<WaypointData>? _missionToUpload;
    private List<WaypointData>? _downloadedMission;
    private int _missionItemsExpected;
    private int _missionItemsReceived;
    private readonly object _missionLock = new object();
    
    // Parameter management state
    private Dictionary<string, float> _parameters;
    private TaskCompletionSource<bool>? _parameterOperationTcs;
    private int _parametersExpected;
    private int _parametersReceived;
    private readonly object _parameterLock = new object();

    private const int MissionOperationTimeoutMs = 30000;
    private const int ParameterOperationTimeoutMs = 5000;
    private const int PlaybackParameterOperationTimeoutMs = 1000;
    private static readonly TimeSpan SerialHandshakeHeartbeatTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitialHeartbeatTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatLossTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan HeartbeatWatchdogInterval = TimeSpan.FromSeconds(2);

    private DateTime _lastTelemetryEventTime = DateTime.MinValue;
    private TimeSpan _telemetryEventInterval = TimeSpan.FromMilliseconds(50);
    private int _targetSystemId = 1;
    private int _targetComponentId = 1;
    
    // Diagnostics
    private readonly IDiagnosticLogger _diagnosticLogger;
    private readonly IPerformanceMonitor _performanceMonitor;
    private readonly IOptimizedTelemetryHandler? _optimizedTelemetryHandler;
    
    // Connection properties
    public bool IsConnected => _isConnected;
    public bool IsInPlaybackMode => _isInPlaybackMode;
    public ConnectionType ConnectionType => _currentConfig?.Type ?? ConnectionType.TCP;
    public string ConnectionString => _currentConfig?.Address ?? "";
    
    // Events
    public event EventHandler<FlightData>? TelemetryReceived;
    public event EventHandler<string>? MessageReceived;
    public event EventHandler<bool>? ConnectionStatusChanged;
    public event EventHandler<MavLinkPacketBase>? PacketReceived;
    public event EventHandler? HeartbeatReceived;

    /// <summary>
    /// Constructor
    /// </summary>
    public MavLinkService(IOptimizedTelemetryHandler? optimizedTelemetryHandler = null)
    {
        _currentFlightData = new FlightData();
        _diagnosticLogger = new DiagnosticLogger();
        _performanceMonitor = new PerformanceMonitor();
        _pendingCommands = new Dictionary<MavCmd, TaskCompletionSource<MavResult>>();
        _parameters = new Dictionary<string, float>();
        _optimizedTelemetryHandler = optimizedTelemetryHandler;
        RefreshTelemetryDispatchInterval();
    }

    private void RefreshTelemetryDispatchInterval()
    {
        if (_optimizedTelemetryHandler == null)
        {
            _telemetryEventInterval = TimeSpan.FromMilliseconds(50);
            return;
        }

        var intervalMs = _optimizedTelemetryHandler.GetRecommendedDispatchIntervalMs();
        _telemetryEventInterval = TimeSpan.FromMilliseconds(Math.Clamp(intervalMs, 20, 500));
    }

    // ── RunCam WiFi Link / external transport injection ────────────────────────
    /// <summary>
    /// Menghubungkan MavLinkService menggunakan transport yang sudah dibuat dari luar
    /// (misalnya oleh RuncamWifiLinkService yang sudah tahu IP/port kamera).
    /// Transport di-wrap dengan MavLinkTransportAdapter agar sesuai ITransport.
    /// </summary>
    public async Task ConnectWithTransportAsync(HarvestmoonGCS.Core.Services.Connection.IMavLinkTransport transport)
    {
        if (_isInPlaybackMode) return;

        await _connectionGate.WaitAsync();
        try
        {
            _manualDisconnectRequested = false;

            if (_isConnected)
                await DisconnectInternalAsync(isManualRequest: false);

            var adapter = new HarvestmoonGCS.Core.Transport.MavLinkTransportAdapter(transport);

            // Transport sudah di-Connect oleh pemanggil, langsung pakai
            _transport = adapter;

            // Buat parser baru
            _parser = new MavLinkAsyncWalker();
            _parser.PacketReceived += OnPacketReceived;

            _isConnected = true;
            _hasReceivedHeartbeatSinceConnect = false;
            _manualDisconnectRequested = false;

            StartHeartbeatWatchdog();

            // Mulai kirim request telemetry stream (sama seperti ConnectAsync normal)
            _ = Task.Run(RequestTelemetryStreams);
            _streamRequestTimer?.Dispose();
            _streamRequestTimer = new Timer(
                _ => RequestTelemetryStreams(),
                null,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2));

            _receiveCts = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));

            ConnectionStatusChanged?.Invoke(this, true);
            Serilog.Log.Information("[MavLinkService] ConnectWithTransportAsync: terhubung via {Name}", transport.ConnectionName);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[MavLinkService] ConnectWithTransportAsync gagal");
            _transport?.Dispose();
            _transport = null;
            _parser = null;
            throw;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    // Connection management
    public async Task<bool> ConnectAsync(ConnectionConfig config)
    {
        System.Diagnostics.Debug.WriteLine($"[MavLinkService] ConnectAsync called: Type={config.Type}, Address={config.Address}, Port={config.Port}, SerialPort={config.SerialPort}, BaudRate={config.BaudRate}");
        Console.WriteLine($"[MavLinkService] Connecting: Type={config.Type}, Address={config.Address}, Port={config.Port}");
        EventHandler? heartbeatHandler = null;
        
        if (_isInPlaybackMode)
        {
            System.Diagnostics.Debug.WriteLine("[MavLinkService] Cannot connect while in playback mode");
            return false;
        }

        await _connectionGate.WaitAsync();
        try
        {
            _manualDisconnectRequested = false;

            if (_isConnected)
            {
                System.Diagnostics.Debug.WriteLine("[MavLinkService] Already connected, disconnecting first");
                await DisconnectInternalAsync(isManualRequest: false);
            }

            _currentConfig = config;

            // Create appropriate transport based on connection type
            System.Diagnostics.Debug.WriteLine($"[MavLinkService] Creating transport: {config.Type}");
            _transport = config.Type switch
            {
                ConnectionType.Serial => new SerialTransport(config.SerialPort, config.BaudRate),
                ConnectionType.UDP => new UdpTransport(config.Address, config.Port),
                ConnectionType.TCP => new TcpTransport(config.Address, config.Port),
                _ => throw new ArgumentException($"Unsupported connection type: {config.Type}")
            };

            // Connect the transport
            System.Diagnostics.Debug.WriteLine("[MavLinkService] Connecting transport...");
            bool connected = await _transport.ConnectAsync();
            if (!connected)
            {
                System.Diagnostics.Debug.WriteLine("[MavLinkService] Transport connection failed");
                Console.WriteLine("[MavLinkService] Connection failed: Transport could not connect. Check that the device is powered on and accessible.");
                _transport?.Dispose();
                _transport = null;
                return false;
            }
            System.Diagnostics.Debug.WriteLine("[MavLinkService] Transport connected successfully");
            Console.WriteLine($"[MavLinkService] Transport connected successfully for {config.Type}");

            // Initialize MAVLink parser
            _parser = new MavLinkAsyncWalker();
            _parser.PacketReceived += OnPacketReceived;
            _lastHeartbeatUtc = DateTime.UtcNow;
            _hasReceivedHeartbeatSinceConnect = false;

            var initialHeartbeatTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            heartbeatHandler = (_, _) => initialHeartbeatTcs.TrySetResult(true);
            HeartbeatReceived += heartbeatHandler;

            // Start receive loop in background
            _receiveCts = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), _receiveCts.Token);

            if (config.Type == ConnectionType.Serial)
            {
                var completed = await Task.WhenAny(initialHeartbeatTcs.Task, Task.Delay(SerialHandshakeHeartbeatTimeout));
                if (completed != initialHeartbeatTcs.Task)
                {
                    Console.WriteLine($"[MavLinkService] Serial handshake timeout: no heartbeat within {SerialHandshakeHeartbeatTimeout.TotalSeconds:F0}s on {config.SerialPort} @ {config.BaudRate}");
                    _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Serial connect failed: no heartbeat during handshake window");
                    await DisconnectInternalAsync(isManualRequest: false);
                    return false;
                }

                Console.WriteLine($"[MavLinkService] Serial heartbeat received during handshake on {config.SerialPort} @ {config.BaudRate}");
            }

            // Update connection state and emit event
            OnConnectionStatusChanged(true);

            _ = Task.Run(RequestTelemetryStreams);
            _streamRequestTimer?.Dispose();
            _streamRequestTimer = new Timer(
                _ => RequestTelemetryStreams(),
                null,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(2));

            StartHeartbeatWatchdog();

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MavLinkService] Connection failed: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[MavLinkService] Connection failed: {ex.GetType().Name}: {ex.Message}");
            _transport?.Dispose();
            _transport = null;
            _parser = null;
            return false;
        }
        finally
        {
            if (heartbeatHandler != null)
            {
                HeartbeatReceived -= heartbeatHandler;
            }
            _connectionGate.Release();
        }
    }

    public async Task<bool> ConnectAsync(string connectionString)
    {
        // Parse connection string format: "tcp://192.168.1.1:5760" or "serial://COM3:57600"
        var config = ParseConnectionString(connectionString);
        if (config == null)
        {
            return false;
        }

        return await ConnectAsync(config);
    }

    public async Task<bool> ConnectAsync(ConnectionType type, string address, int port = 14550)
    {
        var config = new ConnectionConfig
        {
            Type = type,
            Address = address,
            Port = port
        };

        return await ConnectAsync(config);
    }

    public async Task DisconnectAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
            await DisconnectInternalAsync(isManualRequest: true);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task DisconnectInternalAsync(bool isManualRequest)
    {
        try
        {
            var wasConnected = _isConnected;
            _manualDisconnectRequested = isManualRequest;

            _heartbeatWatchdogTimer?.Dispose();
            _heartbeatWatchdogTimer = null;

            _streamRequestTimer?.Dispose();
            _streamRequestTimer = null;

            _receiveCts?.Cancel();
            _receiveCts?.Dispose();
            _receiveCts = null;

            if (_transport != null)
            {
                await _transport.DisconnectAsync();
                _transport.Dispose();
                _transport = null;
            }

            _parser = null;
            _hasReceivedHeartbeatSinceConnect = false;

            lock (_commandLock)
            {
                foreach (var tcs in _pendingCommands.Values)
                {
                    tcs.TrySetCanceled();
                }
                _pendingCommands.Clear();
            }

            if (wasConnected)
            {
                OnConnectionStatusChanged(false);
            }
        }
        catch (Exception)
        {
        }
    }

    // Command operations
    public async Task<bool> SendCommandAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        static float GetFloat(string[] values, int index, float fallback)
        {
            return values.Length > index && float.TryParse(values[index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }

        switch (parts[0].Replace("-", "_", StringComparison.Ordinal).ToUpperInvariant())
        {
            case "ARM":
                return await ArmDisarmAsync(true);
            case "DISARM":
                return await ArmDisarmAsync(false);
            case "TAKEOFF":
            case "TAKE_OFF":
                await SendCommandAsync(Command.TAKE_OFF, GetFloat(parts, 1, 50.0f));
                return true;
            case "LAND":
                await SendCommandAsync(Command.LAND);
                return true;
            case "RTL":
            case "RETURN":
            case "RETURN_TO_LAUNCH":
                await SendCommandAsync(Command.RTL);
                return true;
            case "PAUSE":
                return await PauseMissionAsync();
            case "CONTINUE":
            case "RESUME":
                return await ResumeMissionAsync();
            default:
                return Enum.TryParse<Command>(parts[0], ignoreCase: true, out var parsed)
                    ? await SendParsedCommandAsync(parsed, parts)
                    : false;
        }
    }

    private async Task<bool> SendParsedCommandAsync(Command command, string[] parts)
    {
        var parameters = parts
            .Skip(1)
            .Select(value => float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : (float?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToArray();

        await SendCommandAsync(command, parameters);
        return true;
    }

    public async Task SendCommandAsync(Command command, params float[] parameters)
    {
        if (!_isConnected || _transport == null || _parser == null)
        {
            throw new InvalidOperationException("Not connected to autopilot");
        }

        // Create command message using the encoder
        var message = MavLink.MavLinkCommandEncoder.CreateCommandFromEnum(command, parameters);
        
        // Send the message
        SendMessage(message);
        
        await Task.CompletedTask;
    }

    public async Task<bool> ArmDisarmAsync(bool arm)
    {
        if (_parser == null || (!_isConnected && !_isInPlaybackMode))
        {
            return false;
        }

        try
        {
            // Create ARM/DISARM command
            // MAV_CMD_COMPONENT_ARM_DISARM (400)
            // param1: 1 to arm, 0 to disarm
            var commandLong = new UasCommandLong
            {
                TargetSystem = 1,
                TargetComponent = 0,
                Command = MavCmd.ComponentArmDisarm,
                Confirmation = 0,
                Param1 = arm ? 1.0f : 0.0f,
                Param2 = 0.0f, // 0 = normal, 21196 = force
                Param3 = 0.0f,
                Param4 = 0.0f,
                Param5 = 0.0f,
                Param6 = 0.0f,
                Param7 = 0.0f
            };

            // Register command for acknowledgment tracking
            var tcs = new TaskCompletionSource<MavResult>();
            lock (_commandLock)
            {
                _pendingCommands[MavCmd.ComponentArmDisarm] = tcs;
            }

            // Send the command
            SendMessage(commandLong);

            // Log the command
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Sent {(arm ? "ARM" : "DISARM")} command");

            // Wait for acknowledgment with timeout
            var timeoutTask = Task.Delay(5000); // 5 second timeout
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                // Timeout - remove from pending commands
                lock (_commandLock)
                {
                    _pendingCommands.Remove(MavCmd.ComponentArmDisarm);
                }
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"ARM/DISARM command timeout");
                return false;
            }

            // Get the result
            var result = await tcs.Task;
            bool success = result == MavResult.Accepted;
            
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"ARM/DISARM command result: {result}");
            return success;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error sending ARM/DISARM command: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SetFlightModeAsync(string mode)
    {
        if (_parser == null || (!_isConnected && !_isInPlaybackMode))
        {
            return false;
        }

        try
        {
            uint customMode = GetCustomModeForCurrentVehicle(mode, _currentFlightData.Type);

            // Create SET_MODE command
            // MAV_CMD_DO_SET_MODE (176)
            // param1: mode flag (1 = custom mode enabled)
            // param2: custom mode number
            var commandLong = new UasCommandLong
            {
                TargetSystem = 1,
                TargetComponent = 0,
                Command = MavCmd.DoSetMode,
                Confirmation = 0,
                Param1 = 1.0f, // MAV_MODE_FLAG_CUSTOM_MODE_ENABLED
                Param2 = customMode,
                Param3 = 0.0f,
                Param4 = 0.0f,
                Param5 = 0.0f,
                Param6 = 0.0f,
                Param7 = 0.0f
            };

            // Register command for acknowledgment tracking
            var tcs = new TaskCompletionSource<MavResult>();
            lock (_commandLock)
            {
                _pendingCommands[MavCmd.DoSetMode] = tcs;
            }

            // Send the command
            SendMessage(commandLong);

            // Log the command
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Sent SET_MODE command: {mode} (custom mode: {customMode}, vehicle type: {_currentFlightData.Type})");

            // Wait for acknowledgment with timeout
            var timeoutTask = Task.Delay(5000); // 5 second timeout
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                // Timeout - remove from pending commands
                lock (_commandLock)
                {
                    _pendingCommands.Remove(MavCmd.DoSetMode);
                }
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"SET_MODE command timeout");
                return false;
            }

            // Get the result
            var result = await tcs.Task;
            bool success = result == MavResult.Accepted;
            
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"SET_MODE command result: {result}");
            return success;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error sending SET_MODE command: {ex.Message}");
            return false;
        }
    }

    // Mission operations
    public async Task<bool> UploadMissionAsync(IEnumerable<WaypointData> waypoints)
    {
        if (_parser == null || (!_isConnected && !_isInPlaybackMode))
        {
            return false;
        }

        try
        {
            var waypointList = waypoints.ToList();
            
            TaskCompletionSource<bool> operationTcs;
            lock (_missionLock)
            {
                _missionToUpload = waypointList;
                operationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _missionOperationTcs = operationTcs;
            }

            // Send MISSION_COUNT to start upload
            var missionCount = new UasMissionCount
            {
                TargetSystem = 1,
                TargetComponent = 0,
                Count = (ushort)waypointList.Count
            };

            SendMessage(missionCount);
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Sent MISSION_COUNT: {waypointList.Count} items");

            var timeoutTask = Task.Delay(MissionOperationTimeoutMs);
            var completedTask = await Task.WhenAny(operationTcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Mission upload timeout");
                lock (_missionLock)
                {
                    if (ReferenceEquals(_missionOperationTcs, operationTcs))
                    {
                        _missionOperationTcs = null;
                    }
                    _missionToUpload = null;
                }
                return false;
            }

            var result = await operationTcs.Task;
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Mission upload {(result ? "succeeded" : "failed")}");
            lock (_missionLock)
            {
                if (ReferenceEquals(_missionOperationTcs, operationTcs))
                {
                    _missionOperationTcs = null;
                }
                _missionToUpload = null;
            }
            return result;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error uploading mission: {ex.Message}");
            lock (_missionLock)
            {
                _missionOperationTcs = null;
                _missionToUpload = null;
            }
            return false;
        }
    }

    public async Task<List<WaypointData>> DownloadMissionAsync()
    {
        if (_parser == null || (!_isConnected && !_isInPlaybackMode))
        {
            return new List<WaypointData>();
        }

        try
        {
            TaskCompletionSource<bool> operationTcs;
            lock (_missionLock)
            {
                _downloadedMission = new List<WaypointData>();
                _missionItemsExpected = 0;
                _missionItemsReceived = 0;
                operationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _missionOperationTcs = operationTcs;
            }

            // Send MISSION_REQUEST_LIST to start download
            var missionRequestList = new UasMissionRequestList
            {
                TargetSystem = 1,
                TargetComponent = 0
            };

            SendMessage(missionRequestList);
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Sent MISSION_REQUEST_LIST");

            var timeoutTask = Task.Delay(MissionOperationTimeoutMs);
            var completedTask = await Task.WhenAny(operationTcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Mission download timeout");
                lock (_missionLock)
                {
                    if (ReferenceEquals(_missionOperationTcs, operationTcs))
                    {
                        _missionOperationTcs = null;
                    }
                    _downloadedMission = null;
                }
                return new List<WaypointData>();
            }

            var result = await operationTcs.Task;
            if (!result)
            {
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Mission download failed");
                return new List<WaypointData>();
            }

            // Return downloaded mission
            List<WaypointData> mission;
            lock (_missionLock)
            {
                mission = _downloadedMission ?? new List<WaypointData>();
                _downloadedMission = null;
            }

            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Mission download succeeded: {mission.Count} items");
            return mission;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error downloading mission: {ex.Message}");
            lock (_missionLock)
            {
                _missionOperationTcs = null;
                _downloadedMission = null;
            }
            return new List<WaypointData>();
        }
    }

    // Parameter operations
    public async Task<bool> SetGeofenceAsync(IEnumerable<(double Latitude, double Longitude)> points)
    {
        if (!_isConnected || _transport == null || _parser == null)
        {
            System.Diagnostics.Debug.WriteLine("[MavLinkService.Core] SetGeofenceAsync: not connected");
            return false;
        }

        var pointList = points.ToList();
        if (pointList.Count < 3)
        {
            System.Diagnostics.Debug.WriteLine("[MavLinkService.Core] SetGeofenceAsync: need at least 3 points");
            return false;
        }

        try
        {
            byte totalCount = (byte)(pointList.Count + 1);

            // Return point (idx 0) = centroid
            double centerLat = pointList.Average(p => p.Latitude);
            double centerLon = pointList.Average(p => p.Longitude);

            var returnPoint = new UasFencePoint
            {
                TargetSystem    = (byte)_targetSystemId,
                TargetComponent = (byte)_targetComponentId,
                Idx             = 0,
                Count           = totalCount,
                Lat             = (float)centerLat,
                Lng             = (float)centerLon
            };
            SendMessage(returnPoint);
            await Task.Delay(50);

            for (int i = 0; i < pointList.Count; i++)
            {
                var fp = new UasFencePoint
                {
                    TargetSystem    = (byte)_targetSystemId,
                    TargetComponent = (byte)_targetComponentId,
                    Idx             = (byte)(i + 1),
                    Count           = totalCount,
                    Lat             = (float)pointList[i].Latitude,
                    Lng             = (float)pointList[i].Longitude
                };
                SendMessage(fp);
                await Task.Delay(50);
            }

            System.Diagnostics.Debug.WriteLine($"[MavLinkService.Core] SetGeofenceAsync: sent {totalCount} fence points");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MavLinkService.Core] SetGeofenceAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<Dictionary<string, float>> GetParametersAsync()
    {
        if (_parser == null || (!_isConnected && !_isInPlaybackMode))
        {
            return new Dictionary<string, float>();
        }

        try
        {
            TaskCompletionSource<bool> operationTcs;
            lock (_parameterLock)
            {
                if (!_isInPlaybackMode)
                {
                    _parameters.Clear();
                }
                _parametersExpected = 0;
                _parametersReceived = 0;
                operationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _parameterOperationTcs = operationTcs;
            }

            // Send PARAM_REQUEST_LIST to request all parameters
            var paramRequestList = new UasParamRequestList
            {
                TargetSystem = 1,
                TargetComponent = 0
            };

            SendMessage(paramRequestList);
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Sent PARAM_REQUEST_LIST");

            var timeoutMs = _isInPlaybackMode ? PlaybackParameterOperationTimeoutMs : ParameterOperationTimeoutMs;
            var timeoutTask = Task.Delay(timeoutMs);
            var completedTask = await Task.WhenAny(operationTcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Parameter download timeout");
                lock (_parameterLock)
                {
                    if (ReferenceEquals(_parameterOperationTcs, operationTcs))
                    {
                        _parameterOperationTcs = null;
                    }
                }
                return new Dictionary<string, float>(_parameters);
            }

            var result = await operationTcs.Task;
            if (!result)
            {
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Parameter download failed");
                return new Dictionary<string, float>(_parameters);
            }

            // Return a copy of the parameters
            Dictionary<string, float> parametersCopy;
            lock (_parameterLock)
            {
                parametersCopy = new Dictionary<string, float>(_parameters);
            }

            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Parameter download succeeded: {parametersCopy.Count} parameters");
            return parametersCopy;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error downloading parameters: {ex.Message}");
            lock (_parameterLock)
            {
                _parameterOperationTcs = null;
            }
            return new Dictionary<string, float>(_parameters);
        }
    }

    public async Task<bool> SetParameterAsync(string name, float value)
    {
        if (_parser == null || (!_isConnected && !_isInPlaybackMode))
        {
            return false;
        }

        try
        {
            // Create PARAM_SET message
            var paramSet = new UasParamSet
            {
                TargetSystem = 1,
                TargetComponent = 0,
                ParamId = ToMavLinkParamId(name),
                ParamValue = value,
                ParamType = MavParamType.Real32
            };

            SendMessage(paramSet);
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Sent PARAM_SET: {name} = {value}");

            var timeoutMs = _isInPlaybackMode ? 2000 : 5000;
            var startedAt = DateTime.UtcNow;

            while ((DateTime.UtcNow - startedAt).TotalMilliseconds < timeoutMs)
            {
                lock (_parameterLock)
                {
                    if (_parameters.TryGetValue(name, out float currentValue))
                    {
                        bool success = Math.Abs(currentValue - value) < 0.0001f;
                        if (success)
                        {
                            _diagnosticLogger.LogTelemetryEvent(DateTime.Now,
                                $"PARAM_SET result: {name} = {currentValue} (expected {value}) - success");
                            return true;
                        }
                    }
                }

                await Task.Delay(50);
            }

            lock (_parameterLock)
            {
                if (_parameters.TryGetValue(name, out float currentValue))
                {
                    _diagnosticLogger.LogTelemetryEvent(DateTime.Now,
                        $"PARAM_SET result: {name} = {currentValue} (expected {value}) - failed");
                }
            }

            return !_isInPlaybackMode;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error setting parameter: {ex.Message}");
            return false;
        }
    }

    private static char[] ToMavLinkParamId(string name)
    {
        var paramId = new char[16];
        var source = name.ToCharArray();
        Array.Copy(source, paramId, Math.Min(source.Length, paramId.Length));
        return paramId;
    }

    public Task RequestParameters()
    {
        return RequestParametersAsync();
    }

    public async Task RequestParametersAsync()
    {
        if (_parser == null || (!_isConnected && !_isInPlaybackMode))
        {
            return;
        }

        try
        {
            // Send PARAM_REQUEST_LIST to request all parameters
            var paramRequestList = new UasParamRequestList
            {
                TargetSystem = 1,
                TargetComponent = 0
            };

            SendMessage(paramRequestList);
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Sent PARAM_REQUEST_LIST");

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error requesting parameters: {ex.Message}");
        }
    }

    // Calibration operations
    public async Task SendCommandLongAsync(int command, float param1, float param2, float param3,
                                      float param4, float param5, float param6, float param7)
    {
        if (!_isConnected || _transport == null || _parser == null)
        {
            throw new InvalidOperationException("Not connected to autopilot");
        }

        try
        {
            // Create COMMAND_LONG message
            var commandLong = new UasCommandLong
            {
                TargetSystem = 1,
                TargetComponent = 0,
                Command = (MavCmd)command,
                Confirmation = 0,
                Param1 = param1,
                Param2 = param2,
                Param3 = param3,
                Param4 = param4,
                Param5 = param5,
                Param6 = param6,
                Param7 = param7
            };

            // Send the command
            SendMessage(commandLong);

            // Log the command
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Sent COMMAND_LONG: {command}");

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error sending COMMAND_LONG: {ex.Message}");
            throw;
        }
    }

    public async Task<bool> SendMotorTestAsync(int motorInstance, float throttle, float timeoutSeconds, int motorCount = 1, int throttleType = 0)
    {
        try
        {
            await SendCommandLongAsync(209, motorInstance, throttleType, throttle, timeoutSeconds, motorCount, 0, 0);
            return true;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Motor test failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SendPusherMotorTestAsync(float pwm = 1200, float timeoutSeconds = 5)
    {
        return await SendMotorTestAsync(5, pwm, timeoutSeconds, 1, 0);
    }

    public async Task<bool> SendEmergencyStopAsync()
    {
        try
        {
            // Force Disarm magic key = 21196
            await SendCommandLongAsync((int)MavCmd.ComponentArmDisarm, 0.0f, 21196.0f, 0, 0, 0, 0, 0);
            // Flight termination
            await SendCommandLongAsync(185, 1.0f, 0, 0, 0, 0, 0, 0);
            return true;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Emergency stop failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> StartCompassCalibration42424Async(byte magMask = 0)
    {
        try
        {
            await SendCommandLongAsync(42424, magMask, 1, 1, 0, 0, 0, 0);
            return true;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Start mag cal 42424 failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> AcceptCompassCalibration42425Async(byte magMask = 0)
    {
        try
        {
            await SendCommandLongAsync(42425, magMask, 0, 0, 0, 0, 0, 0);
            return true;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Accept mag cal 42425 failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> CancelCompassCalibration42426Async(byte magMask = 0)
    {
        try
        {
            await SendCommandLongAsync(42426, magMask, 0, 0, 0, 0, 0, 0);
            return true;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Cancel mag cal 42426 failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Send a command and wait for acknowledgment
    /// </summary>
    /// <param name="command">MAVLink command enum value</param>
    /// <param name="param1">Parameter 1</param>
    /// <param name="param2">Parameter 2</param>
    /// <param name="param3">Parameter 3</param>
    /// <param name="param4">Parameter 4</param>
    /// <param name="param5">Parameter 5</param>
    /// <param name="param6">Parameter 6</param>
    /// <param name="param7">Parameter 7</param>
    /// <param name="timeoutMs">Timeout in milliseconds (default 5000)</param>
    /// <returns>Command result</returns>
    public async Task<MavResult> SendCommandLongWithAckAsync(
        MavCmd command,
        float param1 = 0, float param2 = 0, float param3 = 0,
        float param4 = 0, float param5 = 0, float param6 = 0, float param7 = 0,
        int timeoutMs = 5000)
    {
        if (!_isConnected || _transport == null || _parser == null)
        {
            throw new InvalidOperationException("Not connected to autopilot");
        }

        try
        {
            // Create COMMAND_LONG message
            var commandLong = new UasCommandLong
            {
                TargetSystem = 1,
                TargetComponent = 0,
                Command = command,
                Confirmation = 0,
                Param1 = param1,
                Param2 = param2,
                Param3 = param3,
                Param4 = param4,
                Param5 = param5,
                Param6 = param6,
                Param7 = param7
            };

            // Register command for acknowledgment tracking
            var tcs = new TaskCompletionSource<MavResult>();
            lock (_commandLock)
            {
                _pendingCommands[command] = tcs;
            }

            // Send the command
            SendMessage(commandLong);

            // Log the command
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Sent COMMAND_LONG: {command}");

            // Wait for acknowledgment with timeout
            var timeoutTask = Task.Delay(timeoutMs);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                // Timeout - remove from pending commands
                lock (_commandLock)
                {
                    _pendingCommands.Remove(command);
                }
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Command {command} timeout");
                return MavResult.Failed;
            }

            // Get the result
            var result = await tcs.Task;
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Command {command} result: {result}");
            return result;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error sending COMMAND_LONG: {ex.Message}");
            throw;
        }
    }

    // Low-level message operations
    public void SendMessage(UasMessage message)
    {
        if (_parser == null)
        {
            throw new InvalidOperationException("MAVLink parser is not initialized");
        }

        if (!_isConnected && !_isInPlaybackMode)
        {
            throw new InvalidOperationException("Not connected to autopilot");
        }

        try
        {
            // Serialize the message to bytes
            // System ID: 255 (GCS), Component ID: 190 (MissionPlanner/GCS)
            byte[] buffer = _parser.SerializeMessage(message, 255, 190, true, 0);

            if (_isInPlaybackMode)
            {
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Simulated send message: {message.GetType().Name}");
                return;
            }

            // Send via transport
            Task.Run(async () => await _transport!.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false))
                .GetAwaiter()
                .GetResult();

            // Log the message
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Sent message: {message.GetType().Name}");
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error sending message: {ex.Message}");
            throw;
        }
    }

    public void SendRawBytes(byte[] data)
    {
        if (!_isConnected || _transport == null)
        {
            throw new InvalidOperationException("Not connected to autopilot");
        }

        try
        {
            // Send raw bytes via transport
            Task.Run(async () => await _transport.WriteAsync(data, 0, data.Length).ConfigureAwait(false))
                .GetAwaiter()
                .GetResult();

            // Log the raw data
            _diagnosticLogger.LogTransportData(data, data.Length);
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error sending raw bytes: {ex.Message}");
            throw;
        }
    }

    public void InjectPacket(MavLinkPacketBase packet)
    {
        // This method is used for testing to inject packets directly
        // It simulates receiving a packet from the transport layer
        if (packet != null)
        {
            OnPacketReceived(this, packet);
        }
    }

    public void ProcessTlogPacket(byte[] rawPacket)
    {
        if (!_isInPlaybackMode || _parser == null)
        {
            throw new InvalidOperationException("Not in playback mode");
        }

        try
        {
            // Feed the raw packet bytes to the parser
            // The parser will emit PacketReceived events which are handled by OnPacketReceived
            _parser.ProcessReceivedBytes(rawPacket, 0, rawPacket.Length);
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error processing TLOG packet: {ex.Message}");
        }
    }

    // Playback mode management
    public bool EnterPlaybackMode()
    {
        _isInPlaybackMode = true;

        if (_parser == null)
        {
            _parser = new MavLinkAsyncWalker();
            _parser.PacketReceived += OnPacketReceived;
        }

        return true;
    }

    // VTOL specific operations
    public async Task<bool> VtolTransitionAsync(MavLinkNet.MavVtolState targetState)
    {
        if (!_isConnected || _transport == null || _parser == null)
        {
            return false;
        }

        try
        {
            // MAV_CMD_DO_VTOL_TRANSITION (3000)
            // param1: target VTOL state (3=MC, 4=FW)
            var result = await SendCommandLongWithAckAsync(
                (MavCmd)3000, // MAV_CMD_DO_VTOL_TRANSITION
                (float)targetState
            );

            return result == MavResult.Accepted;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error in VTOL transition: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TakeoffAsync(float altitudeMeters = 10f)
    {
        if (!_isConnected || _transport == null || _parser == null)
            return false;
        try
        {
            // MAV_CMD_NAV_TAKEOFF (22): param7 = altitude in meters
            var result = await SendCommandLongWithAckAsync(
                MavCmd.NavTakeoff,
                0, 0, 0, float.NaN, float.NaN, float.NaN, altitudeMeters);
            return result == MavResult.Accepted;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MavLinkService] TakeoffAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> LandAsync()
    {
        if (!_isConnected || _transport == null || _parser == null)
            return false;
        try
        {
            // MAV_CMD_NAV_LAND (21)
            var result = await SendCommandLongWithAckAsync(
                MavCmd.NavLand,
                0, 0, 0, float.NaN, float.NaN, float.NaN, 0);
            return result == MavResult.Accepted;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MavLinkService] LandAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> VtolTakeoffAsync(double latitude, double longitude, double altitude, float heading)
    {
        if (!_isConnected || _transport == null || _parser == null)
        {
            return false;
        }

        try
        {
            // MAV_CMD_NAV_VTOL_TAKEOFF (84)
            // param4: yaw angle in degrees
            // param5: latitude
            // param6: longitude
            // param7: altitude
            var result = await SendCommandLongWithAckAsync(
                (MavCmd)84, // MAV_CMD_NAV_VTOL_TAKEOFF
                0, 0, 0,
                heading,
                (float)latitude,
                (float)longitude,
                (float)altitude
            );

            return result == MavResult.Accepted;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error in VTOL takeoff: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> VtolLandAsync(double latitude, double longitude, double altitude)
    {
        if (!_isConnected || _transport == null || _parser == null)
        {
            return false;
        }

        try
        {
            // MAV_CMD_NAV_VTOL_LAND (85)
            // param5: latitude
            // param6: longitude
            // param7: altitude
            var result = await SendCommandLongWithAckAsync(
                (MavCmd)85, // MAV_CMD_NAV_VTOL_LAND
                0, 0, 0, 0,
                (float)latitude,
                (float)longitude,
                (float)altitude
            );

            return result == MavResult.Accepted;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error in VTOL land: {ex.Message}");
            return false;
        }
    }

    // Mission execution
    public async Task<bool> StartMissionAsync(int firstItem = 0, int lastItem = 0)
    {
        if (!_isConnected || _transport == null || _parser == null)
        {
            return false;
        }

        try
        {
            // MAV_CMD_MISSION_START (300)
            // param1: first mission item to run (0 = start from beginning)
            // param2: last mission item to run (0 = run to end)
            var result = await SendCommandLongWithAckAsync(
                (MavCmd)300, // MAV_CMD_MISSION_START
                firstItem,
                lastItem
            );
            
            bool success = (result == MavResult.Accepted);
            
            if (success)
            {
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Mission started (items {firstItem} to {lastItem})");
            }
            
            return success;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error starting mission: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> PauseMissionAsync()
    {
        if (!_isConnected || _transport == null || _parser == null)
        {
            return false;
        }

        try
        {
            // MAV_CMD_DO_PAUSE_CONTINUE (193)
            // param1: 0 = pause, 1 = continue
            var result = await SendCommandLongWithAckAsync(
                (MavCmd)193, // MAV_CMD_DO_PAUSE_CONTINUE
                0 // 0 = pause
            );
            
            bool success = (result == MavResult.Accepted);
            
            if (success)
            {
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Mission paused");
            }
            
            return success;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error pausing mission: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ResumeMissionAsync()
    {
        if (!_isConnected || _transport == null || _parser == null)
        {
            return false;
        }

        try
        {
            // MAV_CMD_DO_PAUSE_CONTINUE (193)
            // param1: 0 = pause, 1 = continue
            var result = await SendCommandLongWithAckAsync(
                (MavCmd)193, // MAV_CMD_DO_PAUSE_CONTINUE
                1 // 1 = continue/resume
            );
            
            bool success = (result == MavResult.Accepted);
            
            if (success)
            {
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Mission resumed");
            }
            
            return success;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error resuming mission: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SetCurrentWaypointAsync(int waypointIndex)
    {
        if (!_isConnected || _transport == null || _parser == null)
        {
            return false;
        }

        try
        {
            // MAV_CMD_DO_SET_MISSION_CURRENT (224)
            // param1: waypoint index
            var result = await SendCommandLongWithAckAsync(
                (MavCmd)224, // MAV_CMD_DO_SET_MISSION_CURRENT
                waypointIndex
            );
            
            bool success = (result == MavResult.Accepted);
            
            if (success)
            {
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Current waypoint set to {waypointIndex}");
            }
            
            return success;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error setting current waypoint: {ex.Message}");
            return false;
        }
    }

    // Payload/Gripper operations
    public async Task<bool> GripperActionAsync(int action, int gripperNum = 0)
    {
        if (!_isConnected || _transport == null || _parser == null)
        {
            return false;
        }

        try
        {
            // MAV_CMD_DO_GRIPPER (211)
            // param1: gripper number
            // param2: gripper action (0=release, 1=grab)
            var result = await SendCommandLongWithAckAsync(
                MavCmd.DoGripper,
                gripperNum,
                action
            );

            return result == MavResult.Accepted;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error in gripper action: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> DeployPayloadAsync()
    {
        return await GripperActionAsync(0); // 0 = Release
    }

    public async Task<bool> ReleasePayloadAsync()
    {
        return await GripperActionAsync(0); // 0 = Release
    }

    public async Task<bool> ParachuteActionAsync(MavLinkNet.ParachuteAction action)
    {
        if (!_isConnected || _transport == null || _parser == null)
        {
            return false;
        }

        try
        {
            // MAV_CMD_DO_PARACHUTE (208)
            // param1: action (0=disable, 1=enable, 2=release)
            var result = await SendCommandLongWithAckAsync(
                MavCmd.DoParachute,
                (float)action
            );

            return result == MavResult.Accepted;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error in parachute action: {ex.Message}");
            return false;
        }
    }

    // Servo control for custom payload mechanisms
    public async Task<bool> SetServoAsync(int servoNum, int pwmValue)
    {
        if (!_isConnected || _transport == null || _parser == null)
        {
            return false;
        }

        try
        {
            // MAV_CMD_DO_SET_SERVO (183)
            // param1: servo number
            // param2: PWM value
            var result = await SendCommandLongWithAckAsync(
                MavCmd.DoSetServo,
                servoNum,
                pwmValue
            );

            return result == MavResult.Accepted;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error setting servo: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SetRelayAsync(int relayNum, bool state)
    {
        if (!_isConnected || _transport == null || _parser == null)
        {
            return false;
        }

        try
        {
            // MAV_CMD_DO_SET_RELAY (181)
            // param1: relay number
            // param2: state (0=off, 1=on)
            var result = await SendCommandLongWithAckAsync(
                MavCmd.DoSetRelay,
                relayNum,
                state ? 1 : 0
            );

            return result == MavResult.Accepted;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error setting relay: {ex.Message}");
            return false;
        }
    }

    // Speed control (important for fixed wing)
    public async Task<bool> ChangeSpeedAsync(float speedType, float speed, float throttle = -1)
    {
        if (!_isConnected || _transport == null || _parser == null)
        {
            return false;
        }

        try
        {
            // MAV_CMD_DO_CHANGE_SPEED (178)
            // param1: speed type (0=Airspeed, 1=Ground Speed)
            // param2: speed in m/s
            // param3: throttle percentage (-1 means no change)
            var result = await SendCommandLongWithAckAsync(
                MavCmd.DoChangeSpeed,
                speedType,
                speed,
                throttle
            );

            return result == MavResult.Accepted;
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error changing speed: {ex.Message}");
            return false;
        }
    }

    public void ExitPlaybackMode()
    {
        _isInPlaybackMode = false;
        
        // Unsubscribe from parser events
        if (_parser != null)
        {
            _parser.PacketReceived -= OnPacketReceived;
            _parser = null;
        }
        
        // Clear telemetry data
        _currentFlightData = new FlightData();
    }

    public void SimulateConnection(bool connected)
    {
        _isConnected = connected;
        OnConnectionStatusChanged(connected);
    }

    public void SimulateTelemetry(HarvestmoonGCS.Models.FlightData data)
    {
        _currentFlightData = data;
        OnTelemetryReceived();
    }

    // Diagnostic operations
    public IDiagnosticLogger GetDiagnosticLogger()
    {
        return _diagnosticLogger;
    }

    public IPerformanceMonitor GetPerformanceMonitor()
    {
        return _performanceMonitor;
    }

    public string GetDiagnosticSummary()
    {
        return _diagnosticLogger.GetLogSummary();
    }

    // Private helper methods
    
    /// <summary>
    /// Receive loop that continuously reads from transport and parses MAVLink packets
    /// </summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        if (_transport == null || _parser == null)
        {
            return;
        }

        var buffer = new byte[4096]; // Buffer for reading data
        
        try
        {
            while (!ct.IsCancellationRequested && _transport.IsConnected)
            {
                try
                {
                    // Read bytes from transport
                    int bytesRead = await _transport.ReadAsync(buffer, 0, buffer.Length, ct);
                    
                    if (bytesRead > 0)
                    {
                        // Log transport data
#if DEBUG
                        _diagnosticLogger.LogTransportData(buffer, bytesRead);
#endif
                        
                        // Feed bytes to MAVLink parser
                        // The parser will emit PacketReceived events which are handled by OnPacketReceived
                        _parser.ProcessReceivedBytes(buffer, 0, bytesRead);
                        
                        // Track performance
                        _performanceMonitor.RecordStageLatency("Transport", TimeSpan.FromMilliseconds(1));
                    }
                    else
                    {
                        // No data received, connection might be closed
                        await Task.Delay(10, ct); // Small delay to prevent tight loop
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                    break;
                }
                catch (Exception ex)
                {
                    // Log error but continue trying to receive
                    _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error in receive loop: {ex.Message}");
                    
                    // Small delay before retrying to prevent tight error loop
                    await Task.Delay(100, ct);
                }
            }

            if (!ct.IsCancellationRequested && !_manualDisconnectRequested)
            {
                await HandleUnexpectedDisconnectAsync("Transport connection closed");
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            // Log fatal error
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Fatal error in receive loop: {ex.Message}");
        }
    }

    /// <summary>
    /// Parse MAVLink packet and update telemetry data
    /// </summary>
    private void ParsePacket(MavLinkPacketBase packet)
    {
        if (packet?.Message == null)
        {
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            if (_optimizedTelemetryHandler != null &&
                !_optimizedTelemetryHandler.ShouldProcessMessage((int)packet.Message.MessageId, now))
            {
                return;
            }

            _targetSystemId = packet.SystemId;
            _targetComponentId = packet.ComponentId;

            // Update telemetry based on message type
            UpdateTelemetry(packet.Message);

            if (_optimizedTelemetryHandler != null)
            {
                RefreshTelemetryDispatchInterval();
            }

            if (now - _lastTelemetryEventTime >= _telemetryEventInterval)
            {
                _lastTelemetryEventTime = now;
                OnTelemetryReceived();
            }
        }
        catch (Exception)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Error parsing packet");
        }
    }

    /// <summary>
    /// Update FlightData from parsed MAVLink message
    /// </summary>
    private void UpdateTelemetry(UasMessage message)
    {
        switch (message)
        {
            case UasHeartbeat heartbeat:
                // Update vehicle type
                switch (heartbeat.Type)
                {
                    case MavLinkNet.MavType.Quadrotor:
                    case MavLinkNet.MavType.Hexarotor:
                    case MavLinkNet.MavType.Octorotor:
                        _currentFlightData.Tipe = TipeDevice.WAHANA;
                        _currentFlightData.Type = 1; // Copter
                        break;
                    case MavLinkNet.MavType.FixedWing:
                    case MavLinkNet.MavType.VtolTailsitterDuorotor:
                    case MavLinkNet.MavType.VtolTailsitterQuadrotor:
                    case MavLinkNet.MavType.VtolTiltrotor:
                    case MavLinkNet.MavType.VtolFixedrotor:
                    case MavLinkNet.MavType.VtolTailsitter:
                    case MavLinkNet.MavType.VtolTiltwing:
                        _currentFlightData.Tipe = TipeDevice.WAHANA;
                        _currentFlightData.Type = 2; // Fixed wing
                        break;
                    case MavLinkNet.MavType.AntennaTracker:
                        _currentFlightData.Tipe = TipeDevice.TRACKER;
                        break;
                }

                bool isArmed = (((byte)heartbeat.BaseMode) & 0x80) != 0;
                _currentFlightData.FlightMode = MapHeartbeatMode(heartbeat.CustomMode, isArmed, _currentFlightData.Type);
                _lastHeartbeatUtc = DateTime.UtcNow;
                _hasReceivedHeartbeatSinceConnect = true;
                
                // Emit heartbeat event
                HeartbeatReceived?.Invoke(this, EventArgs.Empty);
                break;

            case UasAttitude attitude:
                // Update IMU data (convert from radians to degrees)
                _currentFlightData.IMU.Yaw = (float)(attitude.Yaw * 180 / Math.PI);
                _currentFlightData.IMU.Pitch = (float)(attitude.Pitch * 180 / Math.PI);
                _currentFlightData.IMU.Roll = (float)(attitude.Roll * 180 / Math.PI);
                break;

            case UasGlobalPositionInt position:
                // Update GPS position and altitude
                _currentFlightData.GPS.Latitude = position.Lat;
                _currentFlightData.GPS.Longitude = position.Lon;
                _currentFlightData.Altitude = position.RelativeAlt; // Relative altitude in mm
                _currentFlightData.AltitudeFloat = position.RelativeAlt / 1000.0f; // Convert to meters
                break;

            case UasGpsRawInt gpsRaw:
                // Update GPS quality data
                _currentFlightData.GPS.Sats = gpsRaw.SatellitesVisible;
                _currentFlightData.GPS.Hdop = gpsRaw.Eph / 100.0f;
                break;

            case UasVfrHud vfrHud:
                // Update speed and throttle
                _currentFlightData.Speed = vfrHud.Groundspeed;
                _currentFlightData.ThrottlePercent = vfrHud.Throttle;
                break;

            case UasSysStatus sysStatus:
                // Update battery and signal data
                _currentFlightData.MavlinkMiliVolt = sysStatus.VoltageBattery;
                _currentFlightData.MavlinkCentiAmp = sysStatus.CurrentBattery;
                _currentFlightData.Signal = (byte)(100 - (sysStatus.DropRateComm / 100));
                break;

            case UasBatteryStatus batteryStatus:
                // Update battery data from BATTERY_STATUS message
                // Voltages array contains individual cell voltages in millivolts
                // CurrentBattery is in centiamps (10mA units)
                if (batteryStatus.Voltages != null && batteryStatus.Voltages.Length > 0)
                {
                    // Sum all valid cell voltages to get total battery voltage
                    int totalVoltage = 0;
                    int cellCount = 0;
                    foreach (var cellVoltage in batteryStatus.Voltages)
                    {
                        if (cellVoltage != ushort.MaxValue && cellVoltage > 0)
                        {
                            totalVoltage += cellVoltage;
                            cellCount++;
                        }
                    }
                    
                    if (cellCount > 0)
                    {
                        _currentFlightData.BatteryVolt = (ushort)totalVoltage;
                    }
                }
                
                // Update battery current (convert from centiamps to the expected format)
                if (batteryStatus.CurrentBattery != -1)
                {
                    _currentFlightData.BatteryCurr = (ushort)Math.Abs(batteryStatus.CurrentBattery);
                }
                break;

            case UasScaledPressure pressure:
                // Update barometer data
                _currentFlightData.Barometers = pressure.PressAbs;
                break;

            case UasRcChannels rcChannels:
                // Update RC channel PWM values
                _currentFlightData.ModeCh5PWM = rcChannels.Chan5Raw;
                _currentFlightData.ModeCh6PWM = rcChannels.Chan6Raw;
                _currentFlightData.ModeCh7PWM = rcChannels.Chan7Raw;
                _currentFlightData.ModeCh8PWM = rcChannels.Chan8Raw;
                _currentFlightData.ModeCh9PWM = rcChannels.Chan9Raw;
                _currentFlightData.ModeCh10PWM = rcChannels.Chan10Raw;
                _currentFlightData.ModeCh11PWM = rcChannels.Chan11Raw;
                _currentFlightData.ModeCh12PWM = rcChannels.Chan12Raw;
                _currentFlightData.ModeCh13PWM = rcChannels.Chan13Raw;
                _currentFlightData.ModeCh14PWM = rcChannels.Chan14Raw;
                _currentFlightData.ModeCh15PWM = rcChannels.Chan15Raw;
                _currentFlightData.ModeCh16PWM = rcChannels.Chan16Raw;
                break;

            case UasStatustext statusText:
                // Emit message received event for status text
                string text = new string(statusText.Text).TrimEnd('\0');
                MessageReceived?.Invoke(this, text);
                break;

            case UasCommandAck commandAck:
                // Handle command acknowledgment
                string ackMessage = $"Command {commandAck.Command} result: {commandAck.Result}";
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, ackMessage);
                MessageReceived?.Invoke(this, ackMessage);
                
                // Complete pending command if it exists
                lock (_commandLock)
                {
                    if (_pendingCommands.TryGetValue(commandAck.Command, out var tcs))
                    {
                        tcs.TrySetResult(commandAck.Result);
                        _pendingCommands.Remove(commandAck.Command);
                    }
                }
                break;

            case UasParamValue paramValue:
                // Handle parameter values
                HandleParamValue(paramValue);
                break;

            case UasMagCalProgress magCalProgress:
                // Update compass calibration progress
                if (magCalProgress.CompassId == 0)
                {
                    _currentFlightData.Compass_Progress1 = magCalProgress.CompletionPct;
                }
                else if (magCalProgress.CompassId == 1)
                {
                    _currentFlightData.Compass_Progress2 = magCalProgress.CompletionPct;
                }
                break;

            case UasMissionRequest missionRequest:
                // Handle mission item request during upload
                HandleMissionRequest(missionRequest);
                break;

            case UasMissionAck missionAck:
                // Handle mission acknowledgment
                HandleMissionAck(missionAck);
                break;

            case UasMissionCount missionCount:
                // Handle mission count during download
                HandleMissionCount(missionCount);
                break;

            case UasMissionItem missionItem:
                // Handle mission item during download
                HandleMissionItem(missionItem);
                break;

            case UasMissionItemInt missionItemInt:
                // Handle mission item (int version) during download
                HandleMissionItemInt(missionItemInt);
                break;

            default:
                // Unknown message type - log for debugging
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Unhandled message type: {message.GetType().Name}");
                break;
        }
    }

    private static FlightMode MapHeartbeatMode(uint customMode, bool isArmed, int vehicleType)
    {
        if (!isArmed)
        {
            return FlightMode.DISARMED;
        }

        if (vehicleType == 2)
        {
            return customMode switch
            {
                0 => FlightMode.MANUAL,
                2 => FlightMode.STABILIZER,
                5 => FlightMode.FBWA,
                6 => FlightMode.FBWA,
                10 => FlightMode.AUTO,
                11 => FlightMode.RTL,
                12 => FlightMode.LOITER,
                13 => FlightMode.TAKEOFF,
                17 => FlightMode.Q_Stabilize,
                18 => FlightMode.Q_Hover,
                19 => FlightMode.LOITER,
                20 => FlightMode.Q_Land,
                21 => FlightMode.RTL,
                _ => FlightMode.ARMED
            };
        }

        return customMode switch
        {
            0 => FlightMode.STABILIZER,
            1 => FlightMode.MANUAL,
            2 => FlightMode.HOLD_ALTITUDE,
            3 => FlightMode.AUTO,
            4 => FlightMode.AUTO,
            5 => FlightMode.LOITER,
            6 => FlightMode.RTL,
            7 => FlightMode.MANUAL,
            9 => FlightMode.LAND,
            16 => FlightMode.HOLD_ALTITUDE,
            17 => FlightMode.BRAKE,
            _ => FlightMode.ARMED
        };
    }

    private static uint GetCustomModeForCurrentVehicle(string mode, int vehicleType)
    {
        string normalizedMode = mode.Trim().ToUpperInvariant();

        if (vehicleType == 2)
        {
            return normalizedMode switch
            {
                "MANUAL" => 0,
                "CIRCLE" => 1,
                "STABILIZE" => 2,
                "STABILIZER" => 2,
                "TRAINING" => 3,
                "ACRO" => 4,
                "FBWA" => 5,
                "FBWB" => 6,
                "CRUISE" => 7,
                "AUTOTUNE" => 8,
                "AUTO" => 10,
                "RTL" => 11,
                "LOITER" => 12,
                "TAKEOFF" => 13,
                "Q_STABILIZE" => 17,
                "Q_HOVER" => 18,
                "Q_LOITER" => 19,
                "Q_LAND" => 20,
                "QRTL" => 21,
                "Q_RTL" => 21,
                _ => throw new ArgumentException($"Unknown fixed-wing/VTOL flight mode: {mode}")
            };
        }

        return normalizedMode switch
        {
            "STABILIZE" => 0,
            "ACRO" => 1,
            "ALT_HOLD" => 2,
            "AUTO" => 3,
            "GUIDED" => 4,
            "LOITER" => 5,
            "RTL" => 6,
            "CIRCLE" => 7,
            "LAND" => 9,
            "DRIFT" => 11,
            "SPORT" => 13,
            "FLIP" => 14,
            "AUTOTUNE" => 15,
            "POSHOLD" => 16,
            "BRAKE" => 17,
            "THROW" => 18,
            "AVOID_ADSB" => 19,
            "GUIDED_NOGPS" => 20,
            "SMART_RTL" => 21,
            "FLOWHOLD" => 22,
            "FOLLOW" => 23,
            "ZIGZAG" => 24,
            "SYSTEMID" => 25,
            "AUTOROTATE" => 26,
            "AUTO_RTL" => 27,
            _ => throw new ArgumentException($"Unknown multicopter flight mode: {mode}")
        };
    }

    /// <summary>
    /// Emit telemetry received event
    /// </summary>
    private void OnTelemetryReceived()
    {
        TelemetryReceived?.Invoke(this, _currentFlightData);
    }

    /// <summary>
    /// Emit connection status changed event
    /// </summary>
    private void OnConnectionStatusChanged(bool isConnected)
    {
        _isConnected = isConnected;
        ConnectionStatusChanged?.Invoke(this, isConnected);
    }

    private void RequestTelemetryStreams()
    {
        if (!_isConnected || _isInPlaybackMode || _transport == null || _parser == null)
        {
            return;
        }

        try
        {
            var targetSystem = (byte)Math.Clamp(_targetSystemId, 1, 255);
            var targetComponent = (byte)Math.Clamp(_targetComponentId, 0, 255);

            SendMessage(new UasRequestDataStream { TargetSystem = targetSystem, TargetComponent = targetComponent, ReqStreamId = 0, ReqMessageRate = 10, StartStop = 1 });
            SendMessage(new UasRequestDataStream { TargetSystem = targetSystem, TargetComponent = targetComponent, ReqStreamId = 1, ReqMessageRate = 5, StartStop = 1 });
            SendMessage(new UasRequestDataStream { TargetSystem = targetSystem, TargetComponent = targetComponent, ReqStreamId = 2, ReqMessageRate = 5, StartStop = 1 });
            SendMessage(new UasRequestDataStream { TargetSystem = targetSystem, TargetComponent = targetComponent, ReqStreamId = 3, ReqMessageRate = 5, StartStop = 1 });
            SendMessage(new UasRequestDataStream { TargetSystem = targetSystem, TargetComponent = targetComponent, ReqStreamId = 6, ReqMessageRate = 5, StartStop = 1 });
            SendMessage(new UasRequestDataStream { TargetSystem = targetSystem, TargetComponent = targetComponent, ReqStreamId = 10, ReqMessageRate = 10, StartStop = 1 });
            SendMessage(new UasRequestDataStream { TargetSystem = targetSystem, TargetComponent = targetComponent, ReqStreamId = 11, ReqMessageRate = 10, StartStop = 1 });
            SendMessage(new UasRequestDataStream { TargetSystem = targetSystem, TargetComponent = targetComponent, ReqStreamId = 12, ReqMessageRate = 5, StartStop = 1 });
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Error requesting telemetry streams: {ex.Message}");
        }
    }

    private void StartHeartbeatWatchdog()
    {
        _heartbeatWatchdogTimer?.Dispose();
        _heartbeatWatchdogTimer = new Timer(
            _ => _ = RunHeartbeatWatchdogTickAsync(),
            null,
            HeartbeatWatchdogInterval,
            HeartbeatWatchdogInterval);
    }

    private async Task RunHeartbeatWatchdogTickAsync()
    {
        if (!_isConnected || _manualDisconnectRequested || _isInPlaybackMode)
        {
            return;
        }

        if (_currentConfig?.Type == ConnectionType.Serial)
        {
            var sinceHeartbeat = DateTime.UtcNow - _lastHeartbeatUtc;
            if (!_hasReceivedHeartbeatSinceConnect && sinceHeartbeat > InitialHeartbeatTimeout)
            {
                await HandleUnexpectedDisconnectAsync($"No heartbeat after connect for {sinceHeartbeat.TotalSeconds:F1}s");
                return;
            }

            if (sinceHeartbeat > HeartbeatLossTimeout)
            {
                await HandleUnexpectedDisconnectAsync($"Heartbeat timeout after {sinceHeartbeat.TotalSeconds:F1}s");
            }
        }
    }

    private async Task HandleUnexpectedDisconnectAsync(string reason)
    {
        if (Interlocked.Exchange(ref _unexpectedDisconnectHandling, 1) == 1)
        {
            return;
        }

        try
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Unexpected disconnect detected: {reason}");

            await _connectionGate.WaitAsync();
            try
            {
                if (!_isConnected)
                {
                    return;
                }

                var reconnectConfig = _currentConfig;
                await DisconnectInternalAsync(isManualRequest: false);

                if (reconnectConfig == null)
                {
                    return;
                }

                if (reconnectConfig.Type == ConnectionType.Serial)
                {
                    await Task.Delay(800);
                    _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Attempting automatic serial reconnect");
                    _ = Task.Run(async () => await ConnectAsync(reconnectConfig));
                }
            }
            finally
            {
                _connectionGate.Release();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _unexpectedDisconnectHandling, 0);
        }
    }

    /// <summary>
    /// Handle packet received from MAVLink parser
    /// </summary>
    private void OnPacketReceived(object? sender, MavLinkPacketBase packet)
    {
        try
        {
            // Emit packet received event
            PacketReceived?.Invoke(this, packet);

            // Parse the packet and update telemetry
            ParsePacket(packet);
        }
        catch (Exception ex)
        {
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Packet processing error: {ex.Message}");
        }
    }

    /// <summary>
    /// Handle MISSION_REQUEST message during upload
    /// </summary>
    private void HandleMissionRequest(UasMissionRequest missionRequest)
    {
        lock (_missionLock)
        {
            if (_missionToUpload == null || _missionOperationTcs == null)
            {
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Received MISSION_REQUEST but no upload in progress");
                return;
            }

            int seq = missionRequest.Seq;
            if (seq < 0 || seq >= _missionToUpload.Count)
            {
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Invalid MISSION_REQUEST sequence: {seq}");
                _missionOperationTcs.TrySetResult(false);
                _missionOperationTcs = null;
                _missionToUpload = null;
                return;
            }

            // Send the requested mission item
            var waypoint = _missionToUpload[seq];
            var missionItemInt = new UasMissionItemInt
            {
                TargetSystem = 1,
                TargetComponent = 0,
                Seq = (ushort)seq,
                Frame = MavLinkNet.MavFrame.GlobalRelativeAlt,
                Command = (MavCmd)waypoint.Command,
                Current = (byte)(waypoint.IsCurrent ? 1 : 0),
                Autocontinue = 1,
                Param1 = (float)waypoint.Param1,
                Param2 = (float)waypoint.Param2,
                Param3 = (float)waypoint.Param3,
                Param4 = (float)waypoint.Param4,
                X = (int)(waypoint.Latitude * 1e7),
                Y = (int)(waypoint.Longitude * 1e7),
                Z = (float)waypoint.Altitude
            };

            SendMessage(missionItemInt);
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Sent MISSION_ITEM_INT {seq}/{_missionToUpload.Count}");
        }
    }

    /// <summary>
    /// Handle MISSION_ACK message
    /// </summary>
    private void HandleMissionAck(UasMissionAck missionAck)
    {
        lock (_missionLock)
        {
            if (_missionOperationTcs == null)
            {
                return;
            }

            bool success = missionAck.Type == MavMissionResult.MavMissionAccepted;
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Received MISSION_ACK: {missionAck.Type}");
            
            _missionOperationTcs.TrySetResult(success);
            _missionOperationTcs = null;
            _missionToUpload = null;
        }
    }

    /// <summary>
    /// Handle MISSION_COUNT message during download
    /// </summary>
    private void HandleMissionCount(UasMissionCount missionCount)
    {
        lock (_missionLock)
        {
            if (_downloadedMission == null || _missionOperationTcs == null)
            {
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Received MISSION_COUNT but no download in progress");
                return;
            }

            _missionItemsExpected = missionCount.Count;
            _missionItemsReceived = 0;
            _downloadedMission.Clear();

            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Received MISSION_COUNT: {_missionItemsExpected} items");

            if (_missionItemsExpected == 0)
            {
                // Empty mission
                _missionOperationTcs.TrySetResult(true);
                return;
            }

            // Request first mission item
            var missionRequest = new UasMissionRequestInt
            {
                TargetSystem = 1,
                TargetComponent = 0,
                Seq = 0
            };

            SendMessage(missionRequest);
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Sent MISSION_REQUEST_INT 0");
        }
    }

    /// <summary>
    /// Handle MISSION_ITEM message during download
    /// </summary>
    private void HandleMissionItem(UasMissionItem missionItem)
    {
        lock (_missionLock)
        {
            if (_downloadedMission == null || _missionOperationTcs == null)
            {
                return;
            }

            // Convert to WaypointData
            var waypoint = new WaypointData
            {
                Sequence = missionItem.Seq,
                Latitude = missionItem.X,
                Longitude = missionItem.Y,
                Altitude = missionItem.Z,
                Command = (WaypointCommand)missionItem.Command,
                Param1 = missionItem.Param1,
                Param2 = missionItem.Param2,
                Param3 = missionItem.Param3,
                Param4 = missionItem.Param4,
                IsCurrent = missionItem.Current == 1
            };

            _downloadedMission.Add(waypoint);
            _missionItemsReceived++;

            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Received MISSION_ITEM {_missionItemsReceived}/{_missionItemsExpected}");

            if (_missionItemsReceived >= _missionItemsExpected)
            {
                // All items received, send ACK
                var missionAck = new UasMissionAck
                {
                    TargetSystem = 1,
                    TargetComponent = 0,
                    Type = MavMissionResult.MavMissionAccepted
                };

                SendMessage(missionAck);
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Sent MISSION_ACK");

                _missionOperationTcs.TrySetResult(true);
            }
            else
            {
                // Request next item
                var missionRequest = new UasMissionRequestInt
                {
                    TargetSystem = 1,
                    TargetComponent = 0,
                    Seq = (ushort)_missionItemsReceived
                };

                SendMessage(missionRequest);
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Sent MISSION_REQUEST_INT {_missionItemsReceived}");
            }
        }
    }

    /// <summary>
    /// Handle MISSION_ITEM_INT message during download
    /// </summary>
    private void HandleMissionItemInt(UasMissionItemInt missionItemInt)
    {
        lock (_missionLock)
        {
            if (_downloadedMission == null || _missionOperationTcs == null)
            {
                return;
            }

            // Convert to WaypointData
            var waypoint = new WaypointData
            {
                Sequence = missionItemInt.Seq,
                Latitude = missionItemInt.X / 1e7,
                Longitude = missionItemInt.Y / 1e7,
                Altitude = missionItemInt.Z,
                Command = (WaypointCommand)missionItemInt.Command,
                Param1 = missionItemInt.Param1,
                Param2 = missionItemInt.Param2,
                Param3 = missionItemInt.Param3,
                Param4 = missionItemInt.Param4,
                IsCurrent = missionItemInt.Current == 1
            };

            _downloadedMission.Add(waypoint);
            _missionItemsReceived++;

            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Received MISSION_ITEM_INT {_missionItemsReceived}/{_missionItemsExpected}");

            if (_missionItemsReceived >= _missionItemsExpected)
            {
                // All items received, send ACK
                var missionAck = new UasMissionAck
                {
                    TargetSystem = 1,
                    TargetComponent = 0,
                    Type = MavMissionResult.MavMissionAccepted
                };

                SendMessage(missionAck);
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, "Sent MISSION_ACK");

                _missionOperationTcs.TrySetResult(true);
            }
            else
            {
                // Request next item
                var missionRequest = new UasMissionRequestInt
                {
                    TargetSystem = 1,
                    TargetComponent = 0,
                    Seq = (ushort)_missionItemsReceived
                };

                SendMessage(missionRequest);
                _diagnosticLogger.LogTelemetryEvent(DateTime.Now, $"Sent MISSION_REQUEST_INT {_missionItemsReceived}");
            }
        }
    }

    /// <summary>
    /// Handle PARAM_VALUE message
    /// </summary>
    private void HandleParamValue(UasParamValue paramValue)
    {
        lock (_parameterLock)
        {
            // Extract parameter name (trim null characters)
            string paramName = new string(paramValue.ParamId).TrimEnd('\0');
            
            // Store parameter in dictionary
            _parameters[paramName] = paramValue.ParamValue;
            
            // Log the parameter
            _diagnosticLogger.LogTelemetryEvent(DateTime.Now, 
                $"Received PARAM_VALUE: {paramName} = {paramValue.ParamValue} ({paramValue.ParamIndex + 1}/{paramValue.ParamCount})");
            
            // If this is part of a parameter download operation
            if (_parameterOperationTcs != null)
            {
                // First PARAM_VALUE tells us how many parameters to expect
                if (_parametersExpected == 0)
                {
                    _parametersExpected = paramValue.ParamCount;
                    _parametersReceived = 0;
                }
                
                _parametersReceived++;
                
                // Check if we've received all parameters
                if (_parametersReceived >= _parametersExpected)
                {
                    _diagnosticLogger.LogTelemetryEvent(DateTime.Now, 
                        $"All parameters received: {_parametersReceived}/{_parametersExpected}");
                    _parameterOperationTcs.TrySetResult(true);
                    _parameterOperationTcs = null;
                }
            }
        }
    }

    /// <summary>
    /// Parse connection string into ConnectionConfig
    /// Format: "tcp://192.168.1.1:5760", "udp://127.0.0.1:14550", "serial://COM3:57600"
    /// </summary>
    private ConnectionConfig? ParseConnectionString(string connectionString)
    {
        try
        {
            var uri = new Uri(connectionString);
            var config = new ConnectionConfig();

            switch (uri.Scheme.ToLower())
            {
                case "tcp":
                    config.Type = ConnectionType.TCP;
                    config.Address = uri.Host;
                    config.Port = uri.Port > 0 ? uri.Port : 5760;
                    break;

                case "udp":
                    config.Type = ConnectionType.UDP;
                    config.Address = uri.Host;
                    config.Port = uri.Port > 0 ? uri.Port : 14550;
                    break;

                case "serial":
                    config.Type = ConnectionType.Serial;
                    config.SerialPort = uri.Host;
                    config.BaudRate = uri.Port > 0 ? uri.Port : 57600;
                    break;

                default:
                    return null;
            }

            return config;
        }
        catch
        {
            return null;
        }
    }
}
