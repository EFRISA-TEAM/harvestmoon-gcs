using FsCheck;
using FsCheck.Xunit;
using HarvestmoonGCS.Core.Diagnostics;
using System;
using System.Linq;

namespace HarvestmoonGCS.Tests.Diagnostics;

/// <summary>
/// Property-based tests for PerformanceMonitor.
/// Tests universal properties that should hold across all inputs.
/// Feature: transport-layer-debugging
/// </summary>
public class PerformanceMonitorPropertyTests
{
    /// <summary>
    /// Property 31: Stage Latency Measurement
    /// For any packet flowing through the pipeline, the system should measure 
    /// and log the time taken at each stage (transport, walker, parser, event, viewmodel, UI).
    /// Validates: Requirements 9.1
    /// </summary>
    [Property(MaxTest = 20)]
    public Property StageLatencyMeasurement(NonEmptyString stageNameGen, PositiveInt latencyMs)
    {
        var stageName = stageNameGen?.Get;
        if (string.IsNullOrWhiteSpace(stageName)) stageName = "TestStage";
        var latency = TimeSpan.FromMilliseconds(latencyMs.Get % 10000); // Cap at 10 seconds

        // Arrange
        var monitor = new PerformanceMonitor();

        // Act
        monitor.RecordStageLatency(stageName, latency);

        // Assert
        var report = monitor.GetReport();

        if (!report.StageLatencies.ContainsKey(stageName))
        {
            return false.Label($"Report does not contain stage '{stageName}'");
        }

        var stats = report.StageLatencies[stageName];

        // Verify that the recorded latency matches
        if (Math.Abs(stats.Average.TotalMilliseconds - latency.TotalMilliseconds) > 0.01)
        {
            return false.Label($"Expected average latency {latency.TotalMilliseconds}ms, got {stats.Average.TotalMilliseconds}ms");
        }

        // For a single measurement, min, max, average, and P95 should all be the same
        if (stats.Min != latency || stats.Max != latency || stats.P95 != latency)
        {
            return false.Label("For single measurement, min/max/P95 should equal the recorded value");
        }

        return true.ToProperty();
    }

    /// <summary>
    /// Property 32: End-to-End Latency Measurement
    /// For any packet, the system should calculate and log the total latency 
    /// from byte reception to UI update completion.
    /// Validates: Requirements 9.2
    /// </summary>
    [Property(MaxTest = 20)]
    public Property EndToEndLatencyMeasurement(PositiveInt transportMs, PositiveInt walkerMs, 
        PositiveInt parserMs, PositiveInt eventMs, PositiveInt viewmodelMs, PositiveInt uiMs)
    {
        // Cap each stage at 1 second
        var stages = new[]
        {
            ("Transport", TimeSpan.FromMilliseconds(transportMs.Get % 1000)),
            ("Walker", TimeSpan.FromMilliseconds(walkerMs.Get % 1000)),
            ("Parser", TimeSpan.FromMilliseconds(parserMs.Get % 1000)),
            ("Event", TimeSpan.FromMilliseconds(eventMs.Get % 1000)),
            ("ViewModel", TimeSpan.FromMilliseconds(viewmodelMs.Get % 1000)),
            ("UI", TimeSpan.FromMilliseconds(uiMs.Get % 1000))
        };

        // Arrange
        var monitor = new PerformanceMonitor();

        // Act - Record latency for each stage
        foreach (var (stageName, latency) in stages)
        {
            monitor.RecordStageLatency(stageName, latency);
        }

        // Assert
        var report = monitor.GetReport();

        // Verify all stages are recorded
        foreach (var (stageName, expectedLatency) in stages)
        {
            if (!report.StageLatencies.ContainsKey(stageName))
            {
                return false.Label($"Report missing stage '{stageName}'");
            }

            var stats = report.StageLatencies[stageName];
            if (Math.Abs(stats.Average.TotalMilliseconds - expectedLatency.TotalMilliseconds) > 0.01)
            {
                return false.Label($"Stage '{stageName}' latency mismatch");
            }
        }

        // Calculate total end-to-end latency
        var totalLatency = stages.Sum(s => s.Item2.TotalMilliseconds);
        var reportedTotal = report.StageLatencies.Values.Sum(s => s.Average.TotalMilliseconds);

        if (Math.Abs(totalLatency - reportedTotal) > 0.1)
        {
            return false.Label($"Total latency mismatch: expected {totalLatency}ms, got {reportedTotal}ms");
        }

        return true.ToProperty();
    }

