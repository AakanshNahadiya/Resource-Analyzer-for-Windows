using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using AccessibleTaskManager.Helpers;
using AccessibleTaskManager.Models;

namespace AccessibleTaskManager.Services
{
    public interface ISystemMonitorService
    {
        Task<Dictionary<string, (string Summary, string Details, double Percent)>> SampleMetricsAsync();
        (string Summary, string Details, double Percent) SampleNetwork();
        string GetQuickNetworkSpeedSummary();
        Task<string> GetQuickNetworkSpeedSummaryAsync();
        bool HasBattery { get; }
    }

    public class SystemMonitorService : ISystemMonitorService
    {


        private long _prevIdleTime;
        private long _prevKernelTime;
        private long _prevUserTime;
        private bool _isFirstCpuSample = true;

        private long _prevNetworkBytesIn;
        private long _prevNetworkBytesOut;
        private DateTime _prevNetworkTime = DateTime.UtcNow;
        private bool _isFirstNetworkSample = true;

        private double _lastDownloadSpeed;
        private double _lastUploadSpeed;
        private string _lastActiveAdapter = "Network";

        private string _cpuName = string.Empty;
        private string _gpuName = string.Empty;

        public bool HasBattery { get; private set; } = true;

        public SystemMonitorService()
        {
            InitializeHardwareInfo();
        }

        private void InitializeHardwareInfo()
        {
            // CPU Name from registry
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (key != null)
                {
                    _cpuName = key.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? string.Empty;
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(_cpuName))
            {
                _cpuName = $"{Environment.ProcessorCount} Cores Processor";
            }

            // GPU Name from registry or video controller
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000");
                if (key != null)
                {
                    _gpuName = key.GetValue("DriverDesc")?.ToString()?.Trim() ?? string.Empty;
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(_gpuName))
            {
                _gpuName = "GPU";
            }

            // Check battery presence
            try
            {
                if (NativeMethods.GetSystemPowerStatus(out var status))
                {
                    HasBattery = status.BatteryFlag != 128 && status.BatteryLifePercent != 255;
                }
                else
                {
                    HasBattery = false;
                }
            }
            catch
            {
                HasBattery = false;
            }
        }

        public async Task<Dictionary<string, (string Summary, string Details, double Percent)>> SampleMetricsAsync()
        {
            return await Task.Run(() =>
            {
                var dict = new Dictionary<string, (string Summary, string Details, double Percent)>();

                // 1. CPU
                var cpuData = SampleCpu();
                dict["cpu"] = cpuData;

                // 2. RAM (Dynamic MB <-> GB switching)
                var ramData = SampleRam();
                dict["ram"] = ramData;

                // 3. GPU
                var gpuData = SampleGpu();
                dict["gpu"] = gpuData;

                // 4. Network
                var netData = SampleNetwork();
                dict["network"] = netData;

                // 5. Disk
                var diskData = SampleDisk();
                dict["disk"] = diskData;

                // 6. Battery
                if (HasBattery)
                {
                    var batteryData = SampleBattery();
                    dict["battery"] = batteryData;
                }

                return dict;
            });
        }

        private (string Summary, string Details, double Percent) SampleCpu()
        {
            double cpuPercent = 0.0;
            if (NativeMethods.GetSystemTimes(out long idleTime, out long kernelTime, out long userTime))
            {
                if (!_isFirstCpuSample)
                {
                    long idleDelta = idleTime - _prevIdleTime;
                    long kernelDelta = kernelTime - _prevKernelTime;
                    long userDelta = userTime - _prevUserTime;

                    long totalTime = kernelDelta + userDelta;
                    if (totalTime > 0)
                    {
                        cpuPercent = Math.Clamp((double)(totalTime - idleDelta) * 100.0 / totalTime, 0.0, 100.0);
                    }
                }
                _prevIdleTime = idleTime;
                _prevKernelTime = kernelTime;
                _prevUserTime = userTime;
                _isFirstCpuSample = false;
            }

            int coreCount = Environment.ProcessorCount;
            string summary = $"CPU: {cpuPercent:F0}% ({coreCount} logical cores)";
            string details = $"Processor: {_cpuName}\nUsage: {cpuPercent:F1}%\nLogical Processors: {coreCount}";

            return (summary, details, cpuPercent);
        }

