using System.Text.Json.Serialization;

namespace OsuMappingHelper.Models;

/// <summary>
/// Root container for dan training data.
/// Stored separately from dans.json to preserve original configuration.
/// </summary>
public class TrainingData
{
    /// <summary>
    /// Schema version for future compatibility.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// All training entries collected from user maps.
    /// </summary>
    [JsonPropertyName("entries")]
    public List<TrainingEntry> Entries { get; set; } = new();

    /// <summary>
    /// Gets all entries for a specific pattern type and dan label.
    /// </summary>
    public IEnumerable<TrainingEntry> GetEntries(string patternType, string danLabel)
    {
        return Entries.Where(e => 
            e.PatternType.Equals(patternType, StringComparison.OrdinalIgnoreCase) &&
            e.DanLabel.Equals(danLabel, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets all unique dan labels in the training data.
    /// </summary>
    public IEnumerable<string> GetUniqueDanLabels()
    {
        return Entries.Select(e => e.DanLabel).Distinct();
    }

    /// <summary>
    /// Gets all unique pattern types in the training data.
    /// </summary>
    public IEnumerable<string> GetUniquePatternTypes()
    {
        return Entries.Select(e => e.PatternType).Distinct();
    }

    /// <summary>
    /// Gets the count of entries for a specific pattern/dan combination.
    /// </summary>
    public int GetEntryCount(string patternType, string danLabel)
    {
        return GetEntries(patternType, danLabel).Count();
    }
}

/// <summary>
/// A single training entry representing a pattern detected in a user's map.
/// </summary>
public class TrainingEntry
{
    /// <summary>
    /// The pattern type name (e.g., "Stream", "Jack", "Chordjack").
    /// </summary>
    [JsonPropertyName("patternType")]
    public string PatternType { get; set; } = string.Empty;

    /// <summary>
    /// The BPM at which this pattern was detected.
    /// </summary>
    [JsonPropertyName("bpm")]
    public double Bpm { get; set; }

    /// <summary>
    /// The MSD (difficulty) score for this pattern.
    /// </summary>
    [JsonPropertyName("msd")]
    public double Msd { get; set; }

    /// <summary>
    /// The dan label assigned by the user (e.g., "5", "alpha", "kappa").
    /// </summary>
    [JsonPropertyName("danLabel")]
    public string DanLabel { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when this entry was created.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional: Source beatmap path for reference.
    /// </summary>
    [JsonPropertyName("sourcePath")]
    public string? SourcePath { get; set; }
}

/// <summary>
/// Aggregated training data for a specific pattern type within a dan.
/// Used during merge calculations.
/// </summary>
public class TrainingPatternData
{
    /// <summary>
    /// The pattern type name.
    /// </summary>
    public string PatternType { get; set; } = string.Empty;

    /// <summary>
    /// The dan label.
    /// </summary>
    public string DanLabel { get; set; } = string.Empty;

    /// <summary>
    /// Number of training entries contributing to this aggregate.
    /// </summary>
    public int EntryCount { get; set; }

    /// <summary>
    /// Average BPM across all training entries.
    /// </summary>
    public double AverageBpm { get; set; }

    /// <summary>
    /// Average MSD across all training entries.
    /// </summary>
    public double AverageMsd { get; set; }

    /// <summary>
    /// Minimum BPM observed in training entries.
    /// </summary>
    public double MinObservedBpm { get; set; }

    /// <summary>
    /// Maximum BPM observed in training entries.
    /// </summary>
    public double MaxObservedBpm { get; set; }

    /// <summary>
    /// Minimum MSD observed in training entries.
    /// </summary>
    public double MinObservedMsd { get; set; }

    /// <summary>
    /// Maximum MSD observed in training entries.
    /// </summary>
    public double MaxObservedMsd { get; set; }

    /// <summary>
    /// Creates aggregate data from a collection of training entries.
    /// </summary>
    public static TrainingPatternData FromEntries(string patternType, string danLabel, IEnumerable<TrainingEntry> entries)
    {
        var entryList = entries.ToList();
        if (entryList.Count == 0)
        {
            return new TrainingPatternData
            {
                PatternType = patternType,
                DanLabel = danLabel,
                EntryCount = 0
            };
        }

        return new TrainingPatternData
        {
            PatternType = patternType,
            DanLabel = danLabel,
            EntryCount = entryList.Count,
            AverageBpm = entryList.Average(e => e.Bpm),
            AverageMsd = entryList.Average(e => e.Msd),
            MinObservedBpm = entryList.Min(e => e.Bpm),
            MaxObservedBpm = entryList.Max(e => e.Bpm),
            MinObservedMsd = entryList.Min(e => e.Msd),
            MaxObservedMsd = entryList.Max(e => e.Msd)
        };
    }
}

