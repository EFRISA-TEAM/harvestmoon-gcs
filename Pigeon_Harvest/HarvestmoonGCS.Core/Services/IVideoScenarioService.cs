using System;
using System.Collections.Generic;
using HarvestmoonGCS.Core.Models;

namespace HarvestmoonGCS.Core.Services;

/// <summary>
/// Layanan terpusat untuk mengelola profil skenario video dan sinkronisasi
/// waypoint, telemetri, geofence, serta metrik kesehatan tanaman di seluruh aplikasi.
/// </summary>
public interface IVideoScenarioService
{
    IReadOnlyList<VideoScenarioProfile> PredefinedScenarios { get; }
    VideoScenarioProfile CurrentScenario { get; }
    event EventHandler<VideoScenarioProfile>? ScenarioChanged;

    VideoScenarioProfile SelectScenarioByIndex(int index);
    VideoScenarioProfile LoadScenarioForVideo(string videoPath, double durationSeconds = -1);
    void SetCurrentScenario(VideoScenarioProfile scenario);
    string? ResolveVideoFilePath(VideoScenarioProfile profile);
}
