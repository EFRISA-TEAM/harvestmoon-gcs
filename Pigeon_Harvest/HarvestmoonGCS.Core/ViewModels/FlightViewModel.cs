using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using HarvestmoonGCS.Core.Diagnostics;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Core.Services.MavLink;
using HarvestmoonGCS.Models;
using HarvestmoonGCS.Services;

namespace HarvestmoonGCS.Core.ViewModels;

public class FlightViewModel : ViewModelBase
{
    private readonly IMavLinkService _mavLinkService;
    private readonly IDispatcherService? _dispatcherService;
    private readonly IDiagnosticLogger? _diagnosticLogger;

    private TelemetryData _telemetryData = new();
    private bool _isConnected;
    private HarvestmoonGCS.Core.Models.ConnectionType _connectionType = HarvestmoonGCS.Core.Models.ConnectionType.TCP;
    private string _connectionAddress = "127.0.0.1";
    private int _connectionPort = 14550;
    private FlightMode _flightMode = FlightMode.MANUAL;
    private bool _isArmed;
    private ObservableCollection<ServoOutputItem> _servoOutputs = new();
    private byte[]? _cameraFrame;
    private DateTime _lastTelemetryDebugLogTime = DateTime.MinValue;

    // Legacy compatibility (FlightPage old bindings)
    private string _connectionString = "tcp://127.0.0.1:14550";
    private string _selectedCommandStr = "Land";
    private string _streamOutput = string.Empty;
    private bool _isRecording;
    private bool _isYoloEnabled;
    private bool _suppressConnectionSync;

    private readonly ObservableCollection<string> _commandList = new()
    {
        "Land",
        "Take Off",
        "RTL",
        "Loiter",
        "Start Mission",
        "Get Param",
        "Stabilize",
        "Guided",
        "Auto",
        "Alt Hold",
        "Position Hold",
        "Brake"
    };

    public TelemetryData TelemetryData
    {
        get => _telemetryData;
        set
        {
            if (SetProperty(ref _telemetryData, value))
            {
                OnPropertyChanged(nameof(Telemetry));
            }
        }
    }

