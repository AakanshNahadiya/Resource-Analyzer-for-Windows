using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using AccessibleTaskManager.Helpers;
using AccessibleTaskManager.Models;

namespace AccessibleTaskManager.Services
{
    public interface IBatteryUsageService
    {
        Task<(List<BatteryUsageItem> Sessions, long TotalDischargeMwh, double TotalDischargePercent, TimeSpan ActiveDuration, TimeSpan StandbyDuration, bool HasBattery, DateTime? LastAcDisconnectTime)> GetBatteryUsageAsync(string timeRange, bool sinceLastCharge = false);

        (bool HasBattery, bool IsOnAc, bool IsDischarging, int DischargeRateMw, uint RemainingMwh, uint MaxMwh, DateTime? LastAcDisconnectTime) GetLiveBatteryState();

        DateTime? GetLastAcDisconnectTime();

        void AccumulateAppEnergy(
            IEnumerable<ProcessItem> processes,
            int currentDischargeRateMw,
            bool isDischarging,
            double elapsedSeconds,
            int foregroundPid);

        List<AppBatteryUsageItem> GetAppBatteryUsage(
            IEnumerable<ProcessItem> currentProcesses,
            int currentDischargeRateMw,
            bool isDischarging,
            int foregroundPid,
            string displayMode);

        void ResetAppEnergy();
    }

    public class AppEnergyRecord
    {
        public string ProcessName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public double TotalEnergyConsumedMwh { get; set; }
        public int LastPowerMw { get; set; }
        public bool LastIsForeground { get; set; }
        public double LastCpuPercent { get; set; }
        public int Pid { get; set; }
        public DateTime FirstSeen { get; set; } = DateTime.Now;
        public DateTime LastSeen { get; set; } = DateTime.Now;
    }

    public class BatteryUsageService : IBatteryUsageService
    {
        private string? _cachedXmlContent;
        private DateTime _lastReportTime = DateTime.MinValue;
        private readonly object _lock = new();

        private static bool? _lastKnownAcOnline;
        private static DateTime? _liveLastAcDisconnectTime;

        private readonly Dictionary<string, AppEnergyRecord> _appEnergyRecords = new(StringComparer.OrdinalIgnoreCase);

        public async Task<(List<BatteryUsageItem> Sessions, long TotalDischargeMwh, double TotalDischargePercent, TimeSpan ActiveDuration, TimeSpan StandbyDuration, bool HasBattery, DateTime? LastAcDisconnectTime)> GetBatteryUsageAsync(string timeRange, bool sinceLastCharge = false)
        {
            return await Task.Run(() =>
            {
                string? xmlContent = GetBatteryReportXml();
                DateTime? liveDisconnect = _liveLastAcDisconnectTime;
                DateTime? disconnectTime = liveDisconnect ?? GetLastAcDisconnectTime(xmlContent);
                return ParseBatteryReportXml(xmlContent, timeRange, null, sinceLastCharge, disconnectTime);
            });
        }

