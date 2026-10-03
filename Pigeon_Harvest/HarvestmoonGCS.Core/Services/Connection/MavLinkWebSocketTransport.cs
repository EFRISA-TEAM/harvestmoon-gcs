using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace HarvestmoonGCS.Core.Services.Connection;

/// <summary>
/// WebSocket transport untuk MAVLink.
/// Digunakan di platform Web/WASM dan ketika drone/companion computer
/// mengekspos MAVLink via WebSocket proxy (misalnya: mavlink-router, pymavlink).
///
/// Python proxy contoh (jalankan di companion computer):
///   python3 -c "import asyncio, websockets, socket
///   async def handler(ws):
///       udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
///       udp.bind(('0.0.0.0', 14551))
///       while True:
///           data, _ = udp.recvfrom(4096)
///           await ws.send(data)
///   asyncio.run(websockets.serve(handler, '0.0.0.0', 8765))"
///
/// Atau gunakan mavlink_websocket_proxy.py yang ada di Multi-code/Pigeon_Uno.
/// </summary>
public class MavLinkWebSocketTransport : IMavLinkTransport
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private readonly object _sendLock = new();
    private volatile bool _isConnected;
    private readonly Uri _uri;
    private readonly int _connectTimeoutMs;
    private int _reconnectDelayMs = 1000;

    public bool IsConnected => _isConnected;
    public string ConnectionName => $"WebSocket {_uri}";

    public event Action<byte[]>? OnDataReceived;

    /// <param name="uri">WebSocket URI, contoh: ws://192.168.0.1:8765/mavlink</param>
    /// <param name="connectTimeoutMs">Timeout connect awal.</param>
    public MavLinkWebSocketTransport(string uri, int connectTimeoutMs = 5000)
    {
        _uri = new Uri(uri);
        _connectTimeoutMs = connectTimeoutMs;
    }

    public void Connect()
    {
        if (_isConnected) return;

        _cts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ConnectAndReceiveAsync(_cts.Token));
    }

    public void Disconnect()
    {
        _isConnected = false;
        _cts?.Cancel();

        try
        {
            if (_ws?.State == WebSocketState.Open)
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", CancellationToken.None)
                   .Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* ignore */ }

        _ws?.Dispose();
        _ws = null;

        try { _receiveTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* ignore */ }

        _cts?.Dispose();
        _cts = null;
        Log.Information("[MavLinkWebSocketTransport] Disconnected dari {Name}", ConnectionName);
    }

    public void SendPacket(byte[] packet)
    {
        if (!_isConnected || _ws?.State != WebSocketState.Open) return;

        lock (_sendLock)
        {
            try
            {
                _ws.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, CancellationToken.None)
                   .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MavLinkWebSocketTransport] Send gagal");
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
                Log.Information("[MavLinkWebSocketTransport] Menghubungkan ke {Uri}...", _uri);

                _ws?.Dispose();
                _ws = new ClientWebSocket();

                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(_connectTimeoutMs);

                await _ws.ConnectAsync(_uri, connectCts.Token);

                _isConnected = true;
                _reconnectDelayMs = 1000;
                Log.Information("[MavLinkWebSocketTransport] Terhubung ke {Uri}", _uri);

                await ReceiveLoopAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                Log.Warning(ex, "[MavLinkWebSocketTransport] Koneksi terputus, retry dalam {Delay}ms", _reconnectDelayMs);

                try { await Task.Delay(_reconnectDelayMs, ct); }
                catch (OperationCanceledException) { break; }

                _reconnectDelayMs = Math.Min(_reconnectDelayMs * 2, 30_000);
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log.Warning("[MavLinkWebSocketTransport] Server menutup koneksi WebSocket");
                    break;
                }

                if (result.Count > 0)
                {
                    var data = new byte[result.Count];
                    Array.Copy(buffer, data, result.Count);
                    OnDataReceived?.Invoke(data);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log.Warning(ex, "[MavLinkWebSocketTransport] ReceiveLoop error");
                break;
            }
        }

        _isConnected = false;
    }

    public void Dispose()
    {
        Disconnect();
    }
}
