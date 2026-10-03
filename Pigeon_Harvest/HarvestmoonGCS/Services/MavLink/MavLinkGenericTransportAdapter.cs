using System;
using System.Threading;
using System.Threading.Tasks;
using MavLinkNet;
using HarvestmoonGCS.Core.Services.Connection;
using HarvestmoonGCS.Core.Services.MavLink;
using Serilog;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Adapter yang membungkus IMavLinkTransport (UDP/TCP/WebSocket event-driven)
/// menjadi MavLinkGenericTransport (abstract class yang dibutuhkan oleh
/// MavLinkService di folder /Services).
///
/// Prinsip kerja:
///  1. Initialize() → panggil _inner.Connect() jika belum connect
///  2. Data masuk dari _inner.OnDataReceived → feed ke MavLinkAsyncWalker → HandlePacketReceived
///  3. SendMessage() → serialize ke bytes → _inner.SendPacket()
/// </summary>
internal sealed class MavLinkGenericTransportAdapter : MavLinkGenericTransport
{
    private readonly IMavLinkTransport _inner;
    private MavLinkAsyncWalker? _walker;
    // H-3 fix: pakai int + Interlocked.CompareExchange agar Initialize() idempotent
    // bahkan jika dipanggil dari dua thread secara bersamaan.
    private int _initialized; // 0 = false, 1 = true
    private volatile bool _disposed;

    public string TransportName => _inner.ConnectionName;

    public MavLinkGenericTransportAdapter(IMavLinkTransport inner)
    {
        _inner = inner;
    }

    public override void Initialize()
    {
        // Hanya satu thread yang boleh initialize (0 → 1)
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0) return;

        // Connect inner transport jika belum
        if (!_inner.IsConnected)
            _inner.Connect();

        // Setup MAVLink parser
        _walker = new MavLinkAsyncWalker();
        _walker.PacketReceived += (sender, packet) =>
        {
            try { HandlePacketReceived(sender, packet); }
            catch (Exception ex) { Log.Warning(ex, "[MavLinkGenericTransportAdapter] HandlePacketReceived error"); }
        };

        // Feed data dari UDP/TCP ke parser
        _inner.OnDataReceived += OnRawDataReceived;

        Log.Information("[MavLinkGenericTransportAdapter] Initialized: {Name}", TransportName);
    }

    public override void SendMessage(UasMessage msg)
    {
        if (_disposed || !_inner.IsConnected || _walker == null) return;

        try
        {
            // Serialisasi pesan MAVLink ke bytes menggunakan SerializeMessage dari MavLinkAsyncWalker
            byte[] packet = _walker.SerializeMessage(msg, MavlinkSystemId, MavlinkComponentId, true);
            if (packet != null && packet.Length > 0)
                _inner.SendPacket(packet);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[MavLinkGenericTransportAdapter] SendMessage gagal");
        }
    }

    private void OnRawDataReceived(byte[] data)
    {
        if (_disposed || _walker == null) return;
        try
        {
            _walker.ProcessReceivedBytes(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[MavLinkGenericTransportAdapter] ProcessReceivedBytes error");
        }
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _inner.OnDataReceived -= OnRawDataReceived;
            _inner.Disconnect();
            _inner.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[MavLinkGenericTransportAdapter] Dispose error");
        }

        Log.Information("[MavLinkGenericTransportAdapter] Disposed: {Name}", TransportName);
    }
}
