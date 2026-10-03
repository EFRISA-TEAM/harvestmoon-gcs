using System;
using System.Threading;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Services;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Manages automatic reconnection with exponential backoff.
/// M-2 fix: Dispose CTS lama sebelum membuat yang baru agar tidak ada CTS leak.
/// </summary>
internal class AutoReconnectManager : IDisposable
{
    private readonly MavLinkService _service;
    private Timer? _reconnectTimer;
    private CancellationTokenSource? _reconnectCts;
    private readonly object _ctsLock = new();
    private int _reconnectAttempts;
    private const int MaxReconnectAttempts = 5;
    private const int BaseReconnectDelayMs = 1000;
    private const int MaxReconnectDelayMs = 30000;

    public bool AutoReconnectEnabled { get; set; } = true;

    public AutoReconnectManager(MavLinkService service)
    {
        _service = service;
    }

    public async Task ScheduleReconnectAsync(ConnectionConfig config)
    {
        if (!AutoReconnectEnabled) return;
        if (_reconnectAttempts >= MaxReconnectAttempts) return;

        _reconnectAttempts++;
        var delay = CalculateBackoffDelay();

        System.Diagnostics.Debug.WriteLine(
            $"[AutoReconnect] Scheduling reconnect attempt {_reconnectAttempts}/{MaxReconnectAttempts} in {delay}ms");

        // M-2: Dispose CTS lama sebelum membuat yang baru
        CancellationTokenSource newCts;
        lock (_ctsLock)
        {
            var old = _reconnectCts;
            old?.Cancel();
            old?.Dispose();
            newCts = new CancellationTokenSource();
            _reconnectCts = newCts;
        }

        try
        {
            await Task.Delay(delay, newCts.Token);
            await _service.ConnectAsync(config);
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("[AutoReconnect] Reconnect cancelled");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AutoReconnect] Reconnect failed: {ex.Message}");
        }
    }

    public void CancelReconnect()
    {
        lock (_ctsLock)
        {
            _reconnectCts?.Cancel();
        }
        var t = Interlocked.Exchange(ref _reconnectTimer!, null);
        t?.Dispose();
    }

    public void ResetAttempts()
    {
        _reconnectAttempts = 0;
    }

    private int CalculateBackoffDelay()
    {
        return Math.Min(
            BaseReconnectDelayMs * (int)Math.Pow(2, _reconnectAttempts - 1),
            MaxReconnectDelayMs
        );
    }

    public void Dispose()
    {
        lock (_ctsLock)
        {
            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            _reconnectCts = null;
        }
        var t = Interlocked.Exchange(ref _reconnectTimer!, null);
        t?.Dispose();
    }
}