    /// <summary>
    /// Property 33: Update Rate Tracking
    /// For any one-second measurement period, the system should calculate and log 
    /// the actual telemetry update rate achieved and compare it to the target 30Hz.
    /// Validates: Requirements 9.3
    /// </summary>
    [Property(MaxTest = 20)]
    public Property UpdateRateTracking(PositiveInt updateRate)
    {
        var rate = updateRate.Get % 100; // Cap at 100 Hz

        // Arrange
        var monitor = new PerformanceMonitor();

        // Act
        monitor.RecordUpdateRate(rate);

        // Assert
        var report = monitor.GetReport();

        if (Math.Abs(report.AverageUpdateRate - rate) > 0.01)
        {
            return false.Label($"Expected update rate {rate}, got {report.AverageUpdateRate}");
        }

        if (report.TargetUpdateRate != 30)
        {
            return false.Label($"Expected target rate 30Hz, got {report.TargetUpdateRate}");
        }

        return true.ToProperty();
    }

    /// <summary>
    /// Property 34: High Latency Warning
    /// For any packet with end-to-end latency exceeding 100ms, the system should 
    /// log a warning including a breakdown of time spent in each pipeline stage.
    /// Validates: Requirements 9.4
    /// 
    /// Note: This property tests that the monitor can track high latencies.
    /// The actual warning logging is handled by the integration layer.
    /// </summary>
    [Property(MaxTest = 20)]
    public Property HighLatencyTracking(PositiveInt highLatencyMs)
    {
        // Generate latency above 100ms threshold
        var latency = TimeSpan.FromMilliseconds(100 + (highLatencyMs.Get % 1000));

        // Arrange
        var monitor = new PerformanceMonitor();

        // Act
        monitor.RecordStageLatency("EndToEnd", latency);

        // Assert
        var report = monitor.GetReport();

        if (!report.StageLatencies.ContainsKey("EndToEnd"))
        {
            return false.Label("Report does not contain EndToEnd stage");
        }

        var stats = report.StageLatencies["EndToEnd"];

        // Verify that high latency is correctly recorded
        if (stats.Max.TotalMilliseconds < 100)
        {
            return false.Label($"Expected max latency >= 100ms, got {stats.Max.TotalMilliseconds}ms");
        }

        // Verify P95 is calculated correctly for high latencies
        if (stats.P95.TotalMilliseconds < 100)
        {
            return false.Label($"Expected P95 >= 100ms, got {stats.P95.TotalMilliseconds}ms");
        }

        return true.ToProperty();
    }

    /// <summary>
    /// Additional test: Multiple measurements should calculate correct statistics
    /// </summary>
    [Property(MaxTest = 20)]
    public Property MultipleLatencyMeasurementsCalculateCorrectStats(NonEmptyString stageNameGen)
    {
        var stageName = stageNameGen?.Get;
        if (string.IsNullOrWhiteSpace(stageName)) stageName = "TestStage";

        // Arrange
        var monitor = new PerformanceMonitor();
        var latencies = new[]
        {
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(50)
        };

        // Act
        foreach (var latency in latencies)
        {
            monitor.RecordStageLatency(stageName, latency);
        }

        // Assert
        var report = monitor.GetReport();
        var stats = report.StageLatencies[stageName];

        // Check average (should be 30ms)
        if (Math.Abs(stats.Average.TotalMilliseconds - 30) > 0.01)
        {
            return false.Label($"Expected average 30ms, got {stats.Average.TotalMilliseconds}ms");
        }

        // Check min (should be 10ms)
        if (Math.Abs(stats.Min.TotalMilliseconds - 10) > 0.01)
        {
            return false.Label($"Expected min 10ms, got {stats.Min.TotalMilliseconds}ms");
        }

        // Check max (should be 50ms)
        if (Math.Abs(stats.Max.TotalMilliseconds - 50) > 0.01)
        {
            return false.Label($"Expected max 50ms, got {stats.Max.TotalMilliseconds}ms");
        }

        // Check P95 (should be 50ms for this dataset - 95% of 5 values = index 4)
        if (Math.Abs(stats.P95.TotalMilliseconds - 50) > 0.01)
        {
            return false.Label($"Expected P95 50ms, got {stats.P95.TotalMilliseconds}ms");
        }

        return true.ToProperty();
    }

    /// <summary>
    /// Additional test: Multiple update rate measurements should calculate correct average
    /// </summary>
    [Property(MaxTest = 20)]
    public Property MultipleUpdateRateMeasurementsCalculateCorrectAverage()
    {
        // Arrange
        var monitor = new PerformanceMonitor();
        var rates = new[] { 25, 28, 30, 32, 35 };

        // Act
        foreach (var rate in rates)
        {
            monitor.RecordUpdateRate(rate);
        }

        // Assert
        var report = monitor.GetReport();
        var expectedAverage = rates.Average(); // Should be 30

        if (Math.Abs(report.AverageUpdateRate - expectedAverage) > 0.01)
        {
            return false.Label($"Expected average {expectedAverage}, got {report.AverageUpdateRate}");
        }

        return true.ToProperty();
    }

