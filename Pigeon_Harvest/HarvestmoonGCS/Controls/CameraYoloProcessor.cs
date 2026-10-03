using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

#if !__ANDROID__ && !__WASM__
using OpenCvSharp;
using HarvestmoonGCS.Core.Helpers;
#endif

namespace HarvestmoonGCS.Controls;

internal sealed record YoloFrameResult(
    byte[] FrameData,
    int DetectionCount,
    double Fps,
    string Summary,
    IReadOnlyList<VideoDetectionOverlay> Detections);

internal sealed class CameraYoloProcessor : IDisposable
{
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);

#if !__ANDROID__ && !__WASM__
    private YoloDetector? _detector;
#endif

    public bool IsInitialized { get; private set; }
    public string Status { get; private set; } = "YOLO is not initialized";

    public bool Initialize()
    {
#if __ANDROID__ || __WASM__
        Status = "YOLO overlay is available on desktop builds";
        return false;
#else
        if (IsInitialized)
        {
            return true;
        }

        var modelPath = FindExistingPath(
            Environment.GetEnvironmentVariable("HARVESTMOON_YOLO_MODEL"),
            Environment.GetEnvironmentVariable("PIGEON_YOLO_MODEL"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "models", "moonharvest-uav-det.onnx"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "models", "yolov8n-crop-weed-416.onnx"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "models", "yolov8n-agri.onnx"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "models", "yolov8n-320.onnx"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "models", "yolov8n.onnx"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "models", "yolo11n.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "models", "moonharvest-uav-det.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "models", "yolov8n-crop-weed-416.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "models", "yolov8n-agri.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "models", "yolov8n-320.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "models", "yolov8n.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "models", "yolo11n.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "HarvestmoonGCS", "Assets", "models", "moonharvest-uav-det.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "HarvestmoonGCS", "Assets", "models", "yolov8n-crop-weed-416.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "HarvestmoonGCS", "Assets", "models", "yolov8n-agri.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "HarvestmoonGCS", "Assets", "models", "yolov8n-320.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "HarvestmoonGCS", "Assets", "models", "yolov8n.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "HarvestmoonGCS", "Assets", "models", "yolo11n.onnx"));

        var classNamesPath = FindClassPathForModel(modelPath);

        if (modelPath == null)
        {
            Status = "YOLO model not found: add Assets/models/moonharvest-uav-det.onnx or set HARVESTMOON_YOLO_MODEL";
            return false;
        }

        if (classNamesPath == null)
        {
            Status = "YOLO class names not found: add matching classes file or set HARVESTMOON_YOLO_CLASSES";
            return false;
        }

        _detector = new YoloDetector();
        IsInitialized = _detector.Initialize(modelPath, classNamesPath, useCuda: true);
        if (IsInitialized)
        {
            try
            {
                // Verify OpenCvSharp native methods can be loaded (handles Ubuntu 24.04 vs 22.04 libavcodec mismatch)
                using var testMat = new Mat(1, 1, MatType.CV_8UC1);
            }
            catch (Exception ex)
            {
                Serilog.Log.Information("[CameraYoloProcessor] Native OpenCV runtime skipped ({Msg}); relying on Python AI stream.", ex.Message);
                _detector?.Dispose();
                _detector = null;
                IsInitialized = false;
                Status = "Native OpenCV skipped; Python AI stream active";
                return false;
            }
        }
        Status = IsInitialized ? "YOLO ready" : "Failed to initialize YOLO model";
        return IsInitialized;
#endif
    }

    public async Task<YoloFrameResult?> ProcessAsync(byte[] frameData)
    {
        if (!IsInitialized || frameData.Length == 0 || !await _inferenceLock.WaitAsync(0))
        {
            return null;
        }

        try
        {
#if __ANDROID__ || __WASM__
            return null;
#else
            return await Task.Run(() =>
            {
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    using var frame = Cv2.ImDecode(frameData, ImreadModes.Color);
                    if (frame.Empty() || _detector == null)
                    {
                        return null;
                    }

                    var detections = _detector.Detect(frame);
                    var overlays = detections
                        .Select(detection => new VideoDetectionOverlay
                        {
                            Label = detection.ClassName,
                            Confidence = detection.Confidence,
                            X = detection.BoundingBox.X,
                            Y = detection.BoundingBox.Y,
                            Width = detection.BoundingBox.Width,
                            Height = detection.BoundingBox.Height
                        })
                        .ToList();
                    stopwatch.Stop();

                    var fps = stopwatch.Elapsed.TotalSeconds > 0
                        ? 1.0 / stopwatch.Elapsed.TotalSeconds
                        : 0;
                    var summary = BuildSummary(detections.Select(d => d.ClassName));
                    return new YoloFrameResult(frameData, detections.Count, fps, summary, overlays);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Information("[CameraYoloProcessor] In-process inference disabled ({Msg}); falling back to Python stream", ex.Message);
                    IsInitialized = false;
                    return null;
                }
            });
#endif
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public void Dispose()
    {
#if !__ANDROID__ && !__WASM__
        _detector?.Dispose();
        _detector = null;
#endif
        IsInitialized = false;
    }

    private static string? FindExistingPath(params string?[] candidates)
    {
        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .FirstOrDefault(File.Exists);
    }

    private static string? FindClassPathForModel(string? modelPath)
    {
        var explicitClassPath = FindExistingPath(
            Environment.GetEnvironmentVariable("HARVESTMOON_YOLO_CLASSES"),
            Environment.GetEnvironmentVariable("PIGEON_YOLO_CLASSES"));
        if (explicitClassPath != null)
        {
            return explicitClassPath;
        }

        var modelName = Path.GetFileName(modelPath)?.ToLowerInvariant() ?? string.Empty;
        var preferredClassFile = modelName switch
        {
            var name when name.Contains("moonharvest-uav") || name.Contains("uav-det") => "classes-moonharvest-uav-det.txt",
            var name when name.Contains("crop-weed") => "classes-crop-weed.txt",
            var name when name.Contains("agri") => "classes-yolov8n-agri-basic.txt",
            _ => "classes-yolov8n-coco.txt"
        };

        var modelDirs = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "models"),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "models"),
            Path.Combine(Directory.GetCurrentDirectory(), "HarvestmoonGCS", "Assets", "models")
        };

        return FindExistingPath(modelDirs.Select(dir => Path.Combine(dir, preferredClassFile)).ToArray())
            ?? FindExistingPath(modelDirs.Select(dir => Path.Combine(dir, "coco.names")).ToArray());
    }

    private static string BuildSummary(IEnumerable<string> classNames)
    {
        var groups = classNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(name => name)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Take(4)
            .Select(group => $"{group.Key}: {group.Count()}")
            .ToList();

        return groups.Count == 0 ? "No objects" : string.Join(" | ", groups);
    }
}
