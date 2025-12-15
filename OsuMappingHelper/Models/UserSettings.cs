using System.Text.Json.Serialization;

namespace OsuMappingHelper.Models;

/// <summary>
/// User settings/preferences for the application.
/// </summary>
public class UserSettings
{
    /// <summary>
    /// Schema version for future compatibility.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// Window width in pixels.
    /// </summary>
    [JsonPropertyName("windowWidth")]
    public int WindowWidth { get; set; } = 640;

    /// <summary>
    /// Window height in pixels.
    /// </summary>
    [JsonPropertyName("windowHeight")]
    public int WindowHeight { get; set; } = 810;

    /// <summary>
    /// Window X position in pixels.
    /// </summary>
    [JsonPropertyName("windowX")]
    public int WindowX { get; set; } = 100;

    /// <summary>
    /// Window Y position in pixels.
    /// </summary>
    [JsonPropertyName("windowY")]
    public int WindowY { get; set; } = 100;

    /// <summary>
    /// Window state (Normal, Maximized, etc.).
    /// </summary>
    [JsonPropertyName("windowState")]
    public string WindowState { get; set; } = "Normal";
}
