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
        public string ReleaseName { get; set; } = "";
        public string ReleaseNotes { get; set; } = "";
        public string HtmlUrl { get; set; } = "";
        public string? DownloadUrl { get; set; }
        public string Message { get; set; } = "";
    }

    public interface IUpdateService
    {
        Task<UpdateInfo> CheckForUpdatesAsync();
        string GetCurrentVersion();
        void OpenUrl(string url);
    }

    public class UpdateService : IUpdateService
    {
        private const string RepoOwner = "AakanshNahadiya";
        private const string RepoName = "Resource-Analyzer-for-Windows";
        private const string ApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

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

        public async Task<UpdateInfo> CheckForUpdatesAsync()
        {
            string currentVerStr = GetCurrentVersion();
            var result = new UpdateInfo
            {
                CurrentVersion = currentVerStr,
                LatestVersion = currentVerStr
            };

            try
            {
                using var response = await _httpClient.GetAsync(ApiUrl).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    result.IsSuccess = false;
                    result.Message = $"Could not retrieve update information (HTTP {response.StatusCode}).";
                    return result;
                }

                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" : "";
                string releaseName = root.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? "" : "";
                string body = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? "" : "";
                string htmlUrl = root.TryGetProperty("html_url", out var urlElem) ? urlElem.GetString() ?? "" : "";

                string cleanLatest = tagName.TrimStart('v', 'V').Trim();
                result.LatestVersion = cleanLatest;
                result.ReleaseName = releaseName;
                result.ReleaseNotes = body;
                result.HtmlUrl = string.IsNullOrWhiteSpace(htmlUrl)
                    ? $"https://github.com/{RepoOwner}/{RepoName}/releases"
                    : htmlUrl;

                // Look for setup executable in assets
                if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsElem.EnumerateArray())
                    {
                        if (asset.TryGetProperty("name", out var assetNameElem) &&
                            asset.TryGetProperty("browser_download_url", out var dlElem))
                        {
                            string assetName = assetNameElem.GetString() ?? "";
                            if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            {
                                result.DownloadUrl = dlElem.GetString();
                                break;
                            }
                        }
                    }
                }

                // Version comparison
                if (Version.TryParse(cleanLatest, out var latestVer) &&
                    Version.TryParse(currentVerStr, out var currentVer))
                {
                    result.HasUpdate = latestVer > currentVer;
                }
                else
                {
                    result.HasUpdate = string.Compare(cleanLatest, currentVerStr, StringComparison.OrdinalIgnoreCase) > 0;
                }

                result.IsSuccess = true;
                result.Message = result.HasUpdate
                    ? $"A new version ({cleanLatest}) is available! You are currently on {currentVerStr}."
                    : $"Resource Analyzer for Windows is up to date (Version {currentVerStr}).";

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
