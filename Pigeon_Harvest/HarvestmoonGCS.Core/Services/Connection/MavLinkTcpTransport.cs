using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace HarvestmoonGCS.Core.Services.Connection;

/// <summary>
/// TCP transport untuk MAVLink.
///
/// Fixes:
///  - _reconnectDelayMs: hanya diakses dari satu Task (ConnectAndReceiveAsync), aman
///  - _stream: di-snapshot ke local variable sebelum digunakan di SendPacket/ReceiveLoop
///    agar tidak ada null dereference saat CloseSocket() dipanggil dari thread lain
///  - Disconnect(): CTS cancel dulu, lalu CloseSocket, lalu Wait task
/// </summary>
public class MavLinkTcpTransport : IMavLinkTransport
{
    private TcpClient? _tcpClient;
    // volatile agar update di ConnectAndReceiveAsync visible di SendPacket dari thread lain
    private volatile NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private readonly object _sendLock = new();
    private volatile bool _isConnected;
    private readonly string _host;
    private readonly int _port;
    private readonly int _connectTimeoutMs;
    // hanya diakses dari satu Task loop, tidak perlu sync
    private int _reconnectDelayMs = 1000;

    public bool IsConnected => _isConnected;
    public string ConnectionName => $"TCP {_host}:{_port}";

    public event Action<byte[]>? OnDataReceived;

    public MavLinkTcpTransport(string host, int port = 5760, int connectTimeoutMs = 5000)
    {
        _host             = host;
        _port             = port;
        _connectTimeoutMs = connectTimeoutMs;
    }

    public void Connect()
    {
        if (_isConnected) return;

        _cts         = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ConnectAndReceiveAsync(_cts.Token));
    }

    public void Disconnect()
    {
        _isConnected = false;

        // Cancel dulu agar receive loop berhenti SEBELUM socket ditutup
        _cts?.Cancel();

        // Tutup socket setelah cancel agar ReadAsync / ConnectAsync tidak block
        CloseSocket();

        try { _receiveTask?.Wait(TimeSpan.FromSeconds(3)); }
        catch { /* ignore AggregateException / TaskCanceledException */ }

        _cts?.Dispose();
        _cts = null;
        Log.Information("[MavLinkTcpTransport] Disconnected dari {Name}", ConnectionName);
    }

    public void SendPacket(byte[] packet)
    {
        if (!_isConnected) return;

        // Snapshot stream ke local agar tidak null dereference jika CloseSocket dipanggil bersamaan
        var stream = _stream;
        if (stream == null) return;

        lock (_sendLock)
        {
            try
            {
                stream.Write(packet, 0, packet.Length);
                stream.Flush();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MavLinkTcpTransport] Send gagal");
                _isConnected = false;
            }
        }
    }

    // ── Connect + Receive loop ─────────────────────────────────────────────────

    private async Task ConnectAndReceiveAsync(CancellationToken ct)
    {
        _reconnectDelayMs = 1000;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Log.Information("[MavLinkTcpTransport] Menghubungkan ke {Name}...", ConnectionName);
                CloseSocket();

                _tcpClient = new TcpClient();

                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(_connectTimeoutMs);

                await _tcpClient.ConnectAsync(_host, _port, connectCts.Token);

                // Assign ke volatile field setelah connect sukses
                _stream      = _tcpClient.GetStream();
                _isConnected = true;
                _reconnectDelayMs = 1000;
                Log.Information("[MavLinkTcpTransport] Terhubung ke {Name}", ConnectionName);

                await ReceiveLoopAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                Log.Warning(ex, "[MavLinkTcpTransport] Koneksi terputus, retry dalam {Delay}ms", _reconnectDelayMs);

                try { await Task.Delay(_reconnectDelayMs, ct); }
                catch (OperationCanceledException) { break; }

                _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, 30_000);
            }
        }

        Log.Debug("[MavLinkTcpTransport] ConnectAndReceive loop selesai");
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested)
        {
            // Snapshot stream agar tidak null dereference
            var stream = _stream;
            if (stream == null || _tcpClient?.Connected != true) break;

            try
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read == 0)
                {
                    Log.Warning("[MavLinkTcpTransport] Remote menutup koneksi");
                    break;
                }

                var data = new byte[read];
                Array.Copy(buffer, data, read);
                OnDataReceived?.Invoke(data);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                if (!ct.IsCancellationRequested)
                    Log.Warning(ex, "[MavLinkTcpTransport] IO error saat receive");
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log.Warning(ex, "[MavLinkTcpTransport] ReceiveLoop error");
                break;
            }
        }

        _isConnected = false;
    }

    private void CloseSocket()
    {
        // Null-out volatile field dulu agar SendPacket tidak menggunakannya lagi
        var stream = Interlocked.Exchange(ref _stream!, null);
        try { stream?.Close(); stream?.Dispose(); } catch { /* ignore */ }

        try { _tcpClient?.Close(); _tcpClient?.Dispose(); _tcpClient = null; }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        Disconnect();
    }
}
