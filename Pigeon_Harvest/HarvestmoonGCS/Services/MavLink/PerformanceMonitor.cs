using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Collections.Concurrent;
using HarvestmoonGCS.Core.Diagnostics;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Simple performance monitor implementation.
/// M-13 fix: Ganti List + RemoveAt(0) O(n) dengan Queue (dequeue dari depan) O(1).
/// Juga hilangkan throw ArgumentException di hot path agar tidak crash produksi.
/// </summary>
internal class PerformanceMonitor : IPerformanceMonitor
{
    private readonly Dictionary<string, Queue<TimeSpan>> _latencies = new();
    private readonly Queue<int> _updateRates = new();
    private readonly object _metricsLock = new();
    private const int MaxMetricsPerOperation = 1000;

    public void RecordStageLatency(string stage, TimeSpan latency)
    {
        if (string.IsNullOrWhiteSpace(stage)) return; // defensive, tidak crash

        lock (_metricsLock)
        {
            if (!_latencies.TryGetValue(stage, out var q))
            {
                q = new Queue<TimeSpan>();
                _latencies[stage] = q;
            }

            q.Enqueue(latency);
            if (q.Count > MaxMetricsPerOperation)
                q.Dequeue(); // O(1)
        }
    }

    public void RecordUpdateRate(int updatesPerSecond)
    {
        if (updatesPerSecond < 0) return; // defensive, tidak crash

        lock (_metricsLock)
        {
            _updateRates.Enqueue(updatesPerSecond);
            if (_updateRates.Count > MaxMetricsPerOperation)
                _updateRates.Dequeue(); // O(1)
        }
    }

    public PerformanceReport GetReport()
    {
        var report = new PerformanceReport();

        lock (_metricsLock)
        {
            foreach (var kvp in _latencies)
            {
                if (kvp.Value.Count > 0)
                {
                    var samples = kvp.Value.ToList();
                    report.StageLatencies[kvp.Key] = new LatencyStats
                    {
                        Average = TimeSpan.FromMilliseconds(samples.Average(ts => ts.TotalMilliseconds)),
                        Min     = samples.Min(),
                        Max     = samples.Max(),
                        P95     = CalculatePercentile(samples, 0.95)
                    };
                }
            }

            if (_updateRates.Count > 0)
            {
                report.AverageUpdateRate = _updateRates.Average();
                report.TargetUpdateRate  = 30;
            }
        }

        return report;
    }

    private static TimeSpan CalculatePercentile(List<TimeSpan> values, double percentile)
    {
        if (values == null || values.Count == 0)
            return TimeSpan.Zero;

        var sorted = values.OrderBy(v => v).ToList();
        int index  = Math.Min((int)(sorted.Count * percentile), sorted.Count - 1);
        return sorted[index];
    }
}
