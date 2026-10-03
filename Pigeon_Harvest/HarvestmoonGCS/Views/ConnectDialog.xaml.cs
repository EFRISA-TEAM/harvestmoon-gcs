using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Core.Helpers;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;

namespace HarvestmoonGCS.Views
{
    public sealed partial class ConnectDialog : ContentDialog
    {
        private ConnectionType _selectedType = ConnectionType.UDP;
        private readonly IMavLinkService? _mavLinkService;
        private readonly ISettingsService? _settingsService;
        private readonly IRuncamWifiLinkService? _runcamService;
        private bool _isRuncamMode = false;
        private bool _runcamIpUserEdited;
        private bool _suppressRuncamIpTracking;
        private System.Threading.CancellationTokenSource? _runcamScanCts;
        private bool _isConnecting;
        private int _progressLoopToken;
        private bool _isDisconnecting;
        private bool _isStatusSubscribed;
        private bool _isAutoConnectingPort;
        private string? _lastSerialFailureReason;
        private DateTime _lastHeartbeatUtc = DateTime.MinValue;
        private DispatcherQueueTimer? _portMonitorTimer;
        private HashSet<string> _knownPorts = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan UiConnectTimeout = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan AutoConnectAttemptTimeout = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan HeartbeatWaitTimeout = TimeSpan.FromSeconds(3);
        private const int ManualSerialProbeRounds = 1;
        private const string LastSerialPortKey = "mavlink.serial.lastPort";
        private const string LastBaudRateKey = "mavlink.serial.lastBaud";
        private const string LastConnectionTypeKey = "mavlink.last.type";
        private const string LastHostKey = "mavlink.last.host";
        private const string LastPortKey = "mavlink.last.port";
        private const string LastRuncamIpKey = "runcam.last.ip";
        private const string AutoConnectOnStartupKey = "mavlink.autoConnectOnStartup";
        private const string AutoDetectPortsKey = "mavlink.serial.autoDetect";
        private const string AutoConnectOnNewPortKey = "mavlink.serial.autoConnectOnNewPort";
        private const string AutoDetectLastAttemptUtcKey = "mavlink.serial.autoDetect.lastAttemptUtc";
        private static readonly TimeSpan AutoDetectAttemptCooldown = TimeSpan.FromSeconds(10);
        private static readonly int[] AutoProbeBaudRates = new[] { 115200, 57600, 38400, 19200, 9600 };

        public ConnectDialog(IMavLinkService? mavLinkService = null)
        {
            this.InitializeComponent();
            _mavLinkService = mavLinkService;
            _settingsService = App.Current.Services.GetService<ISettingsService>();
            _runcamService  = App.Current.Services.GetService<IRuncamWifiLinkService>();
            
            // Set initial state
            UpdateTabStyles();
            UpdateFieldsVisibility();
            
            InitializeSelections();
            if (RuncamIpTextBox != null)
            {
                RuncamIpTextBox.TextChanged += RuncamIpTextBox_TextChanged;
            }
            this.Loaded += ConnectDialog_Loaded;
            this.Opened += ConnectDialog_Opened;
            this.Closed += ConnectDialog_Closed;
        }

        private void ConnectDialog_Loaded(object sender, RoutedEventArgs e)
        {
            this.Loaded -= ConnectDialog_Loaded;
            LoadSerialPorts();
            UpdateConnectButtonState();
        }

        private void ConnectDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            UpdateConnectButtonState();
            SubscribeConnectionEvents();
            StartPortMonitor();
        }

