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

                sb.AppendLine($"Committed: {FormatHelper.FormatBytes(commitTotal)} of {FormatHelper.FormatBytes(commitLimit)}");
                sb.AppendLine($"Cached: {FormatHelper.FormatBytes(systemCache)}");
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

                    using var memSearcher = new ManagementObjectSearcher("SELECT Speed, ConfiguredClockSpeed, FormFactor, DeviceLocator, Manufacturer, PartNumber, Capacity FROM Win32_PhysicalMemory");
                    var modules = new List<string>();
                    uint ramSpeed = 0;
                    string formFactorStr = string.Empty;

                    foreach (ManagementObject stick in memSearcher.Get())
                    {
                        if (ramSpeed == 0 && stick["Speed"] != null && uint.TryParse(stick["Speed"]?.ToString(), out uint spd))
                            ramSpeed = spd;

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

                        string capStr = cap > 0 ? FormatHelper.FormatBytes(cap) : "Memory Module";
                        string stickDesc = $"{locator}: {mfg} {capStr}".Trim();
                        if (!string.IsNullOrEmpty(part)) stickDesc += $" (Part: {part})";
                        modules.Add(stickDesc);
                    }

                    if (ramSpeed > 0)
                        staticSb.AppendLine($"Memory Speed: {ramSpeed} MHz");
                    if (totalSlots > 0)
                        staticSb.AppendLine($"Slots Used: {modules.Count} of {totalSlots} ({(totalSlots - modules.Count)} empty)");
                    else if (modules.Count > 0)
                        staticSb.AppendLine($"Memory Modules: {modules.Count} installed");
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

            // Static Disk Hardware Specs (Model, Interface, Media, Status)
            if (_cachedDiskStaticInfo == null)
            {
                var staticSb = new StringBuilder();
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

            // Battery Device & Chemistry from WMI
            try
            {
                using var battSearcher = new ManagementObjectSearcher("SELECT Name, DeviceID, Chemistry FROM Win32_Battery");
                foreach (ManagementObject b in battSearcher.Get())
                {
                    string bName = b["Name"]?.ToString()?.Trim() ?? string.Empty;
                    string bDevId = b["DeviceID"]?.ToString()?.Trim() ?? string.Empty;
                    if (!string.IsNullOrEmpty(bName))
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

        #endregion
    }
}
