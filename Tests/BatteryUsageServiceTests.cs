using System;
using System.Linq;
using AccessibleTaskManager.Services;
using Xunit;

namespace AccessibleTaskManager.Tests
{
    public class BatteryUsageServiceTests
    {
        [Fact]
        public void ParseBatteryReportXml_NullOrEmpty_ReturnsHasBatteryFalse()
        {
            var result = BatteryUsageService.ParseBatteryReportXml(null, "Full");
            Assert.False(result.HasBattery);
            Assert.Empty(result.Sessions);

            var resultEmpty = BatteryUsageService.ParseBatteryReportXml("   ", "Full");
            Assert.False(resultEmpty.HasBattery);
            Assert.Empty(resultEmpty.Sessions);
        }

        [Fact]
        public void ParseBatteryReportXml_NoBatteryElement_ReturnsHasBatteryFalse()
        {
            string xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<BatteryReport xmlns=""http://schemas.microsoft.com/battery/2012"">
  <Batteries>
  </Batteries>
</BatteryReport>";

            var result = BatteryUsageService.ParseBatteryReportXml(xml, "Full");
            Assert.False(result.HasBattery);
            Assert.Empty(result.Sessions);
        }

        [Fact]
        public void ParseBatteryReportXml_ValidSessions_ParsesActiveDischargeAndCharging()
        {
            var baseTime = new DateTime(2026, 9, 25, 12, 0, 0);

            // Duration: 3600 seconds = 3600 * 10,000,000 ticks = 36,000,000,000 ticks
            long oneHourTicks = 36_000_000_000L;
            long halfHourTicks = 18_000_000_000L;

            string xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<BatteryReport xmlns=""http://schemas.microsoft.com/battery/2012"">
  <Batteries>
    <Battery>
      <FullChargeCapacity>50000</FullChargeCapacity>
      <DesignCapacity>50000</DesignCapacity>
    </Battery>
  </Batteries>
  <RecentUsage>
    <!-- 1 hour active use on battery: 10,000 mWh discharge (20% drain) -->
    <UsageEntry>
      <Timestamp>2026-09-25T10:00:00</Timestamp>
      <Duration>{oneHourTicks}</Duration>
      <Ac>0</Ac>
      <EntryType>Active</EntryType>
      <Discharge>10000</Discharge>
      <FullChargeCapacity>50000</FullChargeCapacity>
    </UsageEntry>
    <!-- 30 min plugged in: -5,000 mWh discharge (gained 5,000 mWh, 10% charge) -->
    <UsageEntry>
      <Timestamp>2026-09-25T11:00:00</Timestamp>
      <Duration>{halfHourTicks}</Duration>
      <Ac>1</Ac>
      <EntryType>Active</EntryType>
      <Discharge>-5000</Discharge>
      <FullChargeCapacity>50000</FullChargeCapacity>
    </UsageEntry>
  </RecentUsage>
</BatteryReport>";

            var result = BatteryUsageService.ParseBatteryReportXml(xml, "Full", nowOverride: baseTime);

            Assert.True(result.HasBattery);
            Assert.Equal(2, result.Sessions.Count);

            // Ordered descending by timestamp: 11:00 session first, then 10:00 session
            var chargeSession = result.Sessions[0];
            Assert.True(chargeSession.IsCharging);
            Assert.Equal(TimeSpan.FromMinutes(30), chargeSession.Duration);
            Assert.Equal(-5000, chargeSession.DrainMwh);
            Assert.Equal(10.0, chargeSession.DrainPercent, precision: 1);
            Assert.Contains("Charging: Gained 5,000 mWh (10.0%)", chargeSession.DisplayText);

            var activeSession = result.Sessions[1];
            Assert.False(activeSession.IsCharging);
            Assert.Equal(TimeSpan.FromHours(1), activeSession.Duration);
            Assert.Equal(10000, activeSession.DrainMwh);
            Assert.Equal(20.0, activeSession.DrainPercent, precision: 1);
            Assert.Contains("Active Use: Discharged 10,000 mWh (20.0%)", activeSession.DisplayText);

            // Summary metrics
            Assert.Equal(10000, result.TotalDischargeMwh);
            Assert.Equal(20.0, result.TotalDischargePercent, precision: 1);
            Assert.Equal(TimeSpan.FromHours(1), result.ActiveDuration);
            Assert.Equal(TimeSpan.Zero, result.StandbyDuration);
        }

