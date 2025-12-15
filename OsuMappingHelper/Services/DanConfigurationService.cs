using System.Reflection;
using System.Text.Json;
using OsuMappingHelper.Models;

namespace OsuMappingHelper.Services;

/// <summary>
/// Service for managing dan configuration file.
/// Loads dans.json from next to the executable (copied by build.ps1).
/// </summary>
public class DanConfigurationService
{
    private const string ConfigFileName = "dans.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private DanConfiguration? _configuration;
    private readonly string _configPath;
    private string? _loadError;

    /// <summary>
    /// Gets the loaded configuration.
    /// </summary>
    public DanConfiguration? Configuration => _configuration;

    /// <summary>
    /// Gets whether the configuration has been loaded successfully.
    /// </summary>
    public bool IsLoaded => _configuration != null && _configuration.Dans.Count > 0;

    /// <summary>
    /// Gets the error message if loading failed.
    /// </summary>
    public string? LoadError => _loadError;

    public DanConfigurationService()
    {
        // Get path next to the executable
        var exePath = Assembly.GetExecutingAssembly().Location;
        var exeDir = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
        _configPath = Path.Combine(exeDir, ConfigFileName);
    }

    /// <summary>
    /// Initializes the service by loading the configuration file.
    /// Call this on application startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        await LoadAsync();
    }

    /// <summary>
    /// Loads the configuration from the file.
    /// </summary>
    public async Task LoadAsync()
    {
        _loadError = null;

        if (!File.Exists(_configPath))
        {
            _loadError = $"dans.json not found at {_configPath}";
            Console.WriteLine($"[DanConfig] {_loadError}");
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_configPath);
            _configuration = JsonSerializer.Deserialize<DanConfiguration>(json, JsonOptions);

            if (_configuration == null || _configuration.Dans.Count == 0)
            {
                _loadError = "dans.json is empty or invalid";
                Console.WriteLine($"[DanConfig] {_loadError}");
            }
            else
            {
                Console.WriteLine($"[DanConfig] Loaded {_configuration.Dans.Count} dan definitions");
            }
        }
        catch (Exception ex)
        {
            _loadError = $"Failed to load dans.json: {ex.Message}";
            Console.WriteLine($"[DanConfig] {_loadError}");
        }
    }

    /// <summary>
    /// Classifies a map based on its patterns and MSD.
    /// </summary>
    /// <param name="topPatterns">Top patterns from analysis.</param>
    /// <param name="overallMsd">Overall MSD score.</param>
    /// <returns>Classification result.</returns>
    public DanClassificationResult ClassifyMap(List<TopPattern> topPatterns, double overallMsd)
    {
        var result = new DanClassificationResult
        {
            OverallMsd = overallMsd
        };

        if (_configuration == null || _configuration.Dans.Count == 0 || topPatterns.Count == 0)
        {
            return result;
        }

        // Get the dominant pattern (highest percentage)
        var dominant = topPatterns.FirstOrDefault();
        if (dominant == null)
        {
            return result;
        }

        result.DominantPattern = dominant.ShortName;
        result.DominantBpm = dominant.Bpm;

        // Find the best matching dan
        int bestDanIndex = -1;
        double bestScore = double.MaxValue;
        string? bestVariant = null;

        for (int i = 0; i < _configuration.Dans.Count; i++)
        {
            var dan = _configuration.Dans[i];

            // Try to find the pattern requirement
            if (!dan.Patterns.TryGetValue(dominant.Type.ToString(), out var requirement))
            {
                continue;
            }

            // Calculate score based on how close we are to the dan requirements
            // Lower score = better match
            var (score, variant) = CalculateMatchScore(
                dominant.Bpm, 
                overallMsd, 
                requirement);

            if (score < bestScore)
            {
                bestScore = score;
                bestDanIndex = i;
                bestVariant = variant;
            }
        }

        if (bestDanIndex >= 0)
        {
            var matchedDan = _configuration.Dans[bestDanIndex];
            result.Label = matchedDan.Label;
            result.Variant = bestVariant;
            result.DanIndex = bestDanIndex;

            // Calculate confidence (inverse of score, normalized)
            result.Confidence = Math.Max(0, Math.Min(1, 1.0 - (bestScore / 100.0)));
        }

        return result;
    }

    /// <summary>
    /// Calculates how well a map matches a dan requirement.
    /// Returns (score, variant) where lower score = better match.
    /// </summary>
    private (double Score, string? Variant) CalculateMatchScore(
        double bpm,
        double msd,
        PatternRequirement requirement)
    {
        // Calculate BPM deviation
        double bpmDeviation;
        string? variant = null;

        if (bpm < requirement.MinBpm)
        {
            // Below minimum - poor match
            bpmDeviation = (requirement.MinBpm - bpm) * 2;
        }
        else if (bpm <= requirement.Bpm)
        {
            // In "Low" range
            bpmDeviation = requirement.Bpm - bpm;
            if (bpm < (requirement.MinBpm + requirement.Bpm) / 2)
            {
                variant = "Low";
            }
        }
        else if (bpm <= requirement.MaxBpm)
        {
            // In "High" range
            bpmDeviation = bpm - requirement.Bpm;
            if (bpm > (requirement.Bpm + requirement.MaxBpm) / 2)
            {
                variant = "High";
            }
        }
        else
        {
            // Above maximum - poor match
            bpmDeviation = (bpm - requirement.MaxBpm) * 2;
        }

        // Calculate MSD deviation
        double msdDeviation;

        if (msd < requirement.MinMsd)
        {
            msdDeviation = (requirement.MinMsd - msd) * 5;
        }
        else if (msd <= requirement.Msd)
        {
            msdDeviation = requirement.Msd - msd;
            if (msd < (requirement.MinMsd + requirement.Msd) / 2 && variant == null)
            {
                variant = "Low";
            }
        }
        else if (msd <= requirement.MaxMsd)
        {
            msdDeviation = msd - requirement.Msd;
            if (msd > (requirement.Msd + requirement.MaxMsd) / 2 && variant == null)
            {
                variant = "High";
            }
        }
        else
        {
            msdDeviation = (msd - requirement.MaxMsd) * 5;
        }

        // Combined score (weighted)
        var score = (bpmDeviation * 0.4) + (msdDeviation * 10 * 0.6);

        return (score, variant);
    }

    /// <summary>
    /// Gets the dan at a specific index.
    /// </summary>
    public DanDefinition? GetDan(int index)
    {
        if (_configuration == null || index < 0 || index >= _configuration.Dans.Count)
            return null;

        return _configuration.Dans[index];
    }

    /// <summary>
    /// Gets all dan labels.
    /// </summary>
    public IReadOnlyList<string> GetAllLabels()
    {
        if (_configuration == null)
            return Array.Empty<string>();

        return _configuration.Dans.Select(d => d.Label).ToList();
    }
}
