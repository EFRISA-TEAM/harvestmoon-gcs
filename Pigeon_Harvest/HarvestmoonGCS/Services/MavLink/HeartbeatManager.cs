using System;
using System.Threading;
using MavLinkNet;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Manages heartbeat sending and connection health monitoring.
/// H-4 fix: _lastHeartbeatReceived dibaca/ditulis dari dua thread (Timer + caller).
/// Gunakan long (Ticks) + Interlocked.Read/Exchange untuk akses thread-safe tanpa lock.
/// </summary>
internal class HeartbeatManager : IDisposable
{
    private readonly MavLinkService _service;
    private Timer? _heartbeatTimer;

    // H-4: pakai long (Ticks) + Interlocked agar tidak ada torn read pada 32-bit
    private long _lastHeartbeatReceivedTicks = DateTime.MinValue.Ticks;
    private long _lastHeartbeatSentTicks     = DateTime.MinValue.Ticks;

    public TimeSpan TimeSinceLastHeartbeat
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastHeartbeatReceivedTicks);
            if (ticks == DateTime.MinValue.Ticks)
                return TimeSpan.MaxValue;
            return DateTime.Now - new DateTime(ticks);
        }
    }

    public HeartbeatManager(MavLinkService service)
    {
        _service = service;
    }

    public void Start()
    {
        Stop(); // Stop existing timer if any
        _heartbeatTimer = new Timer(SendHeartbeat, null, 0, 1000);
    }

    public void Stop()
    {
        var t = Interlocked.Exchange(ref _heartbeatTimer!, null);
        t?.Dispose();
    }

    public void OnHeartbeatReceived()
    {
        Interlocked.Exchange(ref _lastHeartbeatReceivedTicks, DateTime.Now.Ticks);
    }

    public bool IsConnectionHealthy()
    {
        return TimeSinceLastHeartbeat.TotalSeconds < 10;
    }

    private void SendHeartbeat(object? state)
    {
        try
        {
            var transport = _service.GetTransport();
            if (transport == null || !_service.IsConnected)
                return;

            var heartbeat = new UasHeartbeat
            {
                Type            = MavType.Gcs,
                Autopilot       = MavAutopilot.Invalid,
                BaseMode        = 0,
                CustomMode      = 0,
                SystemStatus    = MavState.Active,
                MavlinkVersion  = 3
            };

            transport.SendMessage(heartbeat);
            Interlocked.Exchange(ref _lastHeartbeatSentTicks, DateTime.Now.Ticks);

            // H1: Jika koneksi tidak sehat DAN sudah pernah terima heartbeat sebelumnya,
            // trigger reconnect via AutoReconnectManager melalui event di service.
            long rxTicks = Interlocked.Read(ref _lastHeartbeatReceivedTicks);
            if (!IsConnectionHealthy() && rxTicks != DateTime.MinValue.Ticks)
            {
                System.Diagnostics.Debug.WriteLine("[HeartbeatManager] Link loss detected — triggering reconnect");
                _service.RaiseConnectionLost();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HeartbeatManager] Failed to send heartbeat: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