        [Fact]
        public void ParseBatteryReportXml_TimeRangeFilters_FiltersCorrectly()
        {
            var now = new DateTime(2026, 9, 25, 14, 0, 0);

            long ticks = 18_000_000_000L; // 30 mins

            string xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<BatteryReport xmlns=""http://schemas.microsoft.com/battery/2012"">
  <Batteries>
    <Battery>
      <FullChargeCapacity>40000</FullChargeCapacity>
    </Battery>
  </Batteries>
  <RecentUsage>
    <!-- 40 days ago -->
    <UsageEntry>
      <Timestamp>2026-08-16T10:00:00</Timestamp>
      <Duration>{ticks}</Duration>
      <Ac>0</Ac>
      <EntryType>Active</EntryType>
      <Discharge>1000</Discharge>
    </UsageEntry>
    <!-- 15 days ago -->
    <UsageEntry>
      <Timestamp>2026-09-10T10:00:00</Timestamp>
      <Duration>{ticks}</Duration>
      <Ac>0</Ac>
      <EntryType>Active</EntryType>
      <Discharge>2000</Discharge>
    </UsageEntry>
    <!-- 3 days ago -->
    <UsageEntry>
      <Timestamp>2026-09-22T10:00:00</Timestamp>
      <Duration>{ticks}</Duration>
      <Ac>0</Ac>
      <EntryType>Active</EntryType>
      <Discharge>3000</Discharge>
    </UsageEntry>
    <!-- 3 hours ago (Today) -->
    <UsageEntry>
      <Timestamp>2026-09-25T11:00:00</Timestamp>
      <Duration>{ticks}</Duration>
      <Ac>0</Ac>
      <EntryType>Active</EntryType>
      <Discharge>4000</Discharge>
    </UsageEntry>
  </RecentUsage>
</BatteryReport>";

            // Full: All 4 sessions
            var fullResult = BatteryUsageService.ParseBatteryReportXml(xml, "Full", nowOverride: now);
            Assert.Equal(4, fullResult.Sessions.Count);
            Assert.Equal(10000, fullResult.TotalDischargeMwh);

            // Last Month (30 days): 3 sessions (excludes 40 days ago)
            var monthResult = BatteryUsageService.ParseBatteryReportXml(xml, "Last Month", nowOverride: now);
            Assert.Equal(3, monthResult.Sessions.Count);
            Assert.Equal(9000, monthResult.TotalDischargeMwh);

            // Last Week (7 days): 2 sessions (3 days ago and today)
            var weekResult = BatteryUsageService.ParseBatteryReportXml(xml, "Last Week", nowOverride: now);
            Assert.Equal(2, weekResult.Sessions.Count);
            Assert.Equal(7000, weekResult.TotalDischargeMwh);

            // Today: 1 session (today at 11:00)
            var todayResult = BatteryUsageService.ParseBatteryReportXml(xml, "Today", nowOverride: now);
            Assert.Single(todayResult.Sessions);
            Assert.Equal(4000, todayResult.TotalDischargeMwh);
        }

        [Fact]
        public void ParseBatteryReportXml_ConnectedStandbyDischarge_TracksStandbyDuration()
        {
            var now = new DateTime(2026, 9, 25, 12, 0, 0);
            long twoHoursTicks = 72_000_000_000L;

            string xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<BatteryReport xmlns=""http://schemas.microsoft.com/battery/2012"">
  <Batteries>
    <Battery>
      <FullChargeCapacity>60000</FullChargeCapacity>
    </Battery>
  </Batteries>
  <RecentUsage>
    <UsageEntry>
      <Timestamp>2026-09-25T08:00:00</Timestamp>
      <Duration>{twoHoursTicks}</Duration>
      <Ac>0</Ac>
      <EntryType>Connected Standby</EntryType>
      <Discharge>1200</Discharge>
    </UsageEntry>
  </RecentUsage>
</BatteryReport>";

            var result = BatteryUsageService.ParseBatteryReportXml(xml, "Today", nowOverride: now);

            Assert.True(result.HasBattery);
            Assert.Single(result.Sessions);
            Assert.Equal(TimeSpan.FromHours(2), result.StandbyDuration);
            Assert.Equal(TimeSpan.Zero, result.ActiveDuration);
            Assert.Equal(1200, result.TotalDischargeMwh);
            Assert.Equal(2.0, result.TotalDischargePercent, precision: 1);
            Assert.Contains("Connected Standby: Discharged 1,200 mWh", result.Sessions[0].DisplayText);
        }

        [Fact]
        public void ParseBatteryReportXml_WindowsAttributeFormat_ParsesCorrectly()
        {
            string xml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<BatteryReport xmlns=""http://schemas.microsoft.com/battery/2012"">
  <Batteries>
    <Battery>
      <DesignCapacity>41040</DesignCapacity>
      <FullChargeCapacity>22940</FullChargeCapacity>
      <CycleCount>0</CycleCount>
    </Battery>
  </Batteries>
  <RecentUsage>
    <UsageEntry
      Timestamp=""2026-09-25T05:25:56Z""
      LocalTimestamp=""2026-09-25T10:55:56""
      Duration=""14734803959""
      Ac=""0""
      EntryType=""Active""
      ChargeCapacity=""11990""
      Discharge=""4370""
      FullChargeCapacity=""22940""
      IsNextOnBattery=""0""
      />
    <UsageEntry
      Timestamp=""2026-09-25T05:50:30Z""
      LocalTimestamp=""2026-09-25T11:20:30""
      Duration=""21976412001""
      Ac=""1""
      EntryType=""Active""
      ChargeCapacity=""7620""
      Discharge=""-11140""
      FullChargeCapacity=""22940""
      IsNextOnBattery=""1""
      />
  </RecentUsage>
</BatteryReport>";

            var result = BatteryUsageService.ParseBatteryReportXml(xml, "Full");

            Assert.True(result.HasBattery);
            Assert.Equal(2, result.Sessions.Count);

            var charging = result.Sessions.First(s => s.IsCharging);
            Assert.Equal(-11140, charging.DrainMwh);
            Assert.Contains("Charging: Gained 11,140 mWh", charging.DisplayText);

            var active = result.Sessions.First(s => !s.IsCharging);
            Assert.Equal(4370, active.DrainMwh);
            Assert.Contains("Active Use: Discharged 4,370 mWh", active.DisplayText);
        }

        [Fact]
        public async Task GetBatteryUsageAsync_OnRealMachine_ReturnsData()
        {
            var service = new BatteryUsageService();
            var result = await service.GetBatteryUsageAsync("Full");
            if (result.HasBattery)
            {
                Assert.NotEmpty(result.Sessions);
                Assert.True(result.Sessions.Count > 0);
            }
        }
    }
}
