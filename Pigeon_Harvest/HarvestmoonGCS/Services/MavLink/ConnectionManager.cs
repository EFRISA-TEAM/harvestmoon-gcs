using System;
using System.Net;
using System.Net.Sockets;
using System.IO.Ports;
using System.Threading.Tasks;
using MavLinkNet;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services;
using HarvestmoonGCS.Core.Services.MavLink;
using HarvestmoonGCS.Core.Services.Connection;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Manages MAVLink transport connections (TCP, UDP, Serial)
/// </summary>
internal class ConnectionManager
{
    private readonly MavLinkService _service;
    private MavLinkGenericTransport? _transport;
    
    public ConnectionManager(MavLinkService service)
    {
        _service = service;
    }
    
    public async Task<MavLinkGenericTransport?> CreateTransportAsync(ConnectionConfig config)
    {
        try
        {
            // Disconnect existing connection first
            await DisconnectAsync();
            
            System.Diagnostics.Debug.WriteLine($"[ConnectionManager] Creating transport: Type={config.Type}, Address={config.Address}, Port={config.Port}");
            
            MavLinkGenericTransport? transport = null;
            
            switch (config.Type)
            {
                case Core.Models.ConnectionType.TCP:
                    transport = await CreateTcpTransportAsync(config.Address, config.Port);
                    break;
                    
                case Core.Models.ConnectionType.UDP:
                    transport = await CreateUdpTransportAsync(config.Address, config.Port);
                    break;
                    
                case Core.Models.ConnectionType.Serial:
                    transport = await CreateSerialTransportAsync(config.SerialPort, config.BaudRate);
                    break;
                    
                default:
                    throw new ArgumentException($"Unsupported connection type: {config.Type}");
            }
            
            if (transport != null)
            {
                _transport = transport;
                _service.SetTransport(transport);
                System.Diagnostics.Debug.WriteLine($"[ConnectionManager] Transport created successfully");
            }
            
            return transport;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConnectionManager] Error creating transport: {ex.Message}");
            throw;
        }
    }
    
    private async Task<MavLinkGenericTransport?> CreateTcpTransportAsync(string address, int port)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[ConnectionManager] Creating TCP transport to {address}:{port}");

            // Gunakan MavLinkTcpTransport (Core) dibungkus MavLinkGenericTransportAdapter
            var tcpTransport = new MavLinkTcpTransport(address, port, connectTimeoutMs: 5000);
            var adapter = new MavLinkGenericTransportAdapter(tcpTransport);

            await Task.Run(() => adapter.Initialize());

            System.Diagnostics.Debug.WriteLine($"[ConnectionManager] TCP transport initialized to {address}:{port}");
            return adapter;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConnectionManager] TCP connection failed: {ex.Message}");
            Console.WriteLine($"[ConnectionManager] TCP connection failed: {ex.Message}");
            throw;
        }
    }

    private async Task<MavLinkGenericTransport?> CreateUdpTransportAsync(string address, int port)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[ConnectionManager] Creating UDP transport to {address}:{port}");
            Console.WriteLine($"[ConnectionManager] Creating UDP transport to {address}:{port}");

            int bindPort = port > 0 ? port : 14550;
            var transport = new MavLinkUdpClientTransport
            {
                LocalPort = bindPort,
                TargetPort = bindPort
            };

            // ── Listen mode ────────────────────────────────────────────────────
            // UAV yang mendorong telemetri ke GCS (Orange Pi drone companion →
            // GCS:14550) tidak punya IP tetap di setup router/hotspot. Dengan
            // alamat kosong / 0.0.0.0 / any / listen, GCS hanya mendengarkan di
            // port tersebut dan mempelajari IP UAV dari paket pertama.
            bool listenRequested = string.IsNullOrWhiteSpace(address)
                || address == "0.0.0.0"
                || address.Equals("any", StringComparison.OrdinalIgnoreCase)
                || address.Equals("listen", StringComparison.OrdinalIgnoreCase)
                || address.Equals("*", StringComparison.Ordinal);

            if (listenRequested)
            {
                transport.ListenMode = true;
                await Task.Run(() => transport.Initialize());

                Console.WriteLine($"[ConnectionManager] UDP LISTEN mode aktif di port {bindPort} (menunggu UAV mengirim)");
                return transport;
            }
            // ───────────────────────────────────────────────────────────────────

            // H-2 fix: IPAddress.Parse gagal untuk hostname (misalnya "localhost").
            // Gunakan Dns.GetHostAddresses sebagai fallback.
            if (!IPAddress.TryParse(address, out var ip))
            {
                var hostAddresses = await System.Net.Dns.GetHostAddressesAsync(address);
                ip = hostAddresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                     ?? hostAddresses.First();
            }

            transport.TargetIpAddress = ip;
            
            // Initialize the transport (this starts threads and connects)
            await Task.Run(() => transport.Initialize());
            
            System.Diagnostics.Debug.WriteLine($"[ConnectionManager] UDP transport initialized (target={ip}:{bindPort}, local={bindPort})");
            Console.WriteLine($"[ConnectionManager] UDP transport initialized successfully (target={ip}:{bindPort}, local={bindPort})");
            return transport;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConnectionManager] UDP connection failed: {ex.Message}");
            Console.WriteLine($"[ConnectionManager] UDP connection failed: {ex.Message}");
            throw;
        }
    }
    
    private async Task<MavLinkGenericTransport?> CreateSerialTransportAsync(string portName, int baudRate)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine($"[ConnectionManager] CreateSerialTransportAsync START: {portName} @ {baudRate}");
            Console.WriteLine($"[ConnectionManager] CreateSerialTransportAsync START: {portName} @ {baudRate}");
            
            if (string.IsNullOrEmpty(portName))
            {
                var error = "[ConnectionManager] ERROR: Serial port name is null or empty!";
                System.Diagnostics.Debug.WriteLine(error);
                Console.WriteLine(error);
                throw new ArgumentException("Serial port name cannot be null or empty", nameof(portName));
            }
            
            if (baudRate <= 0)
            {
                var error = $"[ConnectionManager] ERROR: Invalid baudrate: {baudRate}";
                System.Diagnostics.Debug.WriteLine(error);
                Console.WriteLine(error);
                throw new ArgumentException($"Invalid baudrate: {baudRate}", nameof(baudRate));
            }
            
            Console.WriteLine($"[ConnectionManager] Creating MavLinkSerialPortTransport instance...");
            var transport = new MavLinkSerialPortTransport();
            transport.SerialPortName = portName;
            transport.BaudRate = baudRate;
            
            Console.WriteLine($"[ConnectionManager] Transport configured: Port={transport.SerialPortName}, Baud={transport.BaudRate}");
            System.Diagnostics.Debug.WriteLine($"[ConnectionManager] Transport configured: Port={transport.SerialPortName}, Baud={transport.BaudRate}");
            
            // Initialize the transport (this opens port and starts threads)
            Console.WriteLine($"[ConnectionManager] Calling transport.Initialize()...");
            System.Diagnostics.Debug.WriteLine($"[ConnectionManager] Calling transport.Initialize()...");
            
            await Task.Run(() => transport.Initialize());
            
            System.Diagnostics.Debug.WriteLine($"[ConnectionManager] Serial transport initialized SUCCESSFULLY");
            Console.WriteLine($"[ConnectionManager] Serial transport initialized SUCCESSFULLY");
            return transport;
        }
        catch (UnauthorizedAccessException ex)
        {
            var error = $"[ConnectionManager] Serial connection PERMISSION DENIED: {ex.Message}";
            System.Diagnostics.Debug.WriteLine(error);
            Console.WriteLine(error);
            Console.WriteLine($"[ConnectionManager] Make sure you have permission to access {portName}");
            Console.WriteLine($"[ConnectionManager] Try: sudo usermod -a -G dialout $USER");
            throw;
        }
        catch (System.IO.IOException ex)
        {
            var error = $"[ConnectionManager] Serial connection IO ERROR: {ex.Message}";
            System.Diagnostics.Debug.WriteLine(error);
            Console.WriteLine(error);
            Console.WriteLine($"[ConnectionManager] Port {portName} may be in use by another application");
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConnectionManager] Serial connection failed: {ex.Message}");
            Console.WriteLine($"[ConnectionManager] Serial connection FAILED: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }
    
    public async Task DisconnectAsync()
    {
        try
        {
            if (_transport != null)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectionManager] Disconnecting transport");
                
                await Task.Run(() =>
                {
                    _transport.Dispose();
                });
                
                _transport = null;
                _service.SetTransport(null);
                
                System.Diagnostics.Debug.WriteLine($"[ConnectionManager] Transport disconnected");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConnectionManager] Error disconnecting: {ex.Message}");
        }
    }
    
    public MavLinkGenericTransport? GetTransport()
    {
        return _service.GetTransport();
    }

    /// <summary>
    /// Daftarkan transport eksternal (dari RuncamWifiLinkService) ke ConnectionManager.
    /// Digunakan ketika transport sudah dibuat di luar ConnectionManager normal.
    /// </summary>
    public void SetExternalTransport(MavLinkGenericTransport transport)
    {
        _transport = transport;
        _service.SetTransport(transport);
    }
}
