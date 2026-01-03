using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OsuMappingHelper.Models;

namespace OsuMappingHelper.Services;

/// <summary>
/// Metadata for the created marathon beatmap.
/// </summary>
public class MarathonMetadata
{
    public string Title { get; set; } = "Marathon";
    public string TitleUnicode { get; set; } = "";
    public string Artist { get; set; } = "Various Artists";
    public string ArtistUnicode { get; set; } = "";
    public string Creator { get; set; } = "Companella";
    public string Version { get; set; } = "Marathon";
    public string Tags { get; set; } = "marathon companella";
}

/// <summary>
/// Result of marathon creation.
/// </summary>
public class MarathonCreationResult
{
    public bool Success { get; set; }
    public string? OutputPath { get; set; }
    public string? ErrorMessage { get; set; }
    public double TotalDurationMs { get; set; }
}

/// <summary>
/// Pre-calculated fade information for an entry.
/// </summary>
internal class EntryFadeInfo
{
    public int Index { get; set; }
    public bool IsPause { get; set; }
    
    /// <summary>For maps: how much EXTRA intro audio to include before first note (ms).</summary>
    public double IntroExtensionMs { get; set; }
    
    /// <summary>For maps: how much EXTRA outro audio to include after last note (ms).</summary>
    public double OutroExtensionMs { get; set; }
    
    /// <summary>For maps: fade-in duration (equals extension - we only fade audio before notes, never during).</summary>
    public double IntroFadeDurationMs { get; set; }
    
    /// <summary>For maps: fade-out duration (equals extension - we only fade audio after notes, never during).</summary>
    public double OutroFadeDurationMs { get; set; }
    
    /// <summary>For pauses: remaining duration after adjacent fades consume it (ms).</summary>
    public double RemainingPauseDurationMs { get; set; }
}

/// <summary>
/// Internal data for a processed map segment.
/// </summary>
internal class ProcessedMapSegment
{
    public MarathonEntry Entry { get; set; } = null!;
    public string TrimmedAudioPath { get; set; } = string.Empty;
    public string ProcessedOsuPath { get; set; } = string.Empty;
    public double OffsetInMarathon { get; set; }
    public double Duration { get; set; }
    /// <summary>
    /// How much audio was included before the first note (for fade-in during preceding pause).
    /// </summary>
    public double IntroFadeDuration { get; set; }
    /// <summary>
    /// How much audio was included after the last note (for fade-out during following pause).
    /// </summary>
    public double OutroFadeDuration { get; set; }
    public List<string> ShiftedHitObjectLines { get; set; } = new();
    public List<string> ShiftedTimingPointLines { get; set; } = new();
    public List<string> ShiftedEventLines { get; set; } = new();
}

/// <summary>
/// Service for creating marathon beatmaps by merging multiple maps together.
/// </summary>
public class MarathonCreatorService
{
    private readonly string _ffmpegPath;
    private readonly OsuFileParser _fileParser;
    private readonly RateChanger _rateChanger;

    public MarathonCreatorService(string ffmpegPath = "ffmpeg")
    {
        _ffmpegPath = ffmpegPath;
        _fileParser = new OsuFileParser();
        _rateChanger = new RateChanger(ffmpegPath);
    }

    /// <summary>
    /// Tracks rate-changed files (.osu and audio) for later cleanup.
    /// </summary>
    private void TrackRateChangedFiles(string osuPath, OsuFile osuFile, List<string> tempRateChangedFiles)
    {
        // Track the .osu file
        if (!tempRateChangedFiles.Contains(osuPath))
        {
            tempRateChangedFiles.Add(osuPath);
            Console.WriteLine($"[Marathon] Tracking temp rate file: {osuPath}");
        }
        
        // Track the audio file
        var audioPath = Path.Combine(osuFile.DirectoryPath, osuFile.AudioFilename);
        if (!tempRateChangedFiles.Contains(audioPath))
        {
            tempRateChangedFiles.Add(audioPath);
            Console.WriteLine($"[Marathon] Tracking temp rate audio: {audioPath}");
        }
    }

