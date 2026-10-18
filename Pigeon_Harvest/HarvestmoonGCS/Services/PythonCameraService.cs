using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Services;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Camera service using Python OpenCV wrapper
/// Works around OpenCvSharp dependency issues on Ubuntu 24.04
/// </summary>
public class PythonCameraService : ICameraService
{
    private static readonly JsonSerializerOptions CameraJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private Process? _streamProcess;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private Task? _stderrTask;
    private string? _currentSource;
    private readonly string _outputDirectory;
    private readonly string? _pythonScript;
    private readonly string? _pythonCommand;
    private readonly string _pythonLauncherArgs;
    private readonly bool _pythonScriptExists;
    private readonly object _recordingSync = new();
    private StreamWriter? _recordingManifest;
    private string _recordingPath = string.Empty;
    private string _recordingFramesDirectory = string.Empty;
    private int _recordingFrameCount;

    /// <summary>
    /// Serializes all Start/Stop operations so only one camera process can exist at a time.
    /// Not reentrant — internal stop helper bypasses this guard.
    /// </summary>
    private readonly SemaphoreSlim _startStopGate = new(1, 1);

    public bool IsStreaming { get; private set; }
    public bool IsRecording { get; private set; }
    public bool IsClassificationStream { get; private set; }

    public event EventHandler<byte[]>? FrameReceived;
    public event EventHandler<bool>? StreamingStatusChanged;
    public event EventHandler<bool>? RecordingStatusChanged;
    public event EventHandler<string>? ConnectionError;
    public event EventHandler<string>? ClassificationSummaryChanged;

    public string? ClassificationPythonCommand { get; private set; }

    public PythonCameraService()
    {
        // Create output directory for screenshots and videos
        _outputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "HarvestmoonGCS",
            "Camera"
        );
        Directory.CreateDirectory(_outputDirectory);

        (_pythonCommand, _pythonLauncherArgs) = ResolvePythonCommand();
        _pythonScript = ResolvePythonScriptPath();
        _pythonScriptExists = !string.IsNullOrWhiteSpace(_pythonScript) && File.Exists(_pythonScript);

        Serilog.Log.Information($"[PythonCameraService] Output directory: {_outputDirectory}");
        Serilog.Log.Information($"[PythonCameraService] Python command: {_pythonCommand ?? "NOT_FOUND"}");
        Serilog.Log.Information($"[PythonCameraService] Python script: {_pythonScript ?? "NOT_FOUND"}");

        if (string.IsNullOrWhiteSpace(_pythonCommand))
        {
            Serilog.Log.Warning("[PythonCameraService] Python runtime not found. Install python3 and ensure it is in PATH.");
        }

