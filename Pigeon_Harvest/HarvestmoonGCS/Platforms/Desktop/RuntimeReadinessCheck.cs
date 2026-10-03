using System.Diagnostics;
using System.Text.Json;

namespace HarvestmoonGCS;

internal static class RuntimeReadinessCheck
{
    public static int Run(string output)
    {
        var checks = new Dictionary<string, object>();
        var passed = true;
        void Check(string name, Func<string> action)
        {
            try { checks[name] = new { passed = true, detail = action() }; }
            catch (Exception ex) { passed = false; checks[name] = new { passed = false, detail = ex.ToString() }; }
        }
        Check("opencv", () => OpenCvSharp.Cv2.GetVersionString());
        Check("onnx", () => Microsoft.ML.OnnxRuntime.OrtEnv.Instance().GetVersionString());
        Check("sqlite", () =>
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            db.Open();
            using var query = db.CreateCommand();
            query.CommandText = "SELECT 1";
            return query.ExecuteScalar()!.ToString()!;
        });
        Check("vlc", () => { LibVLCSharp.Shared.Core.Initialize(); using var vlc = new LibVLCSharp.Shared.LibVLC(); return vlc.Version; });
        foreach (var name in new[] { "camera_service.py", "moonharvest_detect_stream.py", "yolo_classify_stream.py", "recommendation_bridge.py",
            "Assets/models/moonharvest-health-cls-v5.onnx", "Assets/models/moonharvest-uav-det.onnx" })
            Check(name, () => File.Exists(Path.Combine(AppContext.BaseDirectory, name)) ? "present" : throw new FileNotFoundException(name));
        Check("python_modules", () =>
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "python", "python.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import cv2, numpy, onnxruntime, torch, ultralytics; print('Python camera and AI modules OK')");
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); throw new TimeoutException("Python module check exceeded 120 seconds"); }
            var text = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new InvalidOperationException(text);
            return text;
        });
        Check("ffmpeg", () =>
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"), "-version")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000)) { process.Kill(true); throw new TimeoutException("FFmpeg check timed out"); }
            if (process.ExitCode != 0) throw new InvalidOperationException(stderr.GetAwaiter().GetResult());
            return stdout.GetAwaiter().GetResult();
        });
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            passed, os = Environment.OSVersion.ToString(), architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            checks, note = "Runtime checks only. UI, camera, maps, serial ports and flight functions require interactive/device testing."
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}
