#if !__ANDROID__
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Services;

namespace HarvestmoonGCS.Platforms.Desktop.Services;

/// <summary>
/// Desktop file service implementation
/// </summary>
public class DesktopFileService : IFileService
{
    private readonly string _appDataPath;
    
    public DesktopFileService()
    {
        _appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Pigeon"
        );
        
        // Ensure directories exist
        Directory.CreateDirectory(Path.Combine(_appDataPath, "missions"));
        Directory.CreateDirectory(Path.Combine(_appDataPath, "parameters"));
        Directory.CreateDirectory(Path.Combine(_appDataPath, "tlogs"));
    }
    
    public async Task<string> SaveMissionFileAsync(string filename, string content)
    {
        var filePath = Path.Combine(_appDataPath, "missions", filename);
        await File.WriteAllTextAsync(filePath, content);
        return filePath;
    }
    
    public async Task<string?> LoadMissionFileAsync(string filename)
    {
        var filePath = Path.Combine(_appDataPath, "missions", filename);
        
        if (!File.Exists(filePath))
            return null;
        
        return await File.ReadAllTextAsync(filePath);
    }
    
    public async Task<string> SaveParameterFileAsync(string filename, string content)
    {
        var filePath = Path.Combine(_appDataPath, "parameters", filename);
        await File.WriteAllTextAsync(filePath, content);
        return filePath;
    }
    
    public async Task<string?> LoadParameterFileAsync(string filename)
    {
        var filePath = Path.Combine(_appDataPath, "parameters", filename);
        
        if (!File.Exists(filePath))
            return null;
        
        return await File.ReadAllTextAsync(filePath);
    }
    
    public async Task<string> ExportToDownloadsAsync(string filename, string content)
    {
        var downloadsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads"
        );
        
        Directory.CreateDirectory(downloadsPath);
        
        var filePath = Path.Combine(downloadsPath, filename);
        await File.WriteAllTextAsync(filePath, content);
        return filePath;
    }
    
    public string[] ListMissionFiles()
    {
        var missionsPath = Path.Combine(_appDataPath, "missions");
        
        if (!Directory.Exists(missionsPath))
            return Array.Empty<string>();
        
        return Directory.GetFiles(missionsPath, "*.txt")
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .Cast<string>()
            .ToArray();
    }
    
    public string[] ListParameterFiles()
    {
        var parametersPath = Path.Combine(_appDataPath, "parameters");
        
        if (!Directory.Exists(parametersPath))
            return Array.Empty<string>();
        
        return Directory.GetFiles(parametersPath, "*.param")
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .Cast<string>()
            .ToArray();
    }
    
    public bool DeleteMissionFile(string filename)
    {
        var filePath = Path.Combine(_appDataPath, "missions", filename);
        
        if (!File.Exists(filePath))
            return false;
        
        try
        {
            File.Delete(filePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Additional methods for compatibility
    public async Task<string?> PickFileAsync(string[] allowedExtensions)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var filterArg = allowedExtensions != null && allowedExtensions.Length > 0
                    ? $"--file-filter=\"Supported Files | {string.Join(" ", allowedExtensions.Select(e => $"*{e}"))}\""
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
                // Fallback
            }
        }
        return await Task.FromResult<string?>(null);
    }

    public async Task<string> SaveTextFileAsync(string filename, string content, string subfolder = "")
    {
        var folderPath = string.IsNullOrEmpty(subfolder) 
            ? _appDataPath 
            : Path.Combine(_appDataPath, subfolder);
        
        Directory.CreateDirectory(folderPath);
        
        var filePath = Path.Combine(folderPath, filename);
        await File.WriteAllTextAsync(filePath, content);
        return filePath;
    }

    public async Task<string?> LoadTextFileAsync(string filename, string subfolder = "")
    {
        var folderPath = string.IsNullOrEmpty(subfolder) 
            ? _appDataPath 
            : Path.Combine(_appDataPath, subfolder);
        
        var filePath = Path.Combine(folderPath, filename);
        
        if (!File.Exists(filePath))
            return null;
        
        return await File.ReadAllTextAsync(filePath);
    }

    public string[] ListFiles(string subfolder, string extension)
    {
        var folderPath = Path.Combine(_appDataPath, subfolder);
        
        if (!Directory.Exists(folderPath))
            return Array.Empty<string>();
        
        var searchPattern = $"*{extension}";
        return Directory.GetFiles(folderPath, searchPattern)
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .Cast<string>()
            .ToArray();
    }

    public bool DeleteFile(string filename, string subfolder = "")
    {
        var folderPath = string.IsNullOrEmpty(subfolder) 
            ? _appDataPath 
            : Path.Combine(_appDataPath, subfolder);
        
        var filePath = Path.Combine(folderPath, filename);
        
        if (!File.Exists(filePath))
            return false;
        
        try
        {
            File.Delete(filePath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
#endif