        public (bool HasBattery, bool IsOnAc, bool IsDischarging, int DischargeRateMw, uint RemainingMwh, uint MaxMwh, DateTime? LastAcDisconnectTime) GetLiveBatteryState()
        {
            try
            {
                uint status = NativeMethods.CallNtPowerInformation(
                    NativeMethods.SystemBatteryState,
                    IntPtr.Zero,
                    0,
                    out NativeMethods.SYSTEM_BATTERY_STATE state,
                    (uint)Marshal.SizeOf<NativeMethods.SYSTEM_BATTERY_STATE>());

                if (status == 0 && state.BatteryPresent)
                {
                    bool isOnAc = state.AcOnLine;
                    bool isDischarging = state.Discharging;
                    int rateMw = Math.Abs(state.Rate);

                    // Track live AC disconnect or initial on-battery state
                    if (_lastKnownAcOnline == null && !isOnAc)
                    {
                        _lastKnownAcOnline = false;
                        if (!_liveLastAcDisconnectTime.HasValue)
                        {
                            _liveLastAcDisconnectTime = GetLastAcDisconnectTime();
                        }
                    }
                    else if (_lastKnownAcOnline == true && !isOnAc)
                    {
                        _liveLastAcDisconnectTime = DateTime.Now;
                        ResetAppEnergy();
                    }
                    _lastKnownAcOnline = isOnAc;

                    return (
                        HasBattery: true,
                        IsOnAc: isOnAc,
                        IsDischarging: isDischarging,
                        DischargeRateMw: rateMw,
                        RemainingMwh: state.RemainingCapacity,
                        MaxMwh: state.MaxCapacity,
                        LastAcDisconnectTime: _liveLastAcDisconnectTime
                    );
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CallNtPowerInformation error: {ex.Message}");
            }

            // Fallback to GetSystemPowerStatus
            if (NativeMethods.GetSystemPowerStatus(out var sps))
            {
                bool hasBattery = (sps.BatteryFlag & 128) == 0;
                bool isOnAc = sps.ACLineStatus == 1;
                bool isDischarging = !isOnAc && (sps.BatteryFlag & 8) == 0;

                if (_lastKnownAcOnline == null && !isOnAc)
                {
                    _lastKnownAcOnline = false;
                    if (!_liveLastAcDisconnectTime.HasValue)
                    {
                        _liveLastAcDisconnectTime = GetLastAcDisconnectTime();
                    }
                }
                else if (_lastKnownAcOnline == true && !isOnAc)
                {
                    _liveLastAcDisconnectTime = DateTime.Now;
                    ResetAppEnergy();
                }
                _lastKnownAcOnline = isOnAc;

                return (
                    HasBattery: hasBattery,
                    IsOnAc: isOnAc,
                    IsDischarging: isDischarging,
                    DischargeRateMw: 0,
                    RemainingMwh: 0,
                    MaxMwh: 0,
                    LastAcDisconnectTime: _liveLastAcDisconnectTime
                );
            }

            return (false, true, false, 0, 0, 0, null);
        }

        public DateTime? GetLastAcDisconnectTime()
        {
            if (_liveLastAcDisconnectTime.HasValue) return _liveLastAcDisconnectTime;
            string? xmlContent = GetBatteryReportXml();
            return GetLastAcDisconnectTime(xmlContent);
        }

        public void AccumulateAppEnergy(
            IEnumerable<ProcessItem> processes,
            int currentDischargeRateMw,
            bool isDischarging,
            double elapsedSeconds,
            int foregroundPid)
        {
            if (elapsedSeconds <= 0) return;

            lock (_lock)
            {
                var procList = processes.Where(p => !p.IsGroupHeader && p.Pid > 4).ToList();
                if (procList.Count == 0) return;

                double totalCpu = procList.Sum(p => p.CpuPercent);

                // System discharge / power budget in mW
                int systemPowerMw;
                if (isDischarging && currentDischargeRateMw > 0)
                {
                    systemPowerMw = currentDischargeRateMw;
                }
                else
                {
                    // Baseline power consumption estimate based on CPU load (idle ~8W up to ~35W)
                    systemPowerMw = Math.Min(35000, 8000 + (int)(totalCpu * 200));
                }

                // Dynamic software power pool scales with actual system CPU activity:
                // Total system power draw includes platform baseline (screen backlight, Wi-Fi, motherboard, DRAM).
                // When system is idle (< 1% CPU), software dynamic drain is small (~350 mW total across all processes).
                // Under heavy load (100% CPU), software dynamic consumption accounts for up to 60% of total discharge.
                double cpuActivityRatio = Math.Clamp(totalCpu / 100.0, 0.0, 1.0);
                double dynamicPoolMw = Math.Min(systemPowerMw * 0.60, 350.0 + (cpuActivityRatio * (systemPowerMw * 0.55)));

                // Group by application name (e.g. all 15 instances of msedge.exe into one application)
                var appGroups = procList.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

                foreach (var g in appGroups)
                {
                    string appName = g.Key;
                    double appCpu = g.Sum(p => p.CpuPercent);
                    bool isFg = g.Any(p => foregroundPid > 0 && p.Pid == foregroundPid);
                    int repPid = g.First().Pid;
                    string displayName = g.FirstOrDefault(p => !string.IsNullOrEmpty(p.DisplayName))?.DisplayName ?? appName;

                    // App CPU fraction: strictly based on actual CPU consumption
                    double cpuFrac = totalCpu > 0.05 ? (appCpu / totalCpu) : (1.0 / appGroups.Count);

                    // Dynamic CPU power allocated to this app
                    int appDynamicMw = (int)(dynamicPoolMw * cpuFrac);

                    // Minimum idle floor per active application (5 mW)
                    int baseFloorMw = 5;

                    int appPowerMw = Math.Max(baseFloorMw, baseFloorMw + appDynamicMw);
                    double deltaMwh = appPowerMw * (elapsedSeconds / 3600.0);

                    if (!_appEnergyRecords.TryGetValue(appName, out var record))
                    {
                        record = new AppEnergyRecord
                        {
                            ProcessName = appName,
                            DisplayName = displayName,
                            Pid = repPid,
                            FirstSeen = DateTime.Now
                        };
                        _appEnergyRecords[appName] = record;
                    }

                    record.TotalEnergyConsumedMwh += deltaMwh;
                    // Exponential moving average (70% prior / 30% current) for smooth live drain rate display
                    record.LastPowerMw = record.LastPowerMw > 0
                        ? (int)(0.70 * record.LastPowerMw + 0.30 * appPowerMw)
                        : appPowerMw;
                    record.LastIsForeground = isFg;
                    record.LastCpuPercent = appCpu;
                    record.Pid = repPid;
                    record.LastSeen = DateTime.Now;
                    if (!string.IsNullOrEmpty(displayName))
                    {
                        record.DisplayName = displayName;
                    }
                }
            }
        }

        public List<AppBatteryUsageItem> GetAppBatteryUsage(
            IEnumerable<ProcessItem> currentProcesses,
            int currentDischargeRateMw,
            bool isDischarging,
            int foregroundPid,
            string displayMode)
        {
            lock (_lock)
            {
                var result = new List<AppBatteryUsageItem>();
                double totalAppEnergy = _appEnergyRecords.Values.Sum(r => r.TotalEnergyConsumedMwh);

                // Use HashSet for active process names - prevents any duplicate key collisions
                var activeNames = new HashSet<string>(
                    currentProcesses.Where(p => !p.IsGroupHeader && p.Pid > 4).Select(p => p.Name),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var (key, rec) in _appEnergyRecords)
                {
                    bool isActive = activeNames.Contains(key);
                    if (!isActive && rec.TotalEnergyConsumedMwh < 0.01)
                    {
                        continue;
                    }

                    double batteryPercent = totalAppEnergy > 0 ? (rec.TotalEnergyConsumedMwh / totalAppEnergy) * 100.0 : 0.0;
                    int powerMw = isActive ? rec.LastPowerMw : 0;
                    bool isFg = isActive && rec.LastIsForeground;

                    string impact = powerMw switch
                    {
                        >= 5000 => "Very High",
                        >= 2500 => "High",
                        >= 1000 => "Moderate",
                        >= 300 => "Low",
                        _ => "Very Low"
                    };

                    var item = new AppBatteryUsageItem
                    {
                        ProcessName = rec.ProcessName,
                        DisplayName = string.IsNullOrEmpty(rec.DisplayName) ? rec.ProcessName : rec.DisplayName,
                        Pid = rec.Pid,
                        EstimatedPowerMw = powerMw,
                        EnergyConsumedMwh = rec.TotalEnergyConsumedMwh,
                        BatteryPercent = batteryPercent,
                        PowerImpact = impact,
                        IsForeground = isFg,
                        DisplayMode = displayMode
                    };
                    item.UpdateDisplayText();
                    result.Add(item);
                }

                if (displayMode == "DrainRate")
                {
                    result = result.OrderByDescending(i => i.EstimatedPowerMw)
                                   .ThenByDescending(i => i.EnergyConsumedMwh)
                                   .ToList();
                }
                else
                {
                    result = result.OrderByDescending(i => i.BatteryPercent)
                                   .ThenByDescending(i => i.EnergyConsumedMwh)
                                   .ThenByDescending(i => i.EstimatedPowerMw)
                                   .ToList();
                }

                return result;
            }
        }

        public void ResetAppEnergy()
        {
            lock (_lock)
            {
                _appEnergyRecords.Clear();
            }
        }

        public static DateTime? GetLastAcDisconnectTime(string? xmlContent)
        {
            if (string.IsNullOrWhiteSpace(xmlContent)) return null;

            try
            {
                var doc = XDocument.Parse(xmlContent);
                var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                var recentUsage = doc.Root?.Element(ns + "RecentUsage")?.Elements(ns + "UsageEntry").ToList();
                if (recentUsage == null || recentUsage.Count == 0) return null;

                var entries = new List<(DateTime Timestamp, int Ac)>();
                foreach (var entry in recentUsage)
                {
                    string entryType = GetAttrOrElement(entry, ns, "EntryType") ?? "Active";
                    if (entryType.Equals("ReportGenerated", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string tsStr = GetAttrOrElement(entry, ns, "LocalTimestamp") ?? GetAttrOrElement(entry, ns, "Timestamp") ?? string.Empty;
                    if (DateTime.TryParse(tsStr, out var ts))
                    {
                        string? acVal = GetAttrOrElement(entry, ns, "Ac");
                        int ac = 0;
                        if (int.TryParse(acVal, out int parsedAc))
                        {
                            ac = parsedAc;
                        }
                        entries.Add((ts, ac));
                    }
                }

                if (entries.Count == 0) return null;

                entries = entries.OrderBy(e => e.Timestamp).ToList();

                // Find the start of the latest sequence where Ac == 0 (battery power)
                DateTime? lastDisconnect = null;
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    if (entries[i].Ac == 0)
                    {
                        int startIdx = i;
                        while (startIdx > 0 && entries[startIdx - 1].Ac == 0)
                        {
                            startIdx--;
                        }
                        lastDisconnect = entries[startIdx].Timestamp;
                        break;
                    }
                }

                return lastDisconnect;
            }
            catch
            {
                return null;
            }
        }

        internal static (List<BatteryUsageItem> Sessions, long TotalDischargeMwh, double TotalDischargePercent, TimeSpan ActiveDuration, TimeSpan StandbyDuration, bool HasBattery, DateTime? LastAcDisconnectTime) ParseBatteryReportXml(
            string? xmlContent,
            string timeRange,
            DateTime? nowOverride = null,
            bool sinceLastCharge = false,
            DateTime? customCutoff = null)
        {
            if (string.IsNullOrWhiteSpace(xmlContent))
            {
                return (new List<BatteryUsageItem>(), 0, 0.0, TimeSpan.Zero, TimeSpan.Zero, false, null);
            }

            try
            {
                var doc = XDocument.Parse(xmlContent);
                var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

                // Verify battery presence
                var batteries = doc.Root?.Element(ns + "Batteries")?.Elements(ns + "Battery").ToList();
                if (batteries == null || batteries.Count == 0)
                {
                    return (new List<BatteryUsageItem>(), 0, 0.0, TimeSpan.Zero, TimeSpan.Zero, false, null);
                }

                long systemFcc = 0;
                foreach (var b in batteries)
                {
                    string? fccVal = GetAttrOrElement(b, ns, "FullChargeCapacity");
                    if (long.TryParse(fccVal, out long fcc) && fcc > 0)
                    {
                        systemFcc += fcc;
                    }
                }

                var recentUsage = doc.Root?.Element(ns + "RecentUsage")?.Elements(ns + "UsageEntry").ToList();
                if (recentUsage == null || recentUsage.Count == 0)
                {
                    return (new List<BatteryUsageItem>(), 0, 0.0, TimeSpan.Zero, TimeSpan.Zero, true, null);
                }

                DateTime? lastDisconnect = customCutoff ?? GetLastAcDisconnectTime(xmlContent);
                var now = nowOverride ?? DateTime.Now;

                DateTime cutoff;
                if (sinceLastCharge)
                {
                    cutoff = lastDisconnect ?? now.Date;
                }
                else
                {
                    cutoff = timeRange switch
                    {
                        "Today" => now.Date,
                        "Last 24 Hours" => now.AddHours(-24),
                        "Last Week" => now.AddDays(-7),
                        "Last Month" => now.AddDays(-30),
                        _ => DateTime.MinValue // "Full"
                    };
                }

                var sessions = new List<BatteryUsageItem>();
                long totalDischarge = 0;
                var totalActiveDuration = TimeSpan.Zero;
                var totalStandbyDuration = TimeSpan.Zero;

                foreach (var entry in recentUsage)
                {
                    string localTsStr = GetAttrOrElement(entry, ns, "LocalTimestamp") ?? GetAttrOrElement(entry, ns, "Timestamp") ?? string.Empty;
                    if (!DateTime.TryParse(localTsStr, out var timestamp))
                    {
                        continue;
                    }

                    if (timestamp < cutoff)
                    {
                        continue;
                    }

                    long durationTicks = 0;
                    string? durVal = GetAttrOrElement(entry, ns, "Duration");
                    if (long.TryParse(durVal, out long dur))
                    {
                        durationTicks = dur;
                    }
                    var duration = TimeSpan.FromTicks(durationTicks);

                    int ac = 0;
                    string? acVal = GetAttrOrElement(entry, ns, "Ac");
                    if (int.TryParse(acVal, out int acInt))
                    {
                        ac = acInt;
                    }

                    string entryType = GetAttrOrElement(entry, ns, "EntryType") ?? "Active";
                    if (entryType.Equals("ReportGenerated", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    long dischargeMwh = 0;
                    string? disVal = GetAttrOrElement(entry, ns, "Discharge");
                    if (long.TryParse(disVal, out long dis))
                    {
                        dischargeMwh = dis;
                    }

                    long entryFcc = systemFcc;
                    string? efccVal = GetAttrOrElement(entry, ns, "FullChargeCapacity");
                    if (long.TryParse(efccVal, out long efcc) && efcc > 0)
                    {
                        entryFcc = efcc;
                    }

                    double drainPct = 0.0;
                    if (entryFcc > 0 && dischargeMwh != 0)
                    {
                        drainPct = (Math.Abs(dischargeMwh) * 100.0) / entryFcc;
                    }

                    bool isCharging = (ac == 1 && dischargeMwh < 0);
                    string durText = FormatDuration(duration);

                    string displayText;
                    if (isCharging)
                    {
                        long gainedMwh = Math.Abs(dischargeMwh);
                        displayText = $"{timestamp:dd-MMM HH:mm} - Charging: Gained {gainedMwh:N0} mWh ({drainPct:F1}%) in {durText} (Plugged In)";
                    }
                    else if (ac == 0 && dischargeMwh > 0)
                    {
                        totalDischarge += dischargeMwh;
                        if (entryType.Equals("Active", StringComparison.OrdinalIgnoreCase))
                        {
                            totalActiveDuration += duration;
                            displayText = $"{timestamp:dd-MMM HH:mm} - Active Use: Discharged {dischargeMwh:N0} mWh ({drainPct:F1}%) in {durText} (On Battery)";
                        }
                        else
                        {
                            totalStandbyDuration += duration;
                            displayText = $"{timestamp:dd-MMM HH:mm} - {entryType}: Discharged {dischargeMwh:N0} mWh ({drainPct:F1}%) in {durText}";
                        }
                    }
                    else
                    {
                        displayText = $"{timestamp:dd-MMM HH:mm} - {entryType}: {durText} ({(ac == 1 ? "Plugged In" : "On Battery")})";
                    }

                    sessions.Add(new BatteryUsageItem
                    {
                        Timestamp = timestamp,
                        Duration = duration,
                        EntryType = entryType,
                        DrainMwh = dischargeMwh,
                        DrainPercent = drainPct,
                        IsCharging = isCharging,
                        DisplayText = displayText
                    });
                }

                // Order descending (most recent sessions first)
                sessions = sessions.OrderByDescending(s => s.Timestamp).ToList();

                double totalDischargePercent = systemFcc > 0
                    ? (totalDischarge * 100.0) / systemFcc
                    : 0.0;

                return (sessions, totalDischarge, totalDischargePercent, totalActiveDuration, totalStandbyDuration, true, lastDisconnect);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error parsing battery report: {ex.Message}");
                return (new List<BatteryUsageItem>(), 0, 0.0, TimeSpan.Zero, TimeSpan.Zero, true, null);
            }
        }

        private string? GetBatteryReportXml()
        {
            lock (_lock)
            {
                if (_cachedXmlContent != null && (DateTime.UtcNow - _lastReportTime).TotalSeconds < 30)
                {
                    return _cachedXmlContent;
                }

                string tempFile = Path.Combine(Path.GetTempPath(), $"ra_battery_{Guid.NewGuid():N}.xml");
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powercfg.exe",
                        Arguments = $"/batteryreport /xml /output \"{tempFile}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        proc.WaitForExit(4000);
                        if (File.Exists(tempFile))
                        {
                            _cachedXmlContent = File.ReadAllText(tempFile);
                            _lastReportTime = DateTime.UtcNow;
                            return _cachedXmlContent;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to generate battery report: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempFile))
                        {
                            File.Delete(tempFile);
                        }
                    }
                    catch { }
                }

                return _cachedXmlContent;
            }
        }

        private static string? GetAttrOrElement(XElement element, XNamespace ns, string name)
        {
            return element.Attribute(name)?.Value ?? element.Element(ns + name)?.Value;
        }

        private static string FormatDuration(TimeSpan ts)
        {
            if (ts.TotalDays >= 1)
            {
                return $"{ts.Days}d {ts.Hours}h {ts.Minutes}m";
            }
            if (ts.TotalHours >= 1)
            {
                return $"{ts.Hours}h {ts.Minutes}m";
            }
            if (ts.TotalMinutes >= 1)
            {
                return $"{ts.Minutes}m {ts.Seconds}s";
            }
            return $"{ts.Seconds}s";
        }
    }
}