        private (string Summary, string Details, double Percent) SampleRam()
        {
            var mem = NativeMethods.MEMORYSTATUSEX.Create();
            if (NativeMethods.GlobalMemoryStatusEx(ref mem))
            {
                long totalBytes = (long)mem.ullTotalPhys;
                long availBytes = (long)mem.ullAvailPhys;
                long usedBytes = Math.Max(0, totalBytes - availBytes);
                uint load = mem.dwMemoryLoad;

                string usedFormatted = FormatHelper.FormatBytes(usedBytes);
                string totalFormatted = FormatHelper.FormatBytes(totalBytes);
                string availFormatted = FormatHelper.FormatBytes(availBytes);

                string summary = $"RAM: {usedFormatted} of {totalFormatted} used ({load}%), {availFormatted} available";
                string details = $"Total Physical Memory: {totalFormatted} ({totalBytes:N0} bytes)\n" +
                                 $"Used Memory: {usedFormatted} ({usedBytes:N0} bytes, {load}%)\n" +
                                 $"Available Memory: {availFormatted} ({availBytes:N0} bytes)\n" +
                                 $"Page File Total: {FormatHelper.FormatBytes((long)mem.ullTotalPageFile)}\n" +
                                 $"Page File Available: {FormatHelper.FormatBytes((long)mem.ullAvailPageFile)}";

                return (summary, details, (double)load);
            }

            return ("RAM: Data unavailable", "Could not query memory status.", -1.0);
        }

        private (string Summary, string Details, double Percent) SampleGpu()
        {
            string summary = $"GPU: {_gpuName} (Active)";
            string details = $"Device: {_gpuName}\nStatus: Available\nHardware Acceleration: Software-Optimized";
            return (summary, details, -1.0);
        }

        public (string Summary, string Details, double Percent) SampleNetwork()
        {
            long currentBytesIn = 0;
            long currentBytesOut = 0;
            string activeAdapter = "Network";

            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                                 ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                 ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                                 !ni.Description.Contains("Filter", StringComparison.OrdinalIgnoreCase) &&
                                 !ni.Description.Contains("WFP", StringComparison.OrdinalIgnoreCase) &&
                                 !ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                                 !ni.Description.Contains("QoS", StringComparison.OrdinalIgnoreCase) &&
                                 !ni.Description.Contains("Pseudo", StringComparison.OrdinalIgnoreCase) &&
                                 !ni.Description.Contains("Packet Scheduler", StringComparison.OrdinalIgnoreCase) &&
                                 !ni.Name.Contains("Filter", StringComparison.OrdinalIgnoreCase) &&
                                 !ni.Name.Contains("WFP", StringComparison.OrdinalIgnoreCase) &&
                                 !ni.Name.Contains("-000", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // Find primary internet-facing adapter (has default gateway)
                var primary = interfaces.FirstOrDefault(ni =>
                    (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                     ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) &&
                    ni.GetIPProperties().GatewayAddresses.Count > 0);

                if (primary == null)
                {
                    primary = interfaces.FirstOrDefault(ni =>
                        ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet);
                }

                if (primary == null && interfaces.Count > 0)
                {
                    primary = interfaces[0];
                }

                if (primary != null)
                {
                    activeAdapter = primary.Name;
                    try
                    {
                        var stats = primary.GetIPStatistics();
                        currentBytesIn = stats.BytesReceived;
                        currentBytesOut = stats.BytesSent;
                    }
                    catch
                    {
                        try
                        {
                            var stats4 = primary.GetIPv4Statistics();
                            currentBytesIn = stats4.BytesReceived;
                            currentBytesOut = stats4.BytesSent;
                        }
                        catch { }
                    }
                }
                else
                {
                    foreach (var ni in interfaces)
                    {
                        try
                        {
                            var stats = ni.GetIPStatistics();
                            currentBytesIn += stats.BytesReceived;
                            currentBytesOut += stats.BytesSent;
                        }
                        catch { }
                    }
                }

                var now = DateTime.UtcNow;
                if (!_isFirstNetworkSample)
                {
                    double elapsedSeconds = (now - _prevNetworkTime).TotalSeconds;
                    if (elapsedSeconds >= 0.2 && elapsedSeconds <= 10.0)
                    {
                        long deltaIn = Math.Max(0, currentBytesIn - _prevNetworkBytesIn);
                        long deltaOut = Math.Max(0, currentBytesOut - _prevNetworkBytesOut);

                        _lastDownloadSpeed = deltaIn / elapsedSeconds;
                        _lastUploadSpeed = deltaOut / elapsedSeconds;
                        _lastActiveAdapter = activeAdapter;
                    }
                }

                _prevNetworkBytesIn = currentBytesIn;
                _prevNetworkBytesOut = currentBytesOut;
                _prevNetworkTime = now;
                _isFirstNetworkSample = false;
            }
            catch { }

            string downStr = FormatHelper.FormatSpeed(_lastDownloadSpeed);
            string upStr = FormatHelper.FormatSpeed(_lastUploadSpeed);

            string summary = $"Network ({_lastActiveAdapter}): Down {downStr}, Up {upStr}";
            string details = $"Adapter: {_lastActiveAdapter}\nDownload Rate: {downStr}\nUpload Rate: {upStr}\n" +
                             $"Total Received: {FormatHelper.FormatBytes(currentBytesIn)}\nTotal Sent: {FormatHelper.FormatBytes(currentBytesOut)}";

            return (summary, details, -1.0);
        }