        if (!_pythonScriptExists)
        {
            Serilog.Log.Warning("[PythonCameraService] camera_service.py not found. Set MOONHARVEST_CAMERA_SCRIPT or ensure camera_service.py exists in app/repo root.");
        }
    }

    public async Task InitializeAsync()
    {
        Serilog.Log.Information("[PythonCameraService] Initialized");
        await Task.CompletedTask;
    }

    public async Task<List<CameraSource>> GetAvailableSourcesAsync()
    {
        var sources = new List<CameraSource>();

        if (!EnsureRuntimeReady(notifyUser: false))
        {
            return sources;
        }

        try
        {
            Serilog.Log.Information("[PythonCameraService] ========== DETECTING CAMERAS ==========");

            // Call Python script to list cameras
            var process = new Process
            {
                StartInfo = BuildPythonStartInfo("list")
            };

            Serilog.Log.Information("[PythonCameraService] Calling Python script...");
            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (!string.IsNullOrEmpty(error))
            {
                Serilog.Log.Information($"[PythonCameraService] Python stderr: {error}");
            }

            Serilog.Log.Information($"[PythonCameraService] Python output: {output}");

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                Serilog.Log.Warning("[PythonCameraService] Camera listing returned no usable JSON output (exitCode={ExitCode})", process.ExitCode);
                return sources;
            }

            // Parse JSON output
            try
            {
                sources = JsonSerializer.Deserialize<List<CameraSource>>(output, CameraJsonOptions) ?? new List<CameraSource>();
                NormalizeSources(sources);
            }
            catch (JsonException ex)
            {
                Serilog.Log.Warning(ex, "[PythonCameraService] Failed to parse camera list JSON output");
                return new List<CameraSource>();
            }
            
            Serilog.Log.Information($"[PythonCameraService] ========== FOUND {sources.Count} CAMERA SOURCES ==========");
            
            foreach (var source in sources)
            {
                if (source.Type == CameraSourceType.LocalCamera)
                {
                    Serilog.Log.Information($"[PythonCameraService] ✓ {source.Name}: {source.Description}");
                }
            }

            if (sources.Count(s => s.Type == CameraSourceType.LocalCamera) == 0)
            {
                Serilog.Log.Warning("[PythonCameraService] ⚠️  WARNING: No local cameras detected!");
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[PythonCameraService] ========== ERROR DETECTING CAMERAS ==========");
            ConnectionError?.Invoke(this, $"Camera detection error: {ex.Message}");
        }
        
        return sources;
    }

    private static void NormalizeSources(List<CameraSource> sources)
    {
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (!string.IsNullOrWhiteSpace(source.Id))
            {
                usedIds.Add(source.Id);
            }
        }

        var nextLocalIndex = 0;
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id))
            {
                if (source.Type == CameraSourceType.NetworkStream)
                {
                    var candidate = "network";
                    var suffix = 1;
                    while (usedIds.Contains(candidate))
                    {
                        candidate = $"network_{suffix++}";
                    }

                    source.Id = candidate;
                }
                else
                {
                    while (usedIds.Contains(nextLocalIndex.ToString()))
                    {
                        nextLocalIndex++;
                    }

                    source.Id = nextLocalIndex.ToString();
                }

                usedIds.Add(source.Id);
            }

            if (string.IsNullOrWhiteSpace(source.Name))
            {
                source.Name = source.Type == CameraSourceType.NetworkStream
                    ? "Network Stream (RTSP/HTTP)"
                    : $"Camera {source.Id}";
            }

            if (string.IsNullOrWhiteSpace(source.Description))
            {
                source.Description = source.Type == CameraSourceType.NetworkStream
                    ? "Enter custom network stream URL"
                    : $"Local Camera {source.Id}";
            }
        }
    }

    public async Task<bool> StartCameraAsync(string source)
    {
        if (!EnsureRuntimeReady(notifyUser: true))
        {
            return false;
        }

        await _startStopGate.WaitAsync();
        try
        {
            Serilog.Log.Information($"[PythonCameraService] ========== STARTING CAMERA ==========");
            Serilog.Log.Information($"[PythonCameraService] Source: {source}");

            await StopCameraInternalAsync();

            _currentSource = source;

            if (source == "network")
            {
                Serilog.Log.Information("[PythonCameraService] Network stream selected - user needs to provide URL");
                ConnectionError?.Invoke(this, "Please enter network stream URL");
                return false;
            }

            // Start Python process to stream camera
            _streamProcess = new Process
            {
                StartInfo = BuildPythonStartInfo($"stream {QuoteArg(source)}")
            };

            Serilog.Log.Information("[PythonCameraService] Starting Python stream process...");
            _streamProcess.Start();

            IsStreaming = true;
            StreamingStatusChanged?.Invoke(this, true);

            // Start reading frames
            _streamCts = new CancellationTokenSource();
            _streamTask = Task.Run(() => ReadFramesLoop(_streamCts.Token));
            _stderrTask = Task.Run(() => ReadStdErrLoop(_streamCts.Token));

            Serilog.Log.Information("[PythonCameraService] ✓ Camera streaming started");
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[PythonCameraService] ✗ Error starting camera");
            ConnectionError?.Invoke(this, $"Failed to start camera: {ex.Message}");
            return false;
        }
        finally
        {
            _startStopGate.Release();
        }
    }

    public async Task<bool> StartClassifyStreamAsync(
        string source,
        string modelPath,
        int gridRows = 5,
        int gridCols = 7,
        float minConf = 0.3f,
        float boxScale = 0.85f,
        bool showOverlay = true,
        bool demoMode = true)
    {
        // Resolve the .venv-yolo / .venv Python interpreter
        var (yoloPython, _) = ResolvePythonCommand(".venv-yolo");
        if (string.IsNullOrWhiteSpace(yoloPython))
        {
            (yoloPython, _) = ResolvePythonCommand(".venv");
        }
        if (string.IsNullOrWhiteSpace(yoloPython))
        {
            var errMsg = "YOLO Python (.venv/bin/python3) not found. Please ensure .venv exists with ultralytics installed.";
            Serilog.Log.Error("[PythonCameraService] {Msg}", errMsg);
            ConnectionError?.Invoke(this, errMsg);
            return false;
        }

        // Resolve yolo_classify_stream.py script path
        var classifyScript = ResolveClassifyScriptPath();
        if (string.IsNullOrWhiteSpace(classifyScript) || !File.Exists(classifyScript))
        {
            var errMsg = "yolo_classify_stream.py not found. Ensure it exists alongside camera_service.py.";
            Serilog.Log.Error("[PythonCameraService] {Msg}", errMsg);
            ConnectionError?.Invoke(this, errMsg);
            return false;
        }

        await _startStopGate.WaitAsync();
        try
        {
            Serilog.Log.Information("[PythonCameraService] ========== STARTING CLASSIFICATION STREAM ==========");
            Serilog.Log.Information("[PythonCameraService] Source: {Source}, Model: {Model}", source, modelPath);
            Serilog.Log.Information("[PythonCameraService] Grid: {R}x{C}, MinConf: {Conf}, BoxScale: {Scale}", gridRows, gridCols, minConf, boxScale);

            await StopCameraInternalAsync();

            _currentSource = source;
            IsClassificationStream = true;

            // Build arguments for yolo_classify_stream.py
            var args = $"{QuoteArg(classifyScript)} " +
                       $"--source {QuoteArg(source)} " +
                       $"--model {QuoteArg(modelPath)} " +
                       $"--grid-rows {gridRows} " +
                       $"--grid-cols {gridCols} " +
                       $"--min-conf {minConf.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} " +
                       $"--box-scale {boxScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}" +
                       (demoMode ? " --demo" : "") +
                       (showOverlay ? "" : " --no-overlay");

            var classifyPsi = new ProcessStartInfo
            {
                FileName = yoloPython,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // Force CPU mode — avoids cuDNN version mismatch crash on systems with CUDA installed
            classifyPsi.Environment["CUDA_VISIBLE_DEVICES"] = "";

            _streamProcess = new Process { StartInfo = classifyPsi };

            Serilog.Log.Information("[PythonCameraService] Starting classification stream: {Python} {Args}", yoloPython, args);
            _streamProcess.Start();

            IsStreaming = true;
            StreamingStatusChanged?.Invoke(this, true);

            _streamCts = new CancellationTokenSource();
            _streamTask = Task.Run(() => ReadFramesLoop(_streamCts.Token));
            _stderrTask = Task.Run(() => ReadStdErrLoop(_streamCts.Token));

            Serilog.Log.Information("[PythonCameraService] ✓ Classification stream started");
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[PythonCameraService] ✗ Error starting classification stream");
            IsClassificationStream = false;
            ConnectionError?.Invoke(this, $"Failed to start classification stream: {ex.Message}");
            return false;
        }
        finally
        {
            _startStopGate.Release();
        }
    }

    public async Task<bool> StartHsvStreamAsync(
        string source,
        string? modelPath = null,
        float maxFps = 15f,
        bool showOverlay = true,
        bool demo = true,
        float playbackRate = 1.0f,
        bool yoloEnabled = false)
    {
        // Prefer .venv-yolo / .venv (has ultralytics & onnxruntime → YOLO fusion aktif); fallback .venv-camera
        var (yoloPython, _) = ResolvePythonCommand(".venv-yolo");
        if (string.IsNullOrWhiteSpace(yoloPython))
        {
            (yoloPython, _) = ResolvePythonCommand(".venv");
        }
        var (camPython,  _) = ResolvePythonCommand(null);
        var pythonCmd = yoloPython ?? camPython;
        if (string.IsNullOrWhiteSpace(pythonCmd))
        {
            var errMsg = "Python not found. Ensure .venv-yolo or .venv-camera exists.";
            Serilog.Log.Error("[PythonCameraService] {Msg}", errMsg);
            ConnectionError?.Invoke(this, errMsg);
            return false;
        }
        Serilog.Log.Information("[PythonCameraService] HSV stream python: {Py}",
            yoloPython != null ? ".venv-yolo (YOLO fusion enabled)" : ".venv-camera (HSV-only)");

        var hsvScript = ResolveHsvScriptPath();
        if (string.IsNullOrWhiteSpace(hsvScript) || !File.Exists(hsvScript))
        {
            var errMsg = "moonharvest_detect_stream.py not found.";
            Serilog.Log.Error("[PythonCameraService] {Msg}", errMsg);
            ConnectionError?.Invoke(this, errMsg);
            return false;
        }

        await _startStopGate.WaitAsync();
        try
        {
            Serilog.Log.Information("[PythonCameraService] ========== STARTING HSV STREAM ==========");
            Serilog.Log.Information("[PythonCameraService] Source: {Source}", source);

            await StopCameraInternalAsync();

            _currentSource = source;
            IsClassificationStream = true;

            var args = $"{QuoteArg(hsvScript)} " +
                       $"--source {QuoteArg(source)} " +
                       $"--max-fps {maxFps.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} " +
                       $"--playback-rate {Math.Clamp(playbackRate, 0.25f, 2.0f).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}" +
                       (demo ? " --demo" : "") +
                       (showOverlay ? "" : " --no-overlay");

            if (!string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath))
            {
                args += $" --model {QuoteArg(modelPath)}";
            }

            if (yoloEnabled)
            {
                var detModel = ResolveDetModelPath();
                if (!string.IsNullOrWhiteSpace(detModel) && File.Exists(detModel))
                {
                    args += $" --det-model {QuoteArg(detModel)}";
                }
            }
            else
            {
                args += " --no-yolo";
            }

            var psi = new ProcessStartInfo
            {
                FileName = pythonCmd,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.Environment["CUDA_VISIBLE_DEVICES"] = "";
            if (demo)
                psi.Environment["MOONHARVEST_DEMO"] = "1";

            _streamProcess = new Process { StartInfo = psi };

            Serilog.Log.Information("[PythonCameraService] Starting HSV stream: {Python} {Args}", pythonCmd, args);
            _streamProcess.Start();

            IsStreaming = true;
            StreamingStatusChanged?.Invoke(this, true);

            _streamCts = new CancellationTokenSource();
            _streamTask = Task.Run(() => ReadFramesLoop(_streamCts.Token));
            _stderrTask = Task.Run(() => ReadStdErrLoop(_streamCts.Token));

            Serilog.Log.Information("[PythonCameraService] ✓ HSV stream started");
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[PythonCameraService] ✗ Error starting HSV stream");
            IsClassificationStream = false;
            ConnectionError?.Invoke(this, $"Failed to start HSV stream: {ex.Message}");
            return false;
        }
        finally
        {
            _startStopGate.Release();
        }
    }

    /// <summary>
    /// Internal stop helper — does NOT acquire the semaphore. Used by start methods that already hold the gate.
    /// Idempotent, deadlock-free with bounded waits and hard kill.
    /// </summary>
    private async Task StopCameraInternalAsync()
    {
        Serilog.Log.Information("[PythonCameraService] Stopping camera (internal)");

        // Cancel token first so read loops exit cleanly
        var cts = _streamCts;
        _streamCts = null; // Prevent double-dispose race
        cts?.Cancel();

        // Kill process if still alive
        var process = _streamProcess;
        _streamProcess = null; // Null early to avoid races
        if (process != null && !process.HasExited)
        {
            try
            {
#if NET6_0_OR_GREATER
                process.Kill(entireProcessTree: true);
#else
                process.Kill();
#endif
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "[PythonCameraService] Stream process kill failed");
            }
        }

        // Await read tasks with timeout — don't deadlock if pipe read blocks forever
        var streamTask = _streamTask;
        var stderrTask = _stderrTask;
        _streamTask = null;
        _stderrTask = null;

        const int waitMs = 2000;
        var waitTasks = new List<Task>();
        if (streamTask != null) waitTasks.Add(streamTask);
        if (stderrTask != null) waitTasks.Add(stderrTask);

        if (waitTasks.Count > 0)
        {
            var allTasks = Task.WhenAll(waitTasks);
            var completed = await Task.WhenAny(allTasks, Task.Delay(waitMs));
            if (completed != allTasks)
            {
                Serilog.Log.Warning("[PythonCameraService] Read tasks did not complete within {Ms}ms — continuing cleanup", waitMs);
            }
        }

        // Dispose process
        try { process?.Dispose(); } catch { }

        // Dispose CTS
        try { cts?.Dispose(); } catch { }

        IsStreaming = false;
        IsClassificationStream = false;
        StreamingStatusChanged?.Invoke(this, false);

        Serilog.Log.Information("[PythonCameraService] Camera stopped (internal)");
    }

    public async Task StopCameraAsync()
    {
        await _startStopGate.WaitAsync();
        try
        {
            await StopCameraInternalAsync();
        }
        finally
        {
            _startStopGate.Release();
        }
    }

    private void ReadFramesLoop(CancellationToken ct)
    {
        Serilog.Log.Information("[PythonCameraService] Frame reading loop started");
        
        int frameCount = 0;
        int ignoredOutputCount = 0;
        
        try
        {
            while (!ct.IsCancellationRequested && _streamProcess != null && !_streamProcess.HasExited)
            {
                var line = _streamProcess.StandardOutput.ReadLine();
                
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith('{'))
                {
                    ignoredOutputCount++;
                    if (ignoredOutputCount <= 3 || ignoredOutputCount % 50 == 0)
                    {
                        Serilog.Log.Debug("[PythonCameraService] Ignored non-JSON python stdout: {Line}", line);
                    }
                    continue;
                }
                
                try
                {
                    // Parse JSON frame data
                    var frameData = JsonSerializer.Deserialize<JsonElement>(line);
                    var messageType = frameData.TryGetProperty("type", out var type) ? type.GetString() : null;
                    
                    if (messageType == "frame")
                    {
                        if (frameData.TryGetProperty("data", out var data))
                        {
                            var base64Data = data.GetString();
                            if (!string.IsNullOrEmpty(base64Data))
                            {
                                var frameBytes = Convert.FromBase64String(base64Data);
                                WriteRecordingFrame(frameBytes);
                                FrameReceived?.Invoke(this, frameBytes);
                                
                                frameCount++;
                                
                                if (frameCount % 30 == 0)
                                {
                                    Serilog.Log.Information($"[PythonCameraService] Streamed {frameCount} frames");
                                }
                            }
                        }
                    }
                    else if (messageType == "detection")
                    {
                        if (frameData.TryGetProperty("data", out var detData))
                        {
                            // Pass full data JSON so subscribers can access classes/count/summary
                            ClassificationSummaryChanged?.Invoke(this, detData.GetRawText());
                        }
                    }
                    else if (messageType == "info")
                    {
                        if (frameData.TryGetProperty("data", out var info))
                        {
                            Serilog.Log.Information("[PythonCameraService] {Info}", info.GetString());
                        }
                    }
                    else if (messageType == "end")
                    {
                        Serilog.Log.Information("[PythonCameraService] Python stream ended: {Data}",
                            frameData.TryGetProperty("data", out var endData) ? endData.GetString() : "end");
                        break;
                    }
                    else if (frameData.TryGetProperty("error", out var error))
                    {
                        // Previously this only logged and broke the loop — StartCameraAsync/
                        // StartClassifyStreamAsync already returned true optimistically as soon
                        // as the Python process launched, so a real failure here (bad RTSP URL,
                        // camera unplugged, etc.) surfaced as nothing more than a silent revert
                        // to "Start Camera" once the loop ended, with no on-screen explanation.
                        var errMsg = error.GetString() ?? "Unknown camera error";
                        Serilog.Log.Error($"[PythonCameraService] Python error: {errMsg}");
                        ConnectionError?.Invoke(this, errMsg);
                        break;
                    }
                }
                catch (JsonException ex)
                {
                    Serilog.Log.Warning($"[PythonCameraService] Failed to parse frame: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[PythonCameraService] Frame reading error");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsStreaming = false;
                IsClassificationStream = false;
                StreamingStatusChanged?.Invoke(this, false);
            }
        }

        Serilog.Log.Information("[PythonCameraService] Frame reading loop ended");
    }

    private void ReadStdErrLoop(CancellationToken ct)
    {
        try
        {
            if (_streamProcess == null)
            {
                return;
            }

            while (!ct.IsCancellationRequested && !_streamProcess.HasExited)
            {
                var line = _streamProcess.StandardError.ReadLine();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    Serilog.Log.Debug("[PythonCameraService] python stderr: {Line}", line);
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "[PythonCameraService] stderr reader stopped");
        }
    }

    public async Task<bool> TakePictureAsync(string? filename = null)
    {
        if (!EnsureRuntimeReady(notifyUser: false))
        {
            return false;
        }

        if (!IsStreaming || string.IsNullOrEmpty(_currentSource))
        {
            Serilog.Log.Warning("[PythonCameraService] Cannot take picture - not streaming");
            return false;
        }

        try
        {
            filename ??= Path.Combine(_outputDirectory, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");

            // Call Python script to take picture
            var process = new Process
            {
                StartInfo = BuildPythonStartInfo($"picture {QuoteArg(_currentSource!)} {QuoteArg(filename)}")
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return false;
            }
            
            JsonElement result;
            try
            {
                result = JsonSerializer.Deserialize<JsonElement>(output);
            }
            catch (JsonException)
            {
                return false;
            }

            if (result.TryGetProperty("success", out var success) && success.GetBoolean())
            {
                Serilog.Log.Information($"[PythonCameraService] Picture saved: {filename}");
                return true;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[PythonCameraService] Error taking picture");
            return false;
        }
    }

    private bool EnsureRuntimeReady(bool notifyUser)
    {
        if (!_pythonScriptExists || string.IsNullOrWhiteSpace(_pythonScript))
        {
            if (notifyUser)
            {
                ConnectionError?.Invoke(this, "camera_service.py not found. Set MOONHARVEST_CAMERA_SCRIPT or place camera_service.py in app directory.");
            }

            return false;
        }

        if (string.IsNullOrWhiteSpace(_pythonCommand))
        {
            if (notifyUser)
            {
                ConnectionError?.Invoke(this, "Python runtime not found. Install python3 and retry.");
            }

            return false;
        }

        return true;
    }

    private ProcessStartInfo BuildPythonStartInfo(string scriptArguments)
    {
        var launcher = string.IsNullOrWhiteSpace(_pythonLauncherArgs) ? "" : $"{_pythonLauncherArgs} ";
        var args = $"{launcher}{QuoteArg(_pythonScript!)}";
        if (!string.IsNullOrWhiteSpace(scriptArguments))
        {
            args = $"{args} {scriptArguments}";
        }

        return new ProcessStartInfo
        {
            FileName = _pythonCommand!,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private static string QuoteArg(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    private static string? ResolvePythonScriptPath()
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var envPath = Environment.GetEnvironmentVariable("MOONHARVEST_CAMERA_SCRIPT");
        AddCandidate(candidates, seen, envPath);

        AddCandidate(candidates, seen, Path.Combine(AppContext.BaseDirectory, "camera_service.py"));
        AddCandidate(candidates, seen, Path.Combine(Directory.GetCurrentDirectory(), "camera_service.py"));

        var probeRoots = new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct();

        foreach (var root in probeRoots)
        {
            var current = root;
            for (var depth = 0; depth < 8; depth++)
            {
                AddCandidate(candidates, seen, Path.Combine(current, "camera_service.py"));
                AddCandidate(candidates, seen, Path.Combine(current, "HarvestmoonGCS", "camera_service.py"));
                AddCandidate(candidates, seen, Path.Combine(current, "Pigeon_Harvest", "HarvestmoonGCS", "camera_service.py"));
                AddCandidate(candidates, seen, Path.Combine(current, "Pigeon_Harvest", "camera_service.py"));

                var parent = Directory.GetParent(current);
                if (parent == null)
                {
                    break;
                }

                current = parent.FullName;
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveClassifyScriptPath()
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var envPath = Environment.GetEnvironmentVariable("MOONHARVEST_CLASSIFY_SCRIPT");
        AddCandidate(candidates, seen, envPath);

        AddCandidate(candidates, seen, Path.Combine(AppContext.BaseDirectory, "yolo_classify_stream.py"));
        AddCandidate(candidates, seen, Path.Combine(Directory.GetCurrentDirectory(), "yolo_classify_stream.py"));

        var probeRoots = new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct();

        foreach (var root in probeRoots)
        {
            var current = root;
            for (var depth = 0; depth < 8; depth++)
            {
                AddCandidate(candidates, seen, Path.Combine(current, "yolo_classify_stream.py"));
                AddCandidate(candidates, seen, Path.Combine(current, "HarvestmoonGCS", "yolo_classify_stream.py"));
                AddCandidate(candidates, seen, Path.Combine(current, "Pigeon_Harvest", "HarvestmoonGCS", "yolo_classify_stream.py"));

                var parent = Directory.GetParent(current);
                if (parent == null)
                {
                    break;
                }

                current = parent.FullName;
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolveHsvScriptPath()
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var envPath = Environment.GetEnvironmentVariable("MOONHARVEST_HSV_SCRIPT");
        AddCandidate(candidates, seen, envPath);

        AddCandidate(candidates, seen, Path.Combine(AppContext.BaseDirectory, "moonharvest_detect_stream.py"));
        AddCandidate(candidates, seen, Path.Combine(Directory.GetCurrentDirectory(), "moonharvest_detect_stream.py"));

        var probeRoots = new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct();

        foreach (var root in probeRoots)
        {
            var current = root;
            for (var depth = 0; depth < 8; depth++)
            {
                AddCandidate(candidates, seen, Path.Combine(current, "moonharvest_detect_stream.py"));
                AddCandidate(candidates, seen, Path.Combine(current, "HarvestmoonGCS", "moonharvest_detect_stream.py"));
                AddCandidate(candidates, seen, Path.Combine(current, "Pigeon_Harvest", "HarvestmoonGCS", "moonharvest_detect_stream.py"));
                // TEKNOFEST_SIAP folder
                AddCandidate(candidates, seen, Path.Combine(current, "TEKNOFEST_SIAP", "skrip", "moonharvest_detect_stream.py"));
                AddCandidate(candidates, seen, Path.Combine(current, "Harvestmoon", "TEKNOFEST_SIAP", "skrip", "moonharvest_detect_stream.py"));

                var parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }
        }

        // Absolute fallback — known dev-machine paths
        AddCandidate(candidates, seen, "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/skrip/moonharvest_detect_stream.py");
        AddCandidate(candidates, seen, "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/moonharvest_detect_stream.py");

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? ResolveHealthModelPath()
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        // Env override
        var envPath = Environment.GetEnvironmentVariable("MOONHARVEST_HEALTH_MODEL");
        AddCandidate(candidates, seen, envPath);

        var probeRoots = new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct();

        foreach (var root in probeRoots)
        {
            var current = root;
            for (var depth = 0; depth < 8; depth++)
            {
                // YOLO11n PDP proposal model (moonharvest-health-cls.onnx) — 95.75% Top-1, 4 kelas PDP SINTA 2
                AddCandidate(candidates, seen,
                    Path.Combine(current, "Assets", "models", "moonharvest-health-cls.onnx"));
                AddCandidate(candidates, seen,
                    Path.Combine(current, "HarvestmoonGCS", "Assets", "models", "moonharvest-health-cls.onnx"));
                AddCandidate(candidates, seen,
                    Path.Combine(current, "Assets", "models", "yolo11n_proposal_pdp.onnx"));
                AddCandidate(candidates, seen,
                    Path.Combine(current, "HarvestmoonGCS", "Assets", "models", "yolo11n_proposal_pdp.onnx"));
                // v5/v6 model (moonharvest-health-cls-v5.onnx) — fallback
                AddCandidate(candidates, seen,
                    Path.Combine(current, "Assets", "models", "moonharvest-health-cls-v5.onnx"));
                AddCandidate(candidates, seen,
                    Path.Combine(current, "HarvestmoonGCS", "Assets", "models", "moonharvest-health-cls-v5.onnx"));
                // v6 training run weights (paling baru)
                AddCandidate(candidates, seen,
                    Path.Combine(current, "training", "runs", "moonharvest_v6_nano_fp32", "weights", "best.pt"));
                // v5 training run weights
                AddCandidate(candidates, seen,
                    Path.Combine(current, "training", "runs", "moonharvest_v5_nano_fp32", "weights", "best.pt"));
                // v4 training run weights
                AddCandidate(candidates, seen,
                    Path.Combine(current, "training", "runs", "moonharvest_teknofest_v4", "weights", "best.pt"));
                // Legacy paths — v4 model (2026-06-21)
                AddCandidate(candidates, seen,
                    Path.Combine(current, "runs", "classify", "health_train_v3-20260621", "weights", "best.pt"));
                // v3 fallback
                AddCandidate(candidates, seen,
                    Path.Combine(current, "runs", "classify", "health_train_v3-20260619", "weights", "best.pt"));
                // v2 fallback
                AddCandidate(candidates, seen,
                    Path.Combine(current, "runs", "classify", "health_train_v2-20260619", "weights", "best.pt"));

                var parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }
        }

        // Absolute fallback paths
        AddCandidate(candidates, seen,
            "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/models/moonharvest-health-cls-v5.onnx");
        AddCandidate(candidates, seen,
            "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/runs/moonharvest_v6_nano_fp32/weights/best.pt");
        AddCandidate(candidates, seen,
            "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/runs/moonharvest_v5_nano_fp32/weights/best.pt");

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? ResolveDetModelPath()
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var envPath = Environment.GetEnvironmentVariable("MOONHARVEST_DET_MODEL");
        AddCandidate(candidates, seen, envPath);

        var probeRoots = new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct();

        foreach (var root in probeRoots)
        {
            var current = root;
            for (var depth = 0; depth < 8; depth++)
            {
                AddCandidate(candidates, seen, Path.Combine(current, "Assets", "models", "moonharvest-uav-det.onnx"));
                AddCandidate(candidates, seen, Path.Combine(current, "HarvestmoonGCS", "Assets", "models", "moonharvest-uav-det.onnx"));
                AddCandidate(candidates, seen, Path.Combine(current, "TEKNOFEST_SIAP", "model", "moonharvest-uav-det.onnx"));

                var parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }
        }

        AddCandidate(candidates, seen, "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/models/moonharvest-uav-det.onnx");
        AddCandidate(candidates, seen, "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/model/moonharvest-uav-det.onnx");

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void AddCandidate(List<string> candidates, HashSet<string> seen, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (seen.Add(fullPath))
            {
                candidates.Add(fullPath);
            }
        }
        catch
        {
            // Ignore invalid path candidates
        }
    }

    /// <summary>
    /// Caches ResolvePythonCommand results per venvSubdir. Resolution probes every candidate
    /// python by actually spawning it (CanExecute + HasCv2Module), which can mean dozens of
    /// process launches taking seconds. Without this cache the singleton's constructor — and
    /// every video-duration probe on the dashboard — blocked the UI thread, which is what made
    /// the app appear to freeze/lag on Linux.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string? Command, string LauncherArgs)> _pythonCommandCache = new();

    public static (string? Command, string LauncherArgs) ResolvePythonCommand(string? venvSubdir = null)
    {
        var cacheKey = venvSubdir ?? string.Empty;
        if (_pythonCommandCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var resolved = ResolvePythonCommandUncached(venvSubdir);
        _pythonCommandCache[cacheKey] = resolved;
        return resolved;
    }

    private static (string? Command, string LauncherArgs) ResolvePythonCommandUncached(string? venvSubdir)
    {
        var probes = new List<(string Command, string ProbeArgs, string LauncherArgs)>();

        // Environment override takes priority so users can point to their own venv.
        var envOverride = Environment.GetEnvironmentVariable("MOONHARVEST_CAMERA_PYTHON");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            probes.Add((envOverride!, "--version", string.Empty));
        }

        var probedDirs = new List<string>
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
            // Walk up from the executable until we find a .venv-camera directory. This covers
            // the common case where the app is run from the repo root via `dotnet run` while
            // the venv lives a few directories deeper.
        };

        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                probedDirs.Add(dir.FullName);
            }
        }
        catch
        {
        }

        try
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
            {
                probedDirs.Add(dir.FullName);
            }
        }
        catch
        {
        }

        foreach (var root in probedDirs.Distinct())
        {
            if (OperatingSystem.IsWindows())
            {
                probes.Add((Path.Combine(root, "python", "python.exe"), "--version", string.Empty));
                probes.Add((Path.Combine(root, venvSubdir ?? ".venv-camera", "Scripts", "python.exe"), "--version", string.Empty));
            }
            // When a specific venv is requested, probe it FIRST so it wins over .venv-camera
            if (venvSubdir != null)
            {
                probes.Add((Path.Combine(root, venvSubdir, "bin", "python3"), "--version", string.Empty));
                probes.Add((Path.Combine(root, venvSubdir, "bin", "python"), "--version", string.Empty));
                probes.Add((Path.Combine(root, "Pigeon_Harvest", venvSubdir, "bin", "python3"), "--version", string.Empty));
                probes.Add((Path.Combine(root, "Pigeon_Harvest", venvSubdir, "bin", "python"), "--version", string.Empty));
            }
            else
            {
                probes.Add((Path.Combine(root, ".venv", "bin", "python3"), "--version", string.Empty));
                probes.Add((Path.Combine(root, ".venv", "bin", "python"), "--version", string.Empty));
                probes.Add((Path.Combine(root, "Pigeon_Harvest", ".venv", "bin", "python3"), "--version", string.Empty));
                probes.Add((Path.Combine(root, "Pigeon_Harvest", ".venv", "bin", "python"), "--version", string.Empty));
                probes.Add((Path.Combine(root, ".venv-camera", "bin", "python"), "--version", string.Empty));
                probes.Add((Path.Combine(root, ".venv-camera", "bin", "python3"), "--version", string.Empty));
                probes.Add((Path.Combine(root, "Pigeon_Harvest", ".venv-camera", "bin", "python"), "--version", string.Empty));
                probes.Add((Path.Combine(root, "Pigeon_Harvest", ".venv-camera", "bin", "python3"), "--version", string.Empty));
            }
        }

        probes.Add(("python3", "--version", string.Empty));
        probes.Add(("python", "--version", string.Empty));

        if (OperatingSystem.IsWindows())
        {
            probes.Add(("py", "-3 --version", "-3"));
        }

        foreach (var probe in probes)
        {
            // Prefer pythons that also have cv2 available; fall back to any working python.
            if (CanExecute(probe.Command, probe.ProbeArgs) && HasCv2Module(probe.Command))
            {
                Serilog.Log.Information("[PythonCameraService] Selected python with cv2: {Cmd}", probe.Command);
                return (probe.Command, probe.LauncherArgs);
            }
        }

        foreach (var probe in probes)
        {
            if (CanExecute(probe.Command, probe.ProbeArgs))
            {
                Serilog.Log.Warning("[PythonCameraService] Falling back to python without verified cv2: {Cmd}", probe.Command);
                return (probe.Command, probe.LauncherArgs);
            }
        }

        return (null, string.Empty);
    }

    private static bool HasCv2Module(string command)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = "-c \"import cv2\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool CanExecute(string command, string args)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            if (!process.WaitForExit(2000))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Ignore
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> StartRecordingAsync(string? filename = null)
    {
        if (!IsStreaming)
        {
            ConnectionError?.Invoke(this, "Mulai kamera/video dulu sebelum recording.");
            return false;
        }

        if (IsRecording)
        {
            return true;
        }

        try
        {
            var outputPath = ResolveRecordingPath(filename);
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            lock (_recordingSync)
            {
                _recordingPath = outputPath;
                _recordingFramesDirectory = Path.Combine(
                    outputDir ?? _outputDirectory,
                    $"{Path.GetFileNameWithoutExtension(outputPath)}_frames");
                Directory.CreateDirectory(_recordingFramesDirectory);

                _recordingManifest?.Dispose();
                _recordingManifest = new StreamWriter(new FileStream(outputPath + ".frames.txt", FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true
                };
                _recordingManifest.WriteLine("# HarvestmoonGCS Python camera recording");
                _recordingManifest.WriteLine($"started_utc={DateTime.UtcNow:O}");
                _recordingManifest.WriteLine($"source={_currentSource ?? "unknown"}");
                _recordingManifest.WriteLine($"frames_directory={_recordingFramesDirectory}");
                _recordingFrameCount = 0;
                IsRecording = true;
            }

            RecordingStatusChanged?.Invoke(this, true);
            Serilog.Log.Information("[PythonCameraService] Recording started: {Path}", outputPath);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(this, $"Gagal memulai recording: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> StopRecordingAsync()
    {
        if (!IsRecording)
        {
            return true;
        }

        string outputPath;
        string framesDirectory;
        try
        {
            lock (_recordingSync)
            {
                IsRecording = false;
                outputPath = _recordingPath;
                framesDirectory = _recordingFramesDirectory;
                _recordingManifest?.WriteLine($"stopped_utc={DateTime.UtcNow:O}");
                _recordingManifest?.Flush();
                _recordingManifest?.Dispose();
                _recordingManifest = null;
            }

            TryEncodeMp4(outputPath, framesDirectory, 30);
            RecordingStatusChanged?.Invoke(this, false);
            Serilog.Log.Information("[PythonCameraService] Recording stopped: {Path}", outputPath);
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(this, $"Gagal menghentikan recording: {ex.Message}");
            return false;
        }
    }

    public async Task SendCameraControlAsync(CameraControlCommand command, float value)
    {
        Serilog.Log.Information($"[PythonCameraService] Camera control: {command} = {value}");
        // TODO: Implement camera controls if needed
        await Task.CompletedTask;
    }

    private void WriteRecordingFrame(byte[] frameBytes)
    {
        if (!IsRecording || frameBytes.Length == 0)
        {
            return;
        }

        try
        {
            lock (_recordingSync)
            {
                if (!IsRecording)
                {
                    return;
                }

                var frameNumber = ++_recordingFrameCount;
                var frameName = $"frame_{frameNumber:D06}.jpg";
                var framePath = Path.Combine(_recordingFramesDirectory, frameName);
                File.WriteAllBytes(framePath, frameBytes);
                _recordingManifest?.WriteLine($"{frameNumber},{DateTime.UtcNow:O},{frameName},{frameBytes.Length}");
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[PythonCameraService] Failed to write recording frame");
        }
    }

    private string ResolveRecordingPath(string? filename)
    {
        if (!string.IsNullOrWhiteSpace(filename))
        {
            return Path.IsPathRooted(filename)
                ? filename
                : Path.Combine(_outputDirectory, filename);
        }

        return Path.Combine(_outputDirectory, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
    }

    private static void TryEncodeMp4(string outputPath, string framesDirectory, int fps)
    {
        if (string.IsNullOrWhiteSpace(outputPath) ||
            string.IsNullOrWhiteSpace(framesDirectory) ||
            !Directory.Exists(framesDirectory) ||
            Directory.GetFiles(framesDirectory, "frame_*.jpg").Length == 0)
        {
            return;
        }

        if (!IsCommandAvailable("ffmpeg"))
        {
            File.WriteAllText(outputPath, $"ffmpeg not available. Frames directory: {framesDirectory}{Environment.NewLine}");
            return;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-y -hide_banner -loglevel error -framerate {fps} -i \"{Path.Combine(framesDirectory, "frame_%06d.jpg")}\" -c:v libx264 -pix_fmt yuv420p \"{outputPath}\"",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(); } catch { }
                File.WriteAllText(outputPath, $"ffmpeg timeout. Frames directory: {framesDirectory}{Environment.NewLine}");
            }
            else if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                File.WriteAllText(outputPath, $"ffmpeg failed. Frames directory: {framesDirectory}{Environment.NewLine}{process.StandardError.ReadToEnd()}");
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(outputPath, $"ffmpeg error. Frames directory: {framesDirectory}{Environment.NewLine}{ex.Message}");
        }
    }

    private static bool IsCommandAvailable(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                Arguments = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(2000);
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
