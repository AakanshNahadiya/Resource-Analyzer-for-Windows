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
            var info = await service.CheckForUpdatesAsync();

            _output.WriteLine($"GitHub Update Check Success: {info.IsSuccess}");
            _output.WriteLine($"Message: {info.Message}");
            _output.WriteLine($"Latest Version: {info.LatestVersion}");
            _output.WriteLine($"Has Update: {info.HasUpdate}");

            // Should successfully parse release from our live repo
            Assert.True(info.IsSuccess);
            Assert.False(string.IsNullOrWhiteSpace(info.LatestVersion));
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
