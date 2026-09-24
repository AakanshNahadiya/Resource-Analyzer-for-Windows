using System.Linq;
using System.Threading.Tasks;
using AccessibleTaskManager;
using AccessibleTaskManager.Models;
using AccessibleTaskManager.Services;
using Xunit;

namespace AccessibleTaskManager.Tests
{
    public class ProcessServiceTests
    {
        [Fact]
        public async Task GetProcessesAsync_WhenHideSystemProcesses_HidesSvchostAndKernel()
        {
            var service = new ProcessService();
            var processes = await service.GetProcessesAsync(searchTerm: string.Empty, sortBy: "Memory", hideSystemProcesses: true);

            Assert.NotEmpty(processes);
            Assert.DoesNotContain(processes, p => p.Pid == 0 || p.Pid == 4);
            Assert.DoesNotContain(processes, p => p.Name.Equals("svchost", System.StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(processes, p => p.Name.Equals("dwm", System.StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(processes, p => p.Name.Equals("sihost", System.StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(processes, p => p.Name.Equals("RuntimeBroker", System.StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(processes, p => p.Name.Equals("audiodg", System.StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(processes, p => p.DisplayName.Equals("audiodg.exe", System.StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData(0, "Idle")]
        [InlineData(4, "System")]
        [InlineData(100, "dwm")]
        [InlineData(200, "csrss")]
        [InlineData(300, "lsass")]
        [InlineData(400, "winlogon")]
        public async Task KillProcessAsync_ProtectsCriticalKernelProcesses(int pid, string name)
        {
            var service = new ProcessService();
            var (success, msg) = await service.KillProcessAsync(pid, name);

            Assert.False(success);
            Assert.Contains("critical Windows system process", msg);
        }

        [Fact]
        public void ProcessItem_DisplayName_IncludesExeExtension()
        {
            ProcessItem.ShowExtension = true;
            var item = new Models.ProcessItem { Name = "chrome", Pid = 1234, MemoryBytes = 104857600, CpuPercent = 1.5 };
            Assert.Equal("chrome.exe", item.DisplayName);

            // Should not duplicate if already ending with .exe
            var itemWithExe = new Models.ProcessItem { Name = "notepad.exe", Pid = 5678 };
            Assert.Equal("notepad.exe", itemWithExe.DisplayName);

            ProcessItem.ShowExtension = false;
            Assert.Equal("chrome", item.DisplayName);
        }

        [Fact]
        public void ProcessItem_DisplayText_HidesPidWhenConfiguredForCleanSpeech()
        {
            ProcessItem.ShowExtension = true;
            ProcessItem.ShowPid = false;

            var item = new Models.ProcessItem { Name = "ResourceAnalyzer", Pid = 9999, MemoryBytes = 52428800, CpuPercent = 0.5 };
            item.UpdateDisplayText();

            Assert.DoesNotContain("PID", item.DisplayText);
            Assert.StartsWith("ResourceAnalyzer.exe - RAM:", item.DisplayText);
        }

        [Fact]
        public void ProcessItem_DisplayText_IncludesPidWhenConfigured()
        {
            ProcessItem.ShowExtension = true;
            ProcessItem.ShowPid = true;

            var item = new Models.ProcessItem { Name = "ResourceAnalyzer", Pid = 9999, MemoryBytes = 52428800, CpuPercent = 0.5 };
            item.UpdateDisplayText();

            Assert.Contains("(PID: 9999)", item.DisplayText);
            Assert.StartsWith("ResourceAnalyzer.exe (PID: 9999) - RAM:", item.DisplayText);
        }

        [Fact]
        public void Test_NativeProcessSnapshot_EnumeratesSuccessfully()
        {
            var service = new ProcessService();
            var list = service.GetProcessesNative();
            Assert.NotEmpty(list);
            int currentPid = System.Diagnostics.Process.GetCurrentProcess().Id;
            var current = list.FirstOrDefault(p => p.Pid == currentPid);
            Assert.True(current.Pid > 0);
            Assert.True(current.MemoryBytes > 0);
        }

        [Fact]
        public void ProcessItem_DisplayText_IncludesInstanceCountWhenMultiple()
        {
            ProcessItem.ShowExtension = true;
            ProcessItem.ShowPid = false;
            var item = new Models.ProcessItem
            {
                Name = "chrome",
                Pid = 1001,
                MemoryBytes = 104857600,
                CpuPercent = 0.5,
                InstanceIndex = 1,
                InstanceTotal = 6
            };
            item.UpdateDisplayText();

            Assert.Equal("chrome.exe (1 of 6) - RAM: 100.0 MB, CPU: 0.5%", item.DisplayText);
        }

        [Fact]
        public void ProcessItem_DisplayText_OmitsInstanceCountWhenSingle()
        {
            ProcessItem.ShowExtension = true;
            ProcessItem.ShowPid = false;
            var item = new Models.ProcessItem
            {
                Name = "ResourceAnalyzer",
                Pid = 2001,
                MemoryBytes = 52428800,
                CpuPercent = 0.1,
                InstanceIndex = 1,
                InstanceTotal = 1
            };
            item.UpdateDisplayText();

            Assert.Equal("ResourceAnalyzer.exe - RAM: 50.0 MB, CPU: 0.1%", item.DisplayText);
        }

        [Fact]
        public void ProcessItem_GroupHeader_DisplaysAggregatedMetricsAndState()
        {
            ProcessItem.ShowExtension = true;
            var header = new Models.ProcessItem
            {
                Name = "chrome",
                GroupName = "chrome",
                IsGroupHeader = true,
                IsExpanded = false,
                InstanceTotal = 10,
                MemoryBytes = 1288490188, // ~1.2 GB
                CpuPercent = 3.5,
                GroupChildPids = new System.Collections.Generic.List<int> { 101, 102, 103 }
            };
            header.UpdateDisplayText();

            Assert.Equal("chrome.exe (10 instances, collapsed) - RAM: 1.20 GB, CPU: 3.5%", header.DisplayText);
            Assert.Equal("group_chrome", header.ItemKey);

            // Expand header
            header.IsExpanded = true;
            header.UpdateDisplayText();
            Assert.Equal("chrome.exe (10 instances, expanded) - RAM: 1.20 GB, CPU: 3.5%", header.DisplayText);
        }

        [Fact]
        public void ProcessItem_GroupChild_DisplaysIndentedPIDAndMetrics()
        {
            ProcessItem.ShowExtension = true;
            var child = new Models.ProcessItem
            {
                Name = "chrome",
                GroupName = "chrome",
                Pid = 1240,
                IsGroupChild = true,
                MemoryBytes = 104857600, // 100 MB
                CpuPercent = 1.0
            };
            child.UpdateDisplayText();

            Assert.Equal("  chrome.exe (PID: 1240) - RAM: 100.0 MB, CPU: 1.0%", child.DisplayText);
            Assert.Equal("proc_1240", child.ItemKey);
        }

        [Fact]
        public void AppSettings_Default_HasGroupProcessesEnabled()
        {
            var settings = new Models.AppSettings();
            Assert.True(settings.GroupProcesses);
        }

        [Fact]
        public void BuildDisplayList_AggregatesMultiInstanceProcesses_WhenCollapsed()
        {
            var rawList = new System.Collections.Generic.List<ProcessItem>
            {
                new() { Name = "chrome", Pid = 101, MemoryBytes = 100 * 1024 * 1024, CpuPercent = 1.0 },
                new() { Name = "chrome", Pid = 102, MemoryBytes = 200 * 1024 * 1024, CpuPercent = 2.0 },
                new() { Name = "notepad", Pid = 201, MemoryBytes = 50 * 1024 * 1024, CpuPercent = 0.5 }
            };

            var expandedGroups = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            var display = MainWindow.BuildDisplayList(rawList, "Memory", groupProcesses: true, expandedGroups);

            // chrome (2 instances aggregated to 300 MB) + notepad (1 instance) = 2 items
            Assert.Equal(2, display.Count);

            var first = display[0];
            Assert.True(first.IsGroupHeader);
            Assert.Equal("chrome", first.Name);
            Assert.Equal(2, first.InstanceTotal);
            Assert.Equal(300 * 1024 * 1024, first.MemoryBytes);
            Assert.Equal(3.0, first.CpuPercent, 1);
            Assert.False(first.IsExpanded);

            var second = display[1];
            Assert.False(second.IsGroupHeader);
            Assert.Equal("notepad", second.Name);
        }

        [Fact]
        public void BuildDisplayList_ShowsChildrenUnderHeader_WhenExpanded()
        {
            var rawList = new System.Collections.Generic.List<ProcessItem>
            {
                new() { Name = "chrome", Pid = 101, MemoryBytes = 100 * 1024 * 1024, CpuPercent = 1.0 },
                new() { Name = "chrome", Pid = 102, MemoryBytes = 200 * 1024 * 1024, CpuPercent = 2.0 },
                new() { Name = "notepad", Pid = 201, MemoryBytes = 50 * 1024 * 1024, CpuPercent = 0.5 }
            };

            var expandedGroups = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "chrome"
            };

            var display = MainWindow.BuildDisplayList(rawList, "Memory", groupProcesses: true, expandedGroups);

            // chrome header + 2 chrome children + notepad = 4 items
            Assert.Equal(4, display.Count);

            Assert.True(display[0].IsGroupHeader);
            Assert.Equal("chrome", display[0].Name);
            Assert.True(display[0].IsExpanded);

            // Children directly follow header, sorted by Memory descending
            Assert.True(display[1].IsGroupChild);
            Assert.Equal(102, display[1].Pid);
            Assert.Equal(200 * 1024 * 1024, display[1].MemoryBytes);

            Assert.True(display[2].IsGroupChild);
            Assert.Equal(101, display[2].Pid);
            Assert.Equal(100 * 1024 * 1024, display[2].MemoryBytes);

            // Notepad follows
            Assert.False(display[3].IsGroupHeader);
            Assert.False(display[3].IsGroupChild);
            Assert.Equal("notepad", display[3].Name);
        }

        [Fact]
        public void BuildDisplayList_ReturnsFlatList_WhenGroupingDisabled()
        {
            var rawList = new System.Collections.Generic.List<ProcessItem>
            {
                new() { Name = "chrome", Pid = 101, MemoryBytes = 100 * 1024 * 1024, CpuPercent = 1.0 },
                new() { Name = "chrome", Pid = 102, MemoryBytes = 200 * 1024 * 1024, CpuPercent = 2.0 }
            };

            var expandedGroups = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var display = MainWindow.BuildDisplayList(rawList, "Memory", groupProcesses: false, expandedGroups);

            Assert.Equal(2, display.Count);
            Assert.DoesNotContain(display, p => p.IsGroupHeader);
        }

        [Fact]
        public void TrimProcessMemory_ExecutesWithoutException()
        {
            MainWindow.TrimProcessMemory();
        }
    }
}
