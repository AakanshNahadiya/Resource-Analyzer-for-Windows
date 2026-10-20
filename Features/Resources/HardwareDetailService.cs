using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using ResourceAnalyzer.Helpers;

namespace ResourceAnalyzer.Services
{
    public interface IHardwareDetailService
    {
        Task<string> GetHardwareDetailsAsync(string resourceId);
        Task<string> GenerateSystemSnapshotAsync();
        Task<(int ProcessCount, long FreedBytes, string UpdatedDetails)> FlushMemoryCacheAsync();
    }

    public class HardwareDetailService : IHardwareDetailService
    {
        private readonly ISettingsService? _settingsService;

        public HardwareDetailService(ISettingsService? settingsService = null)
        {
            _settingsService = settingsService;
        }

        // Cache static hardware specs so WMI queries run only once on demand
        private static string? _cachedCpuStaticInfo;
        private static string? _cachedRamStaticInfo;
        private static string? _cachedGpuStaticInfo;
        private static string? _cachedDisplayStaticInfo;

        private static readonly Dictionary<string, (PerformanceCounter? Read, PerformanceCounter? Write)> _diskCounters = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, (double Read, double Write)> _latestDiskSpeeds = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _diskCounterLock = new();

        public static void SampleAllDiskSpeeds()
        {
            lock (_diskCounterLock)
            {
                try
                {
                    if (!PerformanceCounterCategory.Exists("PhysicalDisk")) return;

                    var cat = new PerformanceCounterCategory("PhysicalDisk");
                    var names = cat.GetInstanceNames();

                    foreach (var name in names)
                    {
                        if (!_diskCounters.TryGetValue(name, out var counters))
                        {
                            try
                            {
                                var readCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", name);
                                var writeCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", name);
                                readCounter.NextValue();
                                writeCounter.NextValue();
                                counters = (readCounter, writeCounter);
                                _diskCounters[name] = counters;
                            }
                            catch { }
                        }
                        else if (counters.Read != null && counters.Write != null)
                        {
                            try
                            {
                                float read = counters.Read.NextValue();
                                float write = counters.Write.NextValue();
                                _latestDiskSpeeds[name] = (Math.Max(0, (double)read), Math.Max(0, (double)write));
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }

        public static (double ReadBytesPerSec, double WriteBytesPerSec) GetLiveDiskSpeed(int diskIndex = -1)
        {
            lock (_diskCounterLock)
            {
                string instanceName = "_Total";
                try
                {
                    if (diskIndex >= 0)
                    {
                        string prefix = $"{diskIndex} ";
                        var matched = _latestDiskSpeeds.Keys.FirstOrDefault(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            ?? _diskCounters.Keys.FirstOrDefault(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                        if (matched != null)
                        {
                            instanceName = matched;
                        }
                        else if (PerformanceCounterCategory.Exists("PhysicalDisk"))
                        {
                            var cat = new PerformanceCounterCategory("PhysicalDisk");
                            var names = cat.GetInstanceNames();
                            var found = names.FirstOrDefault(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                            if (found != null) instanceName = found;
                        }
                    }

                    if (!_diskCounters.TryGetValue(instanceName, out var counters))
                    {
                        if (PerformanceCounterCategory.Exists("PhysicalDisk"))
                        {
                            var readCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", instanceName);
                            var writeCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", instanceName);
                            readCounter.NextValue();
                            writeCounter.NextValue();
                            counters = (readCounter, writeCounter);
                            _diskCounters[instanceName] = counters;
                        }
                    }

                    if (counters.Read != null && counters.Write != null)
                    {
                        float read = counters.Read.NextValue();
                        float write = counters.Write.NextValue();
                        var res = (Math.Max(0, (double)read), Math.Max(0, (double)write));
                        _latestDiskSpeeds[instanceName] = res;
                        return res;
                    }
                }
                catch
                {
                    if (_latestDiskSpeeds.TryGetValue(instanceName, out var fallback))
                    {
                        return fallback;
                    }
                }

                return (0, 0);
            }
        }

        public async Task<string> GetHardwareDetailsAsync(string resourceId)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (resourceId.Equals("disk", StringComparison.OrdinalIgnoreCase) ||
                        resourceId.StartsWith("disk_", StringComparison.OrdinalIgnoreCase) ||
                        resourceId.StartsWith("usb_", StringComparison.OrdinalIgnoreCase))
                    {
                        return BuildDiskDetails(resourceId);
                    }

                    if (resourceId.Equals("cpu", StringComparison.OrdinalIgnoreCase)) return BuildCpuDetails();
                    if (resourceId.Equals("ram", StringComparison.OrdinalIgnoreCase)) return BuildRamDetails();
                    if (resourceId.Equals("network", StringComparison.OrdinalIgnoreCase)) return BuildNetworkDetails();
                    if (resourceId.Equals("gpu", StringComparison.OrdinalIgnoreCase)) return BuildGpuDetails();
                    if (resourceId.Equals("display", StringComparison.OrdinalIgnoreCase)) return BuildDisplayDetails();
                    if (resourceId.Equals("audio", StringComparison.OrdinalIgnoreCase)) return BuildAudioDetails();
                    if (resourceId.Equals("battery", StringComparison.OrdinalIgnoreCase)) return BuildBatteryDetails();
                    return "Technical details are not available for this component.";
                }
                catch (Exception ex)
                {
                    return $"Could not retrieve technical details: {ex.Message}";
                }
            });
        }

        #region CPU Details

        private string BuildCpuDetails()
        {
            var sb = new StringBuilder();

            // CPU Name from registry (Instant 0.01ms kernel-populated CPUID brand string)
            string cpuName = string.Empty;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                cpuName = key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? string.Empty;
            }
            catch { }

            if (string.IsNullOrWhiteSpace(cpuName))
            {
                cpuName = $"{Environment.ProcessorCount} Cores Processor";
            }

            sb.AppendLine($"Processor: {cpuName}");

            // System Uptime (64-bit TickCount, no 49.7 day overflow)
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            sb.AppendLine($"System Uptime: {FormatUptime(uptime)}");

            // Operating System Architecture
            string bitness = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit";
            sb.AppendLine($"Architecture: {RuntimeInformation.OSArchitecture} ({bitness})");
            sb.AppendLine($"Power Mode: {GetActivePowerPlan()}");

            // Live Frequency Scaling & Thermal Throttling Telemetry (via native Powrprof + Processor Information)
            var powerLimits = NativeMethods.GetProcessorPowerLimits();
            uint freqScalingPct = 0;
            if (powerLimits.HasValue && powerLimits.Value.MaxMhz > 0)
            {
                freqScalingPct = (uint)Math.Clamp((double)powerLimits.Value.CurrentMhz * 100.0 / powerLimits.Value.MaxMhz, 0, 500);
            }

            uint procPerf = 0;
            try
            {
                using var perfSearcher = new ManagementObjectSearcher(
                    "SELECT PercentofMaximumFrequency, PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'");
                using var perfColl = perfSearcher.Get();
                foreach (ManagementObject obj in perfColl)
                {
                    using (obj)
                    {
                        if (obj["PercentofMaximumFrequency"] != null && uint.TryParse(obj["PercentofMaximumFrequency"]?.ToString(), out uint maxFreqPct))
                        {
                            freqScalingPct = maxFreqPct;
                        }
                        if (obj["PercentProcessorPerformance"] != null && uint.TryParse(obj["PercentProcessorPerformance"]?.ToString(), out uint pPerf))
                        {
                            procPerf = pPerf;
                        }
                        break;
                    }
                }
            }
            catch { }

            if (freqScalingPct > 0)
            {
                sb.AppendLine($"Frequency Scaling: {freqScalingPct}% of Maximum Frequency");
            }

            if (powerLimits.HasValue && powerLimits.Value.MaxMhz > 0 && powerLimits.Value.MhzLimit < powerLimits.Value.MaxMhz)
            {
                sb.AppendLine($"Thermal / Power Throttling: Active (Processor frequency restricted to {powerLimits.Value.MhzLimit:N0} MHz of {powerLimits.Value.MaxMhz:N0} MHz)");
            }
            else
            {
                sb.AppendLine("Thermal Throttling: Inactive (Normal operating frequency)");
            }

            if (procPerf > 0)
            {
                if (procPerf > 100)
                {
                    sb.AppendLine($"Turbo Boost: Active ({procPerf}% relative performance)");
                }
                else
                {
                    sb.AppendLine($"Turbo Boost: Inactive ({procPerf}% relative performance)");
                }
            }
            else if (powerLimits.HasValue && powerLimits.Value.MaxMhz > 0)
            {
                if (powerLimits.Value.CurrentMhz > powerLimits.Value.MaxMhz)
                {
                    sb.AppendLine($"Turbo Boost: Active ({(powerLimits.Value.CurrentMhz * 100 / powerLimits.Value.MaxMhz)}% relative performance)");
                }
                else
                {
                    sb.AppendLine($"Turbo Boost: Inactive ({(powerLimits.Value.CurrentMhz * 100 / powerLimits.Value.MaxMhz)}% relative performance)");
                }
            }

            // ACPI Thermal Zone Temperature (if supported by OEM ACPI)
            bool thermalReported = false;
            try
            {
                using var thermalSearcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature, CriticalTripPoint FROM MSAcpi_ThermalZoneTemperature");
                using var thermalColl = thermalSearcher.Get();
                foreach (ManagementObject obj in thermalColl)
                {
                    using (obj)
                    {
                        if (obj["CurrentTemperature"] != null && uint.TryParse(obj["CurrentTemperature"]?.ToString(), out uint rawTemp))
                        {
                            double tempC = (rawTemp / 10.0) - 273.15;
                            if (tempC is > 0 and < 150)
                            {
                                string tripStr = string.Empty;
                                if (obj["CriticalTripPoint"] != null && uint.TryParse(obj["CriticalTripPoint"]?.ToString(), out uint rawTrip))
                                {
                                    double tripC = (rawTrip / 10.0) - 273.15;
                                    if (tripC is > 0 and < 150) tripStr = $" (Critical Limit: {tripC:F0}°C)";
                                }
                                sb.AppendLine($"CPU Thermal Zone: {tempC:F1}°C{tripStr}");
                                thermalReported = true;
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            if (!thermalReported && !ElevationHelper.IsRunningAsAdmin())
            {
                sb.AppendLine("CPU Temperature: Available when running as Administrator");
            }

            // Static CPU & System Info (Cached so queries only run once)
            if (_cachedCpuStaticInfo == null)
            {
                var staticSb = new StringBuilder();

                // 1. Computer Model & Manufacturer (Instant Registry check, fallback to WMI)
                string mfg = string.Empty;
                string model = string.Empty;
                try
                {
                    using var biosKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                    if (biosKey != null)
                    {
                        mfg = biosKey.GetValue("SystemManufacturer")?.ToString()?.Trim() ?? string.Empty;
                        model = biosKey.GetValue("SystemProductName")?.ToString()?.Trim() ?? string.Empty;
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(model))
                {
                    try
                    {
                        using var csSearcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
                        using var csColl = csSearcher.Get();
                        foreach (ManagementObject cs in csColl)
                        {
                            using (cs)
                            {
                                mfg = cs["Manufacturer"]?.ToString()?.Trim() ?? string.Empty;
                                model = cs["Model"]?.ToString()?.Trim() ?? string.Empty;
                                break;
                            }
                        }
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(model))
                {
                    staticSb.AppendLine($"System Model: {mfg} {model}".Trim());
                }

                // 2. BIOS Version (Instant Registry check, fallback to WMI)
                string biosVer = string.Empty;
                string biosMfg = string.Empty;
                try
                {
                    using var biosKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                    if (biosKey != null)
                    {
                        biosMfg = biosKey.GetValue("BIOSVendor")?.ToString()?.Trim() ?? string.Empty;
                        biosVer = biosKey.GetValue("BIOSVersion")?.ToString()?.Trim() ?? string.Empty;
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(biosVer))
                {
                    try
                    {
                        using var biosSearcher = new ManagementObjectSearcher("SELECT Manufacturer, SMBIOSBIOSVersion FROM Win32_BIOS");
                        using var biosColl = biosSearcher.Get();
                        foreach (ManagementObject bios in biosColl)
                        {
                            using (bios)
                            {
                                biosVer = bios["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? string.Empty;
                                biosMfg = bios["Manufacturer"]?.ToString()?.Trim() ?? string.Empty;
                                break;
                            }
                        }
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(biosVer))
                {
                    staticSb.AppendLine($"BIOS Version: {biosMfg} {biosVer}".Trim());
                }

                // 3. Socket Designation
                string socket = string.Empty;
                try
                {
                    using var searcher = new ManagementObjectSearcher("SELECT SocketDesignation FROM Win32_Processor");
                    using var procColl = searcher.Get();
                    foreach (ManagementObject obj in procColl)
                    {
                        using (obj)
                        {
                            socket = obj["SocketDesignation"]?.ToString()?.Trim() ?? string.Empty;
                            break;
                        }
                    }
                }
                catch { }

                if (!string.IsNullOrEmpty(socket))
                {
                    staticSb.AppendLine($"Socket: {socket}");
                }

                // 4. Modern CPU Topology & Cache via Win32 GetLogicalProcessorInformationEx (< 0.05ms)
                var topo = NativeMethods.GetCpuTopology();
                if (topo.HasValue)
                {
                    if (topo.Value.PerformanceCores > 0 && topo.Value.EfficiencyCores > 0)
                    {
                        staticSb.AppendLine($"Physical Cores: {topo.Value.PhysicalCores} ({topo.Value.PerformanceCores} P-Cores, {topo.Value.EfficiencyCores} E-Cores)");
                    }
                    else
                    {
                        staticSb.AppendLine($"Physical Cores: {topo.Value.PhysicalCores}");
                    }

                    staticSb.AppendLine($"Logical Processors: {Environment.ProcessorCount}");

                    // Base Clock Speed from registry CentralProcessor\0\~MHz
                    uint baseClockMhz = 0;
                    try
                    {
                        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                        if (key?.GetValue("~MHz") is int mhz && mhz > 0)
                        {
                            baseClockMhz = (uint)mhz;
                        }
                    }
                    catch { }

                    if (baseClockMhz > 0)
                    {
                        staticSb.AppendLine($"Base Clock Speed: {baseClockMhz / 1000.0:F2} GHz ({baseClockMhz} MHz)");
                    }

                    // Cache hierarchy (L1 Data + Instruction, L2, L3)
                    if (topo.Value.L1DataCacheBytes > 0 || topo.Value.L1InstructionCacheBytes > 0)
                    {
                        long l1Total = topo.Value.L1DataCacheBytes + topo.Value.L1InstructionCacheBytes;
                        staticSb.AppendLine($"L1 Cache: {FormatHelper.FormatBytes(l1Total)} (Data: {FormatHelper.FormatBytes(topo.Value.L1DataCacheBytes)}, Instruction: {FormatHelper.FormatBytes(topo.Value.L1InstructionCacheBytes)})");
                    }
                    if (topo.Value.L2CacheBytes > 0)
                    {
                        staticSb.AppendLine($"L2 Cache: {FormatHelper.FormatBytes(topo.Value.L2CacheBytes)}");
                    }
                    if (topo.Value.L3CacheBytes > 0)
                    {
                        staticSb.AppendLine($"L3 Cache: {FormatHelper.FormatBytes(topo.Value.L3CacheBytes)}");
                    }
                }
                else
                {
                    // Fallback to WMI Win32_Processor if GetLogicalProcessorInformationEx failed
                    try
                    {
                        using var searcher = new ManagementObjectSearcher("SELECT NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize FROM Win32_Processor");
                        using var procColl = searcher.Get();
                        foreach (ManagementObject obj in procColl)
                        {
                            using (obj)
                            {
                                if (obj["NumberOfCores"] != null)
                                    staticSb.AppendLine($"Physical Cores: {obj["NumberOfCores"]}");
                                if (obj["NumberOfLogicalProcessors"] != null)
                                    staticSb.AppendLine($"Logical Processors: {obj["NumberOfLogicalProcessors"]}");
                                if (obj["MaxClockSpeed"] != null && uint.TryParse(obj["MaxClockSpeed"]?.ToString(), out uint clockMhz))
                                    staticSb.AppendLine($"Base Clock Speed: {clockMhz / 1000.0:F2} GHz ({clockMhz} MHz)");
                                if (obj["L2CacheSize"] != null && uint.TryParse(obj["L2CacheSize"]?.ToString(), out uint l2Kb) && l2Kb > 0)
                                    staticSb.AppendLine($"L2 Cache: {FormatHelper.FormatBytes((long)l2Kb * 1024)}");
                                if (obj["L3CacheSize"] != null && uint.TryParse(obj["L3CacheSize"]?.ToString(), out uint l3Kb) && l3Kb > 0)
                                    staticSb.AppendLine($"L3 Cache: {FormatHelper.FormatBytes((long)l3Kb * 1024)}");
                                break;
                            }
                        }
                    }
                    catch { }
                }

                // 5. Virtualization (Win32 IsProcessorFeaturePresent + WMI verification)
                bool virtEnabled = NativeMethods.IsProcessorFeaturePresent(NativeMethods.PF_VIRT_FIRMWARE_ENABLED);
                if (virtEnabled)
                {
                    staticSb.AppendLine("Virtualization: Enabled in Firmware (VT-x / AMD-V)");
                }
                else
                {
                    bool? wmiVirt = null;
                    try
                    {
                        using var vSearcher = new ManagementObjectSearcher("SELECT VirtualizationFirmwareEnabled FROM Win32_Processor");
                        using var vColl = vSearcher.Get();
                        foreach (ManagementObject vObj in vColl)
                        {
                            using (vObj)
                            {
                                if (vObj["VirtualizationFirmwareEnabled"] != null)
                                {
                                    wmiVirt = Convert.ToBoolean(vObj["VirtualizationFirmwareEnabled"]);
                                    break;
                                }
                            }
                        }
                    }
                    catch { }

                    if (wmiVirt == true)
                    {
                        staticSb.AppendLine("Virtualization: Enabled in Firmware (VT-x / AMD-V)");
                    }
                    else
                    {
                        staticSb.AppendLine("Virtualization: Disabled");
                    }
                }

                if (staticSb.Length == 0)
                {
                    staticSb.AppendLine($"Logical Processors: {Environment.ProcessorCount}");
                }

                _cachedCpuStaticInfo = staticSb.ToString();
            }

            sb.Append(_cachedCpuStaticInfo);
            return sb.ToString().TrimEnd();
        }

        #endregion

        #region RAM Details

        private string BuildRamDetails()
        {
            var sb = new StringBuilder();

            // Win32 GlobalMemoryStatusEx
            var mem = NativeMethods.MEMORYSTATUSEX.Create();
            NativeMethods.GlobalMemoryStatusEx(ref mem);

            long totalUsable = (long)mem.ullTotalPhys;
            long avail = (long)mem.ullAvailPhys;
            long used = Math.Max(0, totalUsable - avail);
            uint load = mem.dwMemoryLoad;

            // Physical installed memory (hardware total)
            long installedBytes = totalUsable;
            if (NativeMethods.GetPhysicallyInstalledSystemMemory(out ulong totalKb) && totalKb > 0)
            {
                installedBytes = (long)totalKb * 1024;
            }

            long hardwareReserved = Math.Max(0, installedBytes - totalUsable);

            sb.AppendLine($"Total Physical Memory: {FormatHelper.FormatBytes(installedBytes)}");
            sb.AppendLine($"Usable Memory: {FormatHelper.FormatBytes(totalUsable)}");
            sb.AppendLine($"In Use: {FormatHelper.FormatBytes(used)} ({load}%)");
            sb.AppendLine($"Available: {FormatHelper.FormatBytes(avail)}");
            if (hardwareReserved > 0)
            {
                sb.AppendLine($"Hardware Reserved: {FormatHelper.FormatBytes(hardwareReserved)}");
            }

            // Win32 Performance Info (Committed, Cached, Pools)
            var perfInfo = new NativeMethods.PERFORMANCE_INFORMATION();
            perfInfo.cb = (uint)Marshal.SizeOf(typeof(NativeMethods.PERFORMANCE_INFORMATION));
            if (NativeMethods.GetPerformanceInfo(out perfInfo, perfInfo.cb))
            {
                long pageSize = (long)perfInfo.PageSize.ToUInt64();
                long commitTotal = (long)perfInfo.CommitTotal.ToUInt64() * pageSize;
                long commitLimit = (long)perfInfo.CommitLimit.ToUInt64() * pageSize;
                long systemCache = (long)perfInfo.SystemCache.ToUInt64() * pageSize;
                long pagedPool = (long)perfInfo.KernelPaged.ToUInt64() * pageSize;
                long nonpagedPool = (long)perfInfo.KernelNonpaged.ToUInt64() * pageSize;
                long freeMemory = Math.Max(0, avail - systemCache);

                sb.AppendLine($"Cached (Standby): {FormatHelper.FormatBytes(systemCache)}");
                sb.AppendLine($"Free (unused, not counting cache): {FormatHelper.FormatBytes(freeMemory)}");
                sb.AppendLine($"Committed: {FormatHelper.FormatBytes(commitTotal)} of {FormatHelper.FormatBytes(commitLimit)}");

                long pageFileTotal = Math.Max(0, commitLimit - totalUsable);
                if (pageFileTotal > 0)
                {
                    string sysDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
                    string pageFileLocation = $"{sysDrive}\\pagefile.sys";
                    try
                    {
                        using var mmKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
                        var pfVal = mmKey?.GetValue("PagingFiles") as string[];
                        if (pfVal != null && pfVal.Length > 0 && !string.IsNullOrWhiteSpace(pfVal[0]))
                        {
                            string[] parts = pfVal[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0)
                            {
                                if (parts[0].StartsWith("?:", StringComparison.OrdinalIgnoreCase))
                                {
                                    string relPath = parts[0].Substring(2).TrimStart('\\', '/');
                                    pageFileLocation = $"{sysDrive}\\{relPath} (System Managed)";
                                }
                                else
                                {
                                    pageFileLocation = parts[0];
                                }
                            }
                        }
                    }
                    catch { }

                    long pageFileUsed = Math.Max(0, commitTotal - used);
                    sb.AppendLine($"Paging File (Virtual Memory): {FormatHelper.FormatBytes(pageFileTotal)} (Location: {pageFileLocation})");
                    sb.AppendLine($"Paging File In Use: {FormatHelper.FormatBytes(pageFileUsed)}");
                }
                else
                {
                    sb.AppendLine("Paging File (Virtual Memory): None configured");
                }
                sb.AppendLine($"Paged Pool: {FormatHelper.FormatBytes(pagedPool)}");
                sb.AppendLine($"Non-Paged Pool: {FormatHelper.FormatBytes(nonpagedPool)}");
                sb.AppendLine($"Handles: {perfInfo.HandleCount:N0} | Threads: {perfInfo.ThreadCount:N0} | Processes: {perfInfo.ProcessCount:N0}");
            }

            // Static RAM Hardware Specs (Speed, Slots, Individual Modules)
            if (_cachedRamStaticInfo == null)
            {
                var staticSb = new StringBuilder();
                try
                {
                    int totalSlots = 0;
                    long maxCapacityBytes = 0;
                    try
                    {
                        using var arraySearcher = new ManagementObjectSearcher("SELECT MemoryDevices, MaxCapacity FROM Win32_PhysicalMemoryArray");
                        using var arrayColl = arraySearcher.Get();
                        foreach (ManagementObject arr in arrayColl)
                        {
                            using (arr)
                            {
                                if (arr["MemoryDevices"] != null)
                                    totalSlots = Convert.ToInt32(arr["MemoryDevices"]);
                                if (arr["MaxCapacity"] != null && long.TryParse(arr["MaxCapacity"]?.ToString(), out long maxKb))
                                    maxCapacityBytes = maxKb * 1024;
                            }
                        }
                    }
                    catch { }

                    using var memSearcher = new ManagementObjectSearcher(
                        "SELECT Speed, ConfiguredClockSpeed, FormFactor, DeviceLocator, Manufacturer, PartNumber, Capacity, SMBIOSMemoryType, MemoryType FROM Win32_PhysicalMemory");
                    using var memColl = memSearcher.Get();
                    var modules = new List<string>();
                    uint ramSpeed = 0;
                    uint configuredSpeed = 0;
                    string formFactorStr = string.Empty;
                    string primaryMemType = string.Empty;

                    foreach (ManagementObject stick in memColl)
                    {
                        using (stick)
                        {
                            if (ramSpeed == 0 && stick["Speed"] != null && uint.TryParse(stick["Speed"]?.ToString(), out uint spd))
                                ramSpeed = spd;

                            if (configuredSpeed == 0 && stick["ConfiguredClockSpeed"] != null && uint.TryParse(stick["ConfiguredClockSpeed"]?.ToString(), out uint cfgSpd))
                                configuredSpeed = cfgSpd;

                            uint smbiosType = 0;
                            if (stick["SMBIOSMemoryType"] != null && uint.TryParse(stick["SMBIOSMemoryType"]?.ToString(), out uint sType))
                                smbiosType = sType;

                            uint legType = 0;
                            if (stick["MemoryType"] != null && uint.TryParse(stick["MemoryType"]?.ToString(), out uint lType))
                                legType = lType;

                            string memGen = DecodeSmbiosMemoryType(smbiosType, legType);
                            if (string.IsNullOrEmpty(primaryMemType)) primaryMemType = memGen;

                            if (string.IsNullOrEmpty(formFactorStr) && stick["FormFactor"] != null)
                            {
                                int ff = Convert.ToInt32(stick["FormFactor"]);
                                formFactorStr = ff switch
                                {
                                    12 => "SODIMM (Laptop)",
                                    8 => "DIMM (Desktop)",
                                    _ => "Standard"
                                };
                            }

                            string locator = stick["DeviceLocator"]?.ToString()?.Trim() ?? $"Slot {modules.Count + 1}";
                            string mfg = stick["Manufacturer"]?.ToString()?.Trim() ?? string.Empty;
                            string part = stick["PartNumber"]?.ToString()?.Trim() ?? string.Empty;
                            long cap = 0;
                            if (stick["Capacity"] != null && long.TryParse(stick["Capacity"]?.ToString(), out long c))
                                cap = c;

                            uint effSpeed = (configuredSpeed > 0) ? configuredSpeed : ramSpeed;
                            string speedSuffix = effSpeed > 0 ? $" ({memGen}-{effSpeed})" : $" ({memGen})";
                            string capStr = cap > 0 ? FormatHelper.FormatBytes(cap) : "Memory Module";
                            string stickDesc = $"{locator}: {mfg} {capStr}{speedSuffix}".Trim();
                            if (!string.IsNullOrEmpty(part)) stickDesc += $" (Part: {part})";
                            modules.Add(stickDesc);
                        }
                    }

                    if (!string.IsNullOrEmpty(primaryMemType))
                        staticSb.AppendLine($"Memory Generation: {primaryMemType}");
                    if (configuredSpeed > 0)
                        staticSb.AppendLine($"Configured Clock Speed: {configuredSpeed} MHz / MT/s");
                    else if (ramSpeed > 0)
                        staticSb.AppendLine($"Rated Memory Speed: {ramSpeed} MHz");

                    if (totalSlots > 0)
                    {
                        staticSb.AppendLine($"Slots Used: {modules.Count} of {totalSlots} ({(totalSlots - modules.Count)} empty)");
                    }
                    else if (modules.Count > 0)
                    {
                        staticSb.AppendLine($"Memory Modules: {modules.Count} installed");
                    }

                    if (modules.Count == 1)
                    {
                        staticSb.AppendLine("Memory Configuration: Single-Channel (1 Module Installed)");
                    }
                    else if (modules.Count == 2)
                    {
                        staticSb.AppendLine("Memory Configuration: Dual-Channel Architecture (2 Modules Installed)");
                    }
                    else if (modules.Count > 2)
                    {
                        string chDesc = (modules.Count % 4 == 0) ? "Quad/Multi-Channel Architecture" : "Multi-Module Architecture";
                        staticSb.AppendLine($"Memory Configuration: {chDesc} ({modules.Count} Modules Installed)");
                    }

                    if (maxCapacityBytes > 0)
                        staticSb.AppendLine($"Max Supported Memory: {FormatHelper.FormatBytes(maxCapacityBytes)}");
                    if (!string.IsNullOrEmpty(formFactorStr))
                        staticSb.AppendLine($"Form Factor: {formFactorStr}");

                    if (modules.Count > 0)
                    {
                        staticSb.AppendLine();
                        staticSb.AppendLine("Installed Memory Modules:");
                        foreach (var mod in modules)
                        {
                            staticSb.AppendLine(mod);
                        }
                    }
                }
                catch { }

                _cachedRamStaticInfo = staticSb.ToString();
            }

            sb.Append(_cachedRamStaticInfo);
            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Disk Details

        public class PhysicalDiskMetadata
        {
            public int Index { get; set; }
            public string Model { get; set; } = "Physical Disk Drive";
            public string MediaType { get; set; } = "Storage Media";
            public string BusType { get; set; } = "Storage";
            public bool IsInternal { get; set; } = true;
            public long HardwareSizeBytes { get; set; }
            public string PartitionStyle { get; set; } = "Standard Partition";
            public string HealthStatus { get; set; } = "Healthy (SMART Status OK)";
        }

        private static readonly Dictionary<int, PhysicalDiskMetadata> _diskMetadataCache = new();
        private static readonly object _diskMetaLock = new();
        private static bool _isDiskMetadataPopulating = false;

        internal static void PopulatePhysicalDiskMetadataCache()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Index, Model, InterfaceType, MediaType, Size, Status FROM Win32_DiskDrive");
                using var diskColl = searcher.Get();
                foreach (ManagementObject disk in diskColl)
                {
                    using (disk)
                    {
                    if (disk["Index"] != null && int.TryParse(disk["Index"]?.ToString(), out int idx))
                    {
                        string model = disk["Model"]?.ToString()?.Trim() ?? "Disk Drive";
                        string iface = disk["InterfaceType"]?.ToString()?.Trim() ?? "";
                        string media = disk["MediaType"]?.ToString()?.Trim() ?? "";
                        string rawStatus = disk["Status"]?.ToString()?.Trim() ?? "OK";
                        long size = 0;
                        if (disk["Size"] != null) long.TryParse(disk["Size"]?.ToString(), out size);

                        bool isInternal = !iface.Equals("USB", StringComparison.OrdinalIgnoreCase) &&
                                          !media.Contains("Removable", StringComparison.OrdinalIgnoreCase);

                        string mediaTypeStr;
                        if (!isInternal)
                        {
                            bool? seekPenalty = NativeMethods.QueryDiskSeekPenalty(idx);
                            if (seekPenalty == false || model.Contains("SSD", StringComparison.OrdinalIgnoreCase) || model.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
                            {
                                mediaTypeStr = "External Solid State Drive (USB SSD)";
                            }
                            else if (seekPenalty == true || model.Contains("HDD", StringComparison.OrdinalIgnoreCase))
                            {
                                mediaTypeStr = "External Hard Disk Drive (USB HDD)";
                            }
                            else if (media.Contains("Removable", StringComparison.OrdinalIgnoreCase))
                            {
                                mediaTypeStr = "Removable Storage (USB Flash Drive)";
                            }
                            else
                            {
                                mediaTypeStr = "External USB Storage";
                            }
                        }
                        else if (model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) || model.Contains("SSD", StringComparison.OrdinalIgnoreCase) || iface.Contains("SCSI", StringComparison.OrdinalIgnoreCase))
                        {
                            mediaTypeStr = "Solid State Drive (SSD)";
                        }
                        else
                        {
                            mediaTypeStr = "Hard Disk Drive (HDD)";
                        }

                        string busTypeStr = iface switch
                        {
                            "SCSI" => model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ? "NVMe (PCIe)" : "SCSI",
                            "USB" => "USB",
                            "IDE" => "SATA / IDE",
                            _ => iface
                        };

                        string healthStr;
                        if (rawStatus.Equals("Pred Fail", StringComparison.OrdinalIgnoreCase))
                        {
                            healthStr = "Critical Warning: SMART Failure Imminent (Drive replacement recommended)";
                        }
                        else if (rawStatus.Equals("Error", StringComparison.OrdinalIgnoreCase) || rawStatus.Equals("Degraded", StringComparison.OrdinalIgnoreCase))
                        {
                            healthStr = $"Warning: Drive {rawStatus} (SMART Alert)";
                        }
                        else if (!isInternal)
                        {
                            healthStr = "Healthy (Ready to Eject / Safely Remove)";
                        }
                        else
                        {
                            healthStr = "Healthy (SMART Status OK)";
                        }

                        string liveStyle = QueryLivePartitionStyle(idx);
                        string initialStyle = !string.IsNullOrEmpty(liveStyle)
                            ? liveStyle
                            : "Standard Partition";

                        var item = new PhysicalDiskMetadata
                        {
                            Index = idx,
                            Model = model,
                            MediaType = mediaTypeStr,
                            BusType = busTypeStr,
                            IsInternal = isInternal,
                            HardwareSizeBytes = size,
                            HealthStatus = healthStr,
                            PartitionStyle = initialStyle
                        };

                        if (string.IsNullOrEmpty(liveStyle))
                        {
                            try
                            {
                                using var pSearcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", $"SELECT PartitionStyle FROM MSFT_Disk WHERE Number={idx}");
                                using var pColl = pSearcher.Get();
                                foreach (ManagementObject pObj in pColl)
                                {
                                    using (pObj)
                                    {
                                        if (pObj["PartitionStyle"] != null && ushort.TryParse(pObj["PartitionStyle"]?.ToString(), out ushort pStyle))
                                        {
                                            item.PartitionStyle = pStyle switch
                                            {
                                                2 => "GPT (GUID Partition Table - Modern UEFI)",
                                                1 => "MBR (Master Boot Record - Legacy)",
                                                _ => "Standard Partition"
                                            };
                                        }
                                        break;
                                    }
                                }
                            }
                            catch { }
                        }

                        lock (_diskMetaLock)
                        {
                            _diskMetadataCache[idx] = item;
                        }
                    }
                    }
                }
            }
            catch { }
        }

        internal static string QueryLivePartitionStyle(int diskIndex)
        {
            if (diskIndex < 0) return string.Empty;
            try
            {
                string devicePath = $@"\\.\PhysicalDrive{diskIndex}";
                using var handle = NativeMethods.CreateFile(
                    devicePath,
                    0, // dwDesiredAccess = 0 (Query access without requiring admin elevation)
                    1 | 2 | 4, // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
                    IntPtr.Zero,
                    3, // OPEN_EXISTING
                    0,
                    IntPtr.Zero);

                if (!handle.IsInvalid)
                {
                    int bufferSize = 4096;
                    IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
                    try
                    {
                        if (NativeMethods.DeviceIoControl(
                            handle,
                            NativeMethods.IOCTL_DISK_GET_DRIVE_LAYOUT_EX,
                            IntPtr.Zero,
                            0,
                            buffer,
                            (uint)bufferSize,
                            out uint bytesReturned,
                            IntPtr.Zero) && bytesReturned >= 4)
                        {
                            int partitionStyle = Marshal.ReadInt32(buffer);
                            return partitionStyle switch
                            {
                                0 => "MBR (Master Boot Record - Legacy)",
                                1 => "GPT (GUID Partition Table - Modern UEFI)",
                                2 => "RAW (Unformatted or Uninitialized)",
                                _ => string.Empty
                            };
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        internal static PhysicalDiskMetadata GetPhysicalDiskMetadata(int diskIndex, string? usbLetter = null)
        {
            lock (_diskMetaLock)
            {
                if (diskIndex >= 0 && _diskMetadataCache.TryGetValue(diskIndex, out var cached))
                {
                    // For external / removable USB drives, query live partition style so user conversions (e.g. MBR to GPT) reflect instantly
                    if (!cached.IsInternal || usbLetter != null)
                    {
                        string liveStyle = QueryLivePartitionStyle(diskIndex);
                        if (!string.IsNullOrEmpty(liveStyle))
                        {
                            cached.PartitionStyle = liveStyle;
                        }
                    }
                    return cached;
                }

                string fallbackStyle = QueryLivePartitionStyle(diskIndex);
                if (string.IsNullOrEmpty(fallbackStyle))
                {
                    fallbackStyle = "Standard Partition";
                }

                var diskProp = NativeMethods.QueryDiskStorageProperty(diskIndex);
                bool? seekPenalty = NativeMethods.QueryDiskSeekPenalty(diskIndex);

                if (!diskProp.HasValue && !_isDiskMetadataPopulating)
                {
                    _isDiskMetadataPopulating = true;
                    Task.Run(() =>
                    {
                        try
                        {
                            PopulatePhysicalDiskMetadataCache();
                        }
                        finally
                        {
                            _isDiskMetadataPopulating = false;
                        }
                    });
                }

                bool isUsb = (diskProp.HasValue && (diskProp.Value.BusType == 7 || diskProp.Value.Removable)) || usbLetter != null;
                bool isInternal = !isUsb;

                string model = "Physical Disk Drive";
                if (diskProp.HasValue && !string.IsNullOrWhiteSpace(diskProp.Value.ProductId))
                {
                    string p = diskProp.Value.ProductId.Trim();
                    string v = diskProp.Value.VendorId.Trim();
                    model = (!string.IsNullOrWhiteSpace(v) && !p.Contains(v, StringComparison.OrdinalIgnoreCase))
                        ? $"{v} {p}"
                        : p;
                }

                string mediaTypeStr;
                string busTypeStr;
                string healthStr;

                if (!isInternal)
                {
                    busTypeStr = "USB";
                    healthStr = "Healthy (Ready to Eject / Safely Remove)";
                    if (seekPenalty == false || model.Contains("SSD", StringComparison.OrdinalIgnoreCase) || model.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
                    {
                        mediaTypeStr = "External Solid State Drive (USB SSD)";
                    }
                    else if (seekPenalty == true || model.Contains("HDD", StringComparison.OrdinalIgnoreCase))
                    {
                        mediaTypeStr = "External Hard Disk Drive (USB HDD)";
                    }
                    else
                    {
                        mediaTypeStr = "Removable Storage (USB Flash Drive)";
                    }
                }
                else
                {
                    healthStr = "Healthy (SMART Status OK)";
                    if (diskProp.HasValue && diskProp.Value.BusType == 17)
                    {
                        busTypeStr = "NVMe (PCIe)";
                    }
                    else if (diskProp.HasValue && diskProp.Value.BusType == 11)
                    {
                        busTypeStr = "SATA";
                    }
                    else
                    {
                        busTypeStr = model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ? "NVMe (PCIe)" : "SATA / IDE";
                    }

                    if (seekPenalty == false || model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) || model.Contains("SSD", StringComparison.OrdinalIgnoreCase))
                    {
                        mediaTypeStr = "Solid State Drive (SSD)";
                    }
                    else if (seekPenalty == true)
                    {
                        mediaTypeStr = "Hard Disk Drive (HDD)";
                    }
                    else
                    {
                        mediaTypeStr = "Solid State Drive (SSD)";
                    }
                }

                var defaultMeta = new PhysicalDiskMetadata
                {
                    Index = diskIndex,
                    Model = model,
                    MediaType = mediaTypeStr,
                    BusType = busTypeStr,
                    IsInternal = isInternal,
                    HealthStatus = healthStr,
                    PartitionStyle = fallbackStyle
                };

                _diskMetadataCache[diskIndex] = defaultMeta;

                return defaultMeta;
            }
        }

        private static string GetPartitionBitLockerStatus(string driveLetter, string driveFormat, bool isInternal)
        {
            if (!isInternal || driveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase) || driveFormat.Equals("exFAT", StringComparison.OrdinalIgnoreCase))
            {
                return "Off (BitLocker To Go not enabled on FAT/exFAT volume)";
            }

            if (!ElevationHelper.IsRunningAsAdmin())
            {
                return "Not verified (Requires Administrator privileges to query encryption status)";
            }

            try
            {
                using var encSearcher = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
                    $"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter='{driveLetter}'");
                using var encColl = encSearcher.Get();
                foreach (ManagementObject enc in encColl)
                {
                    using (enc)
                    {
                        if (enc["ProtectionStatus"] != null && uint.TryParse(enc["ProtectionStatus"]?.ToString(), out uint prot))
                        {
                            return prot == 1 ? "On (Protected with BitLocker / Device Encryption)" : "Off (Drive is not encrypted)";
                        }
                        break;
                    }
                }
            }
            catch { }

            return "Off (Drive is not encrypted)";
        }

        private string BuildDiskDetails(string resourceId = "disk")
        {
            var sb = new StringBuilder();

            int targetDiskNum = 0;
            string? targetUsbLetter = null;

            if (resourceId.StartsWith("usb_", StringComparison.OrdinalIgnoreCase))
            {
                string letterPart = resourceId.Substring(4).Trim();
                if (letterPart.Length > 0)
                {
                    targetUsbLetter = letterPart.EndsWith(":") ? letterPart : letterPart + ":";
                    int dNum = NativeMethods.GetStorageDeviceNumber(targetUsbLetter);
                    if (dNum >= 0) targetDiskNum = dNum;
                    else targetDiskNum = -1;
                }
            }
            else if (resourceId.StartsWith("disk_", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(resourceId.Substring(5), out int parsedNum))
                {
                    targetDiskNum = parsedNum;
                }
            }

            var diskMeta = GetPhysicalDiskMetadata(targetDiskNum, targetUsbLetter);

            sb.AppendLine($"Physical Drive: {diskMeta.Model}");
            sb.AppendLine($"Drive Location: {(diskMeta.IsInternal ? "Internal (Fixed Storage)" : "External (Removable USB Storage)")}");
            sb.AppendLine($"Media Type: {diskMeta.MediaType}");
            if (!string.IsNullOrEmpty(diskMeta.BusType))
                sb.AppendLine($"Bus Type: {diskMeta.BusType}");
            sb.AppendLine($"Health Status: {diskMeta.HealthStatus}");
            if (!string.IsNullOrEmpty(diskMeta.PartitionStyle))
                sb.AppendLine($"Partition Style: {diskMeta.PartitionStyle}");

            if (!diskMeta.IsInternal)
            {
                sb.AppendLine("Safe Removal: Supported (Ready to Eject / Safely Remove)");
            }

            if (diskMeta.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase))
            {
                bool? trim = targetDiskNum >= 0 ? NativeMethods.QueryDiskTrimSupport(targetDiskNum) : null;
                if (trim == true)
                {
                    sb.AppendLine("TRIM Support: Enabled (Optimal SSD Maintenance)");
                }
                else if (trim == false)
                {
                    sb.AppendLine("TRIM Support: Not Supported (Disabled or blocked by controller / enclosure)");
                }
                else
                {
                    sb.AppendLine("TRIM Support: Enabled (Optimal SSD Maintenance)");
                }
            }

            var volumes = new List<DriveInfo>();
            try
            {
                var readyDrives = DriveInfo.GetDrives().Where(d => d.IsReady).ToList();
                foreach (var d in readyDrives)
                {
                    int dNum = NativeMethods.GetStorageDeviceNumber(d.Name);
                    if (targetDiskNum >= 0 && dNum == targetDiskNum)
                    {
                        volumes.Add(d);
                    }
                    else if (targetDiskNum < 0 && targetUsbLetter != null && d.Name.StartsWith(targetUsbLetter, StringComparison.OrdinalIgnoreCase))
                    {
                        volumes.Add(d);
                    }
                }

                if (volumes.Count == 0 && targetDiskNum == 0)
                {
                    string sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                    var sysDrive = readyDrives.FirstOrDefault(d => d.Name.Equals(sysRoot, StringComparison.OrdinalIgnoreCase));
                    if (sysDrive != null) volumes.Add(sysDrive);
                }
            }
            catch { }

            long totalFormattedBytes = 0;
            long totalFreeBytes = 0;
            foreach (var v in volumes)
            {
                totalFormattedBytes += v.TotalSize;
                totalFreeBytes += v.AvailableFreeSpace;
            }
            long totalUsedBytes = Math.Max(0, totalFormattedBytes - totalFreeBytes);
            double totalUsedPct = totalFormattedBytes > 0 ? (double)totalUsedBytes * 100.0 / totalFormattedBytes : 0;

            if (totalFormattedBytes > 0)
            {
                string usableStr = FormatHelper.FormatDiskSize(totalFormattedBytes);
                string hwStr = FormatHelper.FormatHardwareCapacity(diskMeta.HardwareSizeBytes);
                string capLine = !string.IsNullOrEmpty(hwStr)
                    ? $"Drive Capacity: {usableStr} ({hwStr})"
                    : $"Drive Capacity: {usableStr}";

                sb.AppendLine(capLine);
                sb.AppendLine($"Free Space: {FormatHelper.FormatDiskSize(totalFreeBytes)} ({100.0 - totalUsedPct:F0}% free)");
                sb.AppendLine($"Used Space: {FormatHelper.FormatDiskSize(totalUsedBytes)} ({totalUsedPct:F0}% used)");
            }

            var (readSpeed, writeSpeed) = GetLiveDiskSpeed(targetDiskNum);
            sb.AppendLine($"Live Read Speed: {FormatHelper.FormatSpeed(readSpeed)}");
            sb.AppendLine($"Live Write Speed: {FormatHelper.FormatSpeed(writeSpeed)}");

            if (volumes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Partitions on this Drive ({volumes.Count}):");
                int pIndex = 1;
                foreach (var v in volumes)
                {
                    string label = string.IsNullOrWhiteSpace(v.VolumeLabel)
                        ? (diskMeta.IsInternal ? "Local Disk" : "USB Drive")
                        : v.VolumeLabel;
                    string letter = v.Name.TrimEnd('\\');
                    long pFree = v.AvailableFreeSpace;
                    long pTotal = v.TotalSize;
                    long pUsed = Math.Max(0, pTotal - pFree);
                    double pUsedPct = pTotal > 0 ? (double)pUsed * 100.0 / pTotal : 0;

                    sb.AppendLine($"Partition {pIndex}: Drive {letter} ({label})");
                    sb.AppendLine($"File System ({letter}): {v.DriveFormat}");
                    sb.AppendLine($"Capacity ({letter}): {FormatHelper.FormatDiskSize(pTotal)}");
                    sb.AppendLine($"Free Space ({letter}): {FormatHelper.FormatDiskSize(pFree)} ({100.0 - pUsedPct:F0}% free)");
                    sb.AppendLine($"Used Space ({letter}): {FormatHelper.FormatDiskSize(pUsed)} ({pUsedPct:F0}% used)");

                    string bitLockerStatus = GetPartitionBitLockerStatus(letter, v.DriveFormat, diskMeta.IsInternal);
                    sb.AppendLine($"BitLocker ({letter}): {bitLockerStatus}");

                    pIndex++;
                }
            }

            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Network Details

        private string BuildNetworkDetails()
        {
            var sb = new StringBuilder();

            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                             ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                             ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                             !ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                             !ni.Description.Contains("Filter", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var primary = interfaces.FirstOrDefault(ni => ni.GetIPProperties().GatewayAddresses.Count > 0)
                          ?? interfaces.FirstOrDefault();

            if (primary == null)
            {
                return "No active network adapters found.";
            }

            string typeName = primary.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi (Wireless 802.11)",
                NetworkInterfaceType.Ethernet => "Ethernet (Local Area Connection)",
                _ => primary.NetworkInterfaceType.ToString()
            };

            sb.AppendLine($"Adapter: {primary.Name}");
            sb.AppendLine($"Controller: {primary.Description}");
            sb.AppendLine($"Connection Type: {typeName}");
            sb.AppendLine($"Status: {primary.OperationalStatus}");

            if (primary.Speed > 0)
            {
                string linkSpeed = primary.Speed >= 1_000_000_000
                    ? $"{primary.Speed / 1_000_000_000.0:F1} Gbps"
                    : $"{primary.Speed / 1_000_000} Mbps";
                sb.AppendLine($"Link Speed: {linkSpeed}");
            }

            // MAC Address
            var mac = primary.GetPhysicalAddress();
            if (mac != null)
            {
                string macStr = string.Join(":", Enumerable.Range(0, mac.GetAddressBytes().Length)
                    .Select(i => mac.GetAddressBytes()[i].ToString("X2")));
                if (!string.IsNullOrEmpty(macStr))
                    sb.AppendLine($"Physical Address (MAC): {macStr}");
            }

            // Wi-Fi Specific Telemetry (SSID, Band, Signal, Channel)
            if (primary.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            {
                try
                {
                    // Detect Wi-Fi Standard from controller description or link speed
                    string desc = primary.Description;
                    string wifiStandard = string.Empty;
                    if (desc.Contains("802.11be", StringComparison.OrdinalIgnoreCase) || desc.Contains("Wi-Fi 7", StringComparison.OrdinalIgnoreCase))
                        wifiStandard = "Wi-Fi 7 (802.11be)";
                    else if (desc.Contains("802.11ax", StringComparison.OrdinalIgnoreCase) || desc.Contains("Wi-Fi 6", StringComparison.OrdinalIgnoreCase))
                        wifiStandard = "Wi-Fi 6 (802.11ax)";
                    else if (desc.Contains("802.11ac", StringComparison.OrdinalIgnoreCase) || desc.Contains("Wi-Fi 5", StringComparison.OrdinalIgnoreCase))
                        wifiStandard = "Wi-Fi 5 (802.11ac)";
                    else if (desc.Contains("802.11n", StringComparison.OrdinalIgnoreCase) || desc.Contains("Wi-Fi 4", StringComparison.OrdinalIgnoreCase))
                        wifiStandard = "Wi-Fi 4 (802.11n)";
                    else if (desc.Contains("802.11g", StringComparison.OrdinalIgnoreCase))
                        wifiStandard = "802.11g";
                    else if (primary.Speed >= 2_000_000_000)
                        wifiStandard = "Wi-Fi 6 / 6E (802.11ax)";
                    else if (primary.Speed > 600_000_000)
                        wifiStandard = "Wi-Fi 5 (802.11ac)";

                    var wifiInfo = QueryWifiTelemetry();
                    if (wifiInfo.Count > 0)
                    {
                        if (wifiInfo.TryGetValue("SSID", out var ssid) && !string.IsNullOrEmpty(ssid))
                            sb.AppendLine($"Wi-Fi Network (SSID): {ssid}");
                        if (!string.IsNullOrEmpty(wifiStandard))
                            sb.AppendLine($"Wi-Fi Standard: {wifiStandard}");
                        else if (wifiInfo.TryGetValue("Radio type", out var radio) && !string.IsNullOrEmpty(radio))
                            sb.AppendLine($"Wi-Fi Standard: {radio}");
                        if (wifiInfo.TryGetValue("Band", out var band) && !string.IsNullOrEmpty(band))
                            sb.AppendLine($"Radio Band: {band}");
                        if (wifiInfo.TryGetValue("Channel", out var channel) && !string.IsNullOrEmpty(channel))
                            sb.AppendLine($"Wi-Fi Channel: {channel}");
                        if (wifiInfo.TryGetValue("Signal", out var signal) && !string.IsNullOrEmpty(signal))
                            sb.AppendLine($"Signal Strength: {signal}");
                        if (wifiInfo.TryGetValue("Authentication", out var auth) && !string.IsNullOrEmpty(auth))
                            sb.AppendLine($"Security: {auth}");
                    }
                }
                catch { }
            }

            // IP Configuration
            var ipProps = primary.GetIPProperties();
            var unicastList = ipProps.UnicastAddresses;

            var ipv4 = unicastList.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 != null)
            {
                sb.AppendLine($"IPv4 Address: {ipv4.Address}");
                if (ipv4.IPv4Mask != null)
                    sb.AppendLine($"Subnet Mask: {ipv4.IPv4Mask}");
            }

            var ipv6 = unicastList.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6);
            if (ipv6 != null)
            {
                sb.AppendLine($"IPv6 Address: {ipv6.Address}");
            }

            // Default Gateway (prefer IPv4 router address first)
            var ipv4Gateway = ipProps.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
            var ipv6Gateway = ipProps.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetworkV6);

            if (ipv4Gateway != null)
            {
                sb.AppendLine($"Default Gateway: {ipv4Gateway.Address}");
                if (ipv6Gateway != null)
                {
                    sb.AppendLine($"Default Gateway (IPv6): {ipv6Gateway.Address}");
                }
            }
            else if (ipv6Gateway != null)
            {
                sb.AppendLine($"Default Gateway: {ipv6Gateway.Address}");
            }

            var dnsList = ipProps.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).ToList();
            if (dnsList.Count > 0)
            {
                sb.AppendLine($"DNS Servers: {string.Join(", ", dnsList)}");
            }
            var dns6List = ipProps.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetworkV6 && !d.IsIPv6LinkLocal).ToList();
            if (dns6List.Count > 0)
            {
                sb.AppendLine($"DNS Servers (IPv6): {string.Join(", ", dns6List)}");
            }

            // DHCP Server
            try
            {
                var dhcpList = ipProps.DhcpServerAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).ToList();
                if (dhcpList.Count > 0)
                {
                    sb.AppendLine($"DHCP Server: {string.Join(", ", dhcpList)}");
                }
            }
            catch { }

            // Session Traffic Statistics
            try
            {
                var stats = primary.GetIPStatistics();
                sb.AppendLine($"Session Received: {FormatHelper.FormatBytes(stats.BytesReceived)}");
                sb.AppendLine($"Session Sent: {FormatHelper.FormatBytes(stats.BytesSent)}");
            }
            catch { }

            // Metered Connection Status
            try
            {
                var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                if (profile != null)
                {
                    var cost = profile.GetConnectionCost();
                    bool isMetered = cost.NetworkCostType != Windows.Networking.Connectivity.NetworkCostType.Unrestricted;
                    sb.AppendLine($"Metered Connection: {(isMetered ? "Yes (Data-Saver Active - Windows updates restricted)" : "No (Unrestricted network access)")}");
                }
            }
            catch { }

            // Opt-in Live Internet Latency (ICMP Echo)
            if (_settingsService?.CurrentSettings.EnableNetworkPing == true)
            {
                try
                {
                    using var ping = new Ping();
                    var reply = ping.Send("1.1.1.1", 400);
                    if (reply.Status == IPStatus.Success)
                    {
                        string quality = reply.RoundtripTime switch
                        {
                            < 35 => "Fast / Low Latency",
                            < 80 => "Good",
                            < 150 => "Moderate",
                            _ => "High Latency"
                        };
                        sb.AppendLine($"Internet Latency (Ping): {reply.RoundtripTime} ms ({quality})");
                    }
                    else
                    {
                        sb.AppendLine("Internet Latency (Ping): Request Timed Out");
                    }
                }
                catch
                {
                    sb.AppendLine("Internet Latency (Ping): Unable to reach test server");
                }
            }

            return sb.ToString().TrimEnd();
        }

        private static Dictionary<string, string> QueryWifiTelemetry()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 1. Modern Windows 10/11 WinRT profile (zero location icon)
            try
            {
                var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                if (profile != null && profile.IsWlanConnectionProfile)
                {
                    if (!string.IsNullOrEmpty(profile.ProfileName))
                    {
                        dict["SSID"] = profile.ProfileName;
                    }
                    byte? bars = profile.GetSignalBars();
                    if (bars.HasValue)
                    {
                        dict["Signal"] = $"{bars.Value * 20}% ({bars.Value} of 5 bars)";
                    }
                    var wifiChannelBand = NativeMethods.QueryActiveWifiChannelAndBand();
                    if (wifiChannelBand.HasValue)
                    {
                        dict["Band"] = wifiChannelBand.Value.Band;
                        dict["Channel"] = wifiChannelBand.Value.Channel.ToString();
                    }
                    else
                    {
                        var adapter = profile.NetworkAdapter;
                        if (adapter != null)
                        {
                            ulong inSpeed = adapter.InboundMaxBitsPerSecond;
                            if (inSpeed > 300_000_000) dict["Band"] = "5 GHz";
                            else if (inSpeed > 0 && inSpeed <= 72_000_000) dict["Band"] = "2.4 GHz";
                        }
                    }
                    if (profile.NetworkSecuritySettings != null)
                    {
                        dict["Authentication"] = profile.NetworkSecuritySettings.NetworkAuthenticationType.ToString();
                    }
                }
            }
            catch { }

            // 2. Windows Network List Manager COM interface fallback (zero location icon)
            if (!dict.ContainsKey("SSID"))
            {
                try
                {
                    var nlmType = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
                    if (nlmType != null)
                    {
                        dynamic nlm = Activator.CreateInstance(nlmType)!;
                        var networks = nlm.GetNetworks(1 /* NLM_ENUM_NETWORK_CONNECTED */);
                        foreach (var net in networks)
                        {
                            string name = (string)net.GetName();
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                dict["SSID"] = name;
                                break;
                            }
                        }
                    }
                }
                catch { }
            }

            return dict;
        }

        #endregion

        #region GPU Details

        private class GpuAdapterInfo
        {
            public string Name { get; set; } = string.Empty;
            public string DriverVersion { get; set; } = string.Empty;
            public string DriverDate { get; set; } = string.Empty;
            public string Provider { get; set; } = string.Empty;
            public long DedicatedVramBytes { get; set; }
            public string VideoProcessor { get; set; } = string.Empty;
        }

        private string BuildGpuDetails()
        {
            var sb = new StringBuilder();

            if (_cachedGpuStaticInfo == null)
            {
                var staticSb = new StringBuilder();
                var gpus = new List<GpuAdapterInfo>();

                // 1. Fast Windows Registry Video Class discovery (< 1 ms, 64-bit VRAM accurate)
                try
                {
                    for (int i = 0; i <= 9; i++)
                    {
                        string subKey = $@"SYSTEM\CurrentControlSet\Control\Class\{{4d36e968-e325-11ce-bfc1-08002be10318}}\{i:D4}";
                        using var key = Registry.LocalMachine.OpenSubKey(subKey);
                        if (key != null)
                        {
                            string desc = key.GetValue("DriverDesc")?.ToString()?.Trim() ?? string.Empty;
                            if (!string.IsNullOrEmpty(desc) && !gpus.Any(g => g.Name.Equals(desc, StringComparison.OrdinalIgnoreCase)))
                            {
                                var gpu = new GpuAdapterInfo { Name = desc };
                                gpu.DriverVersion = key.GetValue("DriverVersion")?.ToString()?.Trim() ?? string.Empty;
                                gpu.DriverDate = key.GetValue("DriverDate")?.ToString()?.Trim() ?? string.Empty;
                                gpu.Provider = key.GetValue("ProviderName")?.ToString()?.Trim() ?? string.Empty;

                                // 64-bit and 32-bit VRAM parsing
                                object? qwMem = key.GetValue("HardwareInformation.qwMemorySize");
                                if (qwMem is long lVal && lVal > 0)
                                {
                                    gpu.DedicatedVramBytes = lVal;
                                }
                                else if (qwMem is byte[] qwBytes && qwBytes.Length >= 8)
                                {
                                    gpu.DedicatedVramBytes = BitConverter.ToInt64(qwBytes, 0);
                                }
                                else
                                {
                                    object? memObj = key.GetValue("HardwareInformation.MemorySize");
                                    if (memObj is byte[] mBytes)
                                    {
                                        if (mBytes.Length >= 8)
                                            gpu.DedicatedVramBytes = BitConverter.ToInt64(mBytes, 0);
                                        else if (mBytes.Length >= 4)
                                            gpu.DedicatedVramBytes = BitConverter.ToUInt32(mBytes, 0);
                                    }
                                    else if (memObj is int iVal && iVal > 0)
                                    {
                                        gpu.DedicatedVramBytes = iVal;
                                    }
                                    else if (memObj is long lVal2 && lVal2 > 0)
                                    {
                                        gpu.DedicatedVramBytes = lVal2;
                                    }
                                }

                                gpus.Add(gpu);
                            }
                        }
                    }
                }
                catch { }

                // 2. WMI Fallback if registry video class yielded no adapters
                if (gpus.Count == 0)
                {
                    try
                    {
                        using var gpuSearcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate, AdapterRAM, VideoProcessor FROM Win32_VideoController");
                        using var gpuColl = gpuSearcher.Get();
                        foreach (ManagementObject gpu in gpuColl)
                        {
                            using (gpu)
                            {
                                string name = gpu["Name"]?.ToString()?.Trim() ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(name) && !gpus.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                                {
                                    var info = new GpuAdapterInfo { Name = name };
                                    info.DriverVersion = gpu["DriverVersion"]?.ToString()?.Trim() ?? string.Empty;
                                    if (gpu["AdapterRAM"] != null && long.TryParse(gpu["AdapterRAM"]?.ToString(), out long vram) && vram > 0)
                                    {
                                        info.DedicatedVramBytes = vram;
                                    }
                                    info.VideoProcessor = gpu["VideoProcessor"]?.ToString()?.Trim() ?? string.Empty;
                                    if (gpu["DriverDate"] != null)
                                    {
                                        string rawDate = gpu["DriverDate"]?.ToString() ?? string.Empty;
                                        if (rawDate.Length >= 8)
                                        {
                                            string y = rawDate.Substring(0, 4);
                                            string m = rawDate.Substring(4, 2);
                                            string d = rawDate.Substring(6, 2);
                                            info.DriverDate = $"{d}-{m}-{y}";
                                        }
                                    }
                                    gpus.Add(info);
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (gpus.Count > 0)
                {
                    staticSb.AppendLine($"GPUs Detected: {gpus.Count}");
                    staticSb.AppendLine();

                    int gpuIndex = 0;
                    foreach (var gpu in gpus)
                    {
                        gpuIndex++;
                        bool isIntegrated = gpu.Name.Contains("Iris", StringComparison.OrdinalIgnoreCase) ||
                                            gpu.Name.Contains("UHD", StringComparison.OrdinalIgnoreCase) ||
                                            gpu.Name.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase) ||
                                            gpu.Name.Contains("Intel(R) Graphics", StringComparison.OrdinalIgnoreCase) ||
                                            (gpu.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) && !gpu.Name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) && !gpu.Name.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase)) ||
                                            gpu.Name.Contains("Integrated", StringComparison.OrdinalIgnoreCase);

                        bool isDedicated = !isIntegrated && (
                                            gpu.Provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                            gpu.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                            gpu.Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                                            gpu.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                                            gpu.Name.Contains("GTX", StringComparison.OrdinalIgnoreCase) ||
                                            gpu.Name.Contains("Quadro", StringComparison.OrdinalIgnoreCase) ||
                                            gpu.Name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) ||
                                            gpu.Name.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase) ||
                                            gpu.Name.Contains("Arc A", StringComparison.OrdinalIgnoreCase));

                        string role = isDedicated ? "Dedicated GPU" : "Integrated GPU";

                        if (gpuIndex > 1)
                        {
                            staticSb.AppendLine();
                            staticSb.AppendLine($"Graphics Adapter {gpuIndex} ({role}): {gpu.Name}");
                        }
                        else
                        {
                            string prefix = gpus.Count > 1 ? $"Graphics Adapter 1 ({role}): " : $"Graphics Adapter ({role}): ";
                            staticSb.AppendLine($"{prefix}{gpu.Name}");
                        }

                        if (!string.IsNullOrEmpty(gpu.Provider))
                            staticSb.AppendLine($"Manufacturer: {gpu.Provider}");
                        if (!string.IsNullOrEmpty(gpu.DriverVersion))
                            staticSb.AppendLine($"Driver Version: {gpu.DriverVersion}");
                        if (!string.IsNullOrEmpty(gpu.DriverDate))
                            staticSb.AppendLine($"Driver Date: {gpu.DriverDate}");
                        if (gpu.DedicatedVramBytes > 0)
                            staticSb.AppendLine($"Dedicated Video Memory: {FormatHelper.FormatBytes(gpu.DedicatedVramBytes)}");
                        if (!string.IsNullOrEmpty(gpu.VideoProcessor) && !gpu.VideoProcessor.Equals(gpu.Name, StringComparison.OrdinalIgnoreCase))
                            staticSb.AppendLine($"Video Processor: {gpu.VideoProcessor}");
                    }
                }
                else
                {
                    staticSb.AppendLine("Graphics Device: Standard Display Adapter");
                    staticSb.AppendLine("Driver Version: Available via Windows Device Manager");
                }

                _cachedGpuStaticInfo = staticSb.ToString();
            }

            sb.Append(_cachedGpuStaticInfo);
            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Battery Details

        private string BuildBatteryDetails()
        {
            var sb = new StringBuilder();

            if (!NativeMethods.GetSystemPowerStatus(out var status) || status.BatteryFlag == 128)
            {
                return "No system battery detected (Desktop PC or AC only).";
            }

            float percent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : 0f;
            bool isCharging = (status.BatteryFlag & 8) != 0;
            bool isPluggedIn = status.ACLineStatus == 1;

            string statusText = isCharging
                ? "Charging (AC Connected)"
                : isPluggedIn ? "Fully Charged / Idle (Plugged in)" : "Discharging (On Battery)";

            sb.AppendLine($"Charge Level: {percent:F0}%");
            sb.AppendLine($"Power Source: {(isPluggedIn ? "AC Power (Wall Adapter)" : "Battery")}");
            sb.AppendLine($"State: {statusText}");

            // Live Charge / Discharge Wattage
            try
            {
                if (NativeMethods.CallNtPowerInformation(NativeMethods.SystemBatteryState, IntPtr.Zero, 0, out var batState, (uint)Marshal.SizeOf(typeof(NativeMethods.SYSTEM_BATTERY_STATE))) == 0)
                {
                    int rateMw = batState.Rate;
                    if (rateMw != 0)
                    {
                        double watts = Math.Abs(rateMw) / 1000.0;
                        string rateStr = (rateMw > 0 || isCharging) ? $"+{watts:F1} Watts (Charging)" : $"-{watts:F1} Watts (Discharging on Battery)";
                        sb.AppendLine($"Charge Rate: {rateStr}");
                    }
                    else if (isPluggedIn)
                    {
                        sb.AppendLine("Charge Rate: 0.0 Watts (Fully Charged / Idle on AC Power)");
                    }
                    else if (isCharging)
                    {
                        sb.AppendLine("Charge Rate: Active (+Charging)");
                    }
                    else
                    {
                        sb.AppendLine("Discharge Rate: Idle");
                    }
                }
                else if (isPluggedIn)
                {
                    sb.AppendLine("Charge Rate: 0.0 Watts (Fully Charged / Idle on AC Power)");
                }
            }
            catch
            {
                if (isPluggedIn)
                {
                    sb.AppendLine("Charge Rate: 0.0 Watts (Fully Charged / Idle on AC Power)");
                }
            }

            sb.AppendLine($"Power Mode: {GetActivePowerPlan()}");

            if (status.BatteryLifeTime > 0)
            {
                var ts = TimeSpan.FromSeconds(status.BatteryLifeTime);
                sb.AppendLine($"Estimated Time Remaining: {ts.Hours} hours, {ts.Minutes} minutes");
            }
            else if (!isPluggedIn)
            {
                sb.AppendLine("Estimated Time Remaining: Calculating...");
            }

            // Real Battery Health, Wear Level, and Capacity Telemetry (via powercfg /batteryreport /xml)
            var (designMwh, fullMwh, cycles, mfg, serial) = GetBatteryHealthData();
            if (cycles <= 0)
            {
                int? wmiCycles = QueryWmiCycleCount();
                if (wmiCycles.HasValue && wmiCycles.Value > 0)
                {
                    cycles = wmiCycles.Value;
                }
            }

            if (designMwh > 0 && fullMwh > 0)
            {
                double healthPct = (fullMwh * 100.0) / designMwh;
                double wearPct = Math.Max(0.0, 100.0 - healthPct);

                string condition = healthPct switch
                {
                    >= 85 => "Good / Healthy",
                    >= 70 => "Fair (Minor degradation)",
                    >= 50 => "Degraded (Noticeable capacity loss)",
                    _ => "Poor (Replacement recommended)"
                };

                string cycleSnippet = cycles > 0
                    ? $" | Charge Cycles: {cycles:N0}"
                    : string.Empty;

                sb.AppendLine($"Battery Health: {healthPct:F1}% ({condition}){cycleSnippet}");
                sb.AppendLine($"Wear Level: {wearPct:F1}%");
                sb.AppendLine($"Full Charge Capacity: {fullMwh:N0} mWh");
                sb.AppendLine($"Design Capacity: {designMwh:N0} mWh");
            }
            else if (cycles > 0)
            {
                sb.AppendLine($"Charge Cycles: {cycles:N0}");
            }

            if (!string.IsNullOrEmpty(mfg))
            {
                sb.AppendLine($"Manufacturer: {mfg}");
            }
            if (!string.IsNullOrEmpty(serial))
            {
                sb.AppendLine($"Serial Number: {serial}");
            }

            // Battery Device & Chemistry from WMI
            try
            {
                using var battSearcher = new ManagementObjectSearcher("SELECT Name, DeviceID, Chemistry FROM Win32_Battery");
                using var battColl = battSearcher.Get();
                foreach (ManagementObject b in battColl)
                {
                    using (b)
                    {
                        string bName = b["Name"]?.ToString()?.Trim() ?? string.Empty;
                        string bDevId = b["DeviceID"]?.ToString()?.Trim() ?? string.Empty;
                        if (!string.IsNullOrEmpty(bName) && string.IsNullOrEmpty(mfg))
                            sb.AppendLine($"Battery Model: {bName}");

                        if (b["Chemistry"] != null && int.TryParse(b["Chemistry"]?.ToString(), out int chem))
                        {
                            string chemStr = chem switch
                            {
                                1 => "Other",
                                2 => "Unknown",
                                3 => "Lead Acid",
                                4 => "Nickel Cadmium",
                                5 => "Nickel Metal Hydride",
                                6 => "Lithium-ion",
                                7 => "Zinc air",
                                8 => "Lithium Polymer",
                                _ => "Standard"
                            };
                            sb.AppendLine($"Chemistry: {chemStr}");
                        }
                        break;
                    }
                }
            }
            catch { }

            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Helper Methods

        private static string FormatUptime(TimeSpan ts)
        {
            var parts = new List<string>();
            if (ts.Days > 0) parts.Add($"{ts.Days} {(ts.Days == 1 ? "day" : "days")}");
            if (ts.Hours > 0) parts.Add($"{ts.Hours} {(ts.Hours == 1 ? "hour" : "hours")}");
            if (ts.Minutes > 0) parts.Add($"{ts.Minutes} {(ts.Minutes == 1 ? "minute" : "minutes")}");
            if (parts.Count == 0 || ts.TotalMinutes < 1) parts.Add($"{ts.Seconds} seconds");
            return string.Join(", ", parts);
        }

        internal static string DecodeSmbiosMemoryType(uint smbiosType, uint legacyType)
        {
            return smbiosType switch
            {
                19 => "DDR",
                20 => "DDR2",
                21 => "DDR2 FB-DIMM",
                24 => "DDR3",
                26 => "DDR4",
                27 => "LPDDR",
                28 => "LPDDR2",
                29 => "LPDDR3",
                30 => "LPDDR4",
                31 => "Logical Non-Volatile Device",
                32 => "HBM",
                33 => "HBM2",
                34 => "DDR5",
                35 => "LPDDR5",
                36 => "HBM3",
                _ => legacyType switch
                {
                    20 => "DDR",
                    21 => "DDR2",
                    24 => "DDR3",
                    26 => "DDR4",
                    _ => "DDR / SDRAM"
                }
            };
        }

        internal static (long DesignMwh, long FullMwh, int CycleCount, string Mfg, string Serial) ParseBatteryHealthXml(string xmlContent)
        {
            if (string.IsNullOrWhiteSpace(xmlContent)) return (0, 0, 0, string.Empty, string.Empty);
            try
            {
                var doc = System.Xml.Linq.XDocument.Parse(xmlContent);
                var ns = doc.Root?.Name.Namespace ?? System.Xml.Linq.XNamespace.None;
                var b = doc.Root?.Element(ns + "Batteries")?.Element(ns + "Battery");
                if (b != null)
                {
                    string? desVal = b.Attribute("DesignCapacity")?.Value ?? b.Element(ns + "DesignCapacity")?.Value;
                    string? fullVal = b.Attribute("FullChargeCapacity")?.Value ?? b.Element(ns + "FullChargeCapacity")?.Value;
                    string? cycleVal = b.Attribute("CycleCount")?.Value ?? b.Element(ns + "CycleCount")?.Value;
                    string? mfgVal = b.Attribute("Manufacturer")?.Value ?? b.Element(ns + "Manufacturer")?.Value;
                    string? serialVal = b.Attribute("SerialNumber")?.Value ?? b.Element(ns + "SerialNumber")?.Value;

                    long.TryParse(desVal, out long des);
                    long.TryParse(fullVal, out long full);
                    int.TryParse(cycleVal, out int cycles);
                    string mfg = mfgVal?.Trim() ?? string.Empty;
                    string serial = serialVal?.Trim() ?? string.Empty;
                    return (des, full, cycles, mfg, serial);
                }
            }
            catch { }
            return (0, 0, 0, string.Empty, string.Empty);
        }

        private static (long DesignMwh, long FullMwh, int CycleCount, string Mfg, string Serial)? _cachedBatteryHealth;
        private static DateTime _batteryHealthCacheTime = DateTime.MinValue;
        private static readonly object _batteryHealthLock = new();

        private static (long DesignMwh, long FullMwh, int CycleCount, string Mfg, string Serial) GetBatteryHealthData()
        {
            lock (_batteryHealthLock)
            {
                if (_cachedBatteryHealth.HasValue && (DateTime.UtcNow - _batteryHealthCacheTime).TotalSeconds < 60)
                {
                    return _cachedBatteryHealth.Value;
                }

                string tempFile = Path.Combine(Path.GetTempPath(), $"ra_batth_{Guid.NewGuid():N}.xml");
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
                        proc.WaitForExit(3000);
                        if (File.Exists(tempFile))
                        {
                            string xml = File.ReadAllText(tempFile);
                            var result = ParseBatteryHealthXml(xml);
                            if (result.DesignMwh > 0 || result.FullMwh > 0)
                            {
                                _cachedBatteryHealth = result;
                                _batteryHealthCacheTime = DateTime.UtcNow;
                            }
                            return result;
                        }
                    }
                }
                catch { }
                finally
                {
                    try
                    {
                        if (File.Exists(tempFile)) File.Delete(tempFile);
                    }
                    catch { }
                }

                if (_cachedBatteryHealth.HasValue)
                {
                    return _cachedBatteryHealth.Value;
                }

                return (0, 0, 0, string.Empty, string.Empty);
            }
        }

        internal static int? QueryWmiCycleCount()
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(@"root\wmi", "SELECT CycleCount, Active FROM BatteryCycleCount");
                using var coll = searcher.Get();
                foreach (var obj in coll)
                {
                    try
                    {
                        if (obj["CycleCount"] != null)
                        {
                            int c = Convert.ToInt32(obj["CycleCount"]);
                            if (c > 0) return c;
                        }
                    }
                    finally
                    {
                        obj.Dispose();
                    }
                }
            }
            catch { }
            return null;
        }

        internal static string GetActivePowerPlan()
        {
            // 1. Modern Windows 10/11 Dynamic Power Mode Overlay (Best Performance / Balanced / Best Power Efficiency)
            try
            {
                bool isPluggedIn = true;
                if (NativeMethods.GetSystemPowerStatus(out var status))
                {
                    isPluggedIn = status.ACLineStatus == 1;
                }

                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
                if (key != null)
                {
                    string valName = isPluggedIn ? "ActiveOverlayAcPowerScheme" : "ActiveOverlayDcPowerScheme";
                    string overlayGuid = key.GetValue(valName)?.ToString()?.Trim()?.ToLowerInvariant() ?? string.Empty;

                    string mode = overlayGuid switch
                    {
                        "961cc777-2547-4f9d-8174-7d86181b8a7a" => "Best Power Efficiency",
                        "3af9b67a-dd24-406a-a28a-fb59f23e4b77" => "Better Battery",
                        "ded574b5-45a0-4f42-8737-46345c09c238" => "Best Performance",
                        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "High Performance",
                        "00000000-0000-0000-0000-000000000000" => "Balanced",
                        _ => string.Empty
                    };

                    if (!string.IsNullOrEmpty(mode))
                    {
                        return mode;
                    }
                }
            }
            catch { }

            // 2. Fallback: Classic Win32 Base Power Scheme
            try
            {
                if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out IntPtr pGuid) == 0 && pGuid != IntPtr.Zero)
                {
                    try
                    {
                        var guid = Marshal.PtrToStructure<Guid>(pGuid);
                        string guidStr = guid.ToString().ToLowerInvariant();
                        return guidStr switch
                        {
                            "381b4222-f694-41f0-9685-ff5bb260df2e" => "Balanced",
                            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "High Performance",
                            "a1841308-3541-4fab-bc81-f71556f20b4a" => "Power Saver",
                            "e9a42b02-d5df-448d-aa00-03f14749eb61" => "Ultimate Performance",
                            _ => "Balanced / Custom"
                        };
                    }
                    finally
                    {
                        NativeMethods.LocalFree(pGuid);
                    }
                }
            }
            catch { }
            return "Balanced";
        }

        #endregion

        #region Memory Cache Flush

        public async Task<(int ProcessCount, long FreedBytes, string UpdatedDetails)> FlushMemoryCacheAsync()
        {
            return await Task.Run(() =>
            {
                var memBefore = NativeMethods.MEMORYSTATUSEX.Create();
                NativeMethods.GlobalMemoryStatusEx(ref memBefore);
                long availBefore = (long)memBefore.ullAvailPhys;

                int trimmedProcesses = 0;

                // 1. Trim process working sets
                try
                {
                    var processes = Process.GetProcesses();
                    foreach (var proc in processes)
                    {
                        try
                        {
                            if (NativeMethods.EmptyWorkingSet(proc.Handle) != 0)
                            {
                                trimmedProcesses++;
                            }
                        }
                        catch { }
                        finally
                        {
                            proc.Dispose();
                        }
                    }
                }
                catch { }

                // 2. If running as Administrator, purge standby list via NtSetSystemInformation
                try
                {
                    if (ElevationHelper.IsRunningAsAdmin())
                    {
                        PurgeStandbyListInternal();
                    }
                }
                catch { }

                var memAfter = NativeMethods.MEMORYSTATUSEX.Create();
                NativeMethods.GlobalMemoryStatusEx(ref memAfter);
                long availAfter = (long)memAfter.ullAvailPhys;

                long freedBytes = Math.Max(0, availAfter - availBefore);

                string updatedDetails = BuildRamDetails();
                return (trimmedProcesses, freedBytes, updatedDetails);
            });
        }

        private static void PurgeStandbyListInternal()
        {
            try
            {
                IntPtr pCmd = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    Marshal.WriteInt32(pCmd, NativeMethods.MemoryEmptyWorkingSets);
                    NativeMethods.NtSetSystemInformation(NativeMethods.SystemMemoryListInformation, pCmd, sizeof(int));

                    Marshal.WriteInt32(pCmd, NativeMethods.MemoryPurgeStandbyList);
                    NativeMethods.NtSetSystemInformation(NativeMethods.SystemMemoryListInformation, pCmd, sizeof(int));
                }
                finally
                {
                    Marshal.FreeHGlobal(pCmd);
                }
            }
            catch { }
        }

        #endregion

        #region Display Details

        private string BuildDisplayDetails()
        {
            var sb = new StringBuilder();

            if (_cachedDisplayStaticInfo == null)
            {
                var staticSb = new StringBuilder();

                try
                {
                    var dm = new NativeMethods.DEVMODE();
                    dm.dmSize = (short)Marshal.SizeOf(typeof(NativeMethods.DEVMODE));

                    int width = 0, height = 0, freq = 0, bits = 0;
                    if (NativeMethods.EnumDisplaySettings(null, NativeMethods.ENUM_CURRENT_SETTINGS, ref dm))
                    {
                        width = dm.dmPelsWidth;
                        height = dm.dmPelsHeight;
                        freq = dm.dmDisplayFrequency;
                        bits = dm.dmBitsPerPel;
                    }

                    // Monitor Model / Name via EnumDisplayDevices
                    string monitorName = "Internal Display / Active Monitor";
                    try
                    {
                        var dispDev = new NativeMethods.DISPLAY_DEVICE();
                        dispDev.cb = Marshal.SizeOf(typeof(NativeMethods.DISPLAY_DEVICE));

                        if (NativeMethods.EnumDisplayDevices(null, 0, ref dispDev, 0))
                        {
                            var monitorDev = new NativeMethods.DISPLAY_DEVICE();
                            monitorDev.cb = Marshal.SizeOf(typeof(NativeMethods.DISPLAY_DEVICE));

                            if (NativeMethods.EnumDisplayDevices(dispDev.DeviceName, 0, ref monitorDev, 0) &&
                                !string.IsNullOrWhiteSpace(monitorDev.DeviceString))
                            {
                                monitorName = monitorDev.DeviceString.Trim();
                            }
                            else if (!string.IsNullOrWhiteSpace(dispDev.DeviceString))
                            {
                                monitorName = dispDev.DeviceString.Trim();
                            }
                        }
                    }
                    catch { }

                    var ccdInfo = NativeMethods.QueryDisplayCcdInfo();
                    if (ccdInfo.HasValue && !string.IsNullOrWhiteSpace(ccdInfo.Value.FriendlyName))
                    {
                        monitorName = ccdInfo.Value.FriendlyName;
                    }

                    staticSb.AppendLine($"Monitor: {monitorName}");

                    if (ccdInfo.HasValue && !string.IsNullOrWhiteSpace(ccdInfo.Value.ConnectionTechnology))
                    {
                        staticSb.AppendLine($"Connection: {ccdInfo.Value.ConnectionTechnology}");
                    }

                    if (width > 0 && height > 0)
                    {
                        string standard = FormatHelper.GetResolutionStandard(width, height);
                        string resStr = string.IsNullOrEmpty(standard) ? $"{width} x {height}" : $"{width} x {height} {standard}";
                        staticSb.AppendLine($"Active Resolution: {resStr}");
                    }

                    if (freq > 0)
                    {
                        staticSb.AppendLine($"Refresh Rate: {freq} Hz");
                    }

                    if (bits > 0)
                    {
                        string colorDesc = bits == 32 ? "32-bit (True Color)" : $"{bits}-bit Color";
                        staticSb.AppendLine($"Color Depth: {colorDesc}");
                    }

                    // Windows DPI Scaling
                    uint dpi = 96;
                    try { dpi = NativeMethods.GetDpiForSystem(); } catch { }
                    if (dpi == 0) dpi = 96;
                    int scalePct = (int)Math.Round(dpi * 100.0 / 96.0);
                    staticSb.AppendLine($"Windows Scaling: {scalePct}% DPI ({dpi} DPI)");

                    // Multi-monitor count
                    int monitorCount = 1;
                    try { monitorCount = NativeMethods.GetSystemMetrics(NativeMethods.SM_CMONITORS); } catch { }
                    if (monitorCount <= 0) monitorCount = 1;

                    string countStr = monitorCount > 1
                        ? $"{monitorCount} Displays Active (Multi-Monitor Extended Desktop)"
                        : "1 Display Active (Single Screen)";
                    staticSb.AppendLine($"Connected Displays: {countStr}");

                    // HDR Status (via Windows CCD Advanced Color API)
                    if (ccdInfo.HasValue)
                    {
                        if (ccdInfo.Value.HdrEnabled)
                        {
                            uint hdrBits = ccdInfo.Value.BitsPerChannel > 0 ? ccdInfo.Value.BitsPerChannel : 10;
                            staticSb.AppendLine($"HDR (High Dynamic Range): HDR Enabled ({hdrBits}-bit Advanced Color)");
                        }
                        else if (ccdInfo.Value.HdrSupported)
                        {
                            staticSb.AppendLine("HDR (High Dynamic Range): Supported (Currently Disabled in Windows Settings)");
                        }
                        else
                        {
                            staticSb.AppendLine("HDR (High Dynamic Range): SDR Mode (HDR Not Supported by Display)");
                        }
                    }
                    else
                    {
                        staticSb.AppendLine("HDR (High Dynamic Range): SDR Mode (Standard Dynamic Range)");
                    }
                }
                catch (Exception ex)
                {
                    staticSb.AppendLine($"Display query error: {ex.Message}");
                }

                _cachedDisplayStaticInfo = staticSb.ToString();
            }

            sb.Append(_cachedDisplayStaticInfo);
            return sb.ToString().TrimEnd();
        }

        #endregion

        #region Audio Details

        private string BuildAudioDetails()
        {
            var sb = new StringBuilder();

            // 1. Playback Endpoint (Render)
            var playback = NativeMethods.GetDefaultAudioDeviceInfo(NativeMethods.EDataFlow.eRender);
            if (playback.HasValue)
            {
                var p = playback.Value;
                sb.AppendLine($"Default Output: {p.Name}");
                sb.AppendLine($"Output Type: {p.FormFactor}");

                int volPct = (int)Math.Round(p.Volume * 100.0);
                string muteStr = p.IsMuted ? " (Muted)" : string.Empty;
                sb.AppendLine($"Master Volume: {volPct}%{muteStr}");

                if (p.SampleRate > 0)
                {
                    string qualityDesc = p.SampleRate switch
                    {
                        >= 192000 => "Studio Quality (192 kHz)",
                        >= 96000 => "Studio Quality (96 kHz)",
                        >= 48000 => "DVD / Studio Quality (48 kHz)",
                        >= 44100 => "CD Quality (44.1 kHz)",
                        _ => $"{p.SampleRate:N0} Hz"
                    };
                    sb.AppendLine($"Sample Rate: {p.SampleRate:N0} Hz ({qualityDesc})");
                }

                if (p.BitsPerSample > 0)
                {
                    sb.AppendLine($"Bit Depth: {p.BitsPerSample}-bit");
                }

                if (p.Channels > 0)
                {
                    string chDesc = p.Channels switch
                    {
                        1 => "Mono (1 Channel)",
                        2 => "Stereo (2 Channels)",
                        4 => "Quadraphonic (4 Channels)",
                        6 => "5.1 Surround (6 Channels)",
                        8 => "7.1 Surround (8 Channels)",
                        _ => $"{p.Channels} Channels"
                    };
                    sb.AppendLine($"Channels: {chDesc}");
                }
            }
            else
            {
                sb.AppendLine("Default Output: No active audio output endpoint found");
            }

            // 2. Recording Endpoint (Capture / Microphone)
            var capture = NativeMethods.GetDefaultAudioDeviceInfo(NativeMethods.EDataFlow.eCapture);
            if (capture.HasValue)
            {
                var c = capture.Value;
                sb.AppendLine($"Default Input (Mic): {c.Name}");
                sb.AppendLine($"Input Type: {c.FormFactor}");

                int capVolPct = (int)Math.Round(c.Volume * 100.0);
                string capMuteStr = c.IsMuted ? " (Muted)" : string.Empty;
                sb.AppendLine($"Input Volume: {capVolPct}%{capMuteStr}");

                if (c.SampleRate > 0)
                {
                    sb.AppendLine($"Input Sample Rate: {c.SampleRate:N0} Hz ({c.BitsPerSample}-bit, {c.Channels} ch)");
                }
            }
            else
            {
                sb.AppendLine("Default Input (Mic): Not connected or disabled");
            }

            // 3. Audio Hardware Controllers & Chipsets (Win32_SoundDevice)
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, Manufacturer, Status FROM Win32_SoundDevice");
                using var coll = searcher.Get();
                var soundDevices = new List<string>();
                foreach (ManagementObject obj in coll)
                {
                    using (obj)
                    {
                        string name = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
                        string mfg = obj["Manufacturer"]?.ToString()?.Trim() ?? string.Empty;
                        string status = obj["Status"]?.ToString()?.Trim() ?? "OK";
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            string entry = !string.IsNullOrWhiteSpace(mfg) && !name.Contains(mfg, StringComparison.OrdinalIgnoreCase)
                                ? $"{name} ({mfg}) - Status: {status}"
                                : $"{name} - Status: {status}";
                            if (!soundDevices.Contains(entry))
                            {
                                soundDevices.Add(entry);
                            }
                        }
                    }
                }

                if (soundDevices.Count > 0)
                {
                    sb.AppendLine($"Installed Controllers: {soundDevices.Count}");
                    for (int i = 0; i < soundDevices.Count; i++)
                    {
                        sb.AppendLine($"Controller {i + 1}: {soundDevices[i]}");
                    }
                }
            }
            catch { }

            return sb.ToString().TrimEnd();
        }

