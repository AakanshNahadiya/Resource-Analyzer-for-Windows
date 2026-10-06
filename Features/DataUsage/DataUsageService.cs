using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
using ResourceAnalyzer.Models;

namespace ResourceAnalyzer.Services
{
    public interface IDataUsageService
    {
        Task<(List<AppDataUsageItem> Apps, long TotalReceived, long TotalSent)> GetDataUsageAsync(
            string timeRange,
            bool currentNetworkOnly,
            string searchTerm);

        Task<(long TotalReceived, long TotalSent)> GetTodayTotalUsageAsync();
    }

    public class DataUsageService : IDataUsageService
    {
        public async Task<(long TotalReceived, long TotalSent)> GetTodayTotalUsageAsync()
        {
            return await Task.Run(async () =>
            {
                var now = DateTimeOffset.Now;
                var startOfDay = new DateTimeOffset(DateTime.Today);

                var states = new NetworkUsageStates
                {
                    Roaming = TriStates.DoNotCare,
                    Shared = TriStates.DoNotCare
                };

                long totalReceived = 0;
                long totalSent = 0;

                try
                {
                    var profiles = NetworkInformation.GetConnectionProfiles();
                    if (profiles != null)
                    {
                        foreach (var prof in profiles)
                        {
                            try
                            {
                                var usageList = await prof.GetNetworkUsageAsync(startOfDay, now, DataUsageGranularity.Total, states);
                                if (usageList != null)
                                {
                                    foreach (var u in usageList)
                                    {
                                        totalReceived += (long)u.BytesReceived;
                                        totalSent += (long)u.BytesSent;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                // Fallback to GetDataUsageAsync if GetNetworkUsageAsync returned 0 or had issues
                if (totalReceived == 0 && totalSent == 0)
                {
                    try
                    {
                        var (_, r, s) = await GetDataUsageAsync("Today", false, string.Empty);
                        return (r, s);
                    }
                    catch { }
                }

                return (totalReceived, totalSent);
            });
        }

        public async Task<(List<AppDataUsageItem> Apps, long TotalReceived, long TotalSent)> GetDataUsageAsync(
            string timeRange,
            bool currentNetworkOnly,
            string searchTerm)
        {
            return await Task.Run(async () =>
            {
                var now = DateTime.UtcNow;
                DateTime startTime = timeRange switch
                {
                    "Today" => DateTime.Today.ToUniversalTime(),
                    "Last 24 Hours" => now.AddHours(-24),
                    "Last Week" => now.AddDays(-7),
                    "Last Month" => now.AddDays(-30),
                    _ => now.AddYears(-1) // "Full"
                };

                var states = new NetworkUsageStates();
                var profilesToQuery = new List<ConnectionProfile>();

                try
                {
                    if (currentNetworkOnly)
                    {
                        var primary = NetworkInformation.GetInternetConnectionProfile();
                        if (primary != null)
                        {
                            profilesToQuery.Add(primary);
                        }
                    }
                    else
                    {
                        var all = NetworkInformation.GetConnectionProfiles();
                        if (all != null)
                        {
                            profilesToQuery.AddRange(all);
                        }
                    }
                }
                catch { }

                var appMap = new Dictionary<string, (long In, long Out, string Raw)>(StringComparer.OrdinalIgnoreCase);
                long grandTotalReceived = 0;
                long grandTotalSent = 0;

                foreach (var prof in profilesToQuery)
                {
                    try
                    {
                        // Query per-app usage
                        var usageList = await prof.GetAttributedNetworkUsageAsync(startTime, now, states);
                        if (usageList != null)
                        {
                            foreach (var u in usageList)
                            {
                                long bytesIn = (long)u.BytesReceived;
                                long bytesOut = (long)u.BytesSent;
                                if (bytesIn == 0 && bytesOut == 0) continue;

                                string cleanName = AppDataUsageItem.CleanAppName(u.AttributionId);

                                if (appMap.TryGetValue(cleanName, out var existing))
                                {
                                    appMap[cleanName] = (existing.In + bytesIn, existing.Out + bytesOut, existing.Raw);
                                }
                                else
                                {
                                    appMap[cleanName] = (bytesIn, bytesOut, u.AttributionId);
                                }

                                grandTotalReceived += bytesIn;
                                grandTotalSent += bytesOut;
                            }
                        }
                    }
                    catch { }
                }

                long grandTotal = grandTotalReceived + grandTotalSent;
                var list = appMap.Select(kvp =>
                {
                    long total = kvp.Value.In + kvp.Value.Out;
                    double pct = grandTotal > 0 ? (double)total * 100.0 / grandTotal : 0;
                    return new AppDataUsageItem
                    {
                        AppName = kvp.Key,
                        RawIdentifier = kvp.Value.Raw,
                        BytesReceived = kvp.Value.In,
                        BytesSent = kvp.Value.Out,
                        UsagePercent = pct
                    };
                }).ToList();

                // Search filtering
                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    string term = searchTerm.Trim();
                    list = list.Where(a => a.AppName.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                // Sort by TotalBytes descending
                list = list.OrderByDescending(a => a.TotalBytes).ToList();

                return (list, grandTotalReceived, grandTotalSent);
            });
        }
    }
}
