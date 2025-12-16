using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using OsuMappingHelper.Models;

namespace OsuMappingHelper.Services;

/// <summary>
/// Service for checking and applying automatic updates from GitHub releases.
/// </summary>
public class AutoUpdaterService : IDisposable
{
    private const string GITHUB_REPO_OWNER = "Leinadix";
    private const string GITHUB_REPO_NAME = "companella";
    private const string RELEASE_ASSET_NAME = "Release.zip";
    private const string VERSION_FILE_NAME = "version.txt";

    /// <summary>
    /// Files that should not be overwritten during updates.
    /// </summary>
    private static readonly HashSet<string> PreservedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "settings.json",
        "dans.json", // Preserve custom dan configurations
        "sessions.db", // Preserve session history database
        "maps.db" // Preserve indexed maps database
    };

    private readonly HttpClient _httpClient;
    private readonly string _applicationDirectory;
    private readonly string _versionFilePath;
    private CancellationTokenSource? _downloadCts;

    /// <summary>
    /// Gets the current application version.
    /// </summary>
    public string CurrentVersion { get; private set; } = "unknown";

    /// <summary>
    /// Event raised when download progress changes.
    /// </summary>
    public event EventHandler<DownloadProgressEventArgs>? DownloadProgressChanged;

    /// <summary>
    /// Event raised when an update is available.
    /// </summary>
    public event EventHandler<UpdateInfo>? UpdateAvailable;

    /// <summary>
    /// Event raised when an error occurs during update check or download.
    /// </summary>
    public event EventHandler<string>? UpdateError;

    public AutoUpdaterService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Companella-AutoUpdater");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

        // Get application directory
        var exePath = Assembly.GetExecutingAssembly().Location;
        _applicationDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory;
        _versionFilePath = Path.Combine(_applicationDirectory, VERSION_FILE_NAME);

        // Load current version
        LoadCurrentVersion();
    }

    /// <summary>
    /// Loads the current version from the version file.
    /// </summary>
    private void LoadCurrentVersion()
    {
        try
        {
            if (File.Exists(_versionFilePath))
            {
                CurrentVersion = File.ReadAllText(_versionFilePath).Trim();
            }
            else
            {
                // Try to get version from assembly
                var assembly = Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                if (version != null)
                {
                    CurrentVersion = $"v{version.Major}.{version.Minor}";
                }
            }
        }
        catch
        {
            CurrentVersion = "unknown";
        }
    }

    /// <summary>
    /// Saves the version to the version file.
    /// </summary>
    private void SaveVersion(string version)
    {
        try
        {
            File.WriteAllText(_versionFilePath, version);
            CurrentVersion = version;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AutoUpdater] Failed to save version: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks for updates from GitHub.
    /// </summary>
    /// <returns>Update info if an update is available, null otherwise.</returns>
    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        try
        {
            var apiUrl = $"https://api.github.com/repos/{GITHUB_REPO_OWNER}/{GITHUB_REPO_NAME}/releases/latest";
            
            Console.WriteLine($"[AutoUpdater] Checking for updates at: {apiUrl}");
            
            var response = await _httpClient.GetAsync(apiUrl);
            
            if (!response.IsSuccessStatusCode)
            {
                var error = $"GitHub API returned {response.StatusCode}";
                Console.WriteLine($"[AutoUpdater] {error}");
                UpdateError?.Invoke(this, error);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);

            if (release == null)
            {
                UpdateError?.Invoke(this, "Failed to parse GitHub release data");
                return null;
            }

            Console.WriteLine($"[AutoUpdater] Latest version: {release.TagName}, Current version: {CurrentVersion}");

            // Check if this is a newer version
            if (!IsNewerVersion(release.TagName))
            {
                Console.WriteLine("[AutoUpdater] Already on latest version");
                return null;
            }

            // Find the Release.zip asset
            var asset = release.Assets.FirstOrDefault(a => 
                a.Name.Equals(RELEASE_ASSET_NAME, StringComparison.OrdinalIgnoreCase));

            if (asset == null)
            {
                Console.WriteLine($"[AutoUpdater] Release.zip asset not found in release {release.TagName}");
                UpdateError?.Invoke(this, $"Release.zip not found in version {release.TagName}");
                return null;
            }

            var updateInfo = new UpdateInfo
            {
                TagName = release.TagName,
                Name = release.Name,
                Body = release.Body,
                Prerelease = release.Prerelease,
                PublishedAt = release.PublishedAt,
                DownloadUrl = asset.BrowserDownloadUrl,
                DownloadSize = asset.Size,
                HtmlUrl = release.HtmlUrl
            };

            Console.WriteLine($"[AutoUpdater] Update available: {updateInfo.TagName} ({FormatBytes(updateInfo.DownloadSize)})");
            UpdateAvailable?.Invoke(this, updateInfo);

            return updateInfo;
        }
        catch (Exception ex)
        {
            var error = $"Failed to check for updates: {ex.Message}";
            Console.WriteLine($"[AutoUpdater] {error}");
            UpdateError?.Invoke(this, error);
            return null;
        }
    }

    /// <summary>
    /// Compares version strings to determine if the remote version is newer.
    /// </summary>
    private bool IsNewerVersion(string remoteVersion)
    {
        // Handle "unknown" current version - always consider remote as newer
        if (CurrentVersion == "unknown")
            return true;

        // Normalize versions (remove 'v' prefix if present)
        var current = CurrentVersion.TrimStart('v', 'V');
        var remote = remoteVersion.TrimStart('v', 'V');

        // Try to parse as Version objects
        if (Version.TryParse(NormalizeVersionString(current), out var currentVer) &&
            Version.TryParse(NormalizeVersionString(remote), out var remoteVer))
        {
            return remoteVer > currentVer;
        }

        // Fall back to string comparison
        return string.Compare(remote, current, StringComparison.OrdinalIgnoreCase) > 0;
    }

    /// <summary>
    /// Normalizes a version string to be parseable by Version.TryParse.
    /// </summary>
    private string NormalizeVersionString(string version)
    {
        // Handle single number versions like "2" -> "2.0"
        if (!version.Contains('.'))
        {
            return version + ".0";
        }
        return version;
    }

    /// <summary>
    /// Downloads and applies an update.
    /// </summary>
    /// <param name="updateInfo">The update information.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <returns>True if the update was successfully downloaded and extracted.</returns>
    public async Task<bool> DownloadAndApplyUpdateAsync(UpdateInfo updateInfo, IProgress<DownloadProgressEventArgs>? progress = null)
    {
        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();
        var cancellationToken = _downloadCts.Token;

        var tempZipPath = Path.Combine(Path.GetTempPath(), $"companella_update_{Guid.NewGuid()}.zip");
        var tempExtractPath = Path.Combine(Path.GetTempPath(), $"companella_update_{Guid.NewGuid()}");

        try
        {
            Console.WriteLine($"[AutoUpdater] Downloading update from: {updateInfo.DownloadUrl}");

            // Download the zip file with progress
            await DownloadFileWithProgressAsync(updateInfo.DownloadUrl, tempZipPath, updateInfo.DownloadSize, progress, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("[AutoUpdater] Download cancelled");
                return false;
            }

            Console.WriteLine("[AutoUpdater] Download complete, extracting...");
            
            // Report extraction progress
            var extractProgress = new DownloadProgressEventArgs
            {
                BytesDownloaded = updateInfo.DownloadSize,
                TotalBytes = updateInfo.DownloadSize,
                ProgressPercentage = 100,
                Status = "Extracting update..."
            };
            progress?.Report(extractProgress);
            DownloadProgressChanged?.Invoke(this, extractProgress);

            // Extract to temp directory first
            Directory.CreateDirectory(tempExtractPath);
            ZipFile.ExtractToDirectory(tempZipPath, tempExtractPath, overwriteFiles: true);

            // Find the actual content directory (might be nested in bin/Release or similar)
            var sourceDir = FindUpdateSourceDirectory(tempExtractPath);
            
            if (sourceDir == null)
            {
                throw new Exception("Could not find update content in downloaded archive");
            }

            Console.WriteLine($"[AutoUpdater] Applying update from: {sourceDir}");

            // Create update script that will run after application closes
            var updateScriptPath = CreateUpdateScript(sourceDir, _applicationDirectory, updateInfo.TagName);

            Console.WriteLine($"[AutoUpdater] Update script created: {updateScriptPath}");
            Console.WriteLine("[AutoUpdater] Update will be applied after restart");

            return true;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[AutoUpdater] Update download was cancelled");
            return false;
        }
        catch (Exception ex)
        {
            var error = $"Failed to download/apply update: {ex.Message}";
            Console.WriteLine($"[AutoUpdater] {error}");
            UpdateError?.Invoke(this, error);
            return false;
        }
        finally
        {
            // Clean up temp zip file
            try
            {
                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Downloads a file with progress reporting.
    /// </summary>
    private async Task DownloadFileWithProgressAsync(string url, string destinationPath, long totalSize, 
        IProgress<DownloadProgressEventArgs>? progress, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength ?? totalSize;

        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        long totalBytesRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
            totalBytesRead += bytesRead;

            var progressArgs = new DownloadProgressEventArgs
            {
                BytesDownloaded = totalBytesRead,
                TotalBytes = contentLength,
                ProgressPercentage = contentLength > 0 ? (int)((totalBytesRead * 100) / contentLength) : 0,
                Status = $"Downloading... {FormatBytes(totalBytesRead)} / {FormatBytes(contentLength)}"
            };

            progress?.Report(progressArgs);
            DownloadProgressChanged?.Invoke(this, progressArgs);
        }
    }

    /// <summary>
    /// Finds the directory containing the update files in the extracted archive.
    /// </summary>
    private string? FindUpdateSourceDirectory(string extractedPath)
    {
        // Check if the extracted content is directly in the root
        if (Directory.GetFiles(extractedPath, "*.exe").Any() ||
            Directory.GetFiles(extractedPath, "*.dll").Any())
        {
            return extractedPath;
        }

        // Check for common nested structures (bin/Release, Release, etc.)
        var possiblePaths = new[]
        {
            Path.Combine(extractedPath, "bin", "Release", "net8.0-windows"),
            Path.Combine(extractedPath, "bin", "Release"),
            Path.Combine(extractedPath, "Release"),
            Path.Combine(extractedPath, "net8.0-windows")
        };

        foreach (var path in possiblePaths)
        {
            if (Directory.Exists(path) && 
                (Directory.GetFiles(path, "*.exe").Any() || Directory.GetFiles(path, "*.dll").Any()))
            {
                return path;
            }
        }

        // Search recursively for the first directory with exe/dll files
        foreach (var dir in Directory.GetDirectories(extractedPath, "*", SearchOption.AllDirectories))
        {
            if (Directory.GetFiles(dir, "*.exe").Any() || Directory.GetFiles(dir, "*.dll").Any())
            {
                return dir;
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a PowerShell script that will apply the update after the application closes.
    /// </summary>
    private string CreateUpdateScript(string sourceDir, string targetDir, string newVersion)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"companella_update_{Guid.NewGuid()}.ps1");
        var exeName = "Companella!.exe";
        var preservedFilesStr = string.Join("','", PreservedFiles);

        var script = $@"
# Companella Update Script
# Generated at: {DateTime.Now}
# Updating to version: {newVersion}

$ErrorActionPreference = 'Stop'

$sourceDir = '{sourceDir.Replace("'", "''")}'
$targetDir = '{targetDir.Replace("'", "''")}'
$exeName = '{exeName}'
$preservedFiles = @('{preservedFilesStr}')
$newVersion = '{newVersion}'

Write-Host 'Companella Auto-Updater' -ForegroundColor Cyan
Write-Host '========================' -ForegroundColor Cyan
Write-Host ''
Write-Host ""Updating to version: $newVersion"" -ForegroundColor Yellow
Write-Host ''

# Wait for the application to close
Write-Host 'Waiting for Companella to close...' -ForegroundColor Gray
$maxWait = 30
$waited = 0
while ($waited -lt $maxWait) {{
    $process = Get-Process -Name 'Companella!' -ErrorAction SilentlyContinue
    if (-not $process) {{
        break
    }}
    Start-Sleep -Seconds 1
    $waited++
}}

if ($waited -ge $maxWait) {{
    Write-Host 'Warning: Application did not close in time, attempting to continue...' -ForegroundColor Yellow
}}

Write-Host 'Applying update...' -ForegroundColor Yellow

# Backup preserved files
$backups = @{{}}
foreach ($file in $preservedFiles) {{
    $targetPath = Join-Path $targetDir $file
    if (Test-Path $targetPath) {{
        $backupPath = ""$targetPath.backup""
        Copy-Item -Path $targetPath -Destination $backupPath -Force
        $backups[$file] = $backupPath
        Write-Host ""  Backed up: $file"" -ForegroundColor Gray
    }}
}}

# Copy new files
try {{
    Write-Host 'Copying new files...' -ForegroundColor Gray
    
    # Copy all files from source to target
    Get-ChildItem -Path $sourceDir -Recurse | ForEach-Object {{
        $relativePath = $_.FullName.Substring($sourceDir.Length + 1)
        $targetPath = Join-Path $targetDir $relativePath
        
        if ($_.PSIsContainer) {{
            if (-not (Test-Path $targetPath)) {{
                New-Item -ItemType Directory -Path $targetPath -Force | Out-Null
            }}
        }} else {{
            # Skip preserved files
            $fileName = $_.Name
            if ($preservedFiles -contains $fileName) {{
                Write-Host ""  Skipped (preserved): $fileName"" -ForegroundColor DarkGray
            }} else {{
                $targetFolder = Split-Path $targetPath -Parent
                if (-not (Test-Path $targetFolder)) {{
                    New-Item -ItemType Directory -Path $targetFolder -Force | Out-Null
                }}
                Copy-Item -Path $_.FullName -Destination $targetPath -Force
            }}
        }}
    }}
    
    # Save new version
    $versionFile = Join-Path $targetDir 'version.txt'
    Set-Content -Path $versionFile -Value $newVersion
    
    Write-Host ''
    Write-Host 'Update complete!' -ForegroundColor Green
    Write-Host ''
    
}} catch {{
    Write-Host ""Error during update: $_"" -ForegroundColor Red
    
    # Restore backups on error
    Write-Host 'Restoring backups...' -ForegroundColor Yellow
    foreach ($file in $backups.Keys) {{
        $targetPath = Join-Path $targetDir $file
        $backupPath = $backups[$file]
        if (Test-Path $backupPath) {{
            Copy-Item -Path $backupPath -Destination $targetPath -Force
        }}
    }}
    
    Write-Host 'Press any key to exit...' -ForegroundColor Gray
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
    exit 1
}}

# Clean up backups
foreach ($backupPath in $backups.Values) {{
    if (Test-Path $backupPath) {{
        Remove-Item -Path $backupPath -Force
    }}
}}

# Clean up source directory
try {{
    Remove-Item -Path $sourceDir -Recurse -Force -ErrorAction SilentlyContinue
}} catch {{ }}

# Restart the application
Write-Host 'Starting Companella...' -ForegroundColor Gray
$exePath = Join-Path $targetDir $exeName
if (Test-Path $exePath) {{
    Start-Process -FilePath $exePath
}}

# Clean up this script after a delay
Start-Sleep -Seconds 2
Remove-Item -Path $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
";

        File.WriteAllText(scriptPath, script);
        return scriptPath;
    }

    /// <summary>
    /// Starts the update process by running the update script and exiting the application.
    /// </summary>
    /// <param name="updateInfo">The update information.</param>
    public void StartUpdateAndRestart(UpdateInfo updateInfo)
    {
        // Find the most recent update script
        var tempDir = Path.GetTempPath();
        var scripts = Directory.GetFiles(tempDir, "companella_update_*.ps1")
            .OrderByDescending(File.GetCreationTime)
            .ToList();

        if (scripts.Count == 0)
        {
            Console.WriteLine("[AutoUpdater] No update script found. Please download the update first.");
            return;
        }

        var scriptPath = scripts.First();
        Console.WriteLine($"[AutoUpdater] Starting update script: {scriptPath}");

        // Start the update script
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            CreateNoWindow = false
        };

        try
        {
            Process.Start(startInfo);
            Console.WriteLine("[AutoUpdater] Update script started, application will now exit");
            
            // Exit the application
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AutoUpdater] Failed to start update script: {ex.Message}");
            UpdateError?.Invoke(this, $"Failed to start update: {ex.Message}");
        }
    }

    /// <summary>
    /// Cancels any ongoing download.
    /// </summary>
    public void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    /// <summary>
    /// Formats bytes into a human-readable string.
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;
        
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }

    public void Dispose()
    {
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _httpClient.Dispose();
    }
}

/// <summary>
/// Event arguments for download progress updates.
/// </summary>
public class DownloadProgressEventArgs : EventArgs
{
    /// <summary>
    /// The number of bytes downloaded so far.
    /// </summary>
    public long BytesDownloaded { get; set; }

    /// <summary>
    /// The total number of bytes to download.
    /// </summary>
    public long TotalBytes { get; set; }

    /// <summary>
    /// The download progress percentage (0-100).
    /// </summary>
    public int ProgressPercentage { get; set; }

    /// <summary>
    /// A status message describing the current operation.
    /// </summary>
    public string Status { get; set; } = string.Empty;
}
