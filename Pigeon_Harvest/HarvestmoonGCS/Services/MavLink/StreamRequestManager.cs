using System;
using System.Threading;
using System.Threading.Tasks;
using MavLinkNet;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Manages automatic MAVLink stream requests to ensure telemetry data is received.
/// M-6 fix: Start() dulu set _isRunning=true sebelum buat Timer, tapi tidak atomic.
/// Gunakan Interlocked.CompareExchange agar hanya satu Start() yang berhasil.
/// </summary>
internal class StreamRequestManager : IDisposable
{
    private readonly MavLinkService _service;
    private Timer? _requestTimer;
    // M-6: pakai int + Interlocked.CompareExchange untuk TOCTOU-safe Start/Stop
    private int _running; // 0 = stopped, 1 = running

    public StreamRequestManager(MavLinkService service)
    {
        _service = service;
    }

    public void Start()
    {
        // Hanya satu thread yang boleh set _running 0→1
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

        // Send initial request immediately
        Task.Run(RequestStreams);

        // Then send every 3 seconds to maintain streams
        _requestTimer = new Timer(_ => RequestStreams(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    public void Stop()
    {
        if (Interlocked.CompareExchange(ref _running, 0, 1) != 1) return;
        var t = Interlocked.Exchange(ref _requestTimer!, null);
        t?.Dispose();
    }

    private void RequestStreams()
    {
        try
        {
            var transport = _service.GetTransport();
            if (transport == null)
                return;

            byte targetSystem    = _service.GetTargetSystemId();
            byte targetComponent = _service.GetTargetComponentId();

            // Request ALL streams to ensure we get data
            RequestDataStream(transport, targetSystem, targetComponent, 0, 10, 1);  // ALL @ 10Hz
            RequestDataStream(transport, targetSystem, targetComponent, 1, 5,  1);  // RAW_SENSORS @ 5Hz
            RequestDataStream(transport, targetSystem, targetComponent, 2, 5,  1);  // EXTENDED_STATUS @ 5Hz
            RequestDataStream(transport, targetSystem, targetComponent, 3, 5,  1);  // RC_CHANNELS @ 5Hz
            RequestDataStream(transport, targetSystem, targetComponent, 6, 5,  1);  // POSITION @ 5Hz
            RequestDataStream(transport, targetSystem, targetComponent, 10, 10, 1); // EXTRA1: ATTITUDE @ 10Hz
            RequestDataStream(transport, targetSystem, targetComponent, 11, 10, 1); // EXTRA2: VFR_HUD @ 10Hz
            RequestDataStream(transport, targetSystem, targetComponent, 12, 5,  1); // EXTRA3: AHRS @ 5Hz
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StreamRequestManager] Error requesting streams: {ex.Message}");
        }
    }

    private void RequestDataStream(MavLinkGenericTransport transport, byte targetSystem,
        byte targetComponent, byte streamId, ushort rate, byte startStop)
    {
        try
        {
            var message = new UasRequestDataStream
            {
                TargetSystem    = targetSystem,
                TargetComponent = targetComponent,
                ReqStreamId     = streamId,
                ReqMessageRate  = rate,
                StartStop       = startStop
            };
            transport.SendMessage(message);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[StreamRequestManager] Error sending stream request {streamId}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
