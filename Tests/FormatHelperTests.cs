using Xunit;
using AccessibleTaskManager.Helpers;
using AccessibleTaskManager.Models;

namespace AccessibleTaskManager.Tests
{
    public class FormatHelperTests
    {
        [Fact]
        public void FormatBytes_Under1024MB_ReturnsMB()
        {
            // 512 MB
            long bytes = 512L * 1024 * 1024;
            string result = FormatHelper.FormatBytes(bytes);
            Assert.Equal("512.0 MB", result);
        }

        [Fact]
        public void FormatBytes_1023MB_ReturnsMB()
        {
            // 1023 MB
            long bytes = 1023L * 1024 * 1024;
            string result = FormatHelper.FormatBytes(bytes);
            Assert.Equal("1023.0 MB", result);
        }

        [Fact]
        public void FormatBytes_AtOrAbove1024MB_SwitchesToGB()
        {
            // 1024 MB = 1 GB
            long bytes = 1024L * 1024 * 1024;
            string result = FormatHelper.FormatBytes(bytes);
            Assert.Equal("1.00 GB", result);
        }

        [Fact]
        public void FormatBytes_HighRAMUsage_ReturnsGBWithDecimals()
        {
            // 5.5 GB
            long bytes = (long)(5.5 * 1024 * 1024 * 1024);
            string result = FormatHelper.FormatBytes(bytes);
            Assert.Equal("5.50 GB", result);
        }

        [Fact]
        public void FormatBytes_StorageAbove1024GB_SwitchesToTB()
        {
            // 2 TB
            long bytes = 2L * 1024 * 1024 * 1024 * 1024;
            string result = FormatHelper.FormatBytes(bytes);
            Assert.Equal("2.00 TB", result);
        }

        [Fact]
        public void FormatSpeed_Under1024KB_ReturnsKBps()
        {
            double speed = 450 * 1024;
            string result = FormatHelper.FormatSpeed(speed);
            Assert.Equal("450 KB/s", result);
        }

        [Fact]
        public void FormatSpeed_Above1024KB_ReturnsMBps()
        {
            double speed = 2.45 * 1024 * 1024;
            string result = FormatHelper.FormatSpeed(speed);
            Assert.Equal("2.45 MB/s", result);
        }

        [Fact]
        public void ProcessItem_FormatsDisplayTextCleanly()
        {
            ProcessItem.ShowExtension = true;
            ProcessItem.ShowPid = false;

            var item = new ProcessItem
            {
                Pid = 1234,
                Name = "chrome",
                MemoryBytes = 850L * 1024 * 1024, // 850 MB
                CpuPercent = 3.2
            };
            item.UpdateDisplayText();

            Assert.Equal("chrome.exe - RAM: 850.0 MB, CPU: 3.2%", item.DisplayText);

            ProcessItem.ShowPid = true;
            item.UpdateDisplayText();
            Assert.Equal("chrome.exe (PID: 1234) - RAM: 850.0 MB, CPU: 3.2%", item.DisplayText);
            Assert.Equal("chrome.exe (PID: 1234) - RAM: 850.0 MB, CPU: 3.2%", item.ToString());
        }

        [Theory]
        [InlineData("", "System & Deleted Applications")]
        [InlineData("   ", "System & Deleted Applications")]
        [InlineData(@"\device\harddiskvolume3\windows\explorer.exe", "explorer")]
        [InlineData(@"\device\harddiskvolume3\program files\google\chrome\application\chrome.exe", "chrome")]
        [InlineData("Microsoft.WindowsNotepad_8wekyb3d8bbwe", "WindowsNotepad")]
        [InlineData("ProtonPass_158qdr94jw63p", "ProtonPass")]
        [InlineData(@"System\IPv6 Control Message", "IPv6 Control Message")]
        [InlineData("System", "System")]
        public void CleanAppName_ExtractsFriendlyNames(string rawId, string expected)
        {
            string actual = AppDataUsageItem.CleanAppName(rawId);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void AppSettings_Defaults_AreStandard()
        {
            var s = new AppSettings();

            Assert.True(s.HideSystemProcesses);
            Assert.True(s.ShowProcessExtension);
            Assert.False(s.ShowProcessPid);
            Assert.True(s.ConfirmBeforeEndTask);
            Assert.True(s.MinimizeToTray);
            Assert.True(s.StartWithWindows);
            Assert.False(s.RememberSortFilter);
            Assert.Equal("System Default", s.Theme);
            Assert.Equal(2, s.RefreshIntervalSeconds);
            Assert.True(s.ShowCpu);
            Assert.True(s.ShowRam);
            Assert.True(s.ShowGpu);
            Assert.True(s.ShowNetwork);
            Assert.True(s.ShowDisk);
            Assert.True(s.ShowBattery);
            Assert.False(s.EnableHighRamAlert);
            Assert.False(s.EnableHighCpuAlert);
        }

        [Fact]
        public void AppSettings_Serialization_RoundTripsSuccessfully()
        {
            var original = new AppSettings
            {
                Theme = "Dark",
                RefreshIntervalSeconds = 5,
                HideSystemProcesses = false,
                ShowProcessExtension = false,
                ShowProcessPid = true,
                ConfirmBeforeEndTask = false,
                MinimizeToTray = false,
                StartWithWindows = true,
                RememberSortFilter = true,
                EnableHighRamAlert = true,
                HighRamLimitValue = 16,
                HighRamLimitUnit = "GB",
                EnableHighCpuAlert = true,
                HighCpuLimitPercent = 85.5,
                ShowGpu = false,
                ShowBattery = false
            };

            string json = System.Text.Json.JsonSerializer.Serialize(original);
            var deserialized = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

            Assert.NotNull(deserialized);
            Assert.Equal("Dark", deserialized.Theme);
            Assert.Equal(5, deserialized.RefreshIntervalSeconds);
            Assert.False(deserialized.HideSystemProcesses);
            Assert.False(deserialized.ShowProcessExtension);
            Assert.True(deserialized.ShowProcessPid);
            Assert.False(deserialized.ConfirmBeforeEndTask);
            Assert.False(deserialized.MinimizeToTray);
            Assert.True(deserialized.StartWithWindows);
            Assert.True(deserialized.RememberSortFilter);
            Assert.True(deserialized.EnableHighRamAlert);
            Assert.Equal(16, deserialized.HighRamLimitValue);
            Assert.Equal("GB", deserialized.HighRamLimitUnit);
            Assert.True(deserialized.EnableHighCpuAlert);
            Assert.Equal(85.5, deserialized.HighCpuLimitPercent);
            Assert.False(deserialized.ShowGpu);
            Assert.False(deserialized.ShowBattery);
        }
    }
}

