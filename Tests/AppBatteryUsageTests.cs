using System;
using System.Collections.Generic;
using System.Linq;
using AccessibleTaskManager.Models;
using AccessibleTaskManager.Services;
using Xunit;

namespace AccessibleTaskManager.Tests
{
    public class AppBatteryUsageTests
    {
        [Fact]
        public void GetLastAcDisconnectTime_TransitionsFromAc1ToAc0_ReturnsDisconnectTimestamp()
        {
            string xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<BatteryReport xmlns=""http://schemas.microsoft.com/battery/2012"">
  <Batteries>
    <Battery>
      <FullChargeCapacity>50000</FullChargeCapacity>
    </Battery>
  </Batteries>
  <RecentUsage>
    <UsageEntry Timestamp=""2026-09-25T08:00:00"" Ac=""1"" Discharge=""-5000"" />
    <UsageEntry Timestamp=""2026-09-25T09:00:00"" Ac=""1"" Discharge=""-2000"" />
    <UsageEntry Timestamp=""2026-09-25T09:36:00"" Ac=""0"" Discharge=""3000"" />
    <UsageEntry Timestamp=""2026-09-25T10:00:00"" Ac=""0"" Discharge=""4000"" />
  </RecentUsage>
</BatteryReport>";

            var disconnectTime = BatteryUsageService.GetLastAcDisconnectTime(xml);

            Assert.NotNull(disconnectTime);
            Assert.Equal(new DateTime(2026, 9, 25, 9, 36, 0), disconnectTime.Value);
        }

        [Fact]
        public void GetLastAcDisconnectTime_AllAc1_ReturnsNull()
        {
            string xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<BatteryReport xmlns=""http://schemas.microsoft.com/battery/2012"">
  <Batteries>
    <Battery><FullChargeCapacity>50000</FullChargeCapacity></Battery>
  </Batteries>
  <RecentUsage>
    <UsageEntry Timestamp=""2026-09-25T08:00:00"" Ac=""1"" Discharge=""-1000"" />
    <UsageEntry Timestamp=""2026-09-25T09:00:00"" Ac=""1"" Discharge=""-2000"" />
  </RecentUsage>
</BatteryReport>";

            var disconnectTime = BatteryUsageService.GetLastAcDisconnectTime(xml);
            Assert.Null(disconnectTime);
        }

        [Fact]
        public void ParseBatteryReportXml_SinceLastCharge_FiltersOnlyPostDisconnectSessions()
        {
            long ticks = 18_000_000_000L; // 30 mins
            string xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<BatteryReport xmlns=""http://schemas.microsoft.com/battery/2012"">
  <Batteries>
    <Battery>
      <FullChargeCapacity>50000</FullChargeCapacity>
    </Battery>
  </Batteries>
  <RecentUsage>
    <!-- Before disconnect: 08:00 plugged in -->
    <UsageEntry Timestamp=""2026-09-25T08:00:00"" Duration=""{ticks}"" Ac=""1"" Discharge=""-5000"" EntryType=""Active"" />
    <!-- Disconnected at 09:00 -->
    <UsageEntry Timestamp=""2026-09-25T09:00:00"" Duration=""{ticks}"" Ac=""0"" Discharge=""2500"" EntryType=""Active"" />
    <!-- Continued on battery at 09:30 -->
    <UsageEntry Timestamp=""2026-09-25T09:30:00"" Duration=""{ticks}"" Ac=""0"" Discharge=""3500"" EntryType=""Active"" />
  </RecentUsage>
</BatteryReport>";

            var now = new DateTime(2026, 9, 25, 10, 0, 0);

            // Filter with sinceLastCharge = true
            var result = BatteryUsageService.ParseBatteryReportXml(xml, "Full", nowOverride: now, sinceLastCharge: true);

            Assert.True(result.HasBattery);
            Assert.Equal(2, result.Sessions.Count); // Only the 09:00 and 09:30 sessions
            Assert.Equal(6000, result.TotalDischargeMwh); // 2500 + 3500
            Assert.Equal(12.0, result.TotalDischargePercent, precision: 1); // 6000 / 50000 = 12%
            Assert.Equal(new DateTime(2026, 9, 25, 9, 0, 0), result.LastAcDisconnectTime);
        }

