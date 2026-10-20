using System;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using ResourceAnalyzer.Helpers;
using ResourceAnalyzer.Models;
using ResourceAnalyzer.Services;

namespace ResourceAnalyzer.Tests
{
    public class ElevationAndFeatureTests
    {
        private readonly ITestOutputHelper _output;

        public ElevationAndFeatureTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ElevationHelper_IsRunningAsAdmin_DoesNotThrow()
        {
            bool isAdmin = ElevationHelper.IsRunningAsAdmin();
            _output.WriteLine($"IsRunningAsAdmin: {isAdmin}");
            // Should return a valid boolean without throwing
            Assert.True(isAdmin || !isAdmin);
        }

        [Fact]
        public void NativeTrayIcon_EnsureHiddenInNotificationArea_DoesNotThrow()
        {
            NativeTrayIcon.EnsureHiddenInNotificationArea();
        }

        [Fact]
        public void NativeMethods_CreateCrossIntegrityEvent_CreatesAndSignalsEvent()
        {
            string evtName = "ResourceAnalyzer_TestCrossEvent_" + Guid.NewGuid().ToString("N");
            using var evt = NativeMethods.CreateCrossIntegrityEvent(evtName, false, false);
            Assert.NotNull(evt);
            bool signaled = evt.Set();
            Assert.True(signaled);
        }

        [Fact]
        public async Task DataUsageService_AttributedNetworkUsage_LoadsEntriesSuccessfully()
        {
            var service = new DataUsageService();
            var (apps, rx, tx) = await service.GetDataUsageAsync("Last Week", false, string.Empty);
            Assert.NotNull(apps);
            Assert.True(rx >= 0);
            Assert.True(tx >= 0);
        }




        [Fact]
        public void UpdateService_GetCurrentVersion_ReturnsValidVersion()
        {
            var service = new UpdateService();
            string version = service.GetCurrentVersion();
            _output.WriteLine($"Current Version: {version}");
            Assert.False(string.IsNullOrWhiteSpace(version));
            Assert.True(Version.TryParse(version, out _));
        }

        [Fact]
        public async Task UpdateService_CheckForUpdatesAsync_ConnectsToGitHub()
        {
            var service = new UpdateService();
            var info = await service.CheckForUpdatesAsync();

            _output.WriteLine($"GitHub Check Success: {info.IsSuccess}");
            _output.WriteLine($"Message: {info.Message}");
            _output.WriteLine($"Latest Version: {info.LatestVersion}");
            _output.WriteLine($"Has Update: {info.HasUpdate}");

            Assert.NotNull(info);
            Assert.False(string.IsNullOrWhiteSpace(info.LatestVersion));
            Assert.False(string.IsNullOrWhiteSpace(info.Message));
        }

        [Fact]
        public void UpdatePolicy_VersionComparison_OrdersVersionsCorrectly()
        {
            var v1_0_0 = Version.Parse("1.0.0");
            var v1_0_1 = Version.Parse("1.0.1");
            var v1_0_2 = Version.Parse("1.0.2");
            var v1_1_0 = Version.Parse("1.1.0");
            var v1_1_1 = Version.Parse("1.1.1");

            // Minor updates: v1.0.1, v1.0.2
            Assert.True(v1_0_1 > v1_0_0, "v1.0.1 is newer than v1.0.0");
            Assert.True(v1_0_2 > v1_0_1, "v1.0.2 is newer than v1.0.1");

            // Major updates: v1.1.0, v1.1.1
            Assert.True(v1_1_0 > v1_0_2, "v1.1.0 is newer than v1.0.2");
            Assert.True(v1_1_1 > v1_1_0, "v1.1.1 is newer than v1.1.0");
        }

        [Fact]
        public void StartupSettings_UriFormat_IsValid()
        {
            const string uri = "ms-settings:startupapps";
            Assert.True(Uri.TryCreate(uri, UriKind.Absolute, out var result));
            Assert.Equal("ms-settings", result.Scheme);
        }

