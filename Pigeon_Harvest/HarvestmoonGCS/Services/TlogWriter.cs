using System;
using System.IO;
using HarvestmoonGCS.Core.Services;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Writes MAVLink telemetry packets to a .tlog file (binary, timestamp-prefixed).
/// Format: [8-byte UTC microseconds][raw MAVLink bytes] — same as Mission Planner tlog.
/// </summary>
public sealed class TlogWriter : IDisposable
{
    private readonly ILoggingService? _logger;
    private FileStream? _fileStream;
    private BinaryWriter? _writer;
    private readonly object _writeLock = new();
    private bool _disposed;

    public bool IsRecording { get; private set; }
    public string? CurrentFilePath { get; private set; }

    public TlogWriter(ILoggingService? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Starts recording to a new .tlog file in the specified folder.
    /// </summary>
    public void StartRecording(string folder)
    {
        lock (_writeLock)
        {
            if (IsRecording)
                return;

            try
            {
                Directory.CreateDirectory(folder);
                var fileName = $"tlog_{DateTime.Now:yyyyMMdd_HHmmss}.tlog";
                CurrentFilePath = Path.Combine(folder, fileName);
                _fileStream = new FileStream(CurrentFilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, useAsync: false);
                _writer = new BinaryWriter(_fileStream);
                IsRecording = true;
                System.Diagnostics.Debug.WriteLine($"[TlogWriter] Recording started: {CurrentFilePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TlogWriter] Failed to start recording: {ex.Message}");
                _logger?.LogError($"TlogWriter start failed: {ex.Message}", nameof(TlogWriter));
                CurrentFilePath = null;
                IsRecording = false;
            }
        }
    }

    /// <summary>
    /// Stops recording and flushes/closes the file.
    /// </summary>
    public void StopRecording()
    {
        lock (_writeLock)
        {
            if (!IsRecording)
                return;

            IsRecording = false;
            FlushAndClose();
            System.Diagnostics.Debug.WriteLine($"[TlogWriter] Recording stopped: {CurrentFilePath}");
        }
    }

    /// <summary>
    /// Writes a raw MAVLink packet with a microsecond UTC timestamp prefix.
    /// </summary>
    public void WritePacket(byte[] rawPacket)
    {
        if (rawPacket == null || rawPacket.Length == 0)
            return;

        lock (_writeLock)
        {
            if (!IsRecording || _writer == null)
                return;

            try
            {
                // Timestamp: microseconds since Unix epoch (big-endian, Mission Planner compatible)
                long microseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
                // Write as big-endian 8 bytes
                _writer.Write((byte)((microseconds >> 56) & 0xFF));
                _writer.Write((byte)((microseconds >> 48) & 0xFF));
                _writer.Write((byte)((microseconds >> 40) & 0xFF));
                _writer.Write((byte)((microseconds >> 32) & 0xFF));
                _writer.Write((byte)((microseconds >> 24) & 0xFF));
                _writer.Write((byte)((microseconds >> 16) & 0xFF));
                _writer.Write((byte)((microseconds >> 8)  & 0xFF));
                _writer.Write((byte)( microseconds        & 0xFF));
                _writer.Write(rawPacket);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TlogWriter] Write failed: {ex.Message}");
                // Stop recording on write error to avoid corrupt file
                IsRecording = false;
                FlushAndClose();
            }
        }
    }

    private void FlushAndClose()
    {
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
            _fileStream?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TlogWriter] Close error: {ex.Message}");
        }
        finally
        {
            _writer = null;
            _fileStream = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopRecording();
    }
}
