using System.Text.Json.Serialization;

namespace OsuMappingHelper.Models;

/// <summary>
/// Root configuration for dans (difficulty levels).
/// </summary>
public class DanConfiguration
{
    /// <summary>
    /// List of all dan definitions.
    /// </summary>
    [JsonPropertyName("dans")]
    public List<DanDefinition> Dans { get; set; } = new();

    /// <summary>
    /// Schema version for future compatibility.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// Creates a default configuration with placeholder values.
    /// </summary>
    public static DanConfiguration CreateDefault()
    {
        var config = new DanConfiguration();

        // Create all 20 levels (1-10 numeric, then Greek letter names)
        var labels = new[]
        {
            "1", "2", "3", "4", "5", "6", "7", "8", "9", "10",
            "alpha", "beta", "gamma", "delta", "epsilon", 
            "zeta", "eta", "theta", "iota", "kappa"
        };

        // Pattern types to include
        var patternTypes = Enum.GetValues<PatternType>();

        for (int i = 0; i < labels.Length; i++)
        {
            var dan = new DanDefinition
            {
                Label = labels[i],
                Patterns = new Dictionary<string, PatternRequirement>()
            };

            // Create placeholder requirements for each pattern type
            // Scale values based on level index
            foreach (var patternType in patternTypes)
            {
                var baseBpm = 80 + (i * 10);  // 80-270 BPM range
                var baseMsd = 5.0 + (i * 1.5); // 5.0-33.5 MSD range

                dan.Patterns[patternType.ToString()] = new PatternRequirement
                {
                    MinBpm = Math.Max(60, baseBpm - 20),
                    Bpm = baseBpm,
                    MaxBpm = baseBpm + 20,
                    MinMsd = Math.Max(1.0, baseMsd - 2.0),
                    Msd = baseMsd,
                    MaxMsd = baseMsd + 2.0
                };
            }

            config.Dans.Add(dan);
        }

        return config;
    }
}

/// <summary>
/// Definition of a single dan (difficulty level).
/// </summary>
public class DanDefinition
{
    /// <summary>
    /// The label for this dan (1-10 or Greek letter).
    /// </summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Pattern requirements for this dan.
    /// Key is the pattern type name (e.g., "Stream", "Jack").
    /// </summary>
    [JsonPropertyName("patterns")]
    public Dictionary<string, PatternRequirement> Patterns { get; set; } = new();

    /// <summary>
    /// Gets the display name for this dan.
    /// </summary>
    [JsonIgnore]
    public string DisplayName => Label;

    /// <summary>
    /// Gets the low variant display name.
    /// </summary>
    [JsonIgnore]
    public string LowDisplayName => $"{Label} Low";

    /// <summary>
    /// Gets the high variant display name.
    /// </summary>
    [JsonIgnore]
    public string HighDisplayName => $"{Label} High";
}

/// <summary>
/// BPM and MSD requirements for a pattern within a dan.
/// </summary>
public class PatternRequirement
{
    /// <summary>
    /// Minimum BPM threshold (for Low variant).
    /// </summary>
    [JsonPropertyName("minBpm")]
    public double MinBpm { get; set; }

    /// <summary>
    /// Target BPM for this dan level.
    /// </summary>
    [JsonPropertyName("bpm")]
    public double Bpm { get; set; }

    /// <summary>
    /// Maximum BPM threshold (for High variant).
    /// </summary>
    [JsonPropertyName("maxBpm")]
    public double MaxBpm { get; set; }

    /// <summary>
    /// Minimum MSD threshold (for Low variant).
    /// </summary>
    [JsonPropertyName("minMsd")]
    public double MinMsd { get; set; }

    /// <summary>
    /// Target MSD for this dan level.
    /// </summary>
    [JsonPropertyName("msd")]
    public double Msd { get; set; }

    /// <summary>
    /// Maximum MSD threshold (for High variant).
    /// </summary>
    [JsonPropertyName("maxMsd")]
    public double MaxMsd { get; set; }
}

/// <summary>
/// Result of classifying a map against dans.
/// </summary>
public class DanClassificationResult
{
    /// <summary>
    /// The matched dan label.
    /// </summary>
    public string Label { get; set; } = "?";

    /// <summary>
    /// Variant: null for exact match, "Low" or "High" for variants.
    /// </summary>
    public string? Variant { get; set; }

    /// <summary>
    /// Gets the full display name including variant.
    /// </summary>
    public string DisplayName => Variant != null ? $"{Label} {Variant}" : Label;

    /// <summary>
    /// Confidence score (0.0 - 1.0).
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// The dominant pattern that influenced classification.
    /// </summary>
    public string? DominantPattern { get; set; }

    /// <summary>
    /// BPM of the dominant pattern.
    /// </summary>
    public double DominantBpm { get; set; }

    /// <summary>
    /// Overall MSD used for classification.
    /// </summary>
    public double OverallMsd { get; set; }

    /// <summary>
    /// Index of the matched dan (0-19, or -1 if unknown).
    /// </summary>
    public int DanIndex { get; set; } = -1;
}