    // Alias for legacy XAML path: Telemetry.*
    public TelemetryData Telemetry => TelemetryData;

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(ConnectionStatus));
            }
        }
    }

    public HarvestmoonGCS.Core.Models.ConnectionType ConnectionType
    {
        get => _connectionType;
        set
        {
            if (SetProperty(ref _connectionType, value))
            {
                SyncConnectionStringFromFields();
            }
        }
    }

    public string ConnectionAddress
    {
        get => _connectionAddress;
        set
        {
            if (SetProperty(ref _connectionAddress, value))
            {
                SyncConnectionStringFromFields();
            }
        }
    }

    public int ConnectionPort
    {
        get => _connectionPort;
        set
        {
            if (SetProperty(ref _connectionPort, value))
            {
                SyncConnectionStringFromFields();
            }
        }
    }

    public FlightMode FlightMode
    {
        get => _flightMode;
        set => SetProperty(ref _flightMode, value);
    }

    public bool IsArmed
    {
        get => _isArmed;
        set => SetProperty(ref _isArmed, value);
    }

    public ObservableCollection<ServoOutputItem> ServoOutputs
    {
        get => _servoOutputs;
        set => SetProperty(ref _servoOutputs, value);
    }

    public byte[]? CameraFrame
    {
        get => _cameraFrame;
        set => SetProperty(ref _cameraFrame, value);
    }

    // Legacy compatibility properties expected by old FlightPage
    public string ConnectionString
    {
        get => _connectionString;
        set
        {
            var normalized = NormalizeConnectionString(value);
            if (SetProperty(ref _connectionString, normalized))
            {
                SyncConnectionFieldsFromConnectionString(normalized);
            }
        }
    }

    public string ConnectionStatus => IsConnected ? "ONLINE" : "OFFLINE";

    public ObservableCollection<string> CommandList => _commandList;

    public string SelectedCommandStr
    {
        get => _selectedCommandStr;
        set => SetProperty(ref _selectedCommandStr, value);
    }

    public string StreamOutput
    {
        get => _streamOutput;
        set => SetProperty(ref _streamOutput, value);
    }

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ArmCommand { get; }
    public ICommand DisarmCommand { get; }
    public ICommand TakeoffCommand { get; }
    public ICommand LandCommand { get; }
    public ICommand RTLCommand { get; }
    public ICommand TakePictureCommand { get; }
    public ICommand StartStreamCommand { get; }
    public ICommand StopStreamCommand { get; }
    public ICommand RequestParamsCommand { get; }

    // Legacy compatibility commands expected by old FlightPage
    public ICommand ToggleArmCommand { get; }
    public ICommand SendSelectedCommandCommand { get; }
    public ICommand ReadStreamCommand { get; }
    public ICommand ToggleRecordingCommand { get; }
    public ICommand ToggleYoloCommand { get; }
    public ICommand StartCameraCommand { get; }

    public FlightViewModel(IMavLinkService mavLinkService, IDispatcherService? dispatcherService = null)
    {
        _mavLinkService = mavLinkService;
        _dispatcherService = dispatcherService;

        _diagnosticLogger = _mavLinkService.GetDiagnosticLogger();

        ConnectCommand = new RelayCommand(Connect);
        DisconnectCommand = new RelayCommand(Disconnect);
        ArmCommand = new RelayCommand(Arm);
        DisarmCommand = new RelayCommand(Disarm);
        TakeoffCommand = new RelayCommand(Takeoff);
        LandCommand = new RelayCommand(Land);
        RTLCommand = new RelayCommand(RTL);
        TakePictureCommand = new RelayCommand(TakePicture);
        StartStreamCommand = new RelayCommand(StartStream);
        StopStreamCommand = new RelayCommand(StopStream);
        RequestParamsCommand = new RelayCommand(RequestParams);

        ToggleArmCommand = new RelayCommand(ToggleArm);
        SendSelectedCommandCommand = new RelayCommand(SendSelectedCommand);
        ReadStreamCommand = new RelayCommand(ReadStream);
        ToggleRecordingCommand = new RelayCommand(ToggleRecording);
        ToggleYoloCommand = new RelayCommand(ToggleYolo);
        StartCameraCommand = new RelayCommand(StartCamera);

        SyncConnectionStringFromFields();

        _mavLinkService.TelemetryReceived += OnTelemetryReceived;
        _mavLinkService.ConnectionStatusChanged += OnConnectionStatusChanged;

        System.Diagnostics.Debug.WriteLine($"[FlightViewModel] Initialized with diagnostic logging: {_diagnosticLogger != null}");
    }

    public async void Connect()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[FlightViewModel] Connect called: Type={ConnectionType}, Address={ConnectionAddress}, Port={ConnectionPort}");
            Console.WriteLine($"[FlightViewModel] Connect called: Type={ConnectionType}, Address={ConnectionAddress}, Port={ConnectionPort}");

            var config = new ConnectionConfig
            {
                Type = ConnectionType,
                Address = ConnectionAddress,
                Port = ConnectionPort
            };

            if (ConnectionType == HarvestmoonGCS.Core.Models.ConnectionType.Serial)
            {
                config.SerialPort = ConnectionAddress;
                config.BaudRate = ConnectionPort;
                System.Diagnostics.Debug.WriteLine($"[FlightViewModel] Serial config: Port={config.SerialPort}, BaudRate={config.BaudRate}");
                Console.WriteLine($"[FlightViewModel] Serial config: Port={config.SerialPort}, BaudRate={config.BaudRate}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[FlightViewModel] Network config: Address={config.Address}, Port={config.Port}");
                Console.WriteLine($"[FlightViewModel] Network config: Address={config.Address}, Port={config.Port}");
            }

            var ok = await _mavLinkService.ConnectAsync(config);
            IsConnected = ok;

            System.Diagnostics.Debug.WriteLine($"[FlightViewModel] Connection result: {ok}");
            Console.WriteLine($"[FlightViewModel] Connection result: {ok}");

            if (!ok)
            {
                System.Diagnostics.Debug.WriteLine("[FlightViewModel] Connection failed - check logs for details");
                Console.WriteLine("[FlightViewModel] Connection failed - check logs for details");
                AppendStreamOutput("Connect failed");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(_mavLinkService.ConnectionString))
                {
                    SetConnectionStringWithoutParsing(_mavLinkService.ConnectionString);
                }
                AppendStreamOutput($"Connected: {ConnectionString}");
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            var error = $"Permission denied accessing serial port. Error: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[FlightViewModel] {error}");
            Console.WriteLine($"[FlightViewModel] {error}");
            IsConnected = false;
            AppendStreamOutput(error);
            throw;
        }
        catch (System.IO.IOException ex)
        {
            var error = $"Serial port error: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[FlightViewModel] {error}");
            Console.WriteLine($"[FlightViewModel] {error}");
            IsConnected = false;
            AppendStreamOutput(error);
            throw;
        }
        catch (Exception ex)
        {
            var error = $"Connection error: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[FlightViewModel] {error}");
            Console.WriteLine($"[FlightViewModel] {error}\n{ex.StackTrace}");
            IsConnected = false;
            AppendStreamOutput(error);
            throw;
        }
    }

    public async void Disconnect()
    {
        try
        {
            await _mavLinkService.DisconnectAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FlightViewModel] Disconnect error: {ex.Message}");
        }

        IsConnected = false;
        AppendStreamOutput("Disconnected");
    }

    private void Arm()
    {
        IsArmed = true;
        SendCommand(Command.ARM);
    }

    private void Disarm()
    {
        IsArmed = false;
        SendCommand(Command.DISARM);
    }

    private void ToggleArm()
    {
        if (IsArmed)
        {
            Disarm();
            return;
        }

        Arm();
    }

    private void Takeoff()
    {
        SendCommand(Command.TAKE_OFF);
    }

    private void Land()
    {
        SendCommand(Command.LAND);
    }

    private void RTL()
    {
        SendCommand(Command.RTL);
    }

    public void TakePicture()
    {
        AppendStreamOutput("Take picture requested");
    }

    public void StartStream()
    {
        AppendStreamOutput("Start stream requested");
    }

    private void StartCamera()
    {
        StartStream();
    }

    public void StopStream()
    {
        AppendStreamOutput("Stop stream requested");
    }

    public void RequestParams()
    {
        _ = _mavLinkService.RequestParametersAsync();
    }

    private void ReadStream()
    {
        RequestParams();
        AppendStreamOutput("Requesting parameters...");
    }

    private void ToggleRecording()
    {
        _isRecording = !_isRecording;
        AppendStreamOutput(_isRecording ? "Recording started" : "Recording stopped");
    }

    private void ToggleYolo()
    {
        _isYoloEnabled = !_isYoloEnabled;
        AppendStreamOutput(_isYoloEnabled ? "YOLO enabled" : "YOLO disabled");
    }

    private void SendSelectedCommand()
    {
        var selected = SelectedCommandStr?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(selected))
        {
            AppendStreamOutput("No command selected");
            return;
        }

        var normalized = selected.ToUpperInvariant();
        switch (normalized)
        {
            case "LAND":
                SendCommand(Command.LAND);
                AppendStreamOutput("Sent: Land");
                break;
            case "TAKE OFF":
            case "TAKEOFF":
                SendCommand(Command.TAKE_OFF);
                AppendStreamOutput("Sent: Take Off");
                break;
            case "RTL":
                SendCommand(Command.RTL);
                AppendStreamOutput("Sent: RTL");
                break;
            case "LOITER":
            case "STABILIZE":
            case "GUIDED":
            case "ALT HOLD":
            case "POSITION HOLD":
            case "BRAKE":
            case "AUTO":
                _ = SetFlightModeAsync(selected);
                AppendStreamOutput($"Flight mode command sent: {selected}");
                break;
            case "START MISSION":
                SendCommand(Command.CONTINUE);
                AppendStreamOutput("Sent: Start Mission");
                break;
            case "GET PARAM":
                RequestParams();
                AppendStreamOutput("Requesting parameters...");
                break;
            default:
                _ = SendCustomCommandAsync(selected);
                break;
        }
    }

    private async Task SendCustomCommandAsync(string commandText)
    {
        try
        {
            var sent = await _mavLinkService.SendCommandAsync(commandText);
            AppendStreamOutput(sent ? $"Sent: {commandText}" : $"Command not recognized: {commandText}");
        }
        catch (Exception ex)
        {
            AppendStreamOutput($"Command error: {ex.Message}");
        }
    }

    private void AppendStreamOutput(string line)
    {
        var text = string.IsNullOrWhiteSpace(StreamOutput)
            ? line
            : $"{line}\r\n{StreamOutput}";

        const int maxChars = 3000;
        if (text.Length > maxChars)
        {
            text = text[..maxChars];
        }

        StreamOutput = text;
    }

    public void SendCommand(Command command)
    {
        switch (command)
        {
            case Command.ARM:
                _ = _mavLinkService.ArmDisarmAsync(true);
                break;
            case Command.DISARM:
                _ = _mavLinkService.ArmDisarmAsync(false);
                break;
            case Command.TAKE_OFF:
                _mavLinkService.SendMessage(MavLinkCommandEncoder.CreateTakeoffCommand(30f));
                break;
            case Command.LAND:
                _mavLinkService.SendMessage(MavLinkCommandEncoder.CreateLandCommand());
                break;
            case Command.RTL:
                _mavLinkService.SendMessage(MavLinkCommandEncoder.CreateRtlCommand());
                break;
            case Command.PAUSE:
                _mavLinkService.SendMessage(MavLinkCommandEncoder.CreatePauseMissionCommand());
                break;
            case Command.CONTINUE:
                _mavLinkService.SendMessage(MavLinkCommandEncoder.CreateContinueMissionCommand());
                break;
            default:
                System.Diagnostics.Debug.WriteLine($"[FlightViewModel] Unsupported command: {command}");
                break;
        }
    }

    public Task<bool> SetFlightModeAsync(string mode)
    {
        return _mavLinkService.SetFlightModeAsync(mode);
    }

    private void OnConnectionStatusChanged(object? sender, bool isConnected)
    {
        void SetConnected()
        {
            IsConnected = isConnected;
            if (isConnected && !string.IsNullOrWhiteSpace(_mavLinkService.ConnectionString))
            {
                SetConnectionStringWithoutParsing(_mavLinkService.ConnectionString);
            }
        }

        if (_dispatcherService != null && !_dispatcherService.IsUIThread)
        {
            _dispatcherService.Enqueue(SetConnected);
        }
        else
        {
            SetConnected();
        }
    }

    private void OnTelemetryReceived(object? sender, FlightData data)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastTelemetryDebugLogTime).TotalSeconds >= 2)
        {
            _lastTelemetryDebugLogTime = now;
            System.Diagnostics.Debug.WriteLine($"[FlightViewModel] OnTelemetryReceived: Roll={data.IMU.Roll:F1}, Pitch={data.IMU.Pitch:F1}, Yaw={data.IMU.Yaw:F1}");
        }

        void UpdateTelemetryOnUI()
        {
            var t = _telemetryData;
            t.Timestamp = DateTime.Now;
            t.Latitude = data.GPS.Latitude / 1e7;
            t.Longitude = data.GPS.Longitude / 1e7;
            t.Altitude = data.AltitudeFloat;
            t.RelativeAltitude = data.AltitudeFloat;
            t.Barometers = data.Barometers > 0 ? data.Barometers : data.AltitudeFloat;
            t.Roll = data.IMU.Roll;
            t.Pitch = data.IMU.Pitch;
            t.Yaw = data.IMU.Yaw;
            t.Heading = data.IMU.Yaw;
            t.GroundSpeed = data.Speed;
            t.AirSpeed = data.Speed;
            t.Speed = data.Speed;
            t.VerticalSpeed = 0;
            t.BatteryVoltage = data.BatteryVolt;
            t.BatteryCurrent = data.BatteryCurr;
            t.BatteryRemaining = 0;
            t.FlightMode = data.FlightMode;
            t.IsArmed = data.FlightMode != FlightMode.DISARMED;
            t.SatelliteCount = data.Sats;
            t.HDOP = data.Hdop / 100.0;
            t.SignalStrength = data.Signal;
            t.ThrottlePercent = data.ThrottlePercent;
        }

        if (_dispatcherService != null && !_dispatcherService.IsUIThread)
        {
            _dispatcherService.Enqueue(UpdateTelemetryOnUI);
        }
        else
        {
            UpdateTelemetryOnUI();
        }
    }

    private void SyncConnectionStringFromFields()
    {
        if (_suppressConnectionSync)
        {
            return;
        }

        var scheme = ConnectionType switch
        {
            HarvestmoonGCS.Core.Models.ConnectionType.UDP => "udp",
            HarvestmoonGCS.Core.Models.ConnectionType.Serial => "serial",
            _ => "tcp"
        };

        var raw = $"{scheme}://{ConnectionAddress}:{ConnectionPort}";
        SetConnectionStringWithoutParsing(raw);
    }

    private void SetConnectionStringWithoutParsing(string raw)
    {
        var normalized = NormalizeConnectionString(raw);

        _suppressConnectionSync = true;
        try
        {
            SetProperty(ref _connectionString, normalized, nameof(ConnectionString));
        }
        finally
        {
            _suppressConnectionSync = false;
        }
    }

    private void SyncConnectionFieldsFromConnectionString(string value)
    {
        if (_suppressConnectionSync)
        {
            return;
        }

        if (!TryParseConnectionString(value, out var type, out var address, out var port))
        {
            return;
        }

        _suppressConnectionSync = true;
        try
        {
            SetProperty(ref _connectionType, type, nameof(ConnectionType));
            SetProperty(ref _connectionAddress, address, nameof(ConnectionAddress));
            SetProperty(ref _connectionPort, port, nameof(ConnectionPort));
        }
        finally
        {
            _suppressConnectionSync = false;
        }
    }

    private static string NormalizeConnectionString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "tcp://127.0.0.1:14550";
        }

        var raw = value.Trim();
        if (raw.Contains("://", StringComparison.Ordinal))
        {
            return raw;
        }

        if (raw.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            return $"tcp://{raw[4..]}";
        }

        if (raw.StartsWith("udp:", StringComparison.OrdinalIgnoreCase))
        {
            return $"udp://{raw[4..]}";
        }

        if (raw.StartsWith("serial:", StringComparison.OrdinalIgnoreCase))
        {
            return $"serial://{raw[7..]}";
        }

        if (raw.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("/dev/", StringComparison.OrdinalIgnoreCase))
        {
            return $"serial://{raw}";
        }

        return raw.Contains(':', StringComparison.Ordinal)
            ? $"tcp://{raw}"
            : $"tcp://{raw}:14550";
    }

    private static bool TryParseConnectionString(
        string value,
        out HarvestmoonGCS.Core.Models.ConnectionType type,
        out string address,
        out int port)
    {
        type = HarvestmoonGCS.Core.Models.ConnectionType.TCP;
        address = "127.0.0.1";
        port = 14550;

        var normalized = NormalizeConnectionString(value);
        string tail;

        if (normalized.StartsWith("udp://", StringComparison.OrdinalIgnoreCase))
        {
            type = HarvestmoonGCS.Core.Models.ConnectionType.UDP;
            tail = normalized[6..];
        }
        else if (normalized.StartsWith("serial://", StringComparison.OrdinalIgnoreCase))
        {
            type = HarvestmoonGCS.Core.Models.ConnectionType.Serial;
            tail = normalized[9..];
        }
        else if (normalized.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            type = HarvestmoonGCS.Core.Models.ConnectionType.TCP;
            tail = normalized[6..];
        }
        else
        {
            tail = normalized;
        }

        var separatorIndex = tail.LastIndexOf(':');
        if (separatorIndex <= 0)
        {
            address = tail;
            port = type == HarvestmoonGCS.Core.Models.ConnectionType.Serial ? 57600 : 14550;
            return !string.IsNullOrWhiteSpace(address);
        }

        var parsedAddress = tail[..separatorIndex];
        var parsedPort = tail[(separatorIndex + 1)..];

        if (string.IsNullOrWhiteSpace(parsedAddress))
        {
            return false;
        }

        if (!int.TryParse(parsedPort, out var parsed))
        {
            return false;
        }

        address = parsedAddress;
        port = parsed;
        return true;
    }

    public void UpdateTelemetry(TelemetryData data)
    {
        TelemetryData = data;
    }
}
