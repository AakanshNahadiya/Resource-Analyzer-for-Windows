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
        public void AppSettings_HideBatteryDisclaimer_DefaultsToFalse()
        {
            var settings = new AppSettings();
            Assert.False(settings.HideBatteryDisclaimer);
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
        public void BatteryUsageItem_HasPercent_BehavesCorrectly()
        {
            var item = new BatteryUsageItem { DrainPercent = 10.0, IsCharging = false };
            Assert.True(item.HasPercent);

            item.IsCharging = true;
            Assert.False(item.HasPercent);

            item.IsCharging = false;
            item.DrainPercent = 0.2;
            Assert.False(item.HasPercent);
        }

        [Fact]
        public void AppBatteryUsageItem_HasPercent_BehavesCorrectly()
        {
            var item = new AppBatteryUsageItem { ProcessName = "Test" };
            item.BatteryPercent = 0.1;
            Assert.False(item.HasPercent);

            bool propChangedFired = false;
            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AppBatteryUsageItem.HasPercent)) propChangedFired = true;
            };

            item.BatteryPercent = 4.2;
            Assert.True(item.HasPercent);
            Assert.True(propChangedFired);
        }
    }
}
