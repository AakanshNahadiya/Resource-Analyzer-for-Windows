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
using AccessibleTaskManager.Helpers;

namespace AccessibleTaskManager.Services
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
        private static string? _cachedDiskStaticInfo;
        private static string? _cachedGpuStaticInfo;

        public async Task<string> GetHardwareDetailsAsync(string resourceId)
        {
            return await Task.Run(() =>
            {
                try
                {
                    return resourceId.ToLowerInvariant() switch
                    {
                        "cpu" => BuildCpuDetails(),
                        "ram" => BuildRamDetails(),
                        "disk" => BuildDiskDetails(),
                        "network" => BuildNetworkDetails(),
                        "gpu" => BuildGpuDetails(),
                        "battery" => BuildBatteryDetails(),
                        _ => "Technical details are not available for this component."
                    };
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
                            sb.AppendLine("⚠️ Thermal Throttling: Active (Processor frequency significantly restricted by thermal or power limits)");
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
                        string channelNote = modules.Count >= 2 && (modules.Count % 2 == 0) ? " - Dual-Channel active" : string.Empty;
                        staticSb.AppendLine($"Slots Used: {modules.Count} of {totalSlots} ({(totalSlots - modules.Count)} empty){channelNote}");
                    }
                    else if (modules.Count > 0)
                    {
                        staticSb.AppendLine($"Memory Modules: {modules.Count} installed");
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

        private string BuildDiskDetails()
        {
            var sb = new StringBuilder();

            string sysDrivePath = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(sysDrivePath);

            if (drive.IsReady)
            {
                long freeBytes = drive.AvailableFreeSpace;
                long totalBytes = drive.TotalSize;
                long usedBytes = Math.Max(0, totalBytes - freeBytes);
                double usedPercent = totalBytes > 0 ? (double)usedBytes * 100.0 / totalBytes : 0;

                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;

                sb.AppendLine($"Drive: {drive.Name.TrimEnd('\\')} ({label})");
                sb.AppendLine($"File System: {drive.DriveFormat}");
                sb.AppendLine($"Drive Type: {drive.DriveType}");
                sb.AppendLine($"Total Capacity: {FormatHelper.FormatBytes(totalBytes)} ({totalBytes:N0} bytes)");
                sb.AppendLine($"Free Space: {FormatHelper.FormatBytes(freeBytes)} ({100.0 - usedPercent:F1}% free)");
                sb.AppendLine($"Used Space: {FormatHelper.FormatBytes(usedBytes)} ({usedPercent:F1}% used)");
            }

            // Static Disk Hardware Specs (Model, Interface, Media, Status, SMART Health)
            if (_cachedDiskStaticInfo == null)
            {
                var staticSb = new StringBuilder();
                bool gotStorageInfo = false;

                // Query Storage Management Provider (root\Microsoft\Windows\Storage)
                try
                {
                    using var storageSearcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                        "SELECT FriendlyName, MediaType, BusType, HealthStatus, OperationalStatus, Size FROM MSFT_PhysicalDisk");
                    foreach (ManagementObject disk in storageSearcher.Get())
                    {
                        string diskName = disk["FriendlyName"]?.ToString()?.Trim() ?? string.Empty;
                        if (string.IsNullOrEmpty(diskName)) continue;

                        string mediaStr = "Solid State Drive (SSD)";
                        if (disk["MediaType"] != null && ushort.TryParse(disk["MediaType"]?.ToString(), out ushort mediaType))
                        {
                            mediaStr = mediaType switch
                            {
                                3 => "HDD (Hard Disk Drive)",
                                4 => "SSD (Solid State Drive)",
                                5 => "SCM (Storage Class Memory)",
                                _ => "Disk Drive"
                            };
                        }

                        string busStr = string.Empty;
                        if (disk["BusType"] != null && ushort.TryParse(disk["BusType"]?.ToString(), out ushort busType))
                        {
                            busStr = busType switch
                            {
                                1 => "SCSI",
                                2 => "ATAPI",
                                3 => "ATA",
                                7 => "USB",
                                8 => "RAID",
                                10 => "SAS",
                                11 => "SATA",
                                17 => "NVMe (PCIe Non-Volatile Memory Express)",
                                _ => busType.ToString()
                            };
                        }

                        if (diskName.Contains("NVMe", StringComparison.OrdinalIgnoreCase) && !busStr.StartsWith("NVMe"))
                        {
                            busStr = "NVMe (PCIe Non-Volatile Memory Express)";
                        }

                        string healthStr = "Healthy";
                        if (disk["HealthStatus"] != null && ushort.TryParse(disk["HealthStatus"]?.ToString(), out ushort health))
                        {
                            healthStr = health switch
                            {
                                0 => "Healthy",
                                1 => "Warning",
                                2 => "Unhealthy",
                                _ => "Unknown"
                            };
                        }

                        string opStr = "OK";
                        if (disk["OperationalStatus"] != null)
                        {
                            if (disk["OperationalStatus"] is Array opArr && opArr.Length > 0)
                            {
                                int firstOp = Convert.ToInt32(opArr.GetValue(0));
                                opStr = firstOp switch
                                {
                                    2 => "OK",
                                    3 => "Degraded",
                                    4 => "Stressed",
                                    5 => "Predictive Failure",
                                    6 => "Error",
                                    _ => "Normal"
                                };
                            }
                        }

                        staticSb.AppendLine($"Physical Drive: {diskName}");
                        staticSb.AppendLine($"Media Type: {mediaStr}");
                        if (!string.IsNullOrEmpty(busStr)) staticSb.AppendLine($"Bus Type: {busStr}");
                        staticSb.AppendLine($"SMART Health Status: {healthStr} (Operational: {opStr})");

                        gotStorageInfo = true;
                        break;
                    }
                }
                catch { }

                // Query Storage Reliability (Wear %, Temperature, Power-on Hours - available when elevated)
                try
                {
                    using var relSearcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                        "SELECT Wear, Temperature, PowerOnHours FROM MSFT_StorageReliabilityCounter");
                    foreach (ManagementObject rel in relSearcher.Get())
                    {
                        if (rel["Wear"] != null && byte.TryParse(rel["Wear"]?.ToString(), out byte wear))
                        {
                            staticSb.AppendLine($"SSD Wear Level: {wear}% used ({(100 - wear)}% life remaining)");
                        }
                        if (rel["Temperature"] != null && short.TryParse(rel["Temperature"]?.ToString(), out short temp) && temp > 0)
                        {
                            staticSb.AppendLine($"Drive Temperature: {temp}°C");
                        }
                        if (rel["PowerOnHours"] != null && ulong.TryParse(rel["PowerOnHours"]?.ToString(), out ulong hours))
                        {
                            staticSb.AppendLine($"Power-On Hours: {hours:N0} hours ({hours / 24:N0} days)");
                        }
                        break;
                    }
                }
                catch { }

                // Fallback to Win32_DiskDrive if root\Microsoft\Windows\Storage returned nothing
                if (!gotStorageInfo)
                {
                    try
                    {
                        using var diskSearcher = new ManagementObjectSearcher("SELECT Model, InterfaceType, MediaType, Partitions, Status, Size FROM Win32_DiskDrive");
                        foreach (ManagementObject disk in diskSearcher.Get())
                        {
                            string model = disk["Model"]?.ToString()?.Trim() ?? string.Empty;
                            string iface = disk["InterfaceType"]?.ToString()?.Trim() ?? string.Empty;
                            string media = disk["MediaType"]?.ToString()?.Trim() ?? string.Empty;
                            string status = disk["Status"]?.ToString()?.Trim() ?? string.Empty;
                            string partitions = disk["Partitions"]?.ToString()?.Trim() ?? string.Empty;

                            if (!string.IsNullOrEmpty(model))
                            {
                                staticSb.AppendLine($"Physical Drive: {model}");
                                if (!string.IsNullOrEmpty(iface))
                                    staticSb.AppendLine($"Interface: {iface}");
                                if (!string.IsNullOrEmpty(media))
                                    staticSb.AppendLine($"Media Type: {media}");
                                if (!string.IsNullOrEmpty(partitions))
                                    staticSb.AppendLine($"Partitions: {partitions}");
                                if (!string.IsNullOrEmpty(status))
                                    staticSb.AppendLine($"Disk Health Status: {status}");
                                break;
                            }
                        }
                    }
                    catch { }
                }

                _cachedDiskStaticInfo = staticSb.ToString();
            }

            sb.Append(_cachedDiskStaticInfo);

            // List other local drives
            try
            {
                var otherDrives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && !string.Equals(d.Name, sysDrivePath, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (otherDrives.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Other Connected Drives:");
                    foreach (var od in otherDrives)
                    {
                        string odLabel = string.IsNullOrWhiteSpace(od.VolumeLabel) ? od.DriveType.ToString() : od.VolumeLabel;
                        sb.AppendLine($"• {od.Name.TrimEnd('\\')} ({odLabel}): {FormatHelper.FormatBytes(od.AvailableFreeSpace)} free of {FormatHelper.FormatBytes(od.TotalSize)}");
                    }
                }
            }
            catch { }

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
                    int gpuIndex = 0;
                    foreach (ManagementObject gpu in gpuSearcher.Get())
                    {
                        gpuIndex++;
                        string name = gpu["Name"]?.ToString()?.Trim() ?? string.Empty;
                        string driverVer = gpu["DriverVersion"]?.ToString()?.Trim() ?? string.Empty;
                        string vramStr = string.Empty;

                        if (gpu["AdapterRAM"] != null && long.TryParse(gpu["AdapterRAM"]?.ToString(), out long vramBytes) && vramBytes > 0)
                        {
                            vramStr = FormatHelper.FormatBytes(vramBytes);
                        }

                        string videoProc = gpu["VideoProcessor"]?.ToString()?.Trim() ?? string.Empty;
                        string mode = gpu["VideoModeDescription"]?.ToString()?.Trim() ?? string.Empty;
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

                        if (!string.IsNullOrEmpty(name))
                        {
                            if (gpuIndex > 1)
                            {
                                staticSb.AppendLine();
                                staticSb.AppendLine($"Secondary Graphics Adapter:");
                            }
                            else
                            {
                                staticSb.AppendLine($"Graphics Adapter: {name}");
                            }

                            if (!string.IsNullOrEmpty(driverVer))
                                staticSb.AppendLine($"Driver Version: {driverVer}");
                            if (!string.IsNullOrEmpty(driverDate))
                                staticSb.AppendLine($"Driver Date: {driverDate}");
                            if (!string.IsNullOrEmpty(vramStr))
                                staticSb.AppendLine($"Dedicated Video Memory: {vramStr}");
                            if (!string.IsNullOrEmpty(videoProc) && !videoProc.Equals(name, StringComparison.OrdinalIgnoreCase))
                                staticSb.AppendLine($"Video Processor: {videoProc}");
                            if (!string.IsNullOrEmpty(mode))
                                staticSb.AppendLine($"Display Mode: {mode}");
                        }
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
