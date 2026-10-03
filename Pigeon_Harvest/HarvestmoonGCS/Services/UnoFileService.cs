using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;
using HarvestmoonGCS.Core.Services;

namespace HarvestmoonGCS.Services;

public class UnoFileService : IFileService
{
    /// <summary>
    /// Root for all mission/parameter files. MyDocuments is empty on Android and on some
    /// Linux desktops, which produced paths rooted at an empty string and made every file
    /// operation fail — fall back to local app data, then to the app directory.
    /// </summary>
    private string BasePath
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(docs)) return docs;

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local)) return local;

            return AppContext.BaseDirectory;
        }
    }

    private string MissionsPath => Path.Combine(BasePath, "Missions");
    private string ParamsPath => Path.Combine(BasePath, "Parameters");
    
    public UnoFileService()
    {
        // Never let an unwritable/absent directory abort construction — the caller still
        // gets a working service and individual operations report their own failures.
        try { Directory.CreateDirectory(MissionsPath); } catch { }
        try { Directory.CreateDirectory(ParamsPath); } catch { }
    }
    
    public async Task<string?> PickFileAsync(string[] extensions)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var filterArg = extensions != null && extensions.Length > 0
                    ? $"--file-filter=\"Supported Files | {string.Join(" ", extensions.Select(e => $"*{e}"))}\""
                    : "";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "zenity",
                    Arguments = $"--file-selection --title=\"Pilih File Model / Class\" {filterArg}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    var output = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    var selectedPath = output.Trim();
                    if (!string.IsNullOrWhiteSpace(selectedPath) && File.Exists(selectedPath))
                    {
                        return selectedPath;
                    }
                }
            }
            catch
            {
                // Fallback to WinUI picker
            }
        }

        try
        {
            var picker = new FileOpenPicker();
            
            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
            InitializeWithWindow.Initialize(picker, hwnd);

            picker.ViewMode = PickerViewMode.List;
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            
            foreach (var ext in extensions)
            {
                picker.FileTypeFilter.Add(ext);
            }

            var file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> SaveFileAsync(string suggestedName, string extension)
    {
        var picker = new FileSavePicker();
        
        var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);

        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = suggestedName;
        picker.FileTypeChoices.Add("File", new[] { extension });

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public Task<string> SaveMissionFileAsync(string filename, string content)
    {
        var path = Path.Combine(MissionsPath, filename);
        File.WriteAllText(path, content);
        return Task.FromResult(path);
    }

    public async Task<string?> LoadMissionFileAsync(string filename)
    {
        var path = Path.Combine(MissionsPath, filename);
        if (File.Exists(path))
            return await File.ReadAllTextAsync(path);
        return null;
    }

    public Task<string> SaveParameterFileAsync(string filename, string content)
    {
        var path = Path.Combine(ParamsPath, filename);
        File.WriteAllText(path, content);
        return Task.FromResult(path);
    }

    public async Task<string?> LoadParameterFileAsync(string filename)
    {
        var path = Path.Combine(ParamsPath, filename);
        if (File.Exists(path))
            return await File.ReadAllTextAsync(path);
        return null;
    }

    public Task<string> ExportToDownloadsAsync(string filename, string content)
    {
        try
        {
            var downloads = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(downloads))
            {
                // Fallback for platforms where MyDocuments is not available
                downloads = AppContext.BaseDirectory;
            }
            var path = Path.Combine(downloads, filename);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, content);
            return Task.FromResult(path);
        }
        catch (Exception ex)
        {
            // Return a clear error message instead of throwing
            Serilog.Log.Error(ex, "[UnoFileService] ExportToDownloadsAsync failed for {Filename}", filename);
            return Task.FromResult($"EXPORT_FAILED: {ex.Message}");
        }
    }

    public string[] ListMissionFiles()
    {
        return Directory.Exists(MissionsPath) 
            ? Directory.GetFiles(MissionsPath, "*.json") 
            : Array.Empty<string>();
    }

    public string[] ListParameterFiles()
    {
        return Directory.Exists(ParamsPath) 
            ? Directory.GetFiles(ParamsPath, "*.json") 
            : Array.Empty<string>();
    }

    public bool DeleteMissionFile(string filename)
    {
        var path = Path.Combine(MissionsPath, filename);
        if (File.Exists(path))
        {
            File.Delete(path);
            return true;
        }
        return false;
    }

    public Task<string> SaveTextFileAsync(string filename, string content, string subfolder = "")
    {
        var path = Path.Combine(BasePath, subfolder);
        Directory.CreateDirectory(path);
        path = Path.Combine(path, filename);
        File.WriteAllText(path, content);
        return Task.FromResult(path);
    }

    public async Task<string?> LoadTextFileAsync(string filename, string subfolder = "")
    {
        var path = Path.Combine(BasePath, subfolder, filename);
        if (File.Exists(path))
            return await File.ReadAllTextAsync(path);
        return null;
    }

    public string[] ListFiles(string subfolder, string extension)
    {
        var path = Path.Combine(BasePath, subfolder);
        if (Directory.Exists(path))
            return Directory.GetFiles(path, $"*{extension}");
        return Array.Empty<string>();
    }

    public bool DeleteFile(string filename, string subfolder = "")
    {
        var path = Path.Combine(BasePath, subfolder, filename);
        if (File.Exists(path))
        {
            File.Delete(path);
            return true;
        }
        return false;
    }
}