namespace Companella.Models.Session;

/// <summary>
/// Represents a single control point on the MSD curve.
/// </summary>
public class MsdControlPoint
{
    /// <summary>
    /// Position on the time axis as a percentage (0-100).
    /// </summary>
    public double TimePercent { get; set; }

    /// <summary>
    /// MSD offset as a percentage from the base MSD (e.g., -10 means 10% below base, +15 means 15% above).
    /// </summary>
    public double MsdPercent { get; set; }

    /// <summary>
    /// Focus skillset for this segment of the session (null for any).
    /// </summary>
    public string? Skillset { get; set; }

    /// <summary>
    /// Creates a new control point.
    /// </summary>
    public MsdControlPoint(double timePercent, double msdPercent, string? skillset = null)
    {
        TimePercent = Math.Clamp(timePercent, 0, 100);
        MsdPercent = msdPercent;
        Skillset = skillset;
    }

    /// <summary>
    /// Creates a copy of this control point.
    /// </summary>
    public MsdControlPoint Clone() => new MsdControlPoint(TimePercent, MsdPercent, Skillset);
}

/// <summary>
/// Configuration for the MSD curve used in session planning.
/// Defines how MSD changes over the duration of a session.
/// </summary>
public class MsdCurveConfig
{
    private readonly List<MsdControlPoint> _points = new();

    /// <summary>
    /// The base MSD value that percentages are calculated from.
    /// </summary>
    public double BaseMsd { get; set; } = 20.0;

    /// <summary>
    /// Total session duration in minutes.
    /// </summary>
    public int TotalSessionMinutes { get; set; } = 80;

    /// <summary>
    /// Read-only access to the control points, sorted by TimePercent.
    /// </summary>
    public IReadOnlyList<MsdControlPoint> Points => _points.AsReadOnly();

    /// <summary>
    /// Minimum number of control points required.
    /// </summary>
    public const int MinimumPoints = 2;

    /// <summary>
    /// Creates a new MsdCurveConfig with default curve.
    /// </summary>
    public MsdCurveConfig()
    {
        SetDefaultCurve();
    }

    /// <summary>
    /// Sets the curve to the default shape that matches the original 3-phase system.
    /// Warmup (0-18.75%): -10% MSD
    /// Ramp-up (18.75-75%): -10% to +15% MSD
    /// Cooldown (75-100%): +15% to 0% MSD
    /// </summary>
    public void SetDefaultCurve()
    {
        _points.Clear();
        _points.Add(new MsdControlPoint(0, -10));      // Warmup start
        _points.Add(new MsdControlPoint(18.75, -10)); // Warmup end
        _points.Add(new MsdControlPoint(75, 15));     // Ramp-up peak
        _points.Add(new MsdControlPoint(100, 0));     // Cooldown end
    }

    /// <summary>
    /// Adds a new control point to the curve.
    /// </summary>
    /// <param name="timePercent">Position on time axis (0-100).</param>
    /// <param name="msdPercent">MSD offset percentage.</param>
    /// <param name="skillset">Optional focus skillset for this point.</param>
    /// <returns>The added point, or null if a point already exists at that time.</returns>
    public MsdControlPoint? AddPoint(double timePercent, double msdPercent, string? skillset = null)
    {
        timePercent = Math.Clamp(timePercent, 0, 100);

        // Check if a point already exists very close to this time
        if (_points.Any(p => Math.Abs(p.TimePercent - timePercent) < 0.5))
            return null;

        var point = new MsdControlPoint(timePercent, msdPercent, skillset);
        _points.Add(point);
        SortPoints();
        return point;
    }

    /// <summary>
    /// Gets the skillset at a specific time (from the nearest previous point).
    /// </summary>
    /// <param name="timePercent">Time position (0-100).</param>
    /// <returns>The skillset, or null if no skillset is set.</returns>
    public string? GetSkillsetAtTime(double timePercent)
    {
        if (_points.Count == 0)
            return null;

        timePercent = Math.Clamp(timePercent, 0, 100);

        // Find the point at or just before this time
        MsdControlPoint? activePoint = null;
        foreach (var point in _points)
        {
            if (point.TimePercent <= timePercent)
                activePoint = point;
            else
                break;
        }

        return activePoint?.Skillset;
    }

    /// <summary>
    /// Removes a control point from the curve.
    /// </summary>
    /// <param name="point">The point to remove.</param>
    /// <returns>True if removed, false if not found or would violate minimum points.</returns>
    public bool RemovePoint(MsdControlPoint point)
    {
        if (_points.Count <= MinimumPoints)
            return false;

        return _points.Remove(point);
    }

