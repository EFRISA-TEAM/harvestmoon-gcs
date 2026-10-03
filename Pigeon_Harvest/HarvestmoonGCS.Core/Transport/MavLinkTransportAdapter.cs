using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Services.Connection;

namespace HarvestmoonGCS.Core.Transport;

/// <summary>
/// Adapter yang membungkus IMavLinkTransport (event-driven) menjadi ITransport (stream-based).
///
/// Fixes:
///  - _disposed sekarang volatile agar visible di semua thread
///  - _partialBuffer/_partialOffset hanya diakses dari satu thread (ReadAsync caller) — aman
///  - Dispose() lebih defensive: complete queue dulu sebelum dispose inner
///  - TryAdd tidak drop data diam-diam: queue diperbesar ke 4096 agar tidak penuh
/// </summary>
public class MavLinkTransportAdapter : ITransport
{
    private readonly IMavLinkTransport _inner;
    // C-4 fix: perbesar queue agar tidak drop data saat throughput tinggi
    private readonly BlockingCollection<byte[]> _receiveQueue = new(4096);
    private byte[]? _partialBuffer;
    private int _partialOffset;
    // volatile agar _disposed visible di semua thread tanpa lock
    private volatile bool _disposed;

    public bool IsConnected => !_disposed && _inner.IsConnected;

    public MavLinkTransportAdapter(IMavLinkTransport inner)
    {
        _inner = inner;
        _inner.OnDataReceived += OnDataReceived;
    }

    private void OnDataReceived(byte[] data)
    {
        if (_disposed) return;
        // Jika queue penuh, log warning tapi jangan drop diam-diam
        if (!_receiveQueue.TryAdd(data, millisecondsTimeout: 0))
        {
            Serilog.Log.Warning(
                "[MavLinkTransportAdapter] Receive queue penuh ({Count}), frame di-drop",
                _receiveQueue.Count);
        }
    }

    public Task<bool> ConnectAsync()
    {
        try
        {
            _inner.Connect();
            return Task.FromResult(_inner.IsConnected);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[MavLinkTransportAdapter] Connect gagal");
            return Task.FromResult(false);
        }
    }

    public Task DisconnectAsync()
    {
        try { _inner.Disconnect(); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "[MavLinkTransportAdapter] Disconnect error"); }
        return Task.CompletedTask;
    }

    /// <summary>
    /// ReadAsync: partial buffer hanya diakses dari thread yang memanggil ReadAsync
    /// (MavLinkService receive loop) sehingga tidak perlu lock.
    /// </summary>
    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        // Jika masih ada sisa data dari chunk sebelumnya, kembalikan dulu
        if (_partialBuffer != null)
        {
            int remaining = _partialBuffer.Length - _partialOffset;
            int toCopy    = Math.Min(remaining, count);
            Array.Copy(_partialBuffer, _partialOffset, buffer, offset, toCopy);
            _partialOffset += toCopy;
            if (_partialOffset >= _partialBuffer.Length)
            {
                _partialBuffer = null;
                _partialOffset = 0;
            }
            return toCopy;
        }

        // Tunggu data baru dari queue
        byte[]? chunk = null;
        while (chunk == null && !cancellationToken.IsCancellationRequested && !_disposed)
        {
            _receiveQueue.TryTake(out chunk, millisecondsTimeout: 50);
            if (chunk == null)
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested || chunk == null)
            return 0;

        int copy = Math.Min(chunk.Length, count);
        Array.Copy(chunk, 0, buffer, offset, copy);

        // Simpan sisa jika chunk lebih besar dari buffer
        if (copy < chunk.Length)
        {
            _partialBuffer = chunk;
            _partialOffset = copy;
        }

        return copy;
    }

    public Task WriteAsync(byte[] buffer, int offset, int count)
    {
        if (_disposed) return Task.CompletedTask;
        try
        {
            var packet = new byte[count];
            Array.Copy(buffer, offset, packet, 0, count);
            _inner.SendPacket(packet);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[MavLinkTransportAdapter] Write gagal");
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe dulu sebelum dispose queue agar tidak ada tambahan enqueue
        try { _inner.OnDataReceived -= OnDataReceived; } catch { /* ignore */ }

        // Complete queue agar ReadAsync tidak hang
        try { _receiveQueue.CompleteAdding(); } catch { /* ignore */ }
        try { _receiveQueue.Dispose(); } catch { /* ignore */ }

        // Dispose inner transport terakhir
        try { _inner.Dispose(); } catch { /* ignore */ }
    }
}