    /// <summary>
    /// Additional test: Empty monitor should return empty report
    /// </summary>
    [Property(MaxTest = 10)]
    public Property EmptyMonitorReturnsEmptyReport()
    {
        // Arrange
        var monitor = new PerformanceMonitor();

        // Act
        var report = monitor.GetReport();

        // Assert
        if (report.StageLatencies.Count != 0)
        {
            return false.Label($"Expected 0 stage latencies, got {report.StageLatencies.Count}");
        }

        if (report.AverageUpdateRate != 0)
        {
            return false.Label($"Expected 0 average update rate, got {report.AverageUpdateRate}");
        }

        return true.ToProperty();
    }

    /// <summary>
    /// Additional test: P95 calculation should be correct for various dataset sizes
    /// </summary>
    [Property(MaxTest = 20)]
    public Property P95CalculationIsCorrect(PositiveInt datasetSize)
    {
        var size = Math.Max(1, datasetSize.Get % 100); // 1-100 samples
        var stageName = "TestStage";

        // Arrange
        var monitor = new PerformanceMonitor();

        // Create a dataset with known values (1ms, 2ms, 3ms, ...)
        for (int i = 1; i <= size; i++)
        {
            monitor.RecordStageLatency(stageName, TimeSpan.FromMilliseconds(i));
        }

        // Act
        var report = monitor.GetReport();
        var stats = report.StageLatencies[stageName];

        // Assert
        // P95 should be at the 95th percentile position
        var expectedP95Index = (int)(size * 0.95);
        expectedP95Index = Math.Min(expectedP95Index, size - 1);
        var expectedP95Value = expectedP95Index + 1; // Since we start from 1ms

        // Allow some tolerance due to rounding
        if (Math.Abs(stats.P95.TotalMilliseconds - expectedP95Value) > 1.0)
        {
            return false.Label($"Expected P95 ~{expectedP95Value}ms, got {stats.P95.TotalMilliseconds}ms for dataset size {size}");
        }

        return true.ToProperty();
    }

    /// <summary>
    /// Additional test: Monitor should handle very small latencies correctly
    /// </summary>
    [Property(MaxTest = 10)]
    public Property HandlesSmallLatenciesCorrectly()
    {
        // Arrange
        var monitor = new PerformanceMonitor();
        var smallLatency = TimeSpan.FromTicks(1); // Very small latency

        // Act
        monitor.RecordStageLatency("SmallLatency", smallLatency);

        // Assert
        var report = monitor.GetReport();
        var stats = report.StageLatencies["SmallLatency"];

        if (stats.Average != smallLatency)
        {
            return false.Label("Small latency not recorded correctly");
        }

        return true.ToProperty();
    }

    /// <summary>
    /// Additional test: Monitor should handle zero update rate correctly
    /// </summary>
    [Property(MaxTest = 10)]
    public Property HandlesZeroUpdateRateCorrectly()
    {
        // Arrange
        var monitor = new PerformanceMonitor();

        // Act
        monitor.RecordUpdateRate(0);

        // Assert
        var report = monitor.GetReport();

        if (report.AverageUpdateRate != 0)
        {
            return false.Label($"Expected 0 update rate, got {report.AverageUpdateRate}");
        }

        return true.ToProperty();
    }

    /// <summary>
    /// Additional test: Verify that stage names are case-sensitive
    /// </summary>
    [Property(MaxTest = 10)]
    public Property StageNamesAreCaseSensitive()
    {
        // Arrange
        var monitor = new PerformanceMonitor();

        // Act
        monitor.RecordStageLatency("Transport", TimeSpan.FromMilliseconds(10));
        monitor.RecordStageLatency("transport", TimeSpan.FromMilliseconds(20));

        // Assert
        var report = monitor.GetReport();

        if (!report.StageLatencies.ContainsKey("Transport") || 
            !report.StageLatencies.ContainsKey("transport"))
        {
            return false.Label("Stage names should be case-sensitive");
        }

        if (Math.Abs(report.StageLatencies["Transport"].Average.TotalMilliseconds - 10) > 0.01)
        {
            return false.Label("'Transport' stage has incorrect value");
        }

        if (Math.Abs(report.StageLatencies["transport"].Average.TotalMilliseconds - 20) > 0.01)
        {
            return false.Label("'transport' stage has incorrect value");
        }

        return true.ToProperty();
    }
}