        [Fact]
        public void AccumulateAppEnergy_IntegratesEnergyAndComputesPercentages()
        {
            var service = new BatteryUsageService();

            var processes = new List<ProcessItem>
            {
                new ProcessItem { Pid = 100, Name = "chrome.exe", Description = "Google Chrome", CpuPercent = 20.0 },
                new ProcessItem { Pid = 200, Name = "code.exe", Description = "Visual Studio Code", CpuPercent = 5.0 },
                new ProcessItem { Pid = 300, Name = "notepad.exe", Description = "Notepad", CpuPercent = 0.0 }
            };

            // Simulate 10 seconds of discharge at 15,000 mW (15 Watts) with Chrome in foreground
            service.AccumulateAppEnergy(
                processes: processes,
                currentDischargeRateMw: 15000,
                isDischarging: true,
                elapsedSeconds: 10.0,
                foregroundPid: 100);

            var items = service.GetAppBatteryUsage(
                currentProcesses: processes,
                currentDischargeRateMw: 15000,
                isDischarging: true,
                foregroundPid: 100,
                displayMode: "Combined");

            Assert.NotEmpty(items);

            // Chrome should have highest usage due to 20% CPU and foreground status
            var chrome = items.First(i => i.ProcessName == "chrome.exe");
            Assert.True(chrome.IsForeground);
            Assert.True(chrome.BatteryPercent > 50.0, $"Expected Chrome to have > 50% battery share, got {chrome.BatteryPercent}%");
            Assert.True(chrome.EstimatedPowerMw > 0);
            Assert.True(chrome.EnergyConsumedMwh > 0);

            // Total percentages across all apps should sum to 100%
            double sumPercent = items.Sum(i => i.BatteryPercent);
            Assert.InRange(sumPercent, 99.9, 100.1);
        }

        [Fact]
        public void AppBatteryUsageItem_DisplayModes_FormatsDisplayTextCorrectly()
        {
            var item = new AppBatteryUsageItem
            {
                Pid = 1234,
                ProcessName = "chrome.exe",
                DisplayName = "Google Chrome (chrome.exe)",
                BatteryPercent = 42.5,
                EnergyConsumedMwh = 2450.3,
                EstimatedPowerMw = 1820,
                PowerImpact = "High",
                IsForeground = true,
                DisplayMode = "Combined"
            };

            // 1. Combined Mode
            item.DisplayMode = "Combined";
            item.UpdateDisplayText();
            Assert.Equal("Google Chrome (chrome.exe): 42.5% of battery used (2,450.3 mWh) - Live: 1,820 mW (High) [Active Window]", item.DisplayText);

            // 2. Percentage Only Mode
            item.DisplayMode = "Percentage";
            item.UpdateDisplayText();
            Assert.Equal("Google Chrome (chrome.exe): 42.5% of battery used (2,450.3 mWh) [Active Window]", item.DisplayText);

            // 3. Drain Rate Only Mode
            item.DisplayMode = "DrainRate";
            item.UpdateDisplayText();
            Assert.Equal("Google Chrome (chrome.exe): 1,820 mW - High Impact [Active Window]", item.DisplayText);

            // Background app test (no active window suffix)
            item.IsForeground = false;
            item.DisplayMode = "Combined";
            item.UpdateDisplayText();
            Assert.Equal("Google Chrome (chrome.exe): 42.5% of battery used (2,450.3 mWh) - Live: 1,820 mW (High)", item.DisplayText);
        }

        [Fact]
        public async System.Threading.Tasks.Task GetAppBatteryUsage_RealMachine_ReturnsItems()
        {
            var service = new BatteryUsageService();
            var procService = new ProcessService();
            var procs = await procService.GetProcessesAsync(string.Empty, "CPU", false);
            var liveState = service.GetLiveBatteryState();

            service.AccumulateAppEnergy(procs, liveState.DischargeRateMw, liveState.IsDischarging, 2.0, 0);
            var apps = service.GetAppBatteryUsage(procs, liveState.DischargeRateMw, liveState.IsDischarging, 0, "Combined");

            Assert.True(apps.Count > 0, $"Expected apps > 0, got {apps.Count}. Procs count: {procs.Count}, LiveState HasBattery: {liveState.HasBattery}, Rate: {liveState.DischargeRateMw}, Discharging: {liveState.IsDischarging}");
        }

