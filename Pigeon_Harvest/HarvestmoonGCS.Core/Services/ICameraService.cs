using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Models;

namespace HarvestmoonGCS.Core.Services;

public interface ICameraService
{
    bool IsStreaming { get; }
    bool IsRecording { get; }
    bool IsClassificationStream { get; }
    
    event EventHandler<byte[]> FrameReceived;
    event EventHandler<bool> StreamingStatusChanged;
    event EventHandler<bool> RecordingStatusChanged;
    event EventHandler<string> ConnectionError;

    Task InitializeAsync();
    Task<List<CameraSource>> GetAvailableSourcesAsync();
    Task<bool> StartCameraAsync(string source);

    /// <summary>
    /// Start HSV detection stream — Python moonharvest_detect_stream.py.
    /// Loop video selamanya di demo mode, deteksi HSV + overlay bounding box.
    /// </summary>
    Task<bool> StartHsvStreamAsync(
        string source,
        string? modelPath = null,
        float maxFps = 15f,
        bool showOverlay = true,
        bool demo = true,
        float playbackRate = 1.0f,
        bool yoloEnabled = false);

    Task StopCameraAsync();
    Task<bool> TakePictureAsync(string filename = null);
    Task<bool> StartRecordingAsync(string filename = null);
    Task<bool> StopRecordingAsync();
    Task SendCameraControlAsync(CameraControlCommand command, float value);
}