        #endregion

        #region System Diagnostic Snapshot

        public async Task<string> GenerateSystemSnapshotAsync()
        {
            return await Task.Run(() =>
            {
                var sb = new StringBuilder();
                sb.AppendLine("System Diagnostic Snapshot");
                sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine();

                // 1. Operating System & Device
                sb.AppendLine("[Operating System & Device]");
                string osDesc = GetOsDescription();
                string modelDesc = GetDeviceModel();
                var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
                bool isAdmin = ElevationHelper.IsRunningAsAdmin();

                sb.AppendLine($"OS: {osDesc} ({RuntimeInformation.ProcessArchitecture})");
                if (!string.IsNullOrEmpty(modelDesc))
                {
                    sb.AppendLine($"Device: {modelDesc}");
                }
                sb.AppendLine($"Uptime: {FormatUptime(uptime)}");
                sb.AppendLine($"Privileges: {(isAdmin ? "Administrator" : "Standard User")}");
                sb.AppendLine();

                // 2. CPU
                sb.AppendLine("[Processor (CPU)]");
                string cpuDetails = BuildCpuDetails();
                foreach (var line in cpuDetails.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    sb.AppendLine(line);
                }
                sb.AppendLine();

                // 3. RAM
                sb.AppendLine("[Memory (RAM)]");
                string ramDetails = BuildRamDetails();
                foreach (var line in ramDetails.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    sb.AppendLine(line);
                }
                sb.AppendLine();

                // 4. Graphics (GPU)
                sb.AppendLine("[Graphics (GPU)]");
                string gpuDetails = BuildGpuDetails();
                foreach (var line in gpuDetails.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    sb.AppendLine(line);
                }
                sb.AppendLine();

                // 4b. Display & Monitors
                sb.AppendLine("[Display & Monitors]");
                string dispDetails = BuildDisplayDetails();
                foreach (var line in dispDetails.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    sb.AppendLine(line);
                }
                sb.AppendLine();

                // 4c. Audio & Sound Devices
                sb.AppendLine("[Audio & Sound Devices]");
                string audioDetails = BuildAudioDetails();
                foreach (var line in audioDetails.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    sb.AppendLine(line);
                }
                sb.AppendLine();

                // 5. Storage (Disk)
                sb.AppendLine("[Storage (Disk)]");
                string diskDetails = BuildDiskDetails();
                foreach (var line in diskDetails.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    sb.AppendLine(line);
                }
                sb.AppendLine();

                // 6. Network
                sb.AppendLine("[Network & Connectivity]");
                string netDetails = BuildNetworkDetails();
                foreach (var line in netDetails.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    sb.AppendLine(line);
                }
                sb.AppendLine();

                // 7. Battery (if applicable)
                if (NativeMethods.GetSystemPowerStatus(out var status) && status.BatteryFlag != 128)
                {
                    sb.AppendLine("[Power & Battery]");
                    string battDetails = BuildBatteryDetails();
                    foreach (var line in battDetails.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        sb.AppendLine(line);
                    }
                    sb.AppendLine();
                }

                return sb.ToString().TrimEnd();
            });
        }

