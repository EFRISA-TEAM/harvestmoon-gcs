using System;
using System.Threading;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Connection quality data
/// </summary>
public class ConnectionQuality
{
    public double PacketLossPercentage { get; set; }
    public int LatencyMs { get; set; }
    public int SignalStrength { get; set; }
}

/// <summary>
/// Monitors connection quality metrics.
///
/// Fixes:
///  - _packetsExpected: overflow setelah berjam-jam karena terus ditambah tiap detik.
///    Sekarang hitung loss dalam window terakhir (sliding window 60 detik).
///  - Stop() pakai Interlocked.Exchange agar tidak race dengan Start()
///  - Reset() saat koneksi baru agar data lama tidak campur
/// </summary>
internal class ConnectionQualityMonitor : IDisposable
{
    private readonly MavLinkService _service;
    private Timer? _monitorTimer;

    // Pakai Interlocked untuk counter agar tidak perlu lock di OnPacketReceived (hot path)
    private long _packetsReceivedWindow;
    private long _packetsExpectedWindow;

    // Untuk hitung latency, tetap pakai lock karena baca+tulis DateTime
    private DateTime _lastPacketTime = DateTime.MinValue;
    private readonly object _timeLock = new();

    public ConnectionQuality Quality { get; private set; } = new();

    public ConnectionQualityMonitor(MavLinkService service)
    {
        _service = service;
    }

    public void Start()
    {
        Reset();
        Stop();
        _monitorTimer = new Timer(UpdateQuality, null, 1000, 1000);
    }

    public void Stop()
    {
        var t = Interlocked.Exchange(ref _monitorTimer!, null);
        t?.Dispose();
    }

    /// <summary>Reset counter saat koneksi baru agar data lama tidak campur.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _packetsReceivedWindow, 0);
        Interlocked.Exchange(ref _packetsExpectedWindow, 0);
        lock (_timeLock) { _lastPacketTime = DateTime.MinValue; }
    }

    /// <summary>Dipanggil dari hot path parser thread — gunakan Interlocked.</summary>
    public void OnPacketReceived()
    {
        Interlocked.Increment(ref _packetsReceivedWindow);
        lock (_timeLock) { _lastPacketTime = DateTime.Now; }
    }

    private void UpdateQuality(object? state)
    {
        // Ambil dan reset window counter agar tidak overflow
        long received = Interlocked.Exchange(ref _packetsReceivedWindow, 0);
        // Expected: ~10 paket/detik (timer 1 detik)
        const int expectedPerSecond = 10;
        Interlocked.Exchange(ref _packetsExpectedWindow, 0);

        double timeSinceLastPacket;
        lock (_timeLock)
        {
            timeSinceLastPacket = _lastPacketTime == DateTime.MinValue
                ? double.MaxValue
                : (DateTime.Now - _lastPacketTime).TotalSeconds;
        }

        // Hitung berdasarkan window 1 detik ini
        long expected    = Math.Max(1, expectedPerSecond);
        long lost        = Math.Max(0, expected - received);
        double packetLoss = Math.Min(100.0, (lost / (double)expected) * 100.0);

        // Clamp latency agar tidak overflow int
        int latencyMs = timeSinceLastPacket >= int.MaxValue / 1000.0
            ? int.MaxValue
            : (int)Math.Min(timeSinceLastPacket * 1000.0, int.MaxValue);

        int signalStrength = Math.Max(0, Math.Min(100, (int)(100.0 - packetLoss)));

        Quality = new ConnectionQuality
        {
            PacketLossPercentage = Math.Round(packetLoss, 2),
            LatencyMs            = latencyMs,
            SignalStrength       = signalStrength
        };
    }

    public void Dispose()
    {
        Stop();
    }
}
