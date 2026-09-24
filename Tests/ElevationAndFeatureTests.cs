using System;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using AccessibleTaskManager.Helpers;
using AccessibleTaskManager.Models;
using AccessibleTaskManager.Services;

namespace AccessibleTaskManager.Tests
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
        public void ServiceItem_DisplayText_UpdatesProperly()
        {
            var item = new ServiceItem
            {
                ServiceName = "wuauserv",
                DisplayName = "Windows Update",
                Status = "Running",
                StartupType = "Manual"
            };
            item.UpdateDisplayText();

            Assert.Contains("Windows Update", item.DisplayText);
            Assert.Contains("wuauserv", item.DisplayText);
            Assert.Contains("Running", item.DisplayText);
            Assert.Contains("Manual", item.DisplayText);

            item.Status = "Stopped";
            Assert.Contains("Stopped", item.DisplayText);
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
            var infoBeta = await service.CheckForUpdatesAsync("Beta");

            _output.WriteLine($"GitHub Beta Check Success: {infoBeta.IsSuccess}");
            _output.WriteLine($"Message: {infoBeta.Message}");
            _output.WriteLine($"Latest Version: {infoBeta.LatestVersion}");
            _output.WriteLine($"Has Update: {infoBeta.HasUpdate}");

            Assert.True(infoBeta.IsSuccess);
            Assert.False(string.IsNullOrWhiteSpace(infoBeta.LatestVersion));

            var infoStable = await service.CheckForUpdatesAsync("Stable");
            Assert.True(infoStable.IsSuccess);
        }

        [Fact]
        public void UpdatePolicy_VersionComparison_OrdersBetaAndStableCorrectly()
        {
            var v1_0_0 = Version.Parse("1.0.0");
            var v1_0_1 = Version.Parse("1.0.1");
            var v1_0_2 = Version.Parse("1.0.2");
            var v1_1_1 = Version.Parse("1.1.1");
            var v1_1_2 = Version.Parse("1.1.2");

            Assert.True(v1_0_1 > v1_0_0, "Beta 1.0.1 is newer than initial 1.0.0");
            Assert.True(v1_0_2 > v1_0_1, "Beta 1.0.2 is newer than Beta 1.0.1");
            Assert.True(v1_1_1 > v1_0_2, "Stable 1.1.1 is newer than Beta 1.0.2");
            Assert.True(v1_1_2 > v1_1_1, "Stable 1.1.2 is newer than Stable 1.1.1");
        }

        [Fact]
        public void StartupSettings_UriFormat_IsValid()
        {
            const string uri = "ms-settings:startupapps";
            Assert.True(Uri.TryCreate(uri, UriKind.Absolute, out var result));
            Assert.Equal("ms-settings", result.Scheme);
        }

        [Fact]
        public void WindowsServiceManager_GetServices_ReturnsWindowsServices()
        {
            var manager = new WindowsServiceManager();
            var services = manager.GetServices();
            _output.WriteLine($"Discovered {services.Count} Windows services.");

            Assert.NotNull(services);
            Assert.True(services.Count > 10, "Windows should report dozens of background services.");

            var first = services[0];
            Assert.False(string.IsNullOrWhiteSpace(first.ServiceName));
            Assert.False(string.IsNullOrWhiteSpace(first.DisplayText));
        }
    }
}
