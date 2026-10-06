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
                try
                {
                    string instanceName = "_Total";
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

                    if (_latestDiskSpeeds.TryGetValue(instanceName, out var speed))
                    {
                        return speed;
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
                catch { }

                return (0, 0);
            }
        }

        public static void PrewarmHardwareCache()
        {
            Task.Run(() =>
            {
                try
                {
                    SampleAllDiskSpeeds();
                    var svc = new HardwareDetailService();
                    svc.BuildCpuDetails();
                    svc.BuildGpuDetails();
                    svc.BuildRamDetails();
                    svc.BuildDisplayDetails();
                    svc.BuildDiskDetails();
                }
                catch { }
            });
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

            // CPU Name from registry
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

            // System Uptime
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            sb.AppendLine($"System Uptime: {FormatUptime(uptime)}");
            sb.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture} (64-bit)");
            sb.AppendLine($"Power Mode: {GetActivePowerPlan()}");

            // Live Frequency Scaling & Thermal Throttling Telemetry (via Processor Information performance counters)
            try
            {
                using var perfSearcher = new ManagementObjectSearcher(
                    "SELECT PercentofMaximumFrequency, PercentProcessorPerformance, PercentProcessorUtility FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'");
                foreach (ManagementObject obj in perfSearcher.Get())
                {
                    if (obj["PercentofMaximumFrequency"] != null && uint.TryParse(obj["PercentofMaximumFrequency"]?.ToString(), out uint maxFreqPct))
                    {
                        sb.AppendLine($"Frequency Scaling: {maxFreqPct}% of Maximum Frequency");
                        if (maxFreqPct < 60)
                        {
                            sb.AppendLine("Thermal Throttling: Active (Processor frequency significantly restricted by thermal or power limits)");
                        }
                        else
                        {
                            sb.AppendLine("Thermal Throttling: Inactive (Normal operating frequency)");
                        }
                    }
                    if (obj["PercentProcessorPerformance"] != null && uint.TryParse(obj["PercentProcessorPerformance"]?.ToString(), out uint procPerf))
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
                    break;
                }
            }
            catch { }

            // ACPI Thermal Zone Temperature (if running as Administrator or supported by OEM ACPI)
            try
            {
                using var thermalSearcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature, CriticalTripPoint FROM MSAcpi_ThermalZoneTemperature");
                foreach (ManagementObject obj in thermalSearcher.Get())
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
                            break;
                        }
                    }
                }
            }
            catch { }

            // Static CPU & System Info
            if (_cachedCpuStaticInfo == null)
            {
                var staticSb = new StringBuilder();

                // Computer Model & Manufacturer
                try
                {
                    using var csSearcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
                    foreach (ManagementObject cs in csSearcher.Get())
                    {
                        string mfg = cs["Manufacturer"]?.ToString()?.Trim() ?? string.Empty;
                        string model = cs["Model"]?.ToString()?.Trim() ?? string.Empty;
                        if (!string.IsNullOrEmpty(model))
                        {
                            staticSb.AppendLine($"System Model: {mfg} {model}".Trim());
                        }
                        break;
                    }
                }
                catch { }

                // BIOS Version
                try
                {
                    using var biosSearcher = new ManagementObjectSearcher("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
                    foreach (ManagementObject bios in biosSearcher.Get())
                    {
                        string biosVer = bios["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? string.Empty;
                        string biosMfg = bios["Manufacturer"]?.ToString()?.Trim() ?? string.Empty;
                        if (!string.IsNullOrEmpty(biosVer))
                        {
                            staticSb.AppendLine($"BIOS Version: {biosMfg} {biosVer}".Trim());
                        }
                        break;
                    }
                }
                catch { }

                // Processor Cores, Cache, Virtualization
                try
                {
                    using var searcher = new ManagementObjectSearcher("SELECT NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, VirtualizationFirmwareEnabled, SocketDesignation FROM Win32_Processor");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        if (obj["SocketDesignation"] != null && !string.IsNullOrWhiteSpace(obj["SocketDesignation"]?.ToString()))
                            staticSb.AppendLine($"Socket: {obj["SocketDesignation"]}");
                        if (obj["NumberOfCores"] != null)
                            staticSb.AppendLine($"Physical Cores: {obj["NumberOfCores"]}");
                        if (obj["NumberOfLogicalProcessors"] != null)
                            staticSb.AppendLine($"Logical Processors: {obj["NumberOfLogicalProcessors"]}");
                        if (obj["MaxClockSpeed"] != null && uint.TryParse(obj["MaxClockSpeed"]?.ToString(), out uint clockMhz))
                            staticSb.AppendLine($"Base Clock Speed: {clockMhz / 1000.0:F2} GHz ({clockMhz} MHz)");
                        if (obj["L2CacheSize"] != null && uint.TryParse(obj["L2CacheSize"]?.ToString(), out uint l2Kb) && l2Kb > 0)
                            staticSb.AppendLine($"L2 Cache: {FormatHelper.FormatBytes((long)l2Kb * 1024)} ({l2Kb:N0} KB)");
                        if (obj["L3CacheSize"] != null && uint.TryParse(obj["L3CacheSize"]?.ToString(), out uint l3Kb) && l3Kb > 0)
                            staticSb.AppendLine($"L3 Cache: {FormatHelper.FormatBytes((long)l3Kb * 1024)} ({l3Kb:N0} KB)");
                        if (obj["VirtualizationFirmwareEnabled"] != null)
                        {
                            bool virt = Convert.ToBoolean(obj["VirtualizationFirmwareEnabled"]);
                            staticSb.AppendLine($"Virtualization: {(virt ? "Enabled in Firmware (VT-x / AMD-V)" : "Disabled")}");
                        }
                        break;
                    }
                }
                catch { }

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
                sb.AppendLine($"Free Memory: {FormatHelper.FormatBytes(freeMemory)}");
                sb.AppendLine($"Committed: {FormatHelper.FormatBytes(commitTotal)} of {FormatHelper.FormatBytes(commitLimit)}");

                long pageFileTotal = Math.Max(0, commitLimit - totalUsable);
                if (pageFileTotal > 0)
                {
                    string pageFileLocation = "C:\\pagefile.sys";
                    try
                    {
                        using var mmKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
                        var pfVal = mmKey?.GetValue("PagingFiles") as string[];
                        if (pfVal != null && pfVal.Length > 0 && !string.IsNullOrWhiteSpace(pfVal[0]))
                        {
                            string[] parts = pfVal[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0) pageFileLocation = parts[0];
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
                        foreach (ManagementObject arr in arraySearcher.Get())
                        {
                            if (arr["MemoryDevices"] != null)
                                totalSlots = Convert.ToInt32(arr["MemoryDevices"]);
                            if (arr["MaxCapacity"] != null && long.TryParse(arr["MaxCapacity"]?.ToString(), out long maxKb))
                                maxCapacityBytes = maxKb * 1024;
                        }
                    }
                    catch { }

                    using var memSearcher = new ManagementObjectSearcher(
                        "SELECT Speed, ConfiguredClockSpeed, FormFactor, DeviceLocator, Manufacturer, PartNumber, Capacity, SMBIOSMemoryType, MemoryType FROM Win32_PhysicalMemory");
                    var modules = new List<string>();
                    uint ramSpeed = 0;
                    uint configuredSpeed = 0;
                    string formFactorStr = string.Empty;
                    string primaryMemType = string.Empty;

                    foreach (ManagementObject stick in memSearcher.Get())
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

                    if (modules.Count >= 2 && (modules.Count % 2 == 0))
                    {
                        staticSb.AppendLine("Memory Channel: Dual-Channel (Optimal Performance)");
                    }
                    else if (modules.Count == 1)
                    {
                        staticSb.AppendLine("Memory Channel: Single-Channel (Note: Adding a second stick enables Dual-Channel speed)");
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
                            staticSb.AppendLine($"• {mod}");
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
            public string MediaType { get; set; } = "Solid State Drive (SSD)";
            public string BusType { get; set; } = "NVMe (PCIe)";
            public bool IsInternal { get; set; } = true;
            public long HardwareSizeBytes { get; set; }
            public string PartitionStyle { get; set; } = "GPT (GUID Partition Table - Modern UEFI)";
            public string HealthStatus { get; set; } = "Healthy";
        }

        private static readonly Dictionary<int, PhysicalDiskMetadata> _diskMetadataCache = new();
        private static readonly object _diskMetaLock = new();

        internal static PhysicalDiskMetadata GetPhysicalDiskMetadata(int diskIndex, string? usbLetter = null)
        {
            lock (_diskMetaLock)
            {
                if (diskIndex >= 0 && _diskMetadataCache.TryGetValue(diskIndex, out var cached))
                {
                    return cached;
                }

                var meta = new PhysicalDiskMetadata
                {
                    Index = diskIndex,
                    Model = "Physical Disk Drive",
                    MediaType = "Solid State Drive (SSD)",
                    BusType = "NVMe (PCIe)",
                    IsInternal = true,
                    HealthStatus = "Healthy",
                    PartitionStyle = "GPT (GUID Partition Table - Modern UEFI)"
                };

                try
                {
                    using var searcher = new ManagementObjectSearcher("SELECT Index, Model, InterfaceType, MediaType, Size FROM Win32_DiskDrive");
                    foreach (ManagementObject disk in searcher.Get())
                    {
                        if (disk["Index"] != null && int.TryParse(disk["Index"]?.ToString(), out int idx))
                        {
                            string model = disk["Model"]?.ToString()?.Trim() ?? "Disk Drive";
                            string iface = disk["InterfaceType"]?.ToString()?.Trim() ?? "";
                            string media = disk["MediaType"]?.ToString()?.Trim() ?? "";
                            long size = 0;
                            if (disk["Size"] != null) long.TryParse(disk["Size"]?.ToString(), out size);

                            bool isInternal = !iface.Equals("USB", StringComparison.OrdinalIgnoreCase) &&
                                              !media.Contains("Removable", StringComparison.OrdinalIgnoreCase);

                            string mediaTypeStr;
                            if (!isInternal)
                            {
                                mediaTypeStr = "Removable Storage (USB Flash Drive)";
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

                            var item = new PhysicalDiskMetadata
                            {
                                Index = idx,
                                Model = model,
                                MediaType = mediaTypeStr,
                                BusType = busTypeStr,
                                IsInternal = isInternal,
                                HardwareSizeBytes = size,
                                HealthStatus = "Healthy",
                                PartitionStyle = idx == 0 ? "GPT (GUID Partition Table - Modern UEFI)" : "MBR (Master Boot Record - Legacy)"
                            };

                            try
                            {
                                using var pSearcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", $"SELECT PartitionStyle FROM MSFT_Disk WHERE Number={idx}");
                                foreach (ManagementObject pObj in pSearcher.Get())
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
                            catch { }

                            _diskMetadataCache[idx] = item;
                            if (idx == diskIndex) meta = item;
                        }
                    }
                }
                catch { }

                if (usbLetter != null)
                {
                    meta.IsInternal = false;
                    meta.MediaType = "Removable Storage (USB Flash Drive)";
                    meta.BusType = "USB";
                    meta.HealthStatus = "Healthy (Ready to Eject / Safely Remove)";
                }

                return meta;
            }
        }

        internal static bool IsPhysicalDiskHdd(int diskNum)
        {
            var meta = GetPhysicalDiskMetadata(diskNum);
            return meta.MediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase);
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
                foreach (ManagementObject enc in encSearcher.Get())
                {
                    if (enc["ProtectionStatus"] != null && uint.TryParse(enc["ProtectionStatus"]?.ToString(), out uint prot))
                    {
                        return prot == 1 ? "On (Protected with BitLocker / Device Encryption)" : "Off (Drive is not encrypted)";
                    }
                    break;
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

            if (diskMeta.MediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("TRIM Support: Enabled (Optimal SSD Maintenance)");
            }
            else if (!diskMeta.IsInternal)
            {
                sb.AppendLine("Safe Removal: Supported (Ready to Eject / Safely Remove)");
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
                    var wifiInfo = QueryWifiTelemetry();
                    if (wifiInfo.Count > 0)
                    {
                        if (wifiInfo.TryGetValue("SSID", out var ssid) && !string.IsNullOrEmpty(ssid))
                            sb.AppendLine($"Wi-Fi Network (SSID): {ssid}");
                        if (wifiInfo.TryGetValue("Radio type", out var radio) && !string.IsNullOrEmpty(radio))
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

            var gateway = ipProps.GatewayAddresses.FirstOrDefault();
            if (gateway != null)
            {
                sb.AppendLine($"Default Gateway: {gateway.Address}");
            }

            var dnsList = ipProps.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).ToList();
            if (dnsList.Count > 0)
            {
                sb.AppendLine($"DNS Servers: {string.Join(", ", dnsList)}");
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

            return sb.ToString().TrimEnd();
        }

        private static Dictionary<string, string> QueryWifiTelemetry()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = "wlan show interfaces",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(1000);

                    using var reader = new StringReader(output);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        int colonIdx = line.IndexOf(':');
                        if (colonIdx > 0 && colonIdx < line.Length - 1)
                        {
                            string key = line.Substring(0, colonIdx).Trim();
                            string val = line.Substring(colonIdx + 1).Trim();
                            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
                            {
                                dict[key] = val;
                            }
                        }
                    }
                }
            }
            catch { }
            return dict;
        }

        #endregion

        #region GPU Details

        private string BuildGpuDetails()
        {
            var sb = new StringBuilder();

            if (_cachedGpuStaticInfo == null)
            {
                var staticSb = new StringBuilder();
                try
                {
                    using var gpuSearcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate, AdapterRAM, VideoProcessor, VideoModeDescription FROM Win32_VideoController");
                    var rawList = gpuSearcher.Get().Cast<ManagementObject>().ToList();
                    var validGpus = rawList.Where(g => !string.IsNullOrWhiteSpace(g["Name"]?.ToString())).ToList();

                    staticSb.AppendLine($"GPUs Detected: {validGpus.Count}");
                    staticSb.AppendLine();

                    int gpuIndex = 0;
                    foreach (ManagementObject gpu in validGpus)
                    {
                        gpuIndex++;
                        string name = gpu["Name"]?.ToString()?.Trim() ?? string.Empty;
                        string driverVer = gpu["DriverVersion"]?.ToString()?.Trim() ?? string.Empty;
                        string vramStr = string.Empty;
                        long vramBytes = 0;

                        if (gpu["AdapterRAM"] != null && long.TryParse(gpu["AdapterRAM"]?.ToString(), out vramBytes) && vramBytes > 0)
                        {
                            vramStr = FormatHelper.FormatBytes(vramBytes);
                        }

                        string videoProc = gpu["VideoProcessor"]?.ToString()?.Trim() ?? string.Empty;
                        string driverDate = string.Empty;
                        if (gpu["DriverDate"] != null)
                        {
                            string rawDate = gpu["DriverDate"]?.ToString() ?? string.Empty;
                            if (rawDate.Length >= 8)
                            {
                                string y = rawDate.Substring(0, 4);
                                string m = rawDate.Substring(4, 2);
                                string d = rawDate.Substring(6, 2);
                                driverDate = $"{d}-{m}-{y}";
                            }
                        }

                        bool isIntegrated = name.Contains("Iris", StringComparison.OrdinalIgnoreCase) ||
                                            name.Contains("UHD", StringComparison.OrdinalIgnoreCase) ||
                                            name.Contains("HD Graphics", StringComparison.OrdinalIgnoreCase) ||
                                            name.Contains("Intel(R) Graphics", StringComparison.OrdinalIgnoreCase) ||
                                            (name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) && !name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) && !name.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase)) ||
                                            name.Contains("Integrated", StringComparison.OrdinalIgnoreCase);

                        bool isDedicated = !isIntegrated && (
                                            name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                            name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                                            name.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                                            name.Contains("GTX", StringComparison.OrdinalIgnoreCase) ||
                                            name.Contains("Quadro", StringComparison.OrdinalIgnoreCase) ||
                                            name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) ||
                                            name.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase) ||
                                            name.Contains("Arc A", StringComparison.OrdinalIgnoreCase));

                        string role = isDedicated ? "Dedicated GPU" : "Integrated GPU";

                        if (gpuIndex > 1)
                        {
                            staticSb.AppendLine();
                            staticSb.AppendLine($"Graphics Adapter {gpuIndex} ({role}): {name}");
                        }
                        else
                        {
                            string prefix = validGpus.Count > 1 ? $"Graphics Adapter 1 ({role}): " : $"Graphics Adapter ({role}): ";
                            staticSb.AppendLine($"{prefix}{name}");
                        }

                        if (!string.IsNullOrEmpty(driverVer))
                            staticSb.AppendLine($"Driver Version: {driverVer}");
                        if (!string.IsNullOrEmpty(driverDate))
                            staticSb.AppendLine($"Driver Date: {driverDate}");
                        if (!string.IsNullOrEmpty(vramStr))
                            staticSb.AppendLine($"Dedicated Video Memory: {vramStr}");
                        if (!string.IsNullOrEmpty(videoProc) && !videoProc.Equals(name, StringComparison.OrdinalIgnoreCase))
                            staticSb.AppendLine($"Video Processor: {videoProc}");
                    }
                }
                catch { }

                if (staticSb.Length == 0)
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

                sb.AppendLine($"Battery Health: {healthPct:F1}% ({condition})");
                sb.AppendLine($"Wear Level: {wearPct:F1}%");
                sb.AppendLine($"Full Charge Capacity: {fullMwh:N0} mWh");
                sb.AppendLine($"Design Capacity: {designMwh:N0} mWh");
            }

            if (cycles > 0)
            {
                sb.AppendLine($"Cycle Count: {cycles:N0} cycles");
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
                foreach (ManagementObject b in battSearcher.Get())
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

        private static (long DesignMwh, long FullMwh, int CycleCount, string Mfg, string Serial) GetBatteryHealthData()
        {
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
                        return ParseBatteryHealthXml(xml);
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
            return (0, 0, 0, string.Empty, string.Empty);
        }

        internal static string GetActivePowerPlan()
        {
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

                    staticSb.AppendLine($"Monitor: {monitorName}");

                    if (width > 0 && height > 0)
                    {
                        string standard = GetResolutionStandard(width, height);
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

                    // HDR Status
                    staticSb.AppendLine("HDR (High Dynamic Range): SDR Mode (Standard Dynamic Range)");
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

        private static string GetResolutionStandard(int width, int height)
        {
            if (width == 3840 && height == 2160) return "(4K Ultra HD)";
            if (width == 2560 && height == 1440) return "(2K QHD)";
            if (width == 1920 && height == 1080) return "(Full HD)";
            if (width == 1600 && height == 900) return "(HD+)";
            if (width == 1366 && height == 768) return "(HD)";
            if (width == 1280 && height == 720) return "(720p HD)";
            return string.Empty;
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
                foreach (ManagementObject obj in searcher.Get())
                {
                    string mfg = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                    string model = obj["Model"]?.ToString()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(mfg) || !string.IsNullOrEmpty(model))
                    {
                        return $"{mfg} {model}".Trim();
                    }
                }
            }
            catch { }

            return string.Empty;
        }

        #endregion
    }
}
