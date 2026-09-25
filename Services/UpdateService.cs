using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace AccessibleTaskManager.Services
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
        public string Message { get; set; } = "";
    }

    public interface IUpdateService
    {
        Task<UpdateInfo> CheckForUpdatesAsync(string channel = "Beta");
        string GetCurrentVersion();
        void OpenUrl(string url);
    }

    public class UpdateService : IUpdateService
    {
        private const string RepoOwner = "AakanshNahadiya";
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

                bool targetStableOnly = channel.Equals("Stable", StringComparison.OrdinalIgnoreCase);

                foreach (var release in doc.RootElement.EnumerateArray())
                {
                    // Skip drafts and prereleases
                    if (release.TryGetProperty("draft", out var draftElem) && draftElem.GetBoolean())
                        continue;
                    if (release.TryGetProperty("prerelease", out var preElem) && preElem.GetBoolean())
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
                    }
                }

                if (bestRelease == null || bestVersion == null)
                {
                    result.IsSuccess = true;
                    result.HasUpdate = false;
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
                    ? $"https://github.com/{RepoOwner}/{RepoName}/releases"
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
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
                // Ignore if browser launch fails
            }
        }
    }
}
