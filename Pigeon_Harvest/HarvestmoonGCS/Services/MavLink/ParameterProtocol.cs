using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MavLinkNet;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Implements MAVLink parameter protocol for reading/writing parameters.
///
/// Fixes:
///  - Mixed SemaphoreSlim + lock(_parameters): hilangkan lock() di HandleParamValue,
///    cukup pakai SemaphoreSlim ATAU pakai ConcurrentDictionary.
///    Pilihan: pakai Dictionary dengan SemaphoreSlim konsisten di semua method.
///    HandleParamValue dipanggil dari thread parser (bukan UI) → pakai lock terpisah
///    yang tidak bisa deadlock dengan _paramLock.
///  - _requestTcs dan _setTcs tidak digunakan (dead fields) → hapus
/// </summary>
internal class ParameterProtocol
{
    private readonly MavLinkService _service;

    // Gunakan lock sederhana untuk akses _parameters dari semua thread
    // (tidak pakai SemaphoreSlim+lock campuran)
    private readonly Dictionary<string, float> _parameters = new();
    private readonly object _parametersLock = new();

    // SemaphoreSlim untuk serialisasi operasi request/set (bisa await)
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public ParameterProtocol(MavLinkService service)
    {
        _service = service;
    }

    public async Task RequestParametersAsync()
    {
        await _operationLock.WaitAsync();
        try
        {
            var transport = _service.GetTransport();
            if (transport == null) return;

            var request = new UasParamRequestList
            {
                TargetSystem    = _service.GetTargetSystemId(),
                TargetComponent = _service.GetTargetComponentId()
            };
            transport.SendMessage(request);

            // Beri sedikit waktu sebelum release lock
            await Task.Delay(100);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ParameterProtocol] Request failed: {ex.Message}");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public Task RequestAllParametersAsync() => RequestParametersAsync();

    /// <summary>Kembalikan snapshot parameter saat ini (tidak await operasi async).</summary>
    public Task<Dictionary<string, float>> GetParametersAsync()
    {
        lock (_parametersLock)
        {
            return Task.FromResult(new Dictionary<string, float>(_parameters));
        }
    }

    public Task<Dictionary<string, float>> GetAllParametersAsync() => GetParametersAsync();

    public async Task<bool> SetParameterAsync(string name, float value)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        await _operationLock.WaitAsync();
        try
        {
            var transport = _service.GetTransport();
            if (transport == null) return false;

            // Kirim PARAM_SET
            var paramSet = new UasParamSet
            {
                TargetSystem    = _service.GetTargetSystemId(),
                TargetComponent = _service.GetTargetComponentId(),
                ParamId         = name.ToCharArray(),
                ParamValue      = value,
                ParamType       = MavParamType.Real32
            };
            transport.SendMessage(paramSet);

            // Optimistically update local cache
            lock (_parametersLock)
            {
                _parameters[name] = value;
            }

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ParameterProtocol] Set parameter failed: {ex.Message}");
            return false;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// Dipanggil dari thread parser MAVLink. Hanya pakai lock(_parametersLock),
    /// tidak menyentuh _operationLock agar tidak deadlock.
    /// </summary>
    public void HandleParamValue(UasParamValue paramValue)
    {
        try
        {
            var paramName = new string(paramValue.ParamId).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(paramName)) return;

            lock (_parametersLock)
            {
                _parameters[paramName] = paramValue.ParamValue;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[ParameterProtocol] Received: {paramName} = {paramValue.ParamValue}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ParameterProtocol] Handle param value failed: {ex.Message}");
        }
    }
}