        [Fact]
        public void GetLiveBatteryState_ExecutesSafely()
        {
            var service = new BatteryUsageService();
            var state = service.GetLiveBatteryState();

            Assert.True(state.RemainingMwh >= 0);
            Assert.True(state.MaxMwh >= 0);
            Assert.True(state.DischargeRateMw >= 0);
        }

        [Fact]
        public void AccumulateAppEnergy_IdleForegroundApp_DoesNotOverestimatePower()
        {
            var service = new BatteryUsageService();

            var processes = new List<ProcessItem>
            {
                new ProcessItem { Pid = 100, Name = "ResourceAnalyzer.exe", Description = "Resource Analyzer", CpuPercent = 0.1 },
                new ProcessItem { Pid = 200, Name = "chrome.exe", Description = "Google Chrome", CpuPercent = 0.1 },
                new ProcessItem { Pid = 300, Name = "svchost.exe", Description = "Host Process", CpuPercent = 0.0 }
            };

            // Discharging at 5,000 mW (5 Watts) on battery with ResourceAnalyzer in foreground
            service.AccumulateAppEnergy(
                processes: processes,
                currentDischargeRateMw: 5000,
                isDischarging: true,
                elapsedSeconds: 2.0,
                foregroundPid: 100);

            var items = service.GetAppBatteryUsage(
                currentProcesses: processes,
                currentDischargeRateMw: 5000,
                isDischarging: true,
                foregroundPid: 100,
                displayMode: "Combined");

            var app = items.First(i => i.ProcessName == "ResourceAnalyzer.exe");

            // Power must be realistic (< 300 mW, Very Low impact) - NEVER 2,000+ mW
            Assert.True(app.EstimatedPowerMw < 300, $"Expected < 300 mW for idle app, got {app.EstimatedPowerMw} mW");
            Assert.Equal("Very Low", app.PowerImpact);
        }

        [Fact]
        public void GetAppBatteryUsage_RelativeAppShare_SumsTo100Percent()
        {
            var service = new BatteryUsageService();

            var processes = new List<ProcessItem>
            {
                new ProcessItem { Pid = 100, Name = "heavy.exe", Description = "Heavy App", CpuPercent = 20.0 },
                new ProcessItem { Pid = 200, Name = "light.exe", Description = "Light App", CpuPercent = 2.0 },
                new ProcessItem { Pid = 300, Name = "idle.exe", Description = "Idle App", CpuPercent = 0.0 }
            };

            service.AccumulateAppEnergy(processes, 10000, true, 10.0, 100);

            var items = service.GetAppBatteryUsage(
                processes,
                10000,
                true,
                100,
                "Combined");

            Assert.Equal(3, items.Count);
            var heavy = items.First(i => i.ProcessName == "heavy.exe");
            var idle = items.First(i => i.ProcessName == "idle.exe");

            Assert.True(heavy.BatteryPercent > idle.BatteryPercent);
            Assert.True(heavy.BatteryPercent > 50.0);

            double sum = items.Sum(i => i.BatteryPercent);
            Assert.InRange(sum, 99.9, 100.1);
        }

        [Fact]
        public void ParseBatteryReportXml_ExcludesReportGeneratedEntries()
        {
            string xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<BatteryReport xmlns=""http://schemas.microsoft.com/battery/2012"">
  <Batteries>
    <Battery><FullChargeCapacity>50000</FullChargeCapacity></Battery>
  </Batteries>
  <RecentUsage>
    <UsageEntry Timestamp=""2026-09-25T11:30:00"" Ac=""0"" Discharge=""2500"" EntryType=""Active"" />
    <UsageEntry Timestamp=""2026-09-25T11:43:00"" Ac=""0"" Discharge=""0"" EntryType=""ReportGenerated"" />
  </RecentUsage>
</BatteryReport>";

            var result = BatteryUsageService.ParseBatteryReportXml(xml, "Full");

            Assert.Single(result.Sessions);
            Assert.Equal("Active", result.Sessions[0].EntryType);
        }
    }
}