    /// <summary>
    /// Cleans up temporary rate-changed files created during marathon creation.
    /// </summary>
    private void CleanupTempRateChangedFiles(List<string> tempRateChangedFiles)
    {
        foreach (var filePath in tempRateChangedFiles)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    Console.WriteLine($"[Marathon] Deleted temp file: {filePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Marathon] Failed to delete temp file {filePath}: {ex.Message}");
                // Ignore cleanup errors - the file might be in use or already deleted
            }
        }
    }

    /// <summary>
    /// Creates a marathon beatmap from the given entries.
    /// </summary>
    /// <param name="entries">Ordered list of marathon entries.</param>
    /// <param name="metadata">Metadata for the marathon.</param>
    /// <param name="outputDirectory">Directory to create the marathon folder in.</param>
    /// <param name="progressCallback">Progress update callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the marathon creation.</returns>
    public async Task<MarathonCreationResult> CreateMarathonAsync(
        List<MarathonEntry> entries,
        MarathonMetadata metadata,
        string outputDirectory,
        Action<string>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return new MarathonCreationResult
            {
                Success = false,
                ErrorMessage = "No entries provided."
            };
        }

        // Track temporary rate-changed files that should be deleted after marathon creation
        var tempRateChangedFiles = new List<string>();

        try
        {
            // Create temp directory for intermediate files
            var tempDir = Path.Combine(Path.GetTempPath(), $"companella_marathon_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // Step 1: Pre-calculate fade durations for all entries
                // We need to do this first because pause reduction depends on BOTH adjacent maps
                progressCallback?.Invoke("Calculating audio transitions...");
                var fadeInfo = await CalculateFadeInfoAsync(entries, tempDir, tempRateChangedFiles, progressCallback, cancellationToken);

                var processedSegments = new List<ProcessedMapSegment>();
                double cumulativeOffset = 0;

                // Step 2: Process each entry with pre-calculated fade info
                for (int i = 0; i < entries.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var entry = entries[i];
                    var info = fadeInfo[i];

                    // Handle pause entries - they may be partially or fully consumed
                    if (entry.IsPause)
                    {
                        var remainingPauseDuration = info.RemainingPauseDurationMs;
                        
                        if (remainingPauseDuration <= 0)
                        {
                            // Pause is fully consumed by adjacent map fades - skip it
                            Console.WriteLine($"[Marathon] Pause {i} fully consumed by fades");
                            continue;
                        }

                        // Generate reduced silent audio
                        progressCallback?.Invoke($"Processing pause {i + 1}/{entries.Count}: {remainingPauseDuration:F0}ms remaining");
                        
                        var segment = new ProcessedMapSegment
                        {
                            Entry = entry,
                            OffsetInMarathon = cumulativeOffset,
                            Duration = remainingPauseDuration
                        };

                        var silentAudioPath = Path.Combine(tempDir, $"pause_{entry.Id}.mp3");
                        await GenerateSilentAudioAsync(silentAudioPath, remainingPauseDuration / 1000.0, cancellationToken);
                        segment.TrimmedAudioPath = silentAudioPath;

                        // Use actual audio duration to avoid timing drift
                        var actualPauseDuration = await GetAudioDurationAsync(silentAudioPath, cancellationToken);
                        if (actualPauseDuration > 0)
                        {
                            segment.Duration = actualPauseDuration;
                            Console.WriteLine($"[Marathon] Pause: using actual duration {actualPauseDuration:F0}ms (requested {remainingPauseDuration:F0}ms)");
                        }

                        segment.ShiftedTimingPointLines.Add($"{cumulativeOffset.ToString("0.###", CultureInfo.InvariantCulture)},500,4,2,0,100,1,0");

                        processedSegments.Add(segment);
                        cumulativeOffset += segment.Duration;
                        continue;
                    }

                    // Process map entry with fades
                    progressCallback?.Invoke($"Processing map {i + 1}/{entries.Count}: {entry.Title}");

                    var mapSegment = await ProcessEntryWithFadesAsync(
                        entry, tempDir, cumulativeOffset,
                        info.IntroExtensionMs, info.IntroFadeDurationMs,
                        info.OutroExtensionMs, info.OutroFadeDurationMs,
                        tempRateChangedFiles, progressCallback, cancellationToken);

                    processedSegments.Add(mapSegment);
                    cumulativeOffset += mapSegment.Duration;
                }

                // Step 3: Concatenate audio files (skip empty paths)
                progressCallback?.Invoke("Concatenating audio files...");
                var audioFiles = processedSegments
                    .Where(s => !string.IsNullOrEmpty(s.TrimmedAudioPath) && File.Exists(s.TrimmedAudioPath))
                    .Select(s => s.TrimmedAudioPath)
                    .ToList();
                var outputFolderName = SanitizeFileName($"{metadata.Artist} - {metadata.Title}");
                var outputFolder = Path.Combine(outputDirectory, outputFolderName);
                Directory.CreateDirectory(outputFolder);

                var finalAudioPath = Path.Combine(outputFolder, "audio.mp3");
                await ConcatenateAudioFilesAsync(audioFiles, finalAudioPath, cancellationToken);

                // Step 3: Merge .osu content
                progressCallback?.Invoke("Merging beatmap content...");
                var finalOsuPath = CreateMergedOsuFile(processedSegments, metadata, outputFolder, finalAudioPath);

                // Step 4: Normalize scroll velocity
                progressCallback?.Invoke("Normalizing scroll velocity...");
                NormalizeSV(finalOsuPath);

                // Cleanup temp directory
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                    // Ignore cleanup errors
                }

                // Cleanup temporary rate-changed files
                CleanupTempRateChangedFiles(tempRateChangedFiles);

                return new MarathonCreationResult
                {
                    Success = true,
                    OutputPath = finalOsuPath,
                    TotalDurationMs = cumulativeOffset
                };
            }
            catch
            {
                // Cleanup temp directory on error
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                    // Ignore cleanup errors
                }

                // Cleanup temporary rate-changed files
                CleanupTempRateChangedFiles(tempRateChangedFiles);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            return new MarathonCreationResult
            {
                Success = false,
                ErrorMessage = "Operation was cancelled."
            };
        }
        catch (Exception ex)
        {
            return new MarathonCreationResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Pre-calculates fade durations for all entries.
    /// This determines how much audio to include before/after notes for fading,
    /// and how much pause duration remains after fades consume it.
    /// </summary>
    private async Task<List<EntryFadeInfo>> CalculateFadeInfoAsync(
        List<MarathonEntry> entries,
        string tempDir,
        List<string> tempRateChangedFiles,
        Action<string>? progressCallback,
        CancellationToken cancellationToken)
    {
        var fadeInfo = new List<EntryFadeInfo>();
        
        // First pass: collect audio availability info for each map
        var mapAudioInfo = new Dictionary<int, (double FirstNoteTime, double LastNoteTime, double AudioDuration, OsuFile WorkingOsu)>();
        
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.IsPause)
            {
                fadeInfo.Add(new EntryFadeInfo { Index = i, IsPause = true, RemainingPauseDurationMs = entry.PauseDurationSeconds * 1000 });
                continue;
            }

            // Get the working osu file (after rate change if needed)
            OsuFile workingOsu = entry.OsuFile!;
            if (Math.Abs(entry.Rate - 1.0) > 0.01)
            {
                // Create rate-changed version (temporary - will be deleted after marathon creation)
                var rateChangedPath = await _rateChanger.CreateRateChangedBeatmapAsync(
                    entry.OsuFile!,
                    entry.Rate,
                    "[[name]] [[rate]]",
                    null);
                workingOsu = _fileParser.Parse(rateChangedPath);
                
                // Track the .osu file and its audio for cleanup
                TrackRateChangedFiles(rateChangedPath, workingOsu, tempRateChangedFiles);
            }

            var hitObjects = ParseHitObjects(workingOsu);
            double firstNoteTime = hitObjects.Count > 0 ? hitObjects.Min(h => h.Time) : 0;
            double lastNoteTime = hitObjects.Count > 0 ? hitObjects.Max(h => h.EndTime) : 0;

            var audioPath = Path.Combine(workingOsu.DirectoryPath, workingOsu.AudioFilename);
            var audioDuration = await GetAudioDurationAsync(audioPath, cancellationToken);

            mapAudioInfo[i] = (firstNoteTime, lastNoteTime, audioDuration, workingOsu);
            fadeInfo.Add(new EntryFadeInfo { Index = i, IsPause = false });
        }

        // Second pass: calculate fade durations based on adjacent pauses
        // We track both:
        // - Extension: how much extra audio to include (trim boundary change)
        // - Fade duration: how long the actual fade effect is (may include note audio if no extra available)
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var info = fadeInfo[i];

            if (entry.IsPause)
            {
                // Calculate how much of this pause is consumed by adjacent maps' fades
                double consumedByPrevious = 0;
                double consumedByNext = 0;
                var pauseDurationMs = entry.PauseDurationSeconds * 1000;

                // Check previous entry (map that fades out into this pause)
                if (i > 0 && !entries[i - 1].IsPause && mapAudioInfo.ContainsKey(i - 1))
                {
                    var prevInfo = mapAudioInfo[i - 1];
                    var prevFadeInfo = fadeInfo[i - 1];
                    
                    // Available outro = audio after last note
                    var availableOutro = Math.Max(0, prevInfo.AudioDuration - prevInfo.LastNoteTime);
                    
                    // Extension is limited by available audio
                    var outroExtension = Math.Min(pauseDurationMs, availableOutro);
                    prevFadeInfo.OutroExtensionMs = outroExtension;
                    
                    // Fade duration: ONLY fade audio that exists AFTER the last note
                    // Never fade out while notes are still playing - if no audio after notes, no fade at all
                    prevFadeInfo.OutroFadeDurationMs = outroExtension;
                    
                    // The pause is consumed by the extension
                    consumedByPrevious = outroExtension;
                    
                    Console.WriteLine($"[Marathon] Map {i-1} outro: {availableOutro:F0}ms available, extension={outroExtension:F0}ms, fade={outroExtension:F0}ms");
                }

                // Check next entry (map that fades in from this pause)
                if (i < entries.Count - 1 && !entries[i + 1].IsPause && mapAudioInfo.ContainsKey(i + 1))
                {
                    var nextInfo = mapAudioInfo[i + 1];
                    var nextFadeInfo = fadeInfo[i + 1];
                    
                    // Available intro = audio before first note
                    var availableIntro = nextInfo.FirstNoteTime;
                    
                    // Use remaining pause duration after previous consumption
                    var remainingForNext = pauseDurationMs - consumedByPrevious;
                    
                    // Extension is limited by available audio and remaining pause
                    var introExtension = Math.Min(remainingForNext, availableIntro);
                    nextFadeInfo.IntroExtensionMs = introExtension;
                    
                    // Fade duration: ONLY fade audio that exists BEFORE the first note
                    // Never fade while notes are playing - if no audio before notes, no fade at all
                    nextFadeInfo.IntroFadeDurationMs = introExtension;
                    
                    // The pause is "consumed" by the extension
                    consumedByNext = introExtension;
                    
                    Console.WriteLine($"[Marathon] Map {i+1} intro: {availableIntro:F0}ms available, extension={introExtension:F0}ms, fade={introExtension:F0}ms");
                }

                // Remaining pause duration (only extensions consume the pause, not boundary fades)
                info.RemainingPauseDurationMs = pauseDurationMs - consumedByPrevious - consumedByNext;
                
                Console.WriteLine($"[Marathon] Pause {i}: {pauseDurationMs:F0}ms total, {consumedByPrevious:F0}ms consumed by prev, {consumedByNext:F0}ms consumed by next, {info.RemainingPauseDurationMs:F0}ms remaining");
            }
        }

        // Edge cases: pauses at start or end get full duration for fading
        // (already handled above - if no adjacent map, consumed stays 0)

        return fadeInfo;
    }

    /// <summary>
    /// Processes a map entry with support for fade transitions during pauses.
    /// Pause entries are handled separately in the main loop.
    /// </summary>
    /// <param name="entry">The marathon entry to process (must be a map, not pause).</param>
    /// <param name="tempDir">Temporary directory for intermediate files.</param>
    /// <param name="offsetInMarathon">Current offset in the marathon timeline.</param>
    /// <param name="introExtensionMs">How much extra audio to include before first note (ms).</param>
    /// <param name="introFadeDurationMs">Duration of fade-in effect (equals extension - only fade audio before notes).</param>
    /// <param name="outroExtensionMs">How much extra audio to include after last note (ms).</param>
    /// <param name="outroFadeDurationMs">Duration of fade-out effect (equals extension - only fade audio after notes).</param>
    /// <param name="tempRateChangedFiles">List to track temporary rate-changed files for cleanup.</param>
    /// <param name="progressCallback">Progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<ProcessedMapSegment> ProcessEntryWithFadesAsync(
        MarathonEntry entry,
        string tempDir,
        double offsetInMarathon,
        double introExtensionMs,
        double introFadeDurationMs,
        double outroExtensionMs,
        double outroFadeDurationMs,
        List<string> tempRateChangedFiles,
        Action<string>? progressCallback,
        CancellationToken cancellationToken)
    {
        var segment = new ProcessedMapSegment
        {
            Entry = entry,
            OffsetInMarathon = offsetInMarathon
        };

        OsuFile workingOsuFile = entry.OsuFile!;

        // If rate is not 1.0, create a rate-changed version (will reuse existing if already created)
        if (Math.Abs(entry.Rate - 1.0) > 0.01)
        {
            progressCallback?.Invoke($"  Creating {entry.Rate:0.0#}x rate version...");
            var rateChangedPath = await _rateChanger.CreateRateChangedBeatmapAsync(
                entry.OsuFile!,
                entry.Rate,
                "[[name]] [[rate]]",
                null);
            workingOsuFile = _fileParser.Parse(rateChangedPath);
            
            // Track for cleanup (may already be tracked from CalculateFadeInfoAsync, but that's fine)
            TrackRateChangedFiles(rateChangedPath, workingOsuFile, tempRateChangedFiles);
        }

        // Parse hit objects to get accurate first/last note times
        var hitObjects = ParseHitObjects(workingOsuFile);
        double firstNoteTime = hitObjects.Count > 0 ? hitObjects.Min(h => h.Time) : 0;
        double lastNoteEndTime = hitObjects.Count > 0 ? hitObjects.Max(h => h.EndTime) : 0;

        var originalAudioPath = Path.Combine(workingOsuFile.DirectoryPath, workingOsuFile.AudioFilename);

        // Calculate trim boundaries - only extend by the EXTENSION amount (actual extra audio)
        // The fade duration may be longer (boundary fade into note audio)
        double trimStartMs = firstNoteTime - introExtensionMs;
        double trimEndMs = lastNoteEndTime + outroExtensionMs;

        segment.IntroFadeDuration = introExtensionMs;
        segment.OutroFadeDuration = outroExtensionMs;

        // The segment duration includes only the extension portions (not boundary fades)
        double noteDuration = lastNoteEndTime - firstNoteTime;
        segment.Duration = introExtensionMs + noteDuration + outroExtensionMs;

        // Trim audio with extended boundaries and apply fades
        // Fade duration may exceed extension (boundary fade into note audio)
        progressCallback?.Invoke($"  Trimming audio (ext: {introExtensionMs:F0}/{outroExtensionMs:F0}ms, fade: {introFadeDurationMs:F0}/{outroFadeDurationMs:F0}ms)...");
        var trimmedAudioPath = Path.Combine(tempDir, $"trimmed_{entry.Id}.mp3");
        await TrimAudioWithFadesAsync(
            originalAudioPath, trimmedAudioPath,
            trimStartMs, trimEndMs,
            introFadeDurationMs, outroFadeDurationMs,
            cancellationToken);
        segment.TrimmedAudioPath = trimmedAudioPath;

        // CRITICAL: Use the ACTUAL audio file duration to avoid timing drift
        // MP3 encoding can cause slight duration differences from our calculations
        var actualAudioDuration = await GetAudioDurationAsync(trimmedAudioPath, cancellationToken);
        if (actualAudioDuration > 0)
        {
            var calculatedDuration = segment.Duration;
            var durationDiff = Math.Abs(actualAudioDuration - calculatedDuration);
            if (durationDiff > 100) // More than 100ms difference is suspicious
            {
                Console.WriteLine($"[Marathon] WARNING: Audio duration mismatch for {entry.Title}: calculated={calculatedDuration:F0}ms, actual={actualAudioDuration:F0}ms");
            }
            segment.Duration = actualAudioDuration;
            Console.WriteLine($"[Marathon] {entry.Title}: using actual audio duration {actualAudioDuration:F0}ms (calculated was {calculatedDuration:F0}ms)");
        }

        // Shift all times in the .osu data
        // Notes start at offsetInMarathon + introExtension (after the extension portion)
        progressCallback?.Invoke($"  Adjusting timing data...");
        var osuDataOffset = offsetInMarathon + introExtensionMs;
        ShiftAndCollectOsuData(workingOsuFile, firstNoteTime, osuDataOffset, segment);

        return segment;
    }

    /// <summary>
    /// Generates silent audio of the specified duration.
    /// </summary>
    private async Task GenerateSilentAudioAsync(
        string outputPath,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        // Use ffmpeg to generate silent audio
        // -f lavfi -i anullsrc generates silence
        // -t sets duration
        var arguments = $"-y -f lavfi -i anullsrc=r=44100:cl=stereo -t {durationSeconds.ToString("0.###", CultureInfo.InvariantCulture)} -c:a libmp3lame -q:a 2 \"{outputPath}\"";

        Console.WriteLine($"[Marathon] Generating silent audio: {_ffmpegPath} {arguments}");

        await RunFfmpegAsync(arguments, cancellationToken);

        if (!File.Exists(outputPath))
            throw new InvalidOperationException($"ffmpeg did not create silent audio: {outputPath}");
    }

    /// <summary>
    /// Gets the duration of an audio file in milliseconds.
    /// Uses ffmpeg to analyze the file (more reliable than ffprobe).
    /// </summary>
    private async Task<double> GetAudioDurationAsync(string audioPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(audioPath))
            return 0;

        // First try ffprobe (more accurate)
        var duration = await TryGetDurationWithFfprobeAsync(audioPath, cancellationToken);
        if (duration > 0)
            return duration;

        // Fallback: use ffmpeg to get duration from stderr output
        duration = await TryGetDurationWithFfmpegAsync(audioPath, cancellationToken);
        if (duration > 0)
            return duration;

        // Last resort: estimate from file size (rough approximation for MP3 at ~192kbps)
        try
        {
            var fileInfo = new FileInfo(audioPath);
            // Rough estimate: 192kbps = 24KB/s
            var estimated = (fileInfo.Length / 24000.0) * 1000;
            Console.WriteLine($"[Marathon] WARNING: Using file size estimate for duration: {estimated:F0}ms");
            return estimated;
        }
        catch
        {
            Console.WriteLine($"[Marathon] ERROR: Could not determine audio duration for {audioPath}");
            return 0;
        }
    }

    private async Task<double> TryGetDurationWithFfprobeAsync(string audioPath, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-i \"{audioPath}\" -show_entries format=duration -v quiet -of csv=\"p=0\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            var outputBuilder = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                    outputBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();

            var completed = await Task.Run(() => process.WaitForExit(10000), cancellationToken);

            if (!completed)
            {
                try { process.Kill(); } catch { }
                return 0;
            }

            if (process.ExitCode == 0)
            {
                var output = outputBuilder.ToString().Trim();
                if (double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out var durationSec))
                {
                    return durationSec * 1000;
                }
            }
        }
        catch { }
        return 0;
    }

    private async Task<double> TryGetDurationWithFfmpegAsync(string audioPath, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = $"-i \"{audioPath}\" -f null -",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            var errorBuilder = new StringBuilder();

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                    errorBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginErrorReadLine();

            var completed = await Task.Run(() => process.WaitForExit(10000), cancellationToken);

            if (!completed)
            {
                try { process.Kill(); } catch { }
                return 0;
            }

            // Parse duration from ffmpeg output: "Duration: 00:03:45.67"
            var output = errorBuilder.ToString();
            var match = Regex.Match(output, @"Duration:\s*(\d+):(\d+):(\d+)\.(\d+)");
            if (match.Success)
            {
                var hours = int.Parse(match.Groups[1].Value);
                var minutes = int.Parse(match.Groups[2].Value);
                var seconds = int.Parse(match.Groups[3].Value);
                var centiseconds = int.Parse(match.Groups[4].Value.PadRight(2, '0').Substring(0, 2));
                
                return (hours * 3600 + minutes * 60 + seconds) * 1000 + centiseconds * 10;
            }
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// Trims audio and applies fade-in/fade-out in a single operation.
    /// </summary>
    private async Task TrimAudioWithFadesAsync(
        string inputPath,
        string outputPath,
        double startMs,
        double endMs,
        double fadeInDurationMs,
        double fadeOutDurationMs,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Audio file not found: {inputPath}");

        var startSec = startMs / 1000.0;
        var endSec = endMs / 1000.0;
        var duration = endSec - startSec;

        // Build ffmpeg command with trim and fades
        var filterParts = new List<string>();

        // Fade-in at the start (clamp to audio duration)
        if (fadeInDurationMs > 0)
        {
            var fadeInSec = Math.Min(fadeInDurationMs / 1000.0, duration);
            filterParts.Add($"afade=t=in:st=0:d={fadeInSec.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        // Fade-out at the end (ensure start is not negative)
        if (fadeOutDurationMs > 0)
        {
            var fadeOutSec = Math.Min(fadeOutDurationMs / 1000.0, duration);
            var fadeOutStart = Math.Max(0, duration - fadeOutSec);
            filterParts.Add($"afade=t=out:st={fadeOutStart.ToString("0.###", CultureInfo.InvariantCulture)}:d={fadeOutSec.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        string arguments;
        if (filterParts.Count > 0)
        {
            var filterChain = string.Join(",", filterParts);
            arguments = $"-y -i \"{inputPath}\" -ss {startSec.ToString("0.###", CultureInfo.InvariantCulture)} -to {endSec.ToString("0.###", CultureInfo.InvariantCulture)} -af \"{filterChain}\" -c:a libmp3lame -q:a 2 \"{outputPath}\"";
        }
        else
        {
            // No fades, just trim
            arguments = $"-y -i \"{inputPath}\" -ss {startSec.ToString("0.###", CultureInfo.InvariantCulture)} -to {endSec.ToString("0.###", CultureInfo.InvariantCulture)} -c:a libmp3lame -q:a 2 \"{outputPath}\"";
        }

        Console.WriteLine($"[Marathon] Trimming with fades: {_ffmpegPath} {arguments}");

        await RunFfmpegAsync(arguments, cancellationToken);

        if (!File.Exists(outputPath))
            throw new InvalidOperationException($"ffmpeg did not create trimmed audio: {outputPath}");
    }

    /// <summary>
    /// Parses hit objects from an OsuFile.
    /// </summary>
    private List<HitObject> ParseHitObjects(OsuFile osuFile)
    {
        var hitObjects = new List<HitObject>();
        if (!osuFile.RawSections.TryGetValue("HitObjects", out var lines))
            return hitObjects;

        var keyCount = (int)osuFile.CircleSize;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//"))
                continue;
            var hitObj = HitObject.Parse(line, keyCount);
            if (hitObj != null)
                hitObjects.Add(hitObj);
        }

        return hitObjects;
    }

    /// <summary>
    /// Concatenates multiple audio files into one.
    /// </summary>
    private async Task ConcatenateAudioFilesAsync(
        List<string> inputPaths,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (inputPaths.Count == 0)
            throw new ArgumentException("No audio files to concatenate.");

        if (inputPaths.Count == 1)
        {
            // Just copy the single file
            File.Copy(inputPaths[0], outputPath, true);
            return;
        }

        // Create a concat list file
        var concatListPath = Path.Combine(Path.GetDirectoryName(inputPaths[0])!, "concat_list.txt");
        var concatContent = new StringBuilder();
        foreach (var path in inputPaths)
        {
            // Escape single quotes in path and use proper format
            var escapedPath = path.Replace("'", "'\\''");
            concatContent.AppendLine($"file '{escapedPath}'");
        }
        await File.WriteAllTextAsync(concatListPath, concatContent.ToString(), cancellationToken);

        // Use concat demuxer
        var arguments = $"-y -f concat -safe 0 -i \"{concatListPath}\" -c:a libmp3lame -q:a 2 \"{outputPath}\"";

        Console.WriteLine($"[Marathon] Concatenating: {_ffmpegPath} {arguments}");

        await RunFfmpegAsync(arguments, cancellationToken);

        // Cleanup concat list
        try { File.Delete(concatListPath); } catch { }

        if (!File.Exists(outputPath))
            throw new InvalidOperationException($"ffmpeg did not create concatenated audio: {outputPath}");
    }

    /// <summary>
    /// Runs ffmpeg with the given arguments.
    /// </summary>
    private async Task RunFfmpegAsync(string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        var errorBuilder = new StringBuilder();

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
                errorBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginErrorReadLine();

        var completed = await Task.Run(() =>
        {
            try
            {
                return process.WaitForExit(300000); // 5 minute timeout
            }
            catch
            {
                return false;
            }
        }, cancellationToken);

        if (!completed)
        {
            try { process.Kill(); } catch { }
            throw new TimeoutException("ffmpeg timed out after 5 minutes");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg failed with exit code {process.ExitCode}:\n{errorBuilder}");
        }
    }

    /// <summary>
    /// Shifts times in the .osu data and collects the modified lines.
    /// </summary>
    private void ShiftAndCollectOsuData(
        OsuFile osuFile,
        double firstNoteTime,
        double offsetInMarathon,
        ProcessedMapSegment segment)
    {
        // Shift HitObjects
        if (osuFile.RawSections.TryGetValue("HitObjects", out var hitObjectLines))
        {
            foreach (var line in hitObjectLines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//"))
                    continue;

                var shifted = ShiftHitObjectLine(line, firstNoteTime, offsetInMarathon);
                if (shifted != null)
                    segment.ShiftedHitObjectLines.Add(shifted);
            }
        }

        // Shift TimingPoints
        if (osuFile.RawSections.TryGetValue("TimingPoints", out var tpLines))
        {
            foreach (var line in tpLines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("//"))
                    continue;

                var shifted = ShiftTimingPointLine(line, firstNoteTime, offsetInMarathon);
                if (shifted != null)
                    segment.ShiftedTimingPointLines.Add(shifted);
            }
        }
        else
        {
            // Fall back to parsed timing points
            foreach (var tp in osuFile.TimingPoints)
            {
                var newTime = Math.Max(0, tp.Time - firstNoteTime + offsetInMarathon);
                var newTp = new TimingPoint
                {
                    Time = newTime,
                    BeatLength = tp.BeatLength,
                    Meter = tp.Meter,
                    SampleSet = tp.SampleSet,
                    SampleIndex = tp.SampleIndex,
                    Volume = tp.Volume,
                    Uninherited = tp.Uninherited,
                    Effects = tp.Effects
                };
                segment.ShiftedTimingPointLines.Add(newTp.ToString());
            }
        }

        // Shift Events (breaks, etc.)
        if (osuFile.RawSections.TryGetValue("Events", out var eventLines))
        {
            foreach (var line in eventLines)
            {
                var shifted = ShiftEventLine(line, firstNoteTime, offsetInMarathon);
                segment.ShiftedEventLines.Add(shifted);
            }
        }
    }

    /// <summary>
    /// Shifts a hit object line by the given offset.
    /// </summary>
    private string? ShiftHitObjectLine(string line, double firstNoteTime, double offsetInMarathon)
    {
        var parts = line.Split(',');
        if (parts.Length < 5) return null;

        try
        {
            // Parse time (index 2)
            var time = double.Parse(parts[2], CultureInfo.InvariantCulture);
            var newTime = Math.Max(0, time - firstNoteTime + offsetInMarathon);
            parts[2] = ((int)Math.Round(newTime)).ToString(CultureInfo.InvariantCulture);

            // Check for hold note end time (in parts[5] before colon)
            if (parts.Length >= 6 && parts[5].Contains(':'))
            {
                var endParts = parts[5].Split(':');
                if (long.TryParse(endParts[0], out var endTime))
                {
                    var newEndTime = Math.Max(0, endTime - firstNoteTime + offsetInMarathon);
                    endParts[0] = ((long)Math.Round(newEndTime)).ToString(CultureInfo.InvariantCulture);
                    parts[5] = string.Join(":", endParts);
                }
            }

            return string.Join(",", parts);
        }
        catch
        {
            return line;
        }
    }

    /// <summary>
    /// Shifts a timing point line by the given offset.
    /// </summary>
    private string? ShiftTimingPointLine(string line, double firstNoteTime, double offsetInMarathon)
    {
        var parts = line.Split(',');
        if (parts.Length < 2) return null;

        try
        {
            var time = double.Parse(parts[0], CultureInfo.InvariantCulture);
            var newTime = Math.Max(0, time - firstNoteTime + offsetInMarathon);
            parts[0] = newTime.ToString("0.###", CultureInfo.InvariantCulture);
            return string.Join(",", parts);
        }
        catch
        {
            return line;
        }
    }

    /// <summary>
    /// Shifts an event line by the given offset (for breaks, etc.).
    /// </summary>
    private string ShiftEventLine(string line, double firstNoteTime, double offsetInMarathon)
    {
        // Background images - keep as-is (will need to handle separately)
        if (line.StartsWith("0,0,"))
            return line;

        // Video - skip for marathon
        if (line.StartsWith("Video,") || line.StartsWith("1,"))
            return "// " + line;

        // Break: 2,startTime,endTime
        if (line.StartsWith("2,") || line.StartsWith("Break,"))
        {
            var parts = line.Split(',');
            if (parts.Length >= 3)
            {
                try
                {
                    if (int.TryParse(parts[1], out var startTime))
                    {
                        var newStart = Math.Max(0, startTime - firstNoteTime + offsetInMarathon);
                        parts[1] = ((int)newStart).ToString();
                    }
                    if (int.TryParse(parts[2], out var endTime))
                    {
                        var newEnd = Math.Max(0, endTime - firstNoteTime + offsetInMarathon);
                        parts[2] = ((int)newEnd).ToString();
                    }
                    return string.Join(",", parts);
                }
                catch { }
            }
        }

        return line;
    }

    /// <summary>
    /// Creates the merged .osu file from all processed segments.
    /// </summary>
    private string CreateMergedOsuFile(
        List<ProcessedMapSegment> segments,
        MarathonMetadata metadata,
        string outputFolder,
        string audioFilename)
    {
        var lines = new List<string>();

        // Get reference from first non-pause segment for format version and other settings
        var firstMapSegment = segments.FirstOrDefault(s => !s.Entry.IsPause);
        var firstOsu = firstMapSegment?.Entry.OsuFile;

        // osu file format version
        lines.Add("osu file format v14");
        lines.Add("");

        // [General]
        lines.Add("[General]");
        lines.Add($"AudioFilename: {Path.GetFileName(audioFilename)}");
        lines.Add("AudioLeadIn: 0");
        lines.Add("PreviewTime: 0");
        lines.Add("Countdown: 0");
        lines.Add("SampleSet: Soft");
        lines.Add("StackLeniency: 0.7");
        lines.Add($"Mode: {firstOsu?.Mode ?? 3}"); // Default to mania mode
        lines.Add("LetterboxInBreaks: 0");
        lines.Add("SpecialStyle: 0");
        lines.Add("WidescreenStoryboard: 0");
        lines.Add("");

        // [Editor]
        lines.Add("[Editor]");
        lines.Add("DistanceSpacing: 1");
        lines.Add("BeatDivisor: 4");
        lines.Add("GridSize: 4");
        lines.Add("TimelineZoom: 1");
        lines.Add("");

        // [Metadata]
        lines.Add("[Metadata]");
        lines.Add($"Title:{metadata.Title}");
        lines.Add($"TitleUnicode:{(string.IsNullOrEmpty(metadata.TitleUnicode) ? metadata.Title : metadata.TitleUnicode)}");
        lines.Add($"Artist:{metadata.Artist}");
        lines.Add($"ArtistUnicode:{(string.IsNullOrEmpty(metadata.ArtistUnicode) ? metadata.Artist : metadata.ArtistUnicode)}");
        lines.Add($"Creator:{metadata.Creator}");
        lines.Add($"Version:{metadata.Version}");
        lines.Add("Source:");
        lines.Add($"Tags:{metadata.Tags}");
        lines.Add("BeatmapID:0");
        lines.Add("BeatmapSetID:-1");
        lines.Add("");

        // [Difficulty]
        lines.Add("[Difficulty]");
        lines.Add($"HPDrainRate:{firstOsu?.HPDrainRate ?? 5}");
        lines.Add($"CircleSize:{firstOsu?.CircleSize ?? 4}");
        lines.Add($"OverallDifficulty:{firstOsu?.OverallDifficulty ?? 8}");
        lines.Add($"ApproachRate:{firstOsu?.ApproachRate ?? 5}");
        lines.Add($"SliderMultiplier:{firstOsu?.SliderMultiplier ?? 1.4}");
        lines.Add($"SliderTickRate:{firstOsu?.SliderTickRate ?? 1}");
        lines.Add("");

        // [Events]
        lines.Add("[Events]");
        lines.Add("//Background and Video events");
        // Copy background from first non-pause map if it exists
        if (firstOsu?.BackgroundFilename != null)
        {
            var bgSourcePath = Path.Combine(firstOsu.DirectoryPath, firstOsu.BackgroundFilename);
            if (File.Exists(bgSourcePath))
            {
                var bgDestPath = Path.Combine(outputFolder, firstOsu.BackgroundFilename);
                try
                {
                    File.Copy(bgSourcePath, bgDestPath, true);
                    lines.Add($"0,0,\"{firstOsu.BackgroundFilename}\",0,0");
                }
                catch
                {
                    // Ignore background copy errors
                }
            }
        }
        lines.Add("//Break Periods");
        // Collect all breaks from segments
        foreach (var segment in segments)
        {
            foreach (var eventLine in segment.ShiftedEventLines)
            {
                if (eventLine.StartsWith("2,") || eventLine.StartsWith("Break,"))
                    lines.Add(eventLine);
            }
        }
        lines.Add("//Storyboard Layer 0 (Background)");
        lines.Add("//Storyboard Layer 1 (Fail)");
        lines.Add("//Storyboard Layer 2 (Pass)");
        lines.Add("//Storyboard Layer 3 (Foreground)");
        lines.Add("//Storyboard Layer 4 (Overlay)");
        lines.Add("//Storyboard Sound Samples");
        lines.Add("");

        // [TimingPoints]
        lines.Add("[TimingPoints]");
        foreach (var segment in segments)
        {
            foreach (var tpLine in segment.ShiftedTimingPointLines)
            {
                lines.Add(tpLine);
            }
        }
        lines.Add("");

        // [Colours] - optional, skip for now
        lines.Add("");

        // [HitObjects]
        lines.Add("[HitObjects]");
        foreach (var segment in segments)
        {
            foreach (var hoLine in segment.ShiftedHitObjectLines)
            {
                lines.Add(hoLine);
            }
        }

        // Generate filename
        var osuFileName = SanitizeFileName($"{metadata.Artist} - {metadata.Title} ({metadata.Creator}) [{metadata.Version}].osu");
        var osuPath = Path.Combine(outputFolder, osuFileName);

        File.WriteAllLines(osuPath, lines);

        Console.WriteLine($"[Marathon] Created: {osuPath}");

        return osuPath;
    }

    /// <summary>
    /// Normalizes scroll velocity in the .osu file using the SvNormalizer service.
    /// </summary>
    private void NormalizeSV(string osuPath)
    {
        if (!File.Exists(osuPath))
            return;

        try
        {
            var osuFile = _fileParser.Parse(osuPath);
            var existingTimingPoints = osuFile.TimingPoints;
            var uninheritedCount = existingTimingPoints.Count(tp => tp.Uninherited);

            if (uninheritedCount <= 1)
            {
                Console.WriteLine($"[Marathon] No BPM changes found - SV normalization not needed.");
                return;
            }

            var svNormalizer = new SvNormalizer();
            var fileWriter = new OsuFileWriter();

            // Determine base BPM (most common by duration)
            var uninherited = existingTimingPoints.Where(tp => tp.Uninherited).OrderBy(tp => tp.Time).ToList();
            double baseBpm = uninherited.Count > 0 ? uninherited[0].Bpm : 120;

            if (uninherited.Count > 1)
            {
                var bpmDurations = new Dictionary<double, double>();
                for (int i = 0; i < uninherited.Count; i++)
                {
                    double bpm = Math.Round(uninherited[i].Bpm, 1);
                    double duration = i < uninherited.Count - 1
                        ? uninherited[i + 1].Time - uninherited[i].Time
                        : 60000;
                    if (!bpmDurations.ContainsKey(bpm)) bpmDurations[bpm] = 0;
                    bpmDurations[bpm] += duration;
                }
                baseBpm = bpmDurations.OrderByDescending(kvp => kvp.Value).First().Key;
            }

            var normalizedTimingPoints = svNormalizer.Normalize(existingTimingPoints, baseBpm);
            var stats = svNormalizer.GetStats(existingTimingPoints, normalizedTimingPoints, baseBpm);

            fileWriter.Write(osuFile, normalizedTimingPoints);

            Console.WriteLine($"[Marathon] SV normalized: {osuPath}");
            Console.WriteLine($"[Marathon] Base BPM: {stats.BaseBpm:F0}, SV range: {stats.MinSv:F2}x - {stats.MaxSv:F2}x");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Marathon] SV normalization failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sanitizes a string for use as a filename.
    /// </summary>
    private string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
        return sanitized.Trim();
    }

    /// <summary>
    /// Checks if ffmpeg is available.
    /// </summary>
    public async Task<bool> CheckFfmpegAvailableAsync()
    {
        return await _rateChanger.CheckFfmpegAvailableAsync();
    }
}