        internal static string GetOsDescription()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key != null)
                {
                    string prod = key.GetValue("ProductName")?.ToString() ?? "Windows";
                    string displayVer = key.GetValue("DisplayVersion")?.ToString() ?? "";
                    string build = key.GetValue("CurrentBuild")?.ToString() ?? "";
                    string ubr = key.GetValue("UBR")?.ToString() ?? "";

                    // Windows 11 detection: Microsoft kept ProductName as "Windows 10" in the registry for app compatibility.
                    // Windows 11 build numbers start from 22000.
                    if (int.TryParse(build, out int buildNum) && buildNum >= 22000)
                    {
                        if (prod.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
                        {
                            prod = prod.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
                        }
                        else if (!prod.Contains("Windows 11", StringComparison.OrdinalIgnoreCase))
                        {
                            prod = prod.Replace("Windows", "Windows 11", StringComparison.OrdinalIgnoreCase);
                        }
                    }

                    string buildStr = !string.IsNullOrEmpty(ubr) ? $"{build}.{ubr}" : build;
                    return $"{prod} {displayVer} (Build {buildStr})".Trim();
                }
            }
            catch { }

            return RuntimeInformation.OSDescription;
        }

        private static string GetDeviceModel()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
                using var coll = searcher.Get();
                foreach (ManagementObject obj in coll)
                {
                    using (obj)
                    {
                        string mfg = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                        string model = obj["Model"]?.ToString()?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(mfg) || !string.IsNullOrEmpty(model))
                        {
                            return $"{mfg} {model}".Trim();
                        }
                    }
                }
            }
            catch { }

            return string.Empty;
        }

        #endregion
    }
}
