using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using ResourceAnalyzer.Models;
using ResourceAnalyzer.Services;

namespace ResourceAnalyzer.Tests
{
    public class DataUsageServiceTests
    {
        [Theory]
        [InlineData(null, "System")]
        [InlineData("", "System")]
        [InlineData("   ", "System")]
        [InlineData(@"System\IPv6 Control Message", "IPv6 Control Message")]
        [InlineData(@"System\DNS", "DNS")]
        [InlineData(@"\device\harddiskvolume3\program files\google\chrome\application\chrome.exe", "chrome")]
        [InlineData(@"\device\harddiskvolume3\windows\system32\svchost.exe", "svchost")]
        [InlineData(@"C:\Program Files\App\my_tool.exe", "my_tool")]
        [InlineData("notepad.exe", "notepad")]
        [InlineData("Microsoft.WindowsTerminal_8wekyb3d8bbwe", "WindowsTerminal")]
        [InlineData("Microsoft.Windows.Search_cw5n1h2txyewy", "Windows.Search")]
        [InlineData("SpotifyAB.SpotifyMusic_zpd5n617xx0vf", "SpotifyAB.SpotifyMusic")]
        public void CleanAppName_NormalizesIdentifiersCorrectly(string? rawId, string expected)
        {
            string actual = AppDataUsageItem.CleanAppName(rawId!);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void AppDataUsageItem_CalculatesTotalsAndFormatting()
        {
            var item = new AppDataUsageItem
            {
                AppName = "Chrome",
                BytesReceived = 1048576, // 1 MB
                BytesSent = 524288,     // 512 KB
                UsagePercent = 45.2
            };

            Assert.Equal(1572864, item.TotalBytes);
            Assert.Contains("1.0 MB", item.ReceivedFormatted);
            Assert.Contains("512 KB", item.SentFormatted);
            Assert.Contains("1.5 MB", item.TotalFormatted);
            Assert.True(item.HasPercent);
            Assert.Contains("Chrome", item.DisplayText);
            Assert.Contains("Total: 1.5 MB", item.DisplayText);
        }

        [Fact]
        public void AppDataUsageItem_PropertyChangeNotifications_FireExpectedEvents()
        {
            var item = new AppDataUsageItem();
            var changedProperties = new HashSet<string>();
            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != null)
                {
                    changedProperties.Add(e.PropertyName);
                }
            };

            item.AppName = "Firefox";
            Assert.Contains(nameof(AppDataUsageItem.AppName), changedProperties);
            Assert.Contains(nameof(AppDataUsageItem.DisplayText), changedProperties);

            changedProperties.Clear();
            item.BytesReceived = 2048;
            Assert.Contains(nameof(AppDataUsageItem.BytesReceived), changedProperties);
            Assert.Contains(nameof(AppDataUsageItem.TotalBytes), changedProperties);
            Assert.Contains(nameof(AppDataUsageItem.TotalFormatted), changedProperties);
            Assert.Contains(nameof(AppDataUsageItem.ReceivedFormatted), changedProperties);

            changedProperties.Clear();
            item.UsagePercent = 12.5;
            Assert.Contains(nameof(AppDataUsageItem.UsagePercent), changedProperties);
            Assert.Contains(nameof(AppDataUsageItem.HasPercent), changedProperties);
        }

        [Fact]
        public async Task DataUsageService_GetTodayTotalUsageAsync_ExecutesSafely()
        {
            var service = new DataUsageService();
            var (recv, sent) = await service.GetTodayTotalUsageAsync();

            Assert.True(recv >= 0, "Received bytes must be non-negative.");
            Assert.True(sent >= 0, "Sent bytes must be non-negative.");
        }

        [Theory]
        [InlineData("Today")]
        [InlineData("Last 24 Hours")]
        [InlineData("Last Week")]
        [InlineData("Last Month")]
        [InlineData("Full")]
        public async Task DataUsageService_GetDataUsageAsync_AllTimeRanges_ExecuteSafely(string timeRange)
        {
            var service = new DataUsageService();
            var (apps, recv, sent) = await service.GetDataUsageAsync(timeRange, currentNetworkOnly: false, searchTerm: string.Empty);

            Assert.NotNull(apps);
            Assert.True(recv >= 0);
            Assert.True(sent >= 0);

            // Verify sorting order: TotalBytes descending
            for (int i = 0; i < apps.Count - 1; i++)
            {
                Assert.True(apps[i].TotalBytes >= apps[i + 1].TotalBytes, "Items should be sorted descending by TotalBytes.");
            }
        }

        [Fact]
        public async Task DataUsageService_GetDataUsageAsync_CurrentNetworkOnly_ExecutesSafely()
        {
            var service = new DataUsageService();
            var (apps, recv, sent) = await service.GetDataUsageAsync("Last Week", currentNetworkOnly: true, searchTerm: string.Empty);

            Assert.NotNull(apps);
            Assert.True(recv >= 0);
            Assert.True(sent >= 0);
        }

        [Fact]
        public async Task DataUsageService_GetDataUsageAsync_SearchFilter_AppliesCorrectly()
        {
            var service = new DataUsageService();
            // Search for an unlikely string
            var (apps, _, _) = await service.GetDataUsageAsync("Full", currentNetworkOnly: false, searchTerm: "NonExistentApplicationZ9999");

            Assert.NotNull(apps);
            Assert.Empty(apps);
        }
    }
}
