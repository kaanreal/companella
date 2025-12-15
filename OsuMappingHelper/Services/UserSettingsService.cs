using System.Reflection;
using System.Text.Json;
using OsuMappingHelper.Models;

namespace OsuMappingHelper.Services;

/// <summary>
/// Service for managing user settings/preferences.
/// Loads and saves settings.json from next to the executable.
/// </summary>
public class UserSettingsService
{
    private const string SettingsFileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private UserSettings _settings;
    private readonly string _settingsPath;
    private string? _loadError;

    /// <summary>
    /// Gets the loaded settings.
    /// </summary>
    public UserSettings Settings => _settings;

    /// <summary>
    /// Gets the error message if loading failed.
    /// </summary>
    public string? LoadError => _loadError;

    public UserSettingsService()
    {
        // Get path next to the executable
        var exePath = Assembly.GetExecutingAssembly().Location;
        var exeDir = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
        _settingsPath = Path.Combine(exeDir, SettingsFileName);
        
        // Initialize with default settings
        _settings = new UserSettings();
    }

    /// <summary>
    /// Initializes the service by loading settings.
    /// Call this on application startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        await LoadAsync();
    }

    /// <summary>
    /// Loads settings from the file.
    /// Creates default settings if file doesn't exist.
    /// </summary>
    public async Task LoadAsync()
    {
        _loadError = null;

        if (!File.Exists(_settingsPath))
        {
            // No settings file yet - use defaults
            _settings = new UserSettings();
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_settingsPath);
            var loaded = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions);

            if (loaded != null)
            {
                _settings = loaded;
            }
            else
            {
                _settings = new UserSettings();
            }
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
            _settings = new UserSettings();
        }
    }

    /// <summary>
    /// Saves settings to the file.
    /// </summary>
    public async Task SaveAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, JsonOptions);
            await File.WriteAllTextAsync(_settingsPath, json);
        }
        catch (Exception ex)
        {
            _loadError = ex.Message;
            throw;
        }
    }
}
