using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Models;
using HarvestmoonGCS.Core.Models.AI;

namespace HarvestmoonGCS.Core.Services;

/// <summary>
/// Cross-platform JSON settings service implementation
/// </summary>
public class JsonSettingsService : ISettingsService
{
    private readonly string _settingsFilePath;
    private readonly Dictionary<string, object> _settings;
    private readonly JsonSerializerOptions _jsonOptions;
    private AppSettings _appSettings;

    /// <summary>
    /// Gets the current application settings
    /// </summary>
    public AppSettings Settings => _appSettings;

    public JsonSettingsService(string? settingsFilePath = null)
    {
        // Determine settings file location
        _settingsFilePath = string.IsNullOrWhiteSpace(settingsFilePath)
            ? Path.Combine(GetAppDataPath(), "settings.json")
            : settingsFilePath;
        var settingsDirectory = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrWhiteSpace(settingsDirectory))
        {
            Directory.CreateDirectory(settingsDirectory);
        }
        
        // Configure JSON serializer
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        // Initialize settings dictionary and AppSettings
        _settings = new Dictionary<string, object>();
        _appSettings = new AppSettings();
        
        // Load existing settings
        LoadSettings();
        ApplyDictionaryToAppSettings();
    }

    /// <summary>
    /// Get app data path for storing settings
    /// </summary>
    private string GetAppDataPath()
    {
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pigeonPath = Path.Combine(appDataPath, "HarvestmoonGCS");
        
        // Create folder if it doesn't exist
        if (!Directory.Exists(pigeonPath))
        {
            Directory.CreateDirectory(pigeonPath);
        }
        
        return pigeonPath;
    }

    /// <summary>
    /// Load settings from file
    /// </summary>
    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var loadedSettings = JsonSerializer.Deserialize<Dictionary<string, object>>(json, _jsonOptions);
                
                if (loadedSettings != null)
                {
                    foreach (var kvp in loadedSettings)
                    {
                        _settings[kvp.Key] = kvp.Value;
                    }
                }
            }
        }
        catch (Exception)
        {
            // If loading fails, start with empty settings
            _settings.Clear();
        }
    }

    /// <summary>
    /// Save settings to file
    /// </summary>
    private void SaveSettings()
    {
        try
        {
            SyncAppSettingsToDictionary();
            var json = JsonSerializer.Serialize(_settings, _jsonOptions);
            File.WriteAllText(_settingsFilePath, json);
        }
        catch (Exception)
        {
            // Ignore save errors for now
        }
    }

    public void SaveString(string key, string value)
    {
        _settings[key] = value;
        SaveSettings();
    }

    public string? GetString(string key, string? defaultValue = null)
    {
        if (_settings.TryGetValue(key, out var value))
        {
            return value?.ToString() ?? defaultValue;
        }
        return defaultValue;
    }

    public void SaveInt(string key, int value)
    {
        _settings[key] = value;
        SaveSettings();
    }

    public int GetInt(string key, int defaultValue = 0)
    {
        if (_settings.TryGetValue(key, out var value))
        {
            if (value is JsonElement element && element.ValueKind == JsonValueKind.Number)
            {
                return element.GetInt32();
            }
            if (int.TryParse(value?.ToString(), out var intValue))
            {
                return intValue;
            }
        }
        return defaultValue;
    }

    public void SaveBool(string key, bool value)
    {
        _settings[key] = value;
        SaveSettings();
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        if (_settings.TryGetValue(key, out var value))
        {
            if (value is JsonElement element && element.ValueKind == JsonValueKind.True)
            {
                return true;
            }
            if (value is JsonElement element2 && element2.ValueKind == JsonValueKind.False)
            {
                return false;
            }
            if (bool.TryParse(value?.ToString(), out var boolValue))
            {
                return boolValue;
            }
        }
        return defaultValue;
    }

    public void SaveFloat(string key, float value)
    {
        _settings[key] = value;
        SaveSettings();
    }

    public float GetFloat(string key, float defaultValue = 0f)
    {
        if (_settings.TryGetValue(key, out var value))
        {
            if (value is JsonElement element && element.ValueKind == JsonValueKind.Number)
            {
                return element.GetSingle();
            }
            if (float.TryParse(value?.ToString(), out var floatValue))
            {
                return floatValue;
            }
        }
        return defaultValue;
    }

    public bool Contains(string key)
    {
        return _settings.ContainsKey(key);
    }

    public void Remove(string key)
    {
        _settings.Remove(key);
        SaveSettings();
    }

    public void Clear()
    {
        _settings.Clear();
        SaveSettings();
    }

    // Additional methods for compatibility
    public async Task<bool> SetSettingAsync<T>(string key, T value)
    {
        try
        {
            _settings[key] = value ?? throw new ArgumentNullException(nameof(value));
            ApplySettingToAppSettings(key, value);
            SaveSettings();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public T GetSetting<T>(string key, T defaultValue)
    {
        if (_settings.TryGetValue(key, out var value))
        {
            try
            {
                if (value is T directValue)
                {
                    return directValue;
                }
                
                if (value is JsonElement element)
                {
                    return JsonSerializer.Deserialize<T>(element.GetRawText()) ?? defaultValue;
                }
                
                // Try to convert string representation
                if (typeof(T) == typeof(string))
                {
                    return (T)(object)(value?.ToString() ?? defaultValue?.ToString() ?? "");
                }
                
                if (typeof(T) == typeof(int) && int.TryParse(value?.ToString(), out var intVal))
                {
                    return (T)(object)intVal;
                }
                
                if (typeof(T) == typeof(bool) && bool.TryParse(value?.ToString(), out var boolVal))
                {
                    return (T)(object)boolVal;
                }
                
                if (typeof(T) == typeof(float) && float.TryParse(value?.ToString(), out var floatVal))
                {
                    return (T)(object)floatVal;
                }
            }
            catch
            {
                // Fall through to default
            }
        }

        var appSettingValue = GetAppSettingValue(key);
        if (appSettingValue is T typed)
        {
            return typed;
        }

        if (appSettingValue != null)
        {
            try
            {
                return (T)Convert.ChangeType(appSettingValue, typeof(T));
            }
            catch
            {
            }
        }

        return defaultValue;
    }

    public async Task<bool> SaveSettingsAsync()
    {
        try
        {
            SaveSettings();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> LoadSettingsAsync()
    {
        try
        {
            LoadSettings();
            ApplyDictionaryToAppSettings();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> ResetToDefaultAsync()
    {
        try
        {
            // Clear all settings
            _settings.Clear();
            
            // Reset AppSettings to defaults
            _appSettings = new AppSettings();
            SyncAppSettingsToDictionary();
            
            // Save the reset settings
            SaveSettings();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SyncAppSettingsToDictionary()
    {
        _settings["Language"] = _appSettings.Language;
        _settings["MapType"] = _appSettings.MapType;
        _settings["Connection.IpAddress"] = _appSettings.Connection.IpAddress;
        _settings["Connection.Port"] = _appSettings.Connection.Port;
        _settings["Map.DefaultWaypointRadius"] = _appSettings.Map.DefaultWaypointRadius;
        _settings["AISettings"] = CloneAISettingsWithoutSecrets(_appSettings.AI);
        _settings["VisionRuntime"] = _appSettings.VisionRuntime;
    }

    private void ApplyDictionaryToAppSettings()
    {
        if (_settings.TryGetValue("Language", out var language))
        {
            _appSettings.Language = ConvertToString(language, _appSettings.Language);
        }

        if (_settings.TryGetValue("MapType", out var mapType))
        {
            _appSettings.MapType = ConvertToString(mapType, _appSettings.MapType);
        }

        if (_settings.TryGetValue("Connection.IpAddress", out var ipAddress))
        {
            _appSettings.Connection.IpAddress = ConvertToString(ipAddress, _appSettings.Connection.IpAddress);
        }

        if (_settings.TryGetValue("Connection.Port", out var port))
        {
            _appSettings.Connection.Port = ConvertToInt(port, _appSettings.Connection.Port);
        }

        if (_settings.TryGetValue("Map.DefaultWaypointRadius", out var radius))
        {
            _appSettings.Map.DefaultWaypointRadius = ConvertToFloat(radius, _appSettings.Map.DefaultWaypointRadius);
        }

        if (_settings.TryGetValue("AISettings", out var aiSettings))
        {
            _appSettings.AI = ConvertToAISettings(aiSettings, _appSettings.AI);
        }

        if (_settings.TryGetValue("VisionRuntime", out var visionRuntime))
        {
            _appSettings.VisionRuntime = ConvertToVisionRuntimeSettings(visionRuntime, _appSettings.VisionRuntime);
        }
    }

    private void ApplySettingToAppSettings(string key, object value)
    {
        if (key == "Language")
        {
            _appSettings.Language = ConvertToString(value, _appSettings.Language);
            return;
        }

        if (key == "MapType")
        {
            _appSettings.MapType = ConvertToString(value, _appSettings.MapType);
            return;
        }

        if (key == "Connection.IpAddress")
        {
            _appSettings.Connection.IpAddress = ConvertToString(value, _appSettings.Connection.IpAddress);
            return;
        }

        if (key == "Connection.Port")
        {
            _appSettings.Connection.Port = ConvertToInt(value, _appSettings.Connection.Port);
            return;
        }

        if (key == "Map.DefaultWaypointRadius")
        {
            _appSettings.Map.DefaultWaypointRadius = ConvertToFloat(value, _appSettings.Map.DefaultWaypointRadius);
            return;
        }

        if (key == "AISettings")
        {
            _appSettings.AI = ConvertToAISettings(value, _appSettings.AI);
            return;
        }

        if (key == "VisionRuntime")
        {
            _appSettings.VisionRuntime = ConvertToVisionRuntimeSettings(value, _appSettings.VisionRuntime);
        }
    }

    private object? GetAppSettingValue(string key)
    {
        return key switch
        {
            "Language" => _appSettings.Language,
            "MapType" => _appSettings.MapType,
            "Connection.IpAddress" => _appSettings.Connection.IpAddress,
            "Connection.Port" => _appSettings.Connection.Port,
            "Map.DefaultWaypointRadius" => _appSettings.Map.DefaultWaypointRadius,
            "AISettings" => _appSettings.AI,
            "VisionRuntime" => _appSettings.VisionRuntime,
            _ => null
        };
    }

    private static string ConvertToString(object value, string fallback)
    {
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                return element.GetString() ?? fallback;
            }

            return element.ToString();
        }

        return value?.ToString() ?? fallback;
    }

    private static int ConvertToInt(object value, int fallback)
    {
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var intValue))
            {
                return intValue;
            }

            if (int.TryParse(element.ToString(), out intValue))
            {
                return intValue;
            }

            return fallback;
        }

        if (int.TryParse(value?.ToString(), out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static float ConvertToFloat(object value, float fallback)
    {
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out var floatValue))
            {
                return floatValue;
            }

            if (float.TryParse(element.ToString(), out floatValue))
            {
                return floatValue;
            }

            return fallback;
        }

        if (float.TryParse(value?.ToString(), out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static AISettings ConvertToAISettings(object value, AISettings fallback)
    {
        try
        {
            if (value is AISettings direct)
            {
                return direct;
            }

            if (value is JsonElement element)
            {
                var json = element.GetRawText();
                var result = JsonSerializer.Deserialize<AISettings>(json);
                return result ?? fallback;
            }

            if (value is string str)
            {
                var result = JsonSerializer.Deserialize<AISettings>(str);
                return result ?? fallback;
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static VisionRuntimeSettings ConvertToVisionRuntimeSettings(object value, VisionRuntimeSettings fallback)
    {
        try
        {
            if (value is VisionRuntimeSettings direct)
            {
                return direct;
            }

            if (value is JsonElement element)
            {
                var json = element.GetRawText();
                var result = JsonSerializer.Deserialize<VisionRuntimeSettings>(json);
                return result ?? fallback;
            }

            if (value is string str)
            {
                var result = JsonSerializer.Deserialize<VisionRuntimeSettings>(str);
                return result ?? fallback;
            }
        }
        catch
        {
        }

        return fallback;
    }

    private AISettings CloneAISettingsWithoutSecrets(AISettings source)
    {
        try
        {
            var json = JsonSerializer.Serialize(source, _jsonOptions);
            var clone = JsonSerializer.Deserialize<AISettings>(json, _jsonOptions) ?? new AISettings();
            clone.ApiKey = string.Empty;
            return clone;
        }
        catch
        {
            return new AISettings
            {
                Enabled = source.Enabled,
                Provider = source.Provider,
                FallbackProvider = source.FallbackProvider,
                ApiKey = string.Empty,
                BaseUrl = source.BaseUrl,
                SiteUrl = source.SiteUrl,
                SiteName = source.SiteName,
                Models = source.Models,
                Analysis = source.Analysis,
                TelemetrySampling = source.TelemetrySampling,
                Cache = source.Cache,
                VoiceCommand = source.VoiceCommand,
                AnomalyDetection = source.AnomalyDetection,
                HistoryRetentionDays = source.HistoryRetentionDays
            };
        }
    }
}
