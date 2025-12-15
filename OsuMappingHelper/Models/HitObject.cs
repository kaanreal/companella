using System.Globalization;

namespace OsuMappingHelper.Models;

/// <summary>
/// Type of hit object in osu!mania.
/// </summary>
public enum HitObjectType
{
    /// <summary>
    /// Single tap note (circle).
    /// </summary>
    Circle,

    /// <summary>
    /// Long note (hold).
    /// </summary>
    Hold
}

/// <summary>
/// Represents a single hit object (note) in a 4K osu!mania beatmap.
/// </summary>
public class HitObject
{
    /// <summary>
    /// Time of the hit object in milliseconds.
    /// </summary>
    public double Time { get; set; }

    /// <summary>
    /// Column index (0-3 for 4K).
    /// </summary>
    public int Column { get; set; }

    /// <summary>
    /// Type of hit object (Circle or Hold).
    /// </summary>
    public HitObjectType Type { get; set; }

    /// <summary>
    /// End time for hold notes (same as Time for circles).
    /// </summary>
    public double EndTime { get; set; }

    /// <summary>
    /// Duration of hold note in milliseconds (0 for circles).
    /// </summary>
    public double Duration => EndTime - Time;

    /// <summary>
    /// Whether this is a hold note.
    /// </summary>
    public bool IsHold => Type == HitObjectType.Hold;

    /// <summary>
    /// Converts X position to column index for 4K mania.
    /// Standard 4K columns: 64, 192, 320, 448 (512 width / 4 columns = 128 per column).
    /// </summary>
    public static int XToColumn(double x, int keyCount = 4)
    {
        // Column width = 512 / keyCount
        var columnWidth = 512.0 / keyCount;
        var column = (int)(x / columnWidth);
        return Math.Clamp(column, 0, keyCount - 1);
    }

    /// <summary>
    /// Parses a hit object from .osu file format.
    /// Format: x,y,time,type,hitSound,endTime:hitSample (for holds)
    /// Format: x,y,time,type,hitSound,hitSample (for circles)
    /// </summary>
    public static HitObject? Parse(string line, int keyCount = 4)
    {
        var parts = line.Split(',');
        if (parts.Length < 5)
            return null;

        try
        {
            var x = double.Parse(parts[0], CultureInfo.InvariantCulture);
            var time = double.Parse(parts[2], CultureInfo.InvariantCulture);
            var typeFlags = int.Parse(parts[3], CultureInfo.InvariantCulture);

            var column = XToColumn(x, keyCount);

            // Check if hold note (type flag 128)
            var isHold = (typeFlags & 128) != 0;

            double endTime = time;
            if (isHold && parts.Length >= 6)
            {
                // Hold notes have endTime in the 6th field, before the colon
                var endTimePart = parts[5].Split(':')[0];
                if (double.TryParse(endTimePart, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedEndTime))
                {
                    endTime = parsedEndTime;
                }
            }

            return new HitObject
            {
                Time = time,
                Column = column,
                Type = isHold ? HitObjectType.Hold : HitObjectType.Circle,
                EndTime = endTime
            };
        }
        catch
        {
            return null;
        }
    }

    public override string ToString()
    {
        return $"[{Time:F0}ms] Col{Column} {Type}{(IsHold ? $" ({Duration:F0}ms)" : "")}";
    }
}
