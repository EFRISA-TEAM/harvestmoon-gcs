using System;
using System.Collections.Generic;
using System.Linq;
using HarvestmoonGCS.Core.Diagnostics;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Simple diagnostic logger implementation.
///
/// Fixes:
///  - _enabled: pakai volatile agar SetEnabled() dari thread lain langsung terlihat
///    tanpa harus masuk lock (check sebelum lock, hot path)
///  - TrimLogs: List.RemoveRange(0, n) → Queue (Dequeue) O(1) seperti PerformanceMonitor
///  - BitConverter.ToString: panjang data bisa lebih kecil dari length → Math.Min safety
/// </summary>
internal class DiagnosticLogger : IDiagnosticLogger
{
    // Gunakan Queue agar TrimLogs O(1) bukan O(n)
    private readonly Queue<LogEntry> _logs = new();
    private readonly object _logLock = new();
    private const int MaxLogEntries = 1000;
    // volatile: SetEnabled dari thread lain harus langsung terlihat di hot path
    private volatile bool _enabled = true;

    public void LogTransportData(byte[] data, int length)
    {
        if (!_enabled) return;

        lock (_logLock)
        {
            // Safety: clamp length agar tidak IndexOutOfRange
            int safeLen = Math.Min(length, data?.Length ?? 0);
            var hex     = safeLen > 0
                ? BitConverter.ToString(data!, 0, Math.Min(16, safeLen)).Replace("-", " ")
                : "(empty)";

            Enqueue(new LogEntry
            {
                Timestamp = DateTime.Now,
                Stage     = "Transport",
                Message   = $"Received {length} bytes: {hex}..."
            });
        }
    }

    public void LogWalkerProcessing(int bytesProcessed, bool success)
    {
        if (!_enabled) return;

        lock (_logLock)
        {
            Enqueue(new LogEntry
            {
                Timestamp = DateTime.Now,
                Stage     = "Walker",
                Message   = $"Processed {bytesProcessed} bytes: {(success ? "SUCCESS" : "FAILED")}"
            });
        }
    }

    public void LogPacketParsed(int messageId, int sequenceNumber, byte systemId)
    {
        if (!_enabled) return;

        lock (_logLock)
        {
            Enqueue(new LogEntry
            {
                Timestamp = DateTime.Now,
                Stage     = "Parser",
                Message   = $"Packet parsed - ID={messageId}, Seq={sequenceNumber}, SysID={systemId}"
            });
        }
    }

    public void LogFlightDataUpdate(string fieldName, object oldValue, object newValue)
    {
        if (!_enabled) return;

        lock (_logLock)
        {
            Enqueue(new LogEntry
            {
                Timestamp = DateTime.Now,
                Stage     = "FlightData",
                Message   = $"{fieldName}: {oldValue} \u2192 {newValue}"
            });
        }
    }

    public void LogTelemetryEvent(DateTime timestamp, string summary)
    {
        if (!_enabled) return;

        lock (_logLock)
        {
            Enqueue(new LogEntry
            {
                Timestamp = DateTime.Now,
                Stage     = "Telemetry",
                Message   = $"[{timestamp:HH:mm:ss.fff}] {summary}"
            });
        }
    }

    public void LogUIUpdate(string controlName, string propertyName, object value)
    {
        if (!_enabled) return;

        lock (_logLock)
        {
            Enqueue(new LogEntry
            {
                Timestamp = DateTime.Now,
                Stage     = "UI",
                Message   = $"{controlName}.{propertyName} = {value}"
            });
        }
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
    }

    public string GetLogSummary()
    {
        lock (_logLock)
        {
            return string.Join(Environment.NewLine, _logs.Select(l => l.ToString()));
        }
    }

    public List<LogEntry> GetRecentLogs(int count = 100)
    {
        lock (_logLock)
        {
            return _logs.TakeLast(Math.Max(0, count)).ToList();
        }
    }

    /// <summary>
    /// Enqueue log entry dan trim jika melebihi batas. O(1) dengan Queue.
    /// HARUS dipanggil di dalam lock(_logLock).
    /// </summary>
    private void Enqueue(LogEntry entry)
    {
        _logs.Enqueue(entry);
        while (_logs.Count > MaxLogEntries)
            _logs.Dequeue(); // O(1)
    }
}