        private void ConnectDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            StopPortMonitor();
            UnsubscribeConnectionEvents();
        }

        private void InitializeSelections()
        {
            var lastBaud = _settingsService?.GetInt(LastBaudRateKey, 0) ?? 0;
            if (lastBaud > 0)
            {
                var baudItem = BaudRateComboBox.Items
                    .OfType<int>()
                    .FirstOrDefault(v => v == lastBaud);
                if (baudItem == lastBaud)
                {
                    BaudRateComboBox.SelectedItem = baudItem;
                }
                else
                {
                    BaudRateComboBox.Text = lastBaud.ToString();
                }
            }
            else
            {
                var baudItem = BaudRateComboBox.Items
                    .OfType<int>()
                    .FirstOrDefault(v => v == 57600);
                if (baudItem == 57600)
                {
                    BaudRateComboBox.SelectedItem = baudItem;
                }
                else if (BaudRateComboBox.Items.Count > 0)
                {
                    BaudRateComboBox.SelectedIndex = 0;
                }
            }

            if (SerialPortComboBox.Items.Count > 0)
            {
                SerialPortComboBox.SelectedIndex = 0;
            }
            else
            {
                LoadSerialPorts();
            }

            if (SerialPortComboBox != null)
            {
                SerialPortComboBox.DropDownOpened -= SerialPortComboBox_DropDownOpened;
                SerialPortComboBox.DropDownOpened += SerialPortComboBox_DropDownOpened;
            }

            if (BaudRateComboBox != null)
            {
                BaudRateComboBox.SelectionChanged -= BaudRateComboBox_SelectionChanged;
                BaudRateComboBox.SelectionChanged += BaudRateComboBox_SelectionChanged;
            }

            if (SerialPortComboBox != null)
            {
                SerialPortComboBox.SelectionChanged -= SerialPortComboBox_SelectionChanged;
                SerialPortComboBox.SelectionChanged += SerialPortComboBox_SelectionChanged;
            }
        }

        private void LoadSerialPorts(bool preserveSelection = true)
        {
            try
            {
                var selectedText = preserveSelection
                    ? (SerialPortComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? SerialPortComboBox.Text
                    : null;
                var ports = SerialPortHelper.GetAvailablePorts();
                _knownPorts = new HashSet<string>(ports, StringComparer.OrdinalIgnoreCase);
                var orderedPorts = GetOrderedPorts(ports);

                if (orderedPorts.Length == 0)
                {
                    return;
                }

                SerialPortComboBox.Items.Clear();
                foreach (var port in orderedPorts)
                {
                    SerialPortComboBox.Items.Add(new ComboBoxItem { Content = port });
                }

                if (!string.IsNullOrWhiteSpace(selectedText))
                {
                    var selected = SerialPortComboBox.Items
                        .OfType<ComboBoxItem>()
                        .FirstOrDefault(item => string.Equals(item.Content?.ToString(), selectedText, StringComparison.OrdinalIgnoreCase));
                    if (selected != null)
                    {
                        SerialPortComboBox.SelectedItem = selected;
                    }
                    else
                    {
                        SerialPortComboBox.Text = selectedText;
                    }
                }
                else
                {
                    var preferred = GetPreferredSerialPort(orderedPorts);
                    if (!string.IsNullOrWhiteSpace(preferred))
                    {
                        var preferredItem = SerialPortComboBox.Items
                            .OfType<ComboBoxItem>()
                            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), preferred, StringComparison.OrdinalIgnoreCase));
                        if (preferredItem != null)
                        {
                            SerialPortComboBox.SelectedItem = preferredItem;
                        }
                        else
                        {
                            SerialPortComboBox.SelectedIndex = 0;
                        }
                    }
                    else
                    {
                        SerialPortComboBox.SelectedIndex = 0;
                    }
                }

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectDialog] Failed to load serial ports: {ex.Message}");
            }
        }

        private void SubscribeConnectionEvents()
        {
            if (_isStatusSubscribed || _mavLinkService == null)
            {
                return;
            }

            _mavLinkService.ConnectionStatusChanged += OnConnectionStatusChanged;
            _mavLinkService.HeartbeatReceived += OnHeartbeatReceived;
            if (_runcamService != null)
            {
                _runcamService.StatusChanged += OnRuncamStatusChanged;
            }
            _isStatusSubscribed = true;
        }

        private void UnsubscribeConnectionEvents()
        {
            if (!_isStatusSubscribed || _mavLinkService == null)
            {
                return;
            }

            _mavLinkService.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _mavLinkService.HeartbeatReceived -= OnHeartbeatReceived;
            if (_runcamService != null)
            {
                _runcamService.StatusChanged -= OnRuncamStatusChanged;
            }
            _isStatusSubscribed = false;
        }

        private void OnRuncamStatusChanged(object? sender, RuncamConnectionStatus status)
        {
            DispatcherQueue.TryEnqueue(UpdateConnectButtonState);
        }

        private void OnConnectionStatusChanged(object? sender, bool isConnected)
        {
            DispatcherQueue.TryEnqueue(UpdateConnectButtonState);
        }

        private void OnHeartbeatReceived(object? sender, EventArgs e)
        {
            _lastHeartbeatUtc = DateTime.UtcNow;
        }

        private void StartPortMonitor()
        {
            if (!IsAutoDetectEnabled() || _portMonitorTimer != null)
            {
                return;
            }

            _knownPorts = new HashSet<string>(SerialPortHelper.GetAvailablePorts(), StringComparer.OrdinalIgnoreCase);
            _portMonitorTimer = DispatcherQueue.CreateTimer();
            _portMonitorTimer.Interval = TimeSpan.FromSeconds(1);
            _portMonitorTimer.Tick += PortMonitorTimer_Tick;
            _portMonitorTimer.Start();
        }

        private void StopPortMonitor()
        {
            if (_portMonitorTimer == null)
            {
                return;
            }

            _portMonitorTimer.Stop();
            _portMonitorTimer.Tick -= PortMonitorTimer_Tick;
            _portMonitorTimer = null;
        }

        private async void PortMonitorTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            await ScanForPortChangesAsync();
        }

        private async Task ScanForPortChangesAsync()
        {
            if (_isConnecting || _isDisconnecting || _isAutoConnectingPort)
            {
                return;
            }

            var currentPorts = new HashSet<string>(SerialPortHelper.GetAvailablePorts(), StringComparer.OrdinalIgnoreCase);
            if (_knownPorts.Count == 0)
            {
                _knownPorts = currentPorts;
                LoadSerialPorts();
                return;
            }

            var newPorts = currentPorts
                .Where(p => !_knownPorts.Contains(p))
                .ToArray();

            var removedPorts = _knownPorts
                .Where(p => !currentPorts.Contains(p))
                .ToArray();

            if (newPorts.Length == 0 && removedPorts.Length == 0)
            {
                return;
            }

            _knownPorts = currentPorts;
            LoadSerialPorts();

            if (newPorts.Length > 0)
            {
                if (IsAutoDetectInCooldown())
                {
                    return;
                }

                var selectedNewPort = GetPreferredSerialPort(newPorts) ?? newPorts[0];
                var selectedItem = SerialPortComboBox.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Content?.ToString(), selectedNewPort, StringComparison.OrdinalIgnoreCase));

                if (selectedItem != null)
                {
                    SerialPortComboBox.SelectedItem = selectedItem;
                }
                else
                {
                    SerialPortComboBox.Text = selectedNewPort;
                }

                if (CanAutoConnectOnNewPort())
                {
                    await TryAutoConnectToPortAsync(selectedNewPort);
                }
            }
        }

        private bool IsAutoDetectInCooldown()
        {
            var lastAttemptUtcRaw = _settingsService?.GetString(AutoDetectLastAttemptUtcKey, null);
            if (string.IsNullOrWhiteSpace(lastAttemptUtcRaw))
            {
                return false;
            }

            if (!DateTime.TryParse(lastAttemptUtcRaw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastAttemptUtc))
            {
                return false;
            }

            return DateTime.UtcNow - lastAttemptUtc < AutoDetectAttemptCooldown;
        }

        private void MarkAutoDetectAttempt()
        {
            _settingsService?.SaveString(AutoDetectLastAttemptUtcKey, DateTime.UtcNow.ToString("O"));
        }

        private bool IsAutoDetectEnabled()
        {
            return _settingsService?.GetBool(AutoDetectPortsKey, true) ?? true;
        }

        private bool CanAutoConnectOnNewPort()
        {
            if (_mavLinkService?.IsConnected == true)
            {
                return false;
            }

            if (_selectedType != ConnectionType.Serial)
            {
                return false;
            }

            return _settingsService?.GetBool(AutoConnectOnNewPortKey, false) ?? false;
        }

        private IEnumerable<int> BuildProbeBaudOrder(string port, int preferredBaud)
        {
            var ordered = new List<int>();

            if (port.Contains("ttyACM", StringComparison.OrdinalIgnoreCase))
            {
                ordered.Add(57600);
                ordered.Add(115200);
                ordered.Add(230400);
                ordered.Add(38400);
                ordered.Add(19200);
                ordered.Add(9600);
            }
            else if (port.Contains("ttyUSB", StringComparison.OrdinalIgnoreCase) || port.Contains("COM", StringComparison.OrdinalIgnoreCase))
            {
                ordered.Add(57600);
                ordered.Add(115200);
                ordered.Add(38400);
                ordered.Add(19200);
                ordered.Add(9600);
            }
            else
            {
                ordered.AddRange(AutoProbeBaudRates);
            }

            if (preferredBaud > 0)
            {
                ordered.Insert(0, preferredBaud);
            }

            var lastBaud = _settingsService?.GetInt(LastBaudRateKey, 0) ?? 0;
            if (lastBaud > 0)
            {
                ordered.Insert(1, lastBaud);
            }

            return ordered
                .Where(v => v > 0)
                .Distinct()
                .ToArray();
        }

        private async Task TryAutoConnectToPortAsync(string port)
        {
            if (_mavLinkService == null || string.IsNullOrWhiteSpace(port))
            {
                return;
            }

            _isAutoConnectingPort = true;
            ConnectButton.IsEnabled = false;
            StatusSubtitle.Text = $"New port detected: {port}. Probing MAVLink...";
            MarkAutoDetectAttempt();

            try
            {
                foreach (var baud in BuildProbeBaudOrder(port, ResolveBaudRate()))
                {
                    var attemptStarted = DateTime.UtcNow;
                    StatusSubtitle.Text = $"Trying {port} @ {baud}...";

                    var config = new ConnectionConfig
                    {
                        Type = ConnectionType.Serial,
                        SerialPort = port,
                        BaudRate = baud,
                        Address = HostTextBox.Text,
                        Port = int.TryParse(PortTextBox.Text, out var parsedPort) ? parsedPort : 14550
                    };

                    await _mavLinkService.DisconnectAsync();
                    var connectTask = _mavLinkService.ConnectAsync(config);
                    var completedTask = await Task.WhenAny(connectTask, Task.Delay(AutoConnectAttemptTimeout));
                    if (completedTask != connectTask)
                    {
                        await _mavLinkService.DisconnectAsync();
                        continue;
                    }

                    var connected = await connectTask;
                    if (!connected)
                    {
                        continue;
                    }

                    _progressLoopToken++;
                    ConnectProgressBar.Value = 100;
                    ProgressPercentage.Text = "100%";
                    StatusSubtitle.Text = $"Auto-connected on {port} @ {baud}";
                    HeaderAccentLine.Fill = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
                    TitleIcon.Glyph = "✓";
                    TitleIconBorder.Background = new SolidColorBrush(Color.FromArgb(51, 16, 185, 129));
                    TitleIconBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(102, 16, 185, 129));

                    SaveLastSelection(port, baud);
                    UpdateConnectButtonState();
                    await Task.Delay(900);
                    this.Hide();
                    return;
                }

                StatusSubtitle.Text = $"Port {port} detected, but no MAVLink heartbeat yet.";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectDialog] Auto-connect failed: {ex.Message}");
                StatusSubtitle.Text = "Auto-detect failed. You can still connect manually.";
            }
            finally
            {
                _isAutoConnectingPort = false;
                ConnectButton.IsEnabled = true;
                UpdateConnectButtonState();
            }
        }

        private async Task<bool> WaitForHeartbeatAfterAsync(DateTime attemptStarted)
        {
            var timeoutAt = DateTime.UtcNow + HeartbeatWaitTimeout;
            while (DateTime.UtcNow < timeoutAt)
            {
                if (_lastHeartbeatUtc >= attemptStarted)
                {
                    return true;
                }

                await Task.Delay(100);
            }

            return false;
        }

        private void SerialPortComboBox_DropDownOpened(object sender, object e)
        {
            LoadSerialPorts();
        }

        private void SerialPortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateConnectButtonState();
        }

        private void BaudRateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateConnectButtonState();
        }

        private void UpdateConnectButtonState()
        {
            if (ConnectButtonText == null || ConnectButtonIcon == null)
            {
                return;
            }

            var isConnected = (_mavLinkService?.IsConnected ?? false)
                || (_runcamService != null && (_runcamService.IsCameraConnected || _runcamService.IsMavLinkConnected));
            ConnectButtonText.Text = isConnected ? "Disconnect" : "Connect";
            ConnectButtonIcon.Glyph = isConnected ? "\uE71A" : "\uE912";
        }

        private string[] GetOrderedPorts(IEnumerable<string> ports)
        {
            var list = ports
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Where(IsLikelyMavlinkPort)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (list.Count <= 1)
            {
                return list.ToArray();
            }

            var ordered = new List<string>();
            var lastPort = _settingsService?.GetString(LastSerialPortKey, null);
            if (!string.IsNullOrWhiteSpace(lastPort))
            {
                var lastMatch = list.FirstOrDefault(p => string.Equals(p, lastPort, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(lastMatch))
                {
                    ordered.Add(lastMatch);
                    list.RemoveAll(p => string.Equals(p, lastMatch, StringComparison.OrdinalIgnoreCase));
                }
            }

            ordered.AddRange(list.Where(p => p.Contains("ttyUSB", StringComparison.OrdinalIgnoreCase)));
            ordered.AddRange(list.Where(p => p.Contains("ttyACM", StringComparison.OrdinalIgnoreCase)));
            ordered.AddRange(list.Where(p => p.Contains("COM", StringComparison.OrdinalIgnoreCase)));

            foreach (var port in list)
            {
                if (!ordered.Contains(port, StringComparer.OrdinalIgnoreCase))
                {
                    ordered.Add(port);
                }
            }

            return ordered.ToArray();
        }

        private static bool IsLikelyMavlinkPort(string port)
        {
            if (string.IsNullOrWhiteSpace(port))
            {
                return false;
            }

            var normalized = port.Trim();
            if (normalized.Contains("ttyUSB", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("ttyACM", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("rfcomm", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("COM", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("cu.", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("tty.usb", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !normalized.Contains("ttyS", StringComparison.OrdinalIgnoreCase);
        }

        private string? GetPreferredSerialPort(IReadOnlyList<string> ports)
        {
            if (ports.Count == 0)
            {
                return null;
            }

            var lastPort = _settingsService?.GetString(LastSerialPortKey, null);
            if (!string.IsNullOrWhiteSpace(lastPort))
            {
                var match = ports.FirstOrDefault(p => string.Equals(p, lastPort, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match))
                {
                    return match;
                }
            }

            var usb = ports.FirstOrDefault(p => p.Contains("ttyUSB", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(usb))
            {
                return usb;
            }

            var acm = ports.FirstOrDefault(p => p.Contains("ttyACM", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(acm))
            {
                return acm;
            }

            var com = ports.FirstOrDefault(p => p.Contains("COM", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(com))
            {
                return com;
            }

            return ports[0];
        }


        private void TypeTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string typeStr)
            {
                if (typeStr == "RuncamWifi")
                {
                    _isRuncamMode = true;
                    _selectedType = ConnectionType.UDP; // internal fallback
                    UpdateTabStyles();
                    UpdateFieldsVisibility();
                    UpdateConnectButtonState();
                    // Cari UAV (Orange Pi / RunCam) di jaringan secara otomatis
                    _ = AutoDetectRuncamAsync();
                    return;
                }

                _isRuncamMode = false;
                if (Enum.TryParse(typeStr, out ConnectionType type))
                {
                    _selectedType = type;
                    if (type == ConnectionType.UDP && HostTextBox != null && (string.IsNullOrWhiteSpace(HostTextBox.Text) || HostTextBox.Text == "127.0.0.1"))
                    {
                        HostTextBox.Text = "0.0.0.0";
                    }
                    UpdateTabStyles();
                    UpdateFieldsVisibility();
                    UpdateConnectButtonState();
                }
            }
        }

        private void UpdateTabStyles()
        {
            if (Resources.ContainsKey("ActiveTabButtonStyle") && Resources.ContainsKey("TabButtonStyle"))
            {
                var activeStyle  = (Style)Resources["ActiveTabButtonStyle"];
                var inactiveStyle = (Style)Resources["TabButtonStyle"];

                UdpTab.Style    = (!_isRuncamMode && _selectedType == ConnectionType.UDP)    ? activeStyle : inactiveStyle;
                TcpTab.Style    = (!_isRuncamMode && _selectedType == ConnectionType.TCP)    ? activeStyle : inactiveStyle;
                SerialTab.Style = (!_isRuncamMode && _selectedType == ConnectionType.Serial) ? activeStyle : inactiveStyle;
                RuncamTab.Style = _isRuncamMode ? activeStyle : inactiveStyle;
            }
        }

        private void UpdateFieldsVisibility()
        {
            bool isNetwork = !_isRuncamMode && (_selectedType == ConnectionType.UDP || _selectedType == ConnectionType.TCP);
            bool isSerial  = !_isRuncamMode && _selectedType == ConnectionType.Serial;
            bool isRuncam  = _isRuncamMode;

            NetworkFields.Visibility    = isNetwork ? Visibility.Visible : Visibility.Collapsed;
            SerialFields.Visibility     = isSerial  ? Visibility.Visible : Visibility.Collapsed;
            RuncamWifiFields.Visibility = isRuncam  ? Visibility.Visible : Visibility.Collapsed;

            // Sembunyikan presets saat mode RunCam
            if (PresetsPanel != null)
                PresetsPanel.Visibility = isRuncam ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string preset)
            {
                switch (preset)
                {
                    case "SITL":
                        _selectedType = ConnectionType.UDP;
                        HostTextBox.Text = "127.0.0.1";
                        PortTextBox.Text = "14550";
                        break;
                    case "GCS":
                        _selectedType = ConnectionType.UDP;
                        HostTextBox.Text = "192.168.1.1";
                        PortTextBox.Text = "14550";
                        break;
                    case "TELEMETRY":
                        _isRuncamMode = false;
                        _selectedType = ConnectionType.Serial;
                        if (SerialPortComboBox.Items.Count > 0)
                        {
                            var preferred = SerialPortComboBox.Items
                                .OfType<ComboBoxItem>()
                                .FirstOrDefault(item =>
                                {
                                    var text = item.Content?.ToString() ?? string.Empty;
                                    return text.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
                                           text.Contains("COM", StringComparison.OrdinalIgnoreCase) ||
                                           text.Contains("tty", StringComparison.OrdinalIgnoreCase);
                                });

                            if (preferred != null)
                            {
                                SerialPortComboBox.SelectedItem = preferred;
                            }
                            else
                            {
                                SerialPortComboBox.SelectedIndex = 0;
                            }
                        }

                        BaudRateComboBox.SelectedItem = 57600;
                        break;
                    case "RUNCAM":
                        // Preset RunCam WiFi Link 2 – aktifkan tab RunCam
                        _isRuncamMode = true;
                        _suppressRuncamIpTracking = true;
                        try
                        {
                            RuncamIpTextBox.Text = "192.168.1.100";
                        }
                        finally
                        {
                            _suppressRuncamIpTracking = false;
                        }
                        RuncamMavLocalPortTextBox.Text  = "14550";
                        RuncamMavRemotePortTextBox.Text = "14551";
                        RuncamProtocolComboBox.SelectedIndex = 2; // Auto
                        break;
                    case "UAVLISTEN":
                        // UAV (Orange Pi companion) MENDORONG telemetri ke GCS:14550.
                        // IP UAV tidak diketahui (DHCP router/hotspot) → mode dengarkan.
                        _isRuncamMode = false;
                        _selectedType = ConnectionType.UDP;
                        HostTextBox.Text = "0.0.0.0";
                        PortTextBox.Text = "14550";
                        break;
                }
                UpdateTabStyles();
                UpdateFieldsVisibility();
                UpdateConnectButtonState();
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnecting)
            {
                return;
            }

            if (_isDisconnecting)
            {
                return;
            }

            if (_isAutoConnectingPort)
            {
                return;
            }

            bool isConnected = _mavLinkService?.IsConnected == true
                || (_runcamService != null && (_runcamService.IsCameraConnected || _runcamService.IsMavLinkConnected));
            if (isConnected)
            {
                _isDisconnecting = true;
                try
                {
                    ConnectButton.IsEnabled = false;
                    StatusSubtitle.Text = "Disconnecting...";
                    // Matikan RunCam (kamera + MAVLink) juga, bukan hanya MAVLink biasa.
                    if (_runcamService != null &&
                        (_runcamService.IsCameraConnected || _runcamService.IsMavLinkConnected))
                    {
                        await _runcamService.DisconnectAllAsync();
                    }
                    if (_mavLinkService?.IsConnected == true)
                    {
                        await _mavLinkService.DisconnectAsync();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ConnectDialog] Disconnect failed with exception: {ex.Message}");
                }
                finally
                {
                    _isDisconnecting = false;
                    ConnectButton.IsEnabled = true;
                    UpdateConnectButtonState();
                }
                return;
            }

            _isConnecting = true;
            var shouldHideDialog = false;
            // Disable UI
            ConnectButton.IsEnabled = false;
            UdpTab.IsEnabled = false;
            TcpTab.IsEnabled = false;
            SerialTab.IsEnabled = false;
            PresetsPanel.IsHitTestVisible = false;
            PresetsPanel.Opacity = 0.5;
            FormFieldsStack.IsHitTestVisible = false;
            FormFieldsStack.Opacity = 0.5;
            
            // Show progress
            ConnectingProgressPanel.Visibility = Visibility.Visible;
            if (Resources.ContainsKey("ConnectingHeaderGradient"))
            {
                HeaderAccentLine.Fill = (Brush)Resources["ConnectingHeaderGradient"];
            }
            StatusSubtitle.Text = "Establishing link...";
            
            // Simulate progress while waiting for service
            _progressLoopToken++;
            var currentToken = _progressLoopToken;
            _ = Task.Run(async () => {
                var random = new Random();
                for (int i = 0; i <= 90; i += random.Next(5, 15))
                {
                    if (currentToken != _progressLoopToken)
                    {
                        break;
                    }

                    if (i > 90) i = 90;
                    
                    DispatcherQueue.TryEnqueue(() => {
                        ConnectProgressBar.Value = i;
                        ProgressPercentage.Text = $"{i}%";
                    });
                    
                    await Task.Delay(200);
                }
            });

            try
            {
                // ── Mode RunCam WiFi Link 2 ─────────────────────────────────────────
                if (_isRuncamMode && _runcamService != null)
                {
                    // Jika IP belum diisi, coba deteksi otomatis dulu (UAV via router/hotspot).
                    if (string.IsNullOrWhiteSpace(RuncamIpTextBox.Text))
                    {
                        StatusSubtitle.Text = "Mencari UAV di jaringan...";
                        await AutoDetectRuncamAsync();
                    }

                    var runcamConfig = BuildRuncamConfig();

                    // Otomatis: kamera + MAVLink selalu terhubung bersamaan.
                    StatusSubtitle.Text = "Menghubungkan kamera + MAVLink via RunCam WiFi Link 2...";
                    bool success = false;

                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
                    try
                    {
                        success = await _runcamService.ConnectAllAsync(runcamConfig, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        success = false;
                    }

                    _progressLoopToken++;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ConnectProgressBar.Value = 100;
                        ProgressPercentage.Text = "100%";
                        ConnectingProgressPanel.Visibility = Visibility.Collapsed;

                        if (success)
                        {
                            var status = _runcamService.Status;
                            StatusSubtitle.Text = $"RunCam terhubung ({status})";
                            RuncamStatusText.Text = $"Terhubung – {runcamConfig.ActiveStreamUrl}";
                            // Simpan untuk auto-connect saat aplikasi dibuka lagi.
                            SaveLastConnection(isRuncam: true, host: runcamConfig.CameraIp,
                                port: runcamConfig.MavLinkLocalPort);
                            shouldHideDialog = true;
                        }
                        else
                        {
                            StatusSubtitle.Text = "Gagal menghubungkan RunCam";
                            RuncamStatusText.Text = "Gagal – periksa koneksi WiFi ke RUNCAM_WIFILINK_XXXXXX";
                        }
                    });

                    if (shouldHideDialog)
                    {
                        await Task.Delay(600);
                        this.Hide();
                    }

                    return;
                }

                // ── Mode MAVLink normal (UDP / TCP / Serial) ────────────────────────
                if (_mavLinkService != null)
                {
                    var config = new ConnectionConfig
                    {
                        Type = _selectedType,
                        Address = HostTextBox.Text,
                        Port = int.TryParse(PortTextBox.Text, out var p) ? p : 14550,
                        SerialPort = (SerialPortComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? SerialPortComboBox.Text,
                        BaudRate = ResolveBaudRate()
                    };

                    bool success = false;
                    try
                    {
                        if (_selectedType == ConnectionType.Serial)
                        {
                            success = await TryManualSerialConnectWithFallbackAsync(config);
                        }
                        else
                        {
                            var connectTask = _mavLinkService.ConnectAsync(config);
                            var completedTask = await Task.WhenAny(connectTask, Task.Delay(UiConnectTimeout));
                            if (completedTask != connectTask)
                            {
                                success = false;
                                System.Diagnostics.Debug.WriteLine("[ConnectDialog] Connect operation timed out");
                            }
                            else
                            {
                                success = await connectTask;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ConnectDialog] Connection failed with exception: {ex.Message}");
                        success = false;
                    }

                    if (success)
                    {
                        _progressLoopToken++;
                        ConnectProgressBar.Value = 100;
                        ProgressPercentage.Text = "100%";
                        StatusSubtitle.Text = "Link established!";

                        // Emerald color for success (hex: #10B981)
                        HeaderAccentLine.Fill = new SolidColorBrush(Color.FromArgb(255, 16, 185, 129));
                        TitleIcon.Glyph = "✓";
                        TitleIconBorder.Background = new SolidColorBrush(Color.FromArgb(51, 16, 185, 129));
                        TitleIconBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(102, 16, 185, 129));

                        // Simpan untuk auto-connect saat aplikasi dibuka lagi.
                        SaveLastConnection(isRuncam: false, host: config.Address, port: config.Port,
                            type: _selectedType);

                        await Task.Delay(1200);
                        shouldHideDialog = true;
                    }
                    else
                    {
                        _progressLoopToken++;
                        
                        // Provide specific error message based on connection type
                        var errorMessage = _selectedType switch
                        {
                            ConnectionType.UDP => "Connection failed!\n\nCheck that:\n• Drone/FC is powered on\n• UDP port 14550 is correct\n• WiFi/Network connection is active",
                            ConnectionType.TCP => "Connection failed!\n\nCheck that:\n• Drone/FC is powered on\n• TCP address and port are correct\n• Network connection is active",
                            ConnectionType.Serial => "Connection failed!\n\nCheck that:\n• USB cable is connected\n• Correct serial port is selected\n• User has permission (run: sudo usermod -a -G dialout USER and logout/login)",
                            _ => "Connection failed!\n\nPlease check your connection settings."
                        };

                        if (_selectedType == ConnectionType.Serial && !string.IsNullOrWhiteSpace(_lastSerialFailureReason))
                        {
                            errorMessage = "Connection failed!\n\n" + _lastSerialFailureReason +
                                           "\n\nCheck that flight controller SERIALx_PROTOCOL=2 (MAVLink), correct SERIALx_BAUD, and no other app is using the port.";
                        }
                        
                        StatusSubtitle.Text = errorMessage;
                        HeaderAccentLine.Fill = new SolidColorBrush(Microsoft.UI.Colors.Red);
                    }
                }
                else
                {
                    // Fallback for demo
                    await Task.Delay(1500);
                    shouldHideDialog = true;
                }
            }
            finally
            {
                _progressLoopToken++;
                _isConnecting = false;

                if (!shouldHideDialog)
                {
                    ConnectButton.IsEnabled = true;
                    UdpTab.IsEnabled = true;
                    TcpTab.IsEnabled = true;
                    SerialTab.IsEnabled = true;
                    PresetsPanel.IsHitTestVisible = true;
                    PresetsPanel.Opacity = 1.0;
                    FormFieldsStack.IsHitTestVisible = true;
                    FormFieldsStack.Opacity = 1.0;
                    ConnectingProgressPanel.Visibility = Visibility.Collapsed;
                }
            }

            if (shouldHideDialog)
            {
                this.Hide();
            }
        }

        private int ResolveBaudRate()
        {
            if (BaudRateComboBox.SelectedItem is int selected)
            {
                return selected;
            }

            if (int.TryParse(BaudRateComboBox.Text, out var parsed))
            {
                return parsed;
            }

            return 57600;
        }

        private async Task<bool> TryManualSerialConnectWithFallbackAsync(ConnectionConfig baseConfig)
        {
            if (_mavLinkService == null)
            {
                return false;
            }

            _lastSerialFailureReason = null;

            var probeOrder = BuildProbeBaudOrder(baseConfig.SerialPort, baseConfig.BaudRate).ToArray();
            if (probeOrder.Length == 0)
            {
                probeOrder = new[] { baseConfig.BaudRate > 0 ? baseConfig.BaudRate : 57600 };
            }

            var attemptedBauds = new List<int>();

            for (var round = 1; round <= ManualSerialProbeRounds; round++)
            {
                foreach (var baud in probeOrder)
                {
                    attemptedBauds.Add(baud);
                    var config = new ConnectionConfig
                    {
                        Type = baseConfig.Type,
                        Address = baseConfig.Address,
                        Port = baseConfig.Port,
                        SerialPort = baseConfig.SerialPort,
                        BaudRate = baud
                    };

                    StatusSubtitle.Text = $"Establishing link... trying {config.SerialPort} @ {baud} (round {round}/{ManualSerialProbeRounds})";
                    Console.WriteLine($"[ConnectDialog] Serial connect attempt: {config.SerialPort} @ {baud} (round {round}/{ManualSerialProbeRounds})");

                    await _mavLinkService.DisconnectAsync();
                    var connectTask = _mavLinkService.ConnectAsync(config);
                    var completedTask = await Task.WhenAny(connectTask, Task.Delay(AutoConnectAttemptTimeout));
                    if (completedTask != connectTask)
                    {
                        Console.WriteLine($"[ConnectDialog] Serial connect timeout: {config.SerialPort} @ {baud}");
                        await _mavLinkService.DisconnectAsync();
                        continue;
                    }

                    var connected = await connectTask;
                    if (!connected)
                    {
                        Console.WriteLine($"[ConnectDialog] Serial connect failed: {config.SerialPort} @ {baud}");
                        continue;
                    }

                    SaveLastSelection(config.SerialPort, config.BaudRate);
                    Console.WriteLine($"[ConnectDialog] Serial link established: {config.SerialPort} @ {baud}");
                    return true;
                }

                if (round < ManualSerialProbeRounds)
                {
                    StatusSubtitle.Text = $"No heartbeat yet. Retrying probe round {round + 1}/{ManualSerialProbeRounds}...";
                    await Task.Delay(1200);
                    LoadSerialPorts();
                    var refreshedPort = (SerialPortComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? SerialPortComboBox.Text;
                    if (!string.IsNullOrWhiteSpace(refreshedPort))
                    {
                        baseConfig.SerialPort = refreshedPort;
                    }
                }
            }

            _lastSerialFailureReason = $"No MAVLink heartbeat from {baseConfig.SerialPort}. Tried baud: {string.Join(", ", attemptedBauds)}";

            return false;
        }

        private void SaveLastSelection(string? port, int baudRate)
        {
            if (_settingsService == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(port))
            {
                _settingsService.SaveString(LastSerialPortKey, port);
            }

            if (baudRate > 0)
            {
                _settingsService.SaveInt(LastBaudRateKey, baudRate);
            }
        }

        private void RefreshPortsButton_Click(object sender, RoutedEventArgs e)
        {
            LoadSerialPorts();
        }

        // ── RunCam WiFi Link 2 handlers ────────────────────────────────────────

        private void RuncamIpTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_suppressRuncamIpTracking)
            {
                _runcamIpUserEdited = true;
            }
        }

        /// <summary>
        /// Cari UAV/Orange Pi di jaringan (LAN TP-Link atau hotspot) lalu isi field IP
        /// secara otomatis. Tidak menimpa IP yang sudah diketik manual oleh user.
        /// </summary>
        private async Task AutoDetectRuncamAsync()
        {
            if (_runcamService == null || _runcamIpUserEdited)
            {
                return;
            }

            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(25));
                var devices = await _runcamService.DetectDevicesAsync(cts.Token);
                if (devices.Count == 0)
                {
                    return;
                }

                var best = devices[0];
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_runcamIpUserEdited)
                    {
                        return;
                    }

                    _suppressRuncamIpTracking = true;
                    try
                    {
                        RuncamIpTextBox.Text = best.CameraIp;
                        RuncamProtocolComboBox.SelectedIndex =
                            best.StreamProtocol == HarvestmoonGCS.Core.Models.RuncamStreamProtocol.Rtsp ? 0 : 1;
                    }
                    finally
                    {
                        _suppressRuncamIpTracking = false;
                    }

                    RuncamStatusText.Text = $"Terdeteksi otomatis: {best.CameraIp} ({best.StreamProtocol})";
                    RuncamStatusBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 34, 197, 94));
                });
            }
            catch (OperationCanceledException)
            {
                // scan dibatalkan / timeout — abaikan, user tetap bisa isi manual
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectDialog] Auto-detect RunCam gagal: {ex.Message}");
            }
        }

        /// <summary>Scan jaringan untuk mendeteksi RunCam WiFi Link 2 secara otomatis.</summary>
        private async void RuncamScanButton_Click(object sender, RoutedEventArgs e)
        {
            if (_runcamService == null) return;

            _runcamScanCts?.Cancel();
            _runcamScanCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));

            RuncamScanButton.IsEnabled = false;
            RuncamStatusText.Text = "Scanning jaringan...";
            RuncamStatusBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

            try
            {
                var devices = await _runcamService.DetectDevicesAsync(_runcamScanCts.Token);

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (devices.Count > 0)
                    {
                        var best = devices[0];
                        RuncamIpTextBox.Text               = best.CameraIp;
                        RuncamMavLocalPortTextBox.Text      = best.MavLinkLocalPort.ToString();
                        RuncamMavRemotePortTextBox.Text     = best.MavLinkRemotePort.ToString();
                        RuncamProtocolComboBox.SelectedIndex = best.StreamProtocol == HarvestmoonGCS.Core.Models.RuncamStreamProtocol.Mjpeg ? 1 : 2;
                        RuncamStatusText.Text = $"Ditemukan: {best.CameraIp} ({best.StreamProtocol})";
                        RuncamStatusBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 34, 197, 94));
                    }
                    else
                    {
                        RuncamStatusText.Text = "Tidak ditemukan. Pastikan laptop terhubung ke WiFi RUNCAM_WIFILINK_XXXXXX atau ke LAN router TP-Link, dan UAV menyala.";
                        RuncamStatusBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 239, 68, 68));
                    }

                    RuncamScanButton.IsEnabled = true;
                });
            }
            catch (OperationCanceledException)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    RuncamStatusText.Text  = "Scan dibatalkan";
                    RuncamScanButton.IsEnabled = true;
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    RuncamStatusText.Text  = $"Scan error: {ex.Message}";
                    RuncamScanButton.IsEnabled = true;
                });
            }
        }

        /// <summary>Bangun RuncamWifiLinkConfig dari field UI saat ini.</summary>
        private HarvestmoonGCS.Core.Models.RuncamWifiLinkConfig BuildRuncamConfig()
        {
            var ip = RuncamIpTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(ip)) ip = "192.168.1.100";

            int localPort  = int.TryParse(RuncamMavLocalPortTextBox.Text,  out var lp) ? lp : 14550;
            int remotePort = int.TryParse(RuncamMavRemotePortTextBox.Text, out var rp) ? rp : 14551;

            var protocol = RuncamProtocolComboBox.SelectedIndex switch
            {
                0 => HarvestmoonGCS.Core.Models.RuncamStreamProtocol.Rtsp,
                1 => HarvestmoonGCS.Core.Models.RuncamStreamProtocol.Mjpeg,
                _ => HarvestmoonGCS.Core.Models.RuncamStreamProtocol.Auto
            };

            return new HarvestmoonGCS.Core.Models.RuncamWifiLinkConfig
            {
                CameraIp          = ip,
                StreamProtocol    = protocol,
                MavLinkLocalPort  = localPort,
                MavLinkRemoteIp   = ip,
                MavLinkRemotePort = remotePort
            };
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _progressLoopToken++;
            _isConnecting = false;
            _runcamScanCts?.Cancel();
            this.Hide();
        }

        // ── Auto-connect saat aplikasi dibuka ───────────────────────────────────

        /// <summary>
        /// Simpan konfigurasi koneksi terakhir supaya bisa dipakai auto-connect
        /// saat MoonHarvest dibuka lagi (tanpa perlu klik Connect manual).
        /// </summary>
        private void SaveLastConnection(bool isRuncam, string host, int port,
            ConnectionType? type = null)
        {
            try
            {
                if (_settingsService == null) return;

                _settingsService.SaveString(LastConnectionTypeKey, isRuncam ? "RUNCAM" : (type ?? ConnectionType.UDP).ToString());
                _settingsService.SaveString(LastHostKey, host ?? string.Empty);
                _settingsService.SaveInt(LastPortKey, port);

                if (isRuncam && !string.IsNullOrWhiteSpace(host))
                {
                    _settingsService.SaveString(LastRuncamIpKey, host);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectDialog] SaveLastConnection gagal: {ex.Message}");
            }
        }

        /// <summary>
        /// Auto-connect saat startup aplikasi (tanpa membuka dialog).
        ///
        /// Dinonaktifkan secara default ("mavlink.autoConnectOnStartup" = false) agar
        /// pengguna memiliki kontrol penuh kapan harus menghubungkan wahana.
        /// </summary>
        public static async Task<bool> TryAutoConnectAsync(
            IMavLinkService? mavLinkService,
            IRuncamWifiLinkService? runcamService,
            ISettingsService? settingsService)
        {
            if (mavLinkService == null || mavLinkService.IsConnected)
            {
                return false;
            }

            // Hormati preferensi pengguna (default: nonaktif).
            bool autoEnabled = settingsService?.GetBool(AutoConnectOnStartupKey, false) ?? false;
            if (!autoEnabled)
            {
                return false;
            }

            var typeStr = settingsService?.GetString(LastConnectionTypeKey, null);
            var host = settingsService?.GetString(LastHostKey, null);
            var port = settingsService?.GetInt(LastPortKey, 14550) ?? 14550;

            try
            {
                // RunCam mode tersimpan → sambungkan kamera + MAVLink sekaligus.
                if (string.Equals(typeStr, "RUNCAM", StringComparison.OrdinalIgnoreCase)
                    && runcamService != null)
                {
                    var ip = settingsService?.GetString(LastRuncamIpKey, host) ?? host;
                    if (string.IsNullOrWhiteSpace(ip))
                    {
                        return false;
                    }

                    var cfg = new RuncamWifiLinkConfig
                    {
                        CameraIp = ip,
                        MavLinkRemoteIp = ip,
                        StreamProtocol = RuncamStreamProtocol.Auto
                    };

                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
                    return await runcamService.ConnectAllAsync(cfg, cts.Token);
                }

                // Default aman: dengarkan UDP 14550 (UAV mendorong telemetri).
                // Jika koneksi terakhir adalah unicast spesifik, pakai itu.
                bool useListen = string.IsNullOrWhiteSpace(host)
                    || host == "0.0.0.0"
                    || string.Equals(typeStr, "UDP", StringComparison.OrdinalIgnoreCase) == false;

                var address = useListen ? "0.0.0.0" : host!;
                var listenPort = port > 0 ? port : 14550;

                Serilog.Log.Information(
                    "[ConnectDialog] Auto-connect: {Type} {Addr}:{Port}",
                    useListen ? "UDP LISTEN" : typeStr, address, listenPort);

                var connectTask = mavLinkService.ConnectAsync(
                    HarvestmoonGCS.Core.Models.ConnectionType.UDP, address, listenPort);

                var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(6)));
                return completed == connectTask && await connectTask;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[ConnectDialog] Auto-connect startup gagal");
                return false;
            }
        }
    }
}
