using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ResourceAnalyzer.Services
{
    public class UpdateInfo
    {
        public bool IsSuccess { get; set; }
        public bool HasUpdate { get; set; }
        public string CurrentVersion { get; set; } = "1.0.0";
        public string LatestVersion { get; set; } = "1.0.0";
        public string Channel { get; set; } = "Beta";
        public bool IsBeta { get; set; } = false;
        public string ReleaseName { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public string HtmlUrl { get; set; } = "";
        public string? DownloadUrl { get; set; }
        public string? ExpectedSha256 { get; set; }
        public string Message { get; set; } = "";

        public string GetEffectiveReleaseUrl()
        {
            if (!string.IsNullOrWhiteSpace(HtmlUrl) && HtmlUrl.Contains("/releases/tag/", StringComparison.OrdinalIgnoreCase))
            {
                return HtmlUrl;
            }
            string ver = HasUpdate ? LatestVersion : CurrentVersion;
            return $"https://github.com/AccessibleTools/Resource-Analyzer-for-Windows/releases/tag/v{ver}";
        }
    }

    public interface IUpdateService
    {
        Task<UpdateInfo> CheckForUpdatesAsync(string channel = "Beta");
        string GetCurrentVersion();
        void OpenUrl(string url);
        Task<string?> DownloadInstallerAsync(string downloadUrl, string? expectedSha256 = null, IProgress<int>? progress = null, CancellationToken cancellationToken = default);
        bool VerifyFileHash(string filePath, string expectedSha256);
        bool LaunchInstaller(string installerPath);
        void CleanupTempInstallers();
        bool ShouldPerformDailyCheck(ResourceAnalyzer.Models.AppSettings settings);
        void RecordCheckPerformed(ResourceAnalyzer.Models.AppSettings settings);
    }

    public class UpdateService : IUpdateService
    {
        private const string RepoOwner = "AccessibleTools";
        private const string RepoName = "Resource-Analyzer-for-Windows";
        private const string ApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases";

        private readonly HttpClient _httpClient;

        public UpdateService(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "ResourceAnalyzer-UpdateChecker");
            }
            if (!_httpClient.DefaultRequestHeaders.Contains("Accept"))
            {
                _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            }
        }

        public bool ShouldPerformDailyCheck(ResourceAnalyzer.Models.AppSettings settings)
        {
            if (!settings.AutoCheckUpdatesDaily) return false;

            if (!string.IsNullOrWhiteSpace(settings.LastUpdateCheckUtc) &&
                DateTime.TryParse(settings.LastUpdateCheckUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastUtc))
            {
                // Do not check again if checked today (same calendar day or within 24 hours)
                if (lastUtc.Date == DateTime.UtcNow.Date || (DateTime.UtcNow - lastUtc).TotalHours < 24.0)
                {
                    return false;
                }
            }

            return true;
        }

        public void RecordCheckPerformed(ResourceAnalyzer.Models.AppSettings settings)
        {
            settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("O");
        }

        public string GetCurrentVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
        }

        public async Task<UpdateInfo> CheckForUpdatesAsync(string channel = "Beta")
        {
            string currentVerStr = GetCurrentVersion();
            var result = new UpdateInfo
            {
                CurrentVersion = currentVerStr,
                LatestVersion = currentVerStr,
                Channel = channel
            };

            try
            {
                using var response = await _httpClient.GetAsync(ApiUrl).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    result.IsSuccess = false;
                    result.Message = $"Could not retrieve update information from GitHub (HTTP {response.StatusCode}).";
                    return result;
                }

                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    result.IsSuccess = false;
                    result.Message = "Unexpected response from GitHub release API.";
                    return result;
                }

                // Parse current version
                Version.TryParse(currentVerStr, out var currentVer);

                JsonElement? bestRelease = null;
                Version? bestVersion = null;
                string bestTag = "";
                bool bestIsBeta = false;

                bool allowBeta = !channel.Equals("Stable", StringComparison.OrdinalIgnoreCase);

                foreach (var release in doc.RootElement.EnumerateArray())
                {
                    // Skip drafts
                    if (release.TryGetProperty("draft", out var draftElem) && draftElem.GetBoolean())
                        continue;

                    bool isPrerelease = release.TryGetProperty("prerelease", out var preElem) && preElem.GetBoolean();
                    if (isPrerelease && !allowBeta)
                        continue;

                    string tagName = release.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(tagName)) continue;

                    string cleanTag = tagName.TrimStart('v', 'V').Trim();
                    string verNumPart = cleanTag.Split('-')[0];

                    if (!Version.TryParse(verNumPart, out var parsedVer))
                        continue;

                    if (bestVersion == null || parsedVer > bestVersion)
                    {
                        bestVersion = parsedVer;
                        bestRelease = release;
                        bestTag = tagName;
                        bestIsBeta = isPrerelease || cleanTag.Contains("beta", StringComparison.OrdinalIgnoreCase);
                    }
                }

                if (bestRelease == null || bestVersion == null)
                {
                    result.IsSuccess = true;
                    result.HasUpdate = false;
                    result.HtmlUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/tag/v{currentVerStr}";
                    result.Message = $"Resource Analyzer for Windows is up to date (Version v{currentVerStr}).";
                    return result;
                }

                var selected = bestRelease.Value;
                string relName = selected.TryGetProperty("name", out var nElem) ? nElem.GetString() ?? "" : "";
                string relBody = selected.TryGetProperty("body", out var bElem) ? bElem.GetString() ?? "" : "";
                string relHtml = selected.TryGetProperty("html_url", out var hElem) ? hElem.GetString() ?? "" : "";

                result.LatestVersion = bestTag.TrimStart('v', 'V').Trim();
                result.ReleaseName = relName;
                result.ReleaseNotes = relBody;
                result.HtmlUrl = string.IsNullOrWhiteSpace(relHtml)
                    ? $"https://github.com/{RepoOwner}/{RepoName}/releases/tag/v{result.LatestVersion}"
                    : relHtml;
                result.IsBeta = bestIsBeta;

                // Check assets for installer .exe
                if (selected.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsElem.EnumerateArray())
                    {
                        if (asset.TryGetProperty("name", out var aNameElem) &&
                            asset.TryGetProperty("browser_download_url", out var dlElem))
                        {
                            string assetName = aNameElem.GetString() ?? "";
                            if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                result.DownloadUrl = dlElem.GetString();
                                if (asset.TryGetProperty("digest", out var digestElem))
                                {
                                    string? digest = digestElem.GetString();
                                    if (!string.IsNullOrWhiteSpace(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                                    {
                                        result.ExpectedSha256 = digest["sha256:".Length..].Trim();
                                    }
                                }
                                break;
                            }
                        }
                    }
                }

                bool hasUpdate = currentVer != null ? bestVersion > currentVer : string.Compare(result.LatestVersion, currentVerStr, StringComparison.OrdinalIgnoreCase) > 0;
                result.HasUpdate = hasUpdate;
                result.IsSuccess = true;

                if (hasUpdate)
                {
                    result.Message = $"A newer version of Resource Analyzer ({bestTag}) is available! You are currently on v{currentVerStr}.";
                }
                else
                {
                    result.Message = $"Resource Analyzer for Windows is up to date (Version v{currentVerStr}).";
                }

                return result;
            }
            catch (Exception ex)
            {
                result.IsSuccess = false;
                result.Message = $"Update check failed: {ex.Message}";
                return result;
            }
        }

        public void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                // Validate that URL is an absolute HTTP/HTTPS address to prevent protocol handler injection (CWE-88)
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = uri.AbsoluteUri,
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
                // Ignore if browser launch fails
            }
        }

        public bool VerifyFileHash(string filePath, string expectedSha256)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || string.IsNullOrWhiteSpace(expectedSha256))
                return false;

            try
            {
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                using var stream = File.OpenRead(filePath);
                byte[] hash = sha256.ComputeHash(stream);
                string computed = Convert.ToHexString(hash);
                return computed.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public async Task<string?> DownloadInstallerAsync(string downloadUrl, string? expectedSha256 = null, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl)) return null;

            // Restrict downloads strictly to HTTPS endpoints
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            string? tempFilePath = null;
            try
            {
                string tempDir = Path.GetTempPath();
                string rawFileName = Path.GetFileName(uri.LocalPath);
                string fileName = (!string.IsNullOrWhiteSpace(rawFileName) && rawFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    ? rawFileName
                    : "ResourceAnalyzer_Update.exe";

                tempFilePath = Path.Combine(tempDir, fileName);

                using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();

                    long? totalBytes = response.Content.Headers.ContentLength;
                    using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                    byte[] buffer = new byte[8192];
                    long totalRead = 0;
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                        totalRead += bytesRead;

                        if (totalBytes.HasValue && totalBytes.Value > 0)
                        {
                            int progressPercent = (int)((totalRead * 100) / totalBytes.Value);
                            progress?.Report(progressPercent);
                        }
                    }
                }

                // Verify SHA-256 integrity if checksum is provided by GitHub
                if (!string.IsNullOrWhiteSpace(expectedSha256))
                {
                    if (!VerifyFileHash(tempFilePath, expectedSha256))
                    {
                        try { File.Delete(tempFilePath); } catch { }
                        throw new System.Security.SecurityException("Downloaded installer failed SHA-256 checksum verification and was deleted for security.");
                    }
                }

                return tempFilePath;
            }
            catch (System.Security.SecurityException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { }
                }
                Debug.WriteLine($"Failed to download installer: {ex.Message}");
                return null;
            }
        }

        public bool LaunchInstaller(string installerPath)
        {
            if (string.IsNullOrWhiteSpace(installerPath) ||
                !File.Exists(installerPath) ||
                !installerPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to launch installer: {ex.Message}");
                return false;
            }
        }

        public static void CleanupTempInstallers()
        {
            try
            {
                string tempDir = Path.GetTempPath();
                if (Directory.Exists(tempDir))
                {
                    var files = Directory.GetFiles(tempDir, "ResourceAnalyzer_*.exe", SearchOption.TopDirectoryOnly);
                    foreach (var file in files)
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch
                        {
                            // File may be locked by an active installer or another process; safely ignore
                        }
                    }
                }
            }
            catch { }
        }

        public void CleanupTempInstallersInstance() => CleanupTempInstallers();
        void IUpdateService.CleanupTempInstallers() => CleanupTempInstallers();
    }
}