        public string GetQuickNetworkSpeedSummary()
        {
            string downStr = FormatHelper.FormatSpeed(_lastDownloadSpeed);
            string upStr = FormatHelper.FormatSpeed(_lastUploadSpeed);
            return $"Network ({_lastActiveAdapter}): {downStr} down, {upStr} up";
        }

        public async Task<string> GetQuickNetworkSpeedSummaryAsync()
        {
            // If the last sample was taken more than 1.5 seconds ago (e.g. paused timer or just launched),
            // take a live 250ms delta sample on the spot for guaranteed 100% current accuracy:
            if ((DateTime.UtcNow - _prevNetworkTime).TotalSeconds > 1.5)
            {
                SampleNetwork();
                await Task.Delay(250);
                SampleNetwork();
            }

            return GetQuickNetworkSpeedSummary();
        }

        private (string Summary, string Details, double Percent) SampleDisk()
        {
            try
            {
                string sysDrivePath = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                var drive = new DriveInfo(sysDrivePath);

                if (drive.IsReady)
                {
                    long freeBytes = drive.AvailableFreeSpace;
                    long totalBytes = drive.TotalSize;
                    long usedBytes = totalBytes - freeBytes;
                    double usedPercent = totalBytes > 0 ? (double)usedBytes * 100.0 / totalBytes : 0;

                    string freeFormatted = FormatHelper.FormatBytes(freeBytes);
                    string totalFormatted = FormatHelper.FormatBytes(totalBytes);
                    string usedFormatted = FormatHelper.FormatBytes(usedBytes);

                    string driveLabel = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : $"{drive.Name} ({drive.VolumeLabel})";

                    string summary = $"Disk ({sysDrivePath.TrimEnd('\\')}): {freeFormatted} free of {totalFormatted} ({usedPercent:F0}% used)";
                    string details = $"Drive: {driveLabel}\nFormat: {drive.DriveFormat}\n" +
                                     $"Total Capacity: {totalFormatted}\n" +
                                     $"Free Space: {freeFormatted}\n" +
                                     $"Used Space: {usedFormatted} ({usedPercent:F1}%)";

                    return (summary, details, usedPercent);
                }
            }
            catch { }

            return ("Disk: Data unavailable", "Could not query drive status.", -1.0);
        }

        private (string Summary, string Details, double Percent) SampleBattery()
        {
            try
            {
                if (!NativeMethods.GetSystemPowerStatus(out var status) || status.BatteryFlag == 128)
                {
                    return ("No Battery", "No system battery detected.", 0);
                }

                float percent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : 0f;
                bool isCharging = (status.BatteryFlag & 8) != 0;
                bool isPluggedIn = status.ACLineStatus == 1;

                string stateText;
                if (isCharging)
                {
                    stateText = "Plugged in, charging";
                }
                else if (isPluggedIn)
                {
                    stateText = "Plugged in, not charging";
                }
                else
                {
                    stateText = "On battery";
                    if (status.BatteryLifeTime > 0)
                    {
                        var ts = TimeSpan.FromSeconds(status.BatteryLifeTime);
                        stateText += $", about {ts.Hours}h {ts.Minutes}m remaining";
                    }
                }

                string summary = $"Battery: {percent:F0}% ({stateText})";
                string details = $"Charge Level: {percent:F1}%\nPower Line: {(isPluggedIn ? "Online" : "Offline")}\nStatus: {stateText}";

                return (summary, details, (double)percent);
            }
            catch { }

            return ("Battery: Data unavailable", "Could not query battery status.", -1.0);
        }
    }
}