    /// <summary>
    /// Removes a control point at the specified index.
    /// </summary>
    /// <param name="index">The index of the point to remove.</param>
    /// <returns>True if removed, false if invalid index or would violate minimum points.</returns>
    public bool RemovePointAt(int index)
    {
        if (_points.Count <= MinimumPoints)
            return false;

        if (index < 0 || index >= _points.Count)
            return false;

        _points.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Updates a point's position and re-sorts the list.
    /// </summary>
    /// <param name="point">The point to update.</param>
    /// <param name="newTimePercent">New time position (0-100).</param>
    /// <param name="newMsdPercent">New MSD offset.</param>
    public void UpdatePoint(MsdControlPoint point, double newTimePercent, double newMsdPercent)
    {
        point.TimePercent = Math.Clamp(newTimePercent, 0, 100);
        point.MsdPercent = newMsdPercent;
        SortPoints();
    }

    /// <summary>
    /// Gets the MSD value at a specific time percentage using linear interpolation.
    /// </summary>
    /// <param name="timePercent">Time position (0-100).</param>
    /// <returns>The interpolated MSD value.</returns>
    public double GetMsdAtTime(double timePercent)
    {
        if (_points.Count == 0)
            return BaseMsd;

        timePercent = Math.Clamp(timePercent, 0, 100);

        // Find the two points to interpolate between
        MsdControlPoint? before = null;
        MsdControlPoint? after = null;

        foreach (var point in _points)
        {
            if (point.TimePercent <= timePercent)
                before = point;
            else if (after == null)
                after = point;
        }

        // Edge cases
        if (before == null && after != null)
            return BaseMsd * (1 + after.MsdPercent / 100.0);
        if (after == null && before != null)
            return BaseMsd * (1 + before.MsdPercent / 100.0);
        if (before == null || after == null)
            return BaseMsd;

        // Exact match
        if (Math.Abs(before.TimePercent - timePercent) < 0.001)
            return BaseMsd * (1 + before.MsdPercent / 100.0);

        // Linear interpolation
        var t = (timePercent - before.TimePercent) / (after.TimePercent - before.TimePercent);
        var msdPercent = before.MsdPercent + t * (after.MsdPercent - before.MsdPercent);
        return BaseMsd * (1 + msdPercent / 100.0);
    }

    /// <summary>
    /// Gets the MSD percentage offset at a specific time using linear interpolation.
    /// </summary>
    /// <param name="timePercent">Time position (0-100).</param>
    /// <returns>The interpolated MSD percentage offset.</returns>
    public double GetMsdPercentAtTime(double timePercent)
    {
        if (_points.Count == 0)
            return 0;

        timePercent = Math.Clamp(timePercent, 0, 100);

        MsdControlPoint? before = null;
        MsdControlPoint? after = null;

        foreach (var point in _points)
        {
            if (point.TimePercent <= timePercent)
                before = point;
            else if (after == null)
                after = point;
        }

        if (before == null && after != null)
            return after.MsdPercent;
        if (after == null && before != null)
            return before.MsdPercent;
        if (before == null || after == null)
            return 0;

        if (Math.Abs(before.TimePercent - timePercent) < 0.001)
            return before.MsdPercent;

        var t = (timePercent - before.TimePercent) / (after.TimePercent - before.TimePercent);
        return before.MsdPercent + t * (after.MsdPercent - before.MsdPercent);
    }

    /// <summary>
    /// Converts a time percentage to minutes based on session duration.
    /// </summary>
    public double TimePercentToMinutes(double timePercent)
    {
        return TotalSessionMinutes * (timePercent / 100.0);
    }

    /// <summary>
    /// Converts minutes to time percentage based on session duration.
    /// </summary>
    public double MinutesToTimePercent(double minutes)
    {
        return (minutes / TotalSessionMinutes) * 100.0;
    }

    /// <summary>
    /// Creates a deep copy of this configuration.
    /// </summary>
    public MsdCurveConfig Clone()
    {
        var clone = new MsdCurveConfig
        {
            BaseMsd = BaseMsd,
            TotalSessionMinutes = TotalSessionMinutes
        };
        clone._points.Clear();
        foreach (var point in _points)
        {
            clone._points.Add(point.Clone());
        }
        return clone;
    }

    /// <summary>
    /// Sorts points by TimePercent.
    /// </summary>
    private void SortPoints()
    {
        _points.Sort((a, b) => a.TimePercent.CompareTo(b.TimePercent));
    }

    /// <summary>
    /// Gets the minimum MSD percentage in the curve.
    /// </summary>
    public double MinMsdPercent => _points.Count > 0 ? _points.Min(p => p.MsdPercent) : 0;

    /// <summary>
    /// Gets the maximum MSD percentage in the curve.
    /// </summary>
    public double MaxMsdPercent => _points.Count > 0 ? _points.Max(p => p.MsdPercent) : 0;

    /// <summary>
    /// Gets the index of a control point in the list.
    /// </summary>
    /// <param name="point">The point to find.</param>
    /// <returns>The index, or -1 if not found.</returns>
    public int IndexOf(MsdControlPoint point)
    {
        return _points.IndexOf(point);
    }

    /// <summary>
    /// Generates a curve configuration based on historical session data.
    /// Analyzes the typical MSD pattern (warmup -> peak -> cooldown) from past sessions.
    /// </summary>
    /// <param name="trends">The skill trends containing historical play data.</param>
    /// <returns>A new curve config based on the analysis, or null if insufficient data.</returns>
    public static MsdCurveConfig? GenerateFromTrends(SkillsTrendResult trends)
    {
        if (trends == null || trends.Plays.Count < 5)
            return null;

        var config = new MsdCurveConfig();
        config._points.Clear();

        // Group plays by session
        var sessionGroups = trends.Plays
            .GroupBy(p => p.SessionId)
            .Where(g => g.Count() >= 3) // Only sessions with enough plays
            .ToList();

        if (sessionGroups.Count == 0)
            return null;

        // For each session, normalize play times to 0-100%
        var normalizedPlays = new List<(double timePercent, float msd, string skillset)>();

        foreach (var session in sessionGroups)
        {
            var plays = session.OrderBy(p => p.PlayedAt).ToList();
            var sessionStart = plays.First().PlayedAt;
            var sessionEnd = plays.Last().PlayedAt;
            var sessionDuration = (sessionEnd - sessionStart).TotalMinutes;

            if (sessionDuration < 5) // Skip very short sessions
                continue;

            foreach (var play in plays)
            {
                var elapsed = (play.PlayedAt - sessionStart).TotalMinutes;
                var timePercent = (elapsed / sessionDuration) * 100.0;
                normalizedPlays.Add((timePercent, play.HighestMsdValue, play.DominantSkillset));
            }
        }

        if (normalizedPlays.Count < 10)
            return null;

        // Calculate base MSD (overall average)
        var baseMsd = trends.OverallSkillLevel > 0 
            ? trends.OverallSkillLevel 
            : normalizedPlays.Average(p => p.msd);
        config.BaseMsd = baseMsd;

        // Create 5 buckets for time segments
        var buckets = new[]
        {
            (start: 0.0, end: 20.0, timePoint: 0.0),
            (start: 20.0, end: 40.0, timePoint: 25.0),
            (start: 40.0, end: 60.0, timePoint: 50.0),
            (start: 60.0, end: 80.0, timePoint: 75.0),
            (start: 80.0, end: 100.0, timePoint: 100.0)
        };

        foreach (var bucket in buckets)
        {
            var bucketPlays = normalizedPlays
                .Where(p => p.timePercent >= bucket.start && p.timePercent < bucket.end)
                .ToList();

            // Handle edge case for last bucket
            if (bucket.end == 100.0)
            {
                bucketPlays = normalizedPlays
                    .Where(p => p.timePercent >= bucket.start && p.timePercent <= bucket.end)
                    .ToList();
            }

            if (bucketPlays.Count == 0)
            {
                // Use interpolated value from nearby buckets
                config._points.Add(new MsdControlPoint(bucket.timePoint, 0, null));
                continue;
            }

            // Calculate average MSD for this bucket
            var avgMsd = bucketPlays.Average(p => p.msd);

            // Convert to percentage offset from base
            var msdPercent = ((avgMsd - baseMsd) / baseMsd) * 100.0;

            // Find most common skillset in this bucket
            var skillsetCounts = bucketPlays
                .Where(p => !string.IsNullOrEmpty(p.skillset) && p.skillset != "unknown")
                .GroupBy(p => p.skillset)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();

            var dominantSkillset = skillsetCounts?.Key;

            config._points.Add(new MsdControlPoint(bucket.timePoint, msdPercent, dominantSkillset));
        }

        // Estimate session duration from average session length
        var avgSessionMinutes = sessionGroups
            .Select(g =>
            {
                var plays = g.OrderBy(p => p.PlayedAt).ToList();
                return (plays.Last().PlayedAt - plays.First().PlayedAt).TotalMinutes;
            })
            .Where(m => m > 5)
            .DefaultIfEmpty(80)
            .Average();

        config.TotalSessionMinutes = Math.Max(30, Math.Min(180, (int)avgSessionMinutes));

        return config;
    }
}
