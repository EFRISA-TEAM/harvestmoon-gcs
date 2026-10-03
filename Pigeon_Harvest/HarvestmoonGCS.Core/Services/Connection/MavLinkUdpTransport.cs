using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace HarvestmoonGCS.Core.Services.Connection;

/// <summary>
/// UDP transport yang ditingkatkan untuk MAVLink via RunCam WiFi Link.
///
/// Perubahan dari versi awal:
///  - Mendukung target IP/port eksplisit saat konstruksi (tidak hanya auto-detect dari paket masuk)
///  - Auto-reconnect jika socket mati
///  - Async receive loop menggunakan Task (bukan raw Thread)
///  - Thread-safe send dengan lock
///  - Heartbeat watchdog: jika tidak ada data > 5 detik, log warning
/// </summary>
public class MavLinkUdpTransport : IMavLinkTransport
{
    private UdpClient? _udpClient;
    private IPEndPoint? _remoteEndPoint;
    private readonly int _localPort;
    private readonly string? _fixedRemoteIp;
    private readonly int _fixedRemotePort;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private readonly object _sendLock = new();
    private volatile bool _isConnected;
    private DateTime _lastReceivedUtc = DateTime.MinValue;

    public bool IsConnected => _isConnected;
    public string ConnectionName => _fixedRemoteIp != null
        ? $"UDP local:{_localPort} → {_fixedRemoteIp}:{_fixedRemotePort}"
        : $"UDP local:{_localPort}";

    public event Action<byte[]>? OnDataReceived;

    /// <param name="localPort">Port lokal yang didengarkan GCS (default 14550).</param>
    /// <param name="remoteIp">IP tujuan kirim (opsional, jika null → gunakan IP dari paket pertama).</param>
    /// <param name="remotePort">Port tujuan kirim (opsional, default 14551).</param>
    public MavLinkUdpTransport(int localPort = 14550, string? remoteIp = null, int remotePort = 14551)
    {
        _localPort = localPort;
        _fixedRemoteIp = remoteIp;
        _fixedRemotePort = remotePort;

        if (remoteIp != null)
        {
            _remoteEndPoint = new IPEndPoint(IPAddress.Parse(remoteIp), remotePort);
        }
    }

    public void Connect()
    {
        if (_isConnected) return;

        try
        {
            _udpClient = new UdpClient(_localPort);
            _udpClient.Client.ReceiveTimeout = 5000; // 5 detik timeout per read
            _isConnected = true;
            _cts = new CancellationTokenSource();

            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            Log.Information("[MavLinkUdpTransport] Terhubung di {Name}", ConnectionName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MavLinkUdpTransport] Connect gagal di port {Port}", _localPort);
            _isConnected = false;
            throw;
        }
    }

    public void Disconnect()
    {
        if (!_isConnected && _udpClient == null) return;

        _isConnected = false;
        _cts?.Cancel();

        try
        {
            _udpClient?.Close();
            _udpClient?.Dispose();
            _udpClient = null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[MavLinkUdpTransport] Disconnect error");
        }

        try
        {
            _receiveTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* ignore */ }

        _cts?.Dispose();
        _cts = null;
        Log.Information("[MavLinkUdpTransport] Disconnected dari {Name}", ConnectionName);
    }

    public void SendPacket(byte[] packet)
    {
        if (!_isConnected || _udpClient == null) return;
        if (_remoteEndPoint == null)
        {
            // Belum ada endpoint tujuan - tunggu sampai paket pertama masuk
            Log.Verbose("[MavLinkUdpTransport] SendPacket dilewati: remote endpoint belum diketahui");
            return;
        }

        lock (_sendLock)
        {
            try
            {
                _udpClient.Send(packet, packet.Length, _remoteEndPoint);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[MavLinkUdpTransport] Send gagal");
            }
        }
    }

    // ── Async receive loop ─────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        Log.Debug("[MavLinkUdpTransport] ReceiveLoop mulai di port {Port}", _localPort);

        while (!ct.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(ct);

                // Selalu pelajari endpoint pengirim yang sebenarnya.
                // Penting untuk setup router (TP-Link): UAV bisa mengirim dari port
                // berbeda dari remotePort yang dikonfigurasi, sehingga balasan
                // (heartbeat/command) harus diarahkan ke endpoint yang benar-benar hidup.
                if (result.RemoteEndPoint != null)
                {
                    if (_fixedRemoteIp == null ||
                        result.RemoteEndPoint.Address.ToString() == _fixedRemoteIp)
                    {
                        _remoteEndPoint = result.RemoteEndPoint;
                    }
                }

                _lastReceivedUtc = DateTime.UtcNow;
                OnDataReceived?.Invoke(result.Buffer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                // Timeout normal - cek apakah lama tidak ada data
                if (_lastReceivedUtc != DateTime.MinValue &&
                    DateTime.UtcNow - _lastReceivedUtc > TimeSpan.FromSeconds(10))
                {
                    Log.Warning("[MavLinkUdpTransport] Tidak ada data selama >10 detik di {Port}", _localPort);
                }
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log.Warning(ex, "[MavLinkUdpTransport] ReceiveLoop error");
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }

        Log.Debug("[MavLinkUdpTransport] ReceiveLoop selesai di port {Port}", _localPort);
    }

    public void Dispose()
    {
        Disconnect();
    }
}