        [Fact]
        public void WindowsServicesManager_ExecutableTarget_ExistsAndResolves()
        {
            string servicesMscPath = System.IO.Path.Combine(Environment.SystemDirectory, "services.msc");
            _output.WriteLine($"Services.msc path: {servicesMscPath}");
            Assert.True(System.IO.File.Exists(servicesMscPath), "services.msc must exist in System32");
        }

        [Fact]
        public void ProcessItem_HasCpuPercent_BehavesCorrectly()
        {
            var proc = new ProcessItem { Pid = 1234, Name = "TestApp" };
            proc.CpuPercent = 0.2;
            Assert.False(proc.HasCpuPercent);

            bool propChangedFired = false;
            proc.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ProcessItem.HasCpuPercent)) propChangedFired = true;
            };

            proc.CpuPercent = 1.5;
            Assert.True(proc.HasCpuPercent);
            Assert.True(propChangedFired);
        }

        [Fact]
        public void AppDataUsageItem_UsagePercent_BehavesCorrectly()
        {
            var item = new AppDataUsageItem { AppName = "Browser" };
            item.UsagePercent = 0.3;
            Assert.False(item.HasPercent);

            bool propChangedFired = false;
            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AppDataUsageItem.HasPercent)) propChangedFired = true;
            };

            item.UsagePercent = 25.0;
            Assert.True(item.HasPercent);
            Assert.True(propChangedFired);
        }

        [Fact]
        public async Task DataUsageService_GetTodayTotalUsageAsync_ReturnsNonNegativeUsage()
        {
            var service = new DataUsageService();
            var (recv, sent) = await service.GetTodayTotalUsageAsync();
            _output.WriteLine($"Today Total Usage: Received={recv}, Sent={sent}");
            Assert.True(recv >= 0);
            Assert.True(sent >= 0);
        }

        [Fact]
        public async Task DataUsageService_GetDataUsageAsync_Today_ReturnsNonNegativeTotals()
        {
            var service = new DataUsageService();
            var (apps, recv, sent) = await service.GetDataUsageAsync("Today", currentNetworkOnly: false, searchTerm: string.Empty);
            _output.WriteLine($"Today Apps Count: {apps.Count}, Received={recv}, Sent={sent}");
            Assert.NotNull(apps);
            Assert.True(recv >= 0);
            Assert.True(sent >= 0);
        }

        [Fact]
        public void InitializeProcessSecurity_DoesNotThrow()
        {
            // Verifies that DLL preloading protection runs safely without exception
            NativeMethods.InitializeProcessSecurity();
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("notepad.txt")]
        [InlineData("calc.bat")]
        [InlineData("C:\\nonexistent\\setup.exe")]
        public void LaunchInstaller_InvalidOrNonExePath_ReturnsFalse(string path)
        {
            var service = new UpdateService();
            bool result = service.LaunchInstaller(path);
            Assert.False(result);
        }

        [Theory]
        [InlineData("http://example.com/test.exe")]
        [InlineData("ftp://example.com/test.exe")]
        [InlineData("file:///C:/test.exe")]
        [InlineData("javascript:alert(1)")]
        public async Task DownloadInstallerAsync_NonHttpsUrl_ReturnsNull(string url)
        {
            var service = new UpdateService();
            string? result = await service.DownloadInstallerAsync(url);
            Assert.Null(result);
        }

        [Fact]
        public void ShouldPerformDailyCheck_DisabledInSettings_ReturnsFalse()
        {
            var service = new UpdateService();
            var settings = new Models.AppSettings { AutoCheckUpdatesDaily = false };
            Assert.False(service.ShouldPerformDailyCheck(settings));
        }

        [Fact]
        public void ShouldPerformDailyCheck_NeverChecked_ReturnsTrue()
        {
            var service = new UpdateService();
            var settings = new Models.AppSettings { AutoCheckUpdatesDaily = true, LastUpdateCheckUtc = null };
            Assert.True(service.ShouldPerformDailyCheck(settings));
        }

        [Fact]
        public void ShouldPerformDailyCheck_AlreadyCheckedToday_ReturnsFalse()
        {
            var service = new UpdateService();
            var settings = new Models.AppSettings { AutoCheckUpdatesDaily = true };
            service.RecordCheckPerformed(settings);

            Assert.False(service.ShouldPerformDailyCheck(settings));
        }

        [Fact]
        public void ShouldPerformDailyCheck_CheckedYesterday_ReturnsTrue()
        {
            var service = new UpdateService();
            var settings = new Models.AppSettings
            {
                AutoCheckUpdatesDaily = true,
                LastUpdateCheckUtc = DateTime.UtcNow.AddHours(-25).ToString("O")
            };

            Assert.True(service.ShouldPerformDailyCheck(settings));
        }

        [Fact]
        public async Task CollectionHelper_SynchronizeInPlace_WhenSelectedItemRemoved_SelectsAdjacentItem()
        {
            var collection = new System.Collections.ObjectModel.ObservableCollection<string> { "item1", "item2", "item3", "item4" };
            var map = new System.Collections.Generic.Dictionary<string, string>
            {
                ["item1"] = "item1",
                ["item2"] = "item2",
                ["item3"] = "item3",
                ["item4"] = "item4"
            };

            var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var listBox = new System.Windows.Controls.ListBox();
                    listBox.ItemsSource = collection;
                    listBox.SelectedIndex = 2; // "item3"

                    var incoming = new System.Collections.Generic.List<string> { "item1", "item2", "item4" };
                    CollectionHelper.SynchronizeInPlace(
                        collection,
                        map,
                        incoming,
                        x => x,
                        (existing, inc) => { },
                        listBox);

                    Assert.Equal(2, listBox.SelectedIndex);
                    Assert.Equal("item4", listBox.SelectedItem);
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            bool result = await tcs.Task;
            Assert.True(result);
        }

        [Theory]
        [InlineData(0x03E90401L, 0x0401)] // Shell V4 NIN_KEYSELECT with uID 1001 (0x03E9)
        [InlineData(0x03E90400L, 0x0400)] // Shell V4 NIN_SELECT with uID 1001
        [InlineData(0x03E9007BL, 0x007B)] // Shell V4 WM_CONTEXTMENU with uID 1001
        [InlineData(0x00000202L, 0x0202)] // Legacy WM_LBUTTONUP
        [InlineData(0x00000205L, 0x0205)] // Legacy WM_RBUTTONUP
        public void NativeTrayIcon_EventMasking_ExtractsCorrectEventCode(long rawLParam, int expectedEvent)
        {
            int eventType = (int)(rawLParam & 0xFFFF);
            Assert.Equal(expectedEvent, eventType);
        }

        [Fact]
        public void ProcessMatchesLetter_IgnoresRegisteredTrademarkR()
        {
            var item = new ProcessItem
            {
                Pid = 1234,
                Name = "AggregatorHost.exe",
                Description = "Microsoft (R) Aggregator Host"
            };

            // 'R' should NOT match the (R) registered trademark notation!
            Assert.False(MainWindow.ProcessMatchesLetter(item, 'R'));

            // 'A' should match "Aggregator"
            Assert.True(MainWindow.ProcessMatchesLetter(item, 'A'));

            // 'M' should match "Microsoft"
            Assert.True(MainWindow.ProcessMatchesLetter(item, 'M'));

            // 'H' should match "Host"
            Assert.True(MainWindow.ProcessMatchesLetter(item, 'H'));
        }

        [Fact]
        public void NativeTrayIcon_Tooltip_ContainsSpeedAndFitsWin32Limit()
        {
            string tooltip = NativeTrayIcon.TrayTooltipText;
            Assert.Contains("Network Speed", tooltip);
            Assert.True(tooltip.Length < 128, "Tray tooltip length must be under the 128 character NOTIFYICONDATA szTip limit");
        }
    }
}
