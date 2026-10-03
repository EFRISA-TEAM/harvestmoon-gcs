using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MavLinkNet;
using HarvestmoonGCS.Core.Models;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Implements MAVLink mission protocol for waypoint upload/download.
///
/// Fixes:
///  - TCS tidak di-cancel saat timeout → sekarang TrySetCanceled dipanggil
///  - ushort overflow saat count > 65535: clamp ke ushort.MaxValue
///  - CancellationToken support pada UploadMissionAsync / DownloadMissionAsync
///  - _uploadTcs / _downloadTcs di-null setelah dipakai agar tidak stale
/// </summary>
internal class MissionProtocol
{
    private readonly MavLinkService _service;
    private readonly SemaphoreSlim _missionLock = new(1, 1);

    private TaskCompletionSource<bool>? _uploadTcs;
    private TaskCompletionSource<List<WaypointData>>? _downloadTcs;

    private List<WaypointData>? _missionToUpload;
    private List<WaypointData>? _downloadedMission;

    private int _missionItemsExpected;
    private int _missionItemsReceived;

    private const int OperationTimeoutMs = 15000;

    public MissionProtocol(MavLinkService service)
    {
        _service = service;
    }

    public async Task<bool> UploadMissionAsync(IEnumerable<WaypointData> waypoints,
        CancellationToken ct = default)
    {
        await _missionLock.WaitAsync(ct);
        try
        {
            var waypointList = waypoints.ToList();
            _missionToUpload = waypointList;

            var transport = _service.GetTransport();
            if (transport == null)
                return false;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _uploadTcs = tcs;

            _missionItemsExpected = waypointList.Count;
            _missionItemsReceived = 0;

            // ushort overflow fix: clamp count
            ushort count = (ushort)Math.Min(waypointList.Count, ushort.MaxValue);
            var missionCount = new UasMissionCount
            {
                TargetSystem    = _service.GetTargetSystemId(),
                TargetComponent = _service.GetTargetComponentId(),
                Count           = count
            };
            transport.SendMessage(missionCount);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(OperationTimeoutMs);

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                // TCS fix: cancel TCS agar tidak abandoned
                tcs.TrySetCanceled();
                _uploadTcs = null;
                return false;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MissionProtocol] UploadMission error: {ex.Message}");
            return false;
        }
        finally
        {
            _missionLock.Release();
        }
    }

    public async Task<List<WaypointData>?> DownloadMissionAsync(CancellationToken ct = default)
    {
        await _missionLock.WaitAsync(ct);
        try
        {
            var transport = _service.GetTransport();
            if (transport == null)
                return null;

            _downloadedMission    = new List<WaypointData>();
            _missionItemsReceived = 0;
            _missionItemsExpected = 0;

            var tcs = new TaskCompletionSource<List<WaypointData>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _downloadTcs = tcs;

            var missionRequestList = new UasMissionRequestList
            {
                TargetSystem    = _service.GetTargetSystemId(),
                TargetComponent = _service.GetTargetComponentId()
            };
            transport.SendMessage(missionRequestList);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(OperationTimeoutMs);

            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled();
                _downloadTcs = null;
                return null;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MissionProtocol] DownloadMission error: {ex.Message}");
            return null;
        }
        finally
        {
            _missionLock.Release();
        }
    }

    public async Task<bool> SetCurrentWaypointAsync(int waypointIndex)
    {
        try
        {
            var transport = _service.GetTransport();
            if (transport == null) return false;

            var missionSetCurrent = new UasMissionSetCurrent
            {
                TargetSystem    = _service.GetTargetSystemId(),
                TargetComponent = _service.GetTargetComponentId(),
                Seq             = (ushort)Math.Clamp(waypointIndex, 0, ushort.MaxValue)
            };
            transport.SendMessage(missionSetCurrent);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MissionProtocol] SetCurrentWaypoint error: {ex.Message}");
            return false;
        }
    }

    // ── Packet handlers (dipanggil dari MavLinkService) ─────────────────────────

    public void HandleMissionCount(UasMissionCount msg)
    {
        try
        {
            _missionItemsExpected = msg.Count;
            _missionItemsReceived = 0;
            _downloadedMission    = new List<WaypointData>();

            System.Diagnostics.Debug.WriteLine($"[MissionProtocol] Download: expecting {msg.Count} items");

            if (msg.Count == 0)
            {
                _downloadTcs?.TrySetResult(new List<WaypointData>());
                _downloadTcs = null;
                return;
            }

            // Request item 0
            var req = new UasMissionRequestInt
            {
                TargetSystem    = _service.GetTargetSystemId(),
                TargetComponent = _service.GetTargetComponentId(),
                Seq             = 0
            };
            _service.GetTransport()?.SendMessage(req);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MissionProtocol] HandleMissionCount error: {ex.Message}");
        }
    }

    public void HandleMissionItemInt(UasMissionItemInt msg)
    {
        try
        {
            if (_downloadTcs == null || _downloadedMission == null) return;

            var wp = new WaypointData
            {
                Sequence  = msg.Seq,
                Latitude  = msg.X / 1e7,
                Longitude = msg.Y / 1e7,
                Altitude  = msg.Z,
                Command   = (WaypointCommand)msg.Command
            };
            _downloadedMission.Add(wp);
            _missionItemsReceived++;

            if (_missionItemsReceived >= _missionItemsExpected)
            {
                var ack = new UasMissionAck
                {
                    TargetSystem    = _service.GetTargetSystemId(),
                    TargetComponent = _service.GetTargetComponentId(),
                    Type            = MavMissionResult.MavMissionAccepted
                };
                _service.GetTransport()?.SendMessage(ack);

                var result = _downloadedMission.ToList();
                _downloadTcs.TrySetResult(result);
                _downloadTcs = null;
                System.Diagnostics.Debug.WriteLine($"[MissionProtocol] Download complete: {result.Count} items");
            }
            else
            {
                var nextSeq = (ushort)_missionItemsReceived;
                var reqNext = new UasMissionRequestInt
                {
                    TargetSystem    = _service.GetTargetSystemId(),
                    TargetComponent = _service.GetTargetComponentId(),
                    Seq             = nextSeq
                };
                _service.GetTransport()?.SendMessage(reqNext);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MissionProtocol] HandleMissionItemInt error: {ex.Message}");
        }
    }

    public void HandleMissionItem(UasMissionItem msg)
    {
        try
        {
            if (_downloadTcs == null || _downloadedMission == null) return;

            var wp = new WaypointData
            {
                Sequence  = msg.Seq,
                Latitude  = msg.X,
                Longitude = msg.Y,
                Altitude  = msg.Z,
                Command   = (WaypointCommand)msg.Command
            };
            _downloadedMission.Add(wp);
            _missionItemsReceived++;

            if (_missionItemsReceived >= _missionItemsExpected)
            {
                var ack = new UasMissionAck
                {
                    TargetSystem    = _service.GetTargetSystemId(),
                    TargetComponent = _service.GetTargetComponentId(),
                    Type            = MavMissionResult.MavMissionAccepted
                };
                _service.GetTransport()?.SendMessage(ack);

                var result = _downloadedMission.ToList();
                _downloadTcs.TrySetResult(result);
                _downloadTcs = null;
                System.Diagnostics.Debug.WriteLine("[MissionProtocol] Download complete (non-int)");
            }
            else
            {
                var nextSeq = (ushort)_missionItemsReceived;
                var reqNext = new UasMissionRequest
                {
                    TargetSystem    = _service.GetTargetSystemId(),
                    TargetComponent = _service.GetTargetComponentId(),
                    Seq             = nextSeq
                };
                _service.GetTransport()?.SendMessage(reqNext);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MissionProtocol] HandleMissionItem error: {ex.Message}");
        }
    }

    public void HandleMissionRequest(UasMissionRequest msg)
    {
        try
        {
            if (_uploadTcs == null || _missionToUpload == null) return;

            int seq = msg.Seq;
            if (seq < 0 || seq >= _missionToUpload.Count)
            {
                System.Diagnostics.Debug.WriteLine($"[MissionProtocol] Invalid seq {seq}");
                return;
            }

            var wp   = _missionToUpload[seq];
            var item = new UasMissionItem
            {
                TargetSystem    = _service.GetTargetSystemId(),
                TargetComponent = _service.GetTargetComponentId(),
                Seq             = (ushort)seq,
                Command         = (MavCmd)wp.Command,
                X               = (float)wp.Latitude,
                Y               = (float)wp.Longitude,
                Z               = (float)wp.Altitude,
                Autocontinue    = 1,
                Frame           = MavLinkNet.MavFrame.GlobalRelativeAlt
            };
            _service.GetTransport()?.SendMessage(item);
            _missionItemsReceived++;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MissionProtocol] HandleMissionRequest error: {ex.Message}");
        }
    }

    public void HandleMissionRequestInt(UasMissionRequestInt msg)
    {
        try
        {
            if (_uploadTcs == null || _missionToUpload == null) return;

            int seq = msg.Seq;
            if (seq < 0 || seq >= _missionToUpload.Count) return;

            var wp   = _missionToUpload[seq];
            var item = new UasMissionItemInt
            {
                TargetSystem    = _service.GetTargetSystemId(),
                TargetComponent = _service.GetTargetComponentId(),
                Seq             = (ushort)seq,
                Command         = (MavCmd)wp.Command,
                X               = (int)(wp.Latitude  * 1e7),
                Y               = (int)(wp.Longitude * 1e7),
                Z               = (float)wp.Altitude,
                Autocontinue    = 1,
                Frame           = MavLinkNet.MavFrame.GlobalRelativeAlt
            };
            _service.GetTransport()?.SendMessage(item);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MissionProtocol] HandleMissionRequestInt error: {ex.Message}");
        }
    }

    public void HandleMissionAck(UasMissionAck msg)
    {
        try
        {
            bool success = msg.Type == MavMissionResult.MavMissionAccepted;
            var tcs      = _uploadTcs;
            _uploadTcs   = null;
            tcs?.TrySetResult(success);
            System.Diagnostics.Debug.WriteLine(
                $"[MissionProtocol] Upload ACK: {msg.Type} → {success}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MissionProtocol] HandleMissionAck error: {ex.Message}");
        }
    }
}
