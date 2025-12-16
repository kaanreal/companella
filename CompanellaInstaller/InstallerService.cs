using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompanellaInstaller;

/// <summary>
/// Service that handles downloading and installing Companella from GitHub releases.
/// This is a simplified version of the AutoUpdaterService designed for fresh installations.
/// </summary>
public class InstallerService : IDisposable
{
    private const string GITHUB_REPO_OWNER = "Leinadix";
    private const string GITHUB_REPO_NAME = "companella";
    private const string RELEASE_ASSET_NAME = "Release.zip";

    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _downloadCts;

    /// <summary>
    /// Event raised when download progress changes.
    /// </summary>
    public event EventHandler<DownloadProgressEventArgs>? DownloadProgressChanged;

    /// <summary>
    /// Event raised when an error occurs.
    /// </summary>
    public event EventHandler<string>? Error;

    /// <summary>
    /// Event raised when status changes.
    /// </summary>
    public event EventHandler<string>? StatusChanged;

    public InstallerService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Companella-Installer");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
    }

    /// <summary>
    /// Gets the latest release information from GitHub.
    /// </summary>
    public async Task<ReleaseInfo?> GetLatestReleaseAsync()
    {
        try
        {
            StatusChanged?.Invoke(this, "Checking for latest version...");

            var apiUrl = $"https://api.github.com/repos/{GITHUB_REPO_OWNER}/{GITHUB_REPO_NAME}/releases/latest";
            var response = await _httpClient.GetAsync(apiUrl);

            if (!response.IsSuccessStatusCode)
            {
                Error?.Invoke(this, $"GitHub API returned {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);

            if (release == null)
            {
                Error?.Invoke(this, "Failed to parse GitHub release data");
                return null;
            }

            var asset = release.Assets.FirstOrDefault(a =>
                a.Name.Equals(RELEASE_ASSET_NAME, StringComparison.OrdinalIgnoreCase));

            if (asset == null)
            {
                Error?.Invoke(this, $"Release.zip not found in version {release.TagName}");
                return null;
            }

            return new ReleaseInfo
            {
                TagName = release.TagName,
                Name = release.Name,
                Body = release.Body,
                DownloadUrl = asset.BrowserDownloadUrl,
                DownloadSize = asset.Size
            };
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Failed to check for latest version: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Downloads and installs the release to the specified directory.
    /// </summary>
    public async Task<bool> DownloadAndInstallAsync(ReleaseInfo releaseInfo, string installPath)
    {
        _downloadCts?.Cancel();
        _downloadCts = new CancellationTokenSource();
        var cancellationToken = _downloadCts.Token;

        var tempZipPath = Path.Combine(Path.GetTempPath(), $"companella_install_{Guid.NewGuid()}.zip");
        var tempExtractPath = Path.Combine(Path.GetTempPath(), $"companella_install_{Guid.NewGuid()}");

        try
        {
            StatusChanged?.Invoke(this, "Downloading Companella...");

            // Download the zip file with progress
            await DownloadFileWithProgressAsync(releaseInfo.DownloadUrl, tempZipPath, releaseInfo.DownloadSize, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                StatusChanged?.Invoke(this, "Installation cancelled");
                return false;
            }

            StatusChanged?.Invoke(this, "Extracting files...");

            // Extract to temp directory first
            Directory.CreateDirectory(tempExtractPath);
            ZipFile.ExtractToDirectory(tempZipPath, tempExtractPath, overwriteFiles: true);

            // Find the actual content directory
            var sourceDir = FindSourceDirectory(tempExtractPath);

            if (sourceDir == null)
            {
                Error?.Invoke(this, "Could not find application files in downloaded archive");
                return false;
            }

            StatusChanged?.Invoke(this, "Installing files...");

            // Create install directory if it doesn't exist
            Directory.CreateDirectory(installPath);

            // Copy all files to install path
            CopyDirectory(sourceDir, installPath);

            // Save version file
            var versionFilePath = Path.Combine(installPath, "version.txt");
            await File.WriteAllTextAsync(versionFilePath, releaseInfo.TagName, cancellationToken);

            StatusChanged?.Invoke(this, "Installation complete!");
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusChanged?.Invoke(this, "Installation cancelled");
            return false;
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Installation failed: {ex.Message}");
            return false;
        }
        finally
        {
            // Clean up temp files
            try
            {
                if (File.Exists(tempZipPath))
                    File.Delete(tempZipPath);
                if (Directory.Exists(tempExtractPath))
                    Directory.Delete(tempExtractPath, true);
            }
            catch { }
        }
    }

    /// <summary>
    /// Downloads a file with progress reporting.
    /// </summary>
    private async Task DownloadFileWithProgressAsync(string url, string destinationPath, long totalSize, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength ?? totalSize;

        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        long totalBytesRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalBytesRead += bytesRead;

            var progressArgs = new DownloadProgressEventArgs
            {
                BytesDownloaded = totalBytesRead,
                TotalBytes = contentLength,
                ProgressPercentage = contentLength > 0 ? (int)((totalBytesRead * 100) / contentLength) : 0,
                Status = $"Downloading... {FormatBytes(totalBytesRead)} / {FormatBytes(contentLength)}"
            };

            DownloadProgressChanged?.Invoke(this, progressArgs);
        }
    }

    /// <summary>
    /// Finds the directory containing the application files in the extracted archive.
    /// </summary>
    private string? FindSourceDirectory(string extractedPath)
    {
        // Check if the extracted content is directly in the root
        if (Directory.GetFiles(extractedPath, "*.exe").Any() ||
            Directory.GetFiles(extractedPath, "*.dll").Any())
        {
            return extractedPath;
        }

        // Check for common nested structures
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
    /// Recursively copies a directory.
    /// </summary>
    private void CopyDirectory(string sourceDir, string targetDir)
    {
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            var destFile = Path.Combine(targetDir, fileName);
            File.Copy(file, destFile, true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            var destDir = Path.Combine(targetDir, dirName);
            Directory.CreateDirectory(destDir);
            CopyDirectory(dir, destDir);
        }
    }

    /// <summary>
    /// Creates a desktop shortcut for the application.
    /// </summary>
    public bool CreateDesktopShortcut(string installPath)
    {
        try
        {
            var exePath = Path.Combine(installPath, "Companella!.exe");
            if (!File.Exists(exePath))
            {
                Error?.Invoke(this, "Application executable not found");
                return false;
            }

            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var shortcutPath = Path.Combine(desktopPath, "Companella!.lnk");

            // Use PowerShell to create the shortcut
            var script = $@"
$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut('{shortcutPath.Replace("'", "''")}')
$Shortcut.TargetPath = '{exePath.Replace("'", "''")}'
$Shortcut.WorkingDirectory = '{installPath.Replace("'", "''")}'
$Shortcut.IconLocation = '{exePath.Replace("'", "''")}'
$Shortcut.Save()
";

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            process?.WaitForExit(5000);

            return File.Exists(shortcutPath);
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Failed to create shortcut: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Launches the installed application.
    /// </summary>
    public bool LaunchApplication(string installPath)
    {
        try
        {
            var exePath = Path.Combine(installPath, "Companella!.exe");
            if (!File.Exists(exePath))
            {
                Error?.Invoke(this, "Application executable not found");
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = installPath,
                UseShellExecute = true
            });

            return true;
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Failed to launch application: {ex.Message}");
            return false;
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
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public int ProgressPercentage { get; set; }
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Information about a release.
/// </summary>
public class ReleaseInfo
{
    public string TagName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public long DownloadSize { get; set; }
}

/// <summary>
/// GitHub API release response.
/// </summary>
public class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = new();
}

/// <summary>
/// GitHub API asset response.
/// </summary>
public class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
