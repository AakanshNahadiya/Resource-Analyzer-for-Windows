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
using ResourceAnalyzer.Helpers;
using ResourceAnalyzer.Models;

namespace ResourceAnalyzer.Services
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
        private string? _cachedWifiInfo;
        private DateTime _lastWifiQueryTime = DateTime.MinValue;
        private bool _isWifiQueryInProgress = false;
        private bool _lastIsWireless = false;

        private string _cpuName = string.Empty;
        private string _gpuName = string.Empty;
        private int _gpuCount = 1;
        private string _gpuSummary = string.Empty;

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

            // Multi-GPU detection from registry video class
            try
            {
                var detectedGpus = new List<string>();
                for (int i = 0; i <= 5; i++)
                {
                    string subKey = $@"SYSTEM\CurrentControlSet\Control\Class\{{4d36e968-e325-11ce-bfc1-08002be10318}}\{i:D4}";
                    using var key = Registry.LocalMachine.OpenSubKey(subKey);
                    if (key != null)
                    {
                        string desc = key.GetValue("DriverDesc")?.ToString()?.Trim() ?? string.Empty;
                        if (!string.IsNullOrEmpty(desc) && !detectedGpus.Contains(desc, StringComparer.OrdinalIgnoreCase))
                        {
                            detectedGpus.Add(desc);
                        }
                    }
                }

                if (detectedGpus.Count > 0)
                {
                    _gpuCount = detectedGpus.Count;
                    _gpuName = detectedGpus[0];
                    _gpuSummary = _gpuCount > 1
                        ? $"GPU: {_gpuName} ({_gpuCount} GPUs detected)"
                        : $"GPU: {_gpuName} (Active)";
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(_gpuName))
            {
                _gpuName = "GPU";
                _gpuSummary = "GPU: Active";
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

                // 3b. Display (placed right next to GPU)
                var displayData = SampleDisplay();
                dict["display"] = displayData;

                // 4. Network
                var netData = SampleNetwork();
                dict["network"] = netData;

                // 5. Disk
                var allDisks = SampleAllDisks();
                foreach (var diskKvp in allDisks)
                {
                    dict[diskKvp.Key] = diskKvp.Value;
                }

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
            string summary = _gpuSummary;
            string countStr = _gpuCount > 1 ? $"{_gpuCount} GPUs Detected" : "1 GPU Detected";
            string details = $"Device: {_gpuName}\nGPU Count: {countStr}\nStatus: Available";
            return (summary, details, -1.0);
        }

        private (string Summary, string Details, double Percent) SampleDisplay()
        {
            try
            {
                var dm = new NativeMethods.DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(NativeMethods.DEVMODE));

                if (NativeMethods.EnumDisplaySettings(null, NativeMethods.ENUM_CURRENT_SETTINGS, ref dm))
                {
                    int width = dm.dmPelsWidth;
                    int height = dm.dmPelsHeight;
                    int freq = dm.dmDisplayFrequency;
                    int bits = dm.dmBitsPerPel;

                    uint dpi = 96;
                    try { dpi = NativeMethods.GetDpiForSystem(); } catch { }
                    if (dpi == 0) dpi = 96;
                    int scalePct = (int)Math.Round(dpi * 100.0 / 96.0);

                    int monitorCount = 1;
                    try { monitorCount = NativeMethods.GetSystemMetrics(NativeMethods.SM_CMONITORS); } catch { }
                    if (monitorCount <= 0) monitorCount = 1;

                    string std = GetResolutionStandard(width, height);
                    string summary = (freq > 0)
                        ? $"Display: {width}x{height} @ {freq} Hz ({scalePct}% Scaling)"
                        : $"Display: {width}x{height} ({scalePct}% Scaling)";

                    string countStr = monitorCount > 1 ? $"{monitorCount} Displays Connected (Multi-Monitor)" : "1 Display Connected";
                    string details = $"Resolution: {width} x {height} {std}\n" +
                                     $"Refresh Rate: {freq} Hz\n" +
                                     $"Color Depth: {bits}-bit True Color\n" +
                                     $"Windows Scaling: {scalePct}% ({dpi} DPI)\n" +
                                     $"Monitors: {countStr}";

                    return (summary, details, -1.0);
                }
            }
            catch { }

            return ("Display: Active", "Display information unavailable.", -1.0);
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
                    _lastIsWireless = primary.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                                      primary.Name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                                      primary.Description.Contains("Wireless", StringComparison.OrdinalIgnoreCase);
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
                    _lastIsWireless = false;
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

                // Wi-Fi Telemetry Cache (SSID, Band, Signal Strength)
                if (_lastIsWireless)
                {
                    if (_lastWifiQueryTime == DateTime.MinValue)
                    {
                        try
                        {
                            string raw = QueryWifiInterfacesRaw();
                            var (ssid, band, signal) = ParseWifiInterfacesOutput(raw);
                            _cachedWifiInfo = FormatWifiSummaryTag(ssid, band, signal);
                        }
                        catch { }
                        _lastWifiQueryTime = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - _lastWifiQueryTime).TotalSeconds >= 5 && !_isWifiQueryInProgress)
                    {
                        _isWifiQueryInProgress = true;
                        Task.Run(() =>
                        {
                            try
                            {
                                string raw = QueryWifiInterfacesRaw();
                                var (ssid, band, signal) = ParseWifiInterfacesOutput(raw);
                                _cachedWifiInfo = FormatWifiSummaryTag(ssid, band, signal);
                            }
                            catch { }
                            finally
                            {
                                _lastWifiQueryTime = DateTime.UtcNow;
                                _isWifiQueryInProgress = false;
                            }
                        });
                    }
                }
                else
                {
                    _cachedWifiInfo = null;
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

            string displayAdapter = _lastActiveAdapter;
            if (_lastIsWireless && !string.IsNullOrEmpty(_cachedWifiInfo))
            {
                displayAdapter = $"Wi-Fi - {_cachedWifiInfo}";
            }

            string summary = $"Network ({displayAdapter}): Down {downStr}, Up {upStr}";
            string details = $"Adapter: {_lastActiveAdapter}\n" +
                             (_lastIsWireless && !string.IsNullOrEmpty(_cachedWifiInfo) ? $"Wi-Fi Connection: {_cachedWifiInfo}\n" : "") +
                             $"Download Rate: {downStr}\nUpload Rate: {upStr}\n" +
                             $"Total Received: {FormatHelper.FormatBytes(currentBytesIn)}\nTotal Sent: {FormatHelper.FormatBytes(currentBytesOut)}";

            return (summary, details, -1.0);
        }

        public static string QueryWifiInterfacesRaw()
        {
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
                    return output;
                }
            }
            catch { }
            return string.Empty;
        }

        public static (string? Ssid, string? Band, string? Signal) ParseWifiInterfacesOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return (null, null, null);

            string? ssid = null;
            string? band = null;
            string? signal = null;
            bool isConnected = false;

            using var reader = new StringReader(output);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                int colonIdx = line.IndexOf(':');
                if (colonIdx > 0 && colonIdx < line.Length - 1)
                {
                    string key = line.Substring(0, colonIdx).Trim();
                    string val = line.Substring(colonIdx + 1).Trim();

                    if (key.Equals("State", StringComparison.OrdinalIgnoreCase))
                    {
                        if (val.Equals("connected", StringComparison.OrdinalIgnoreCase))
                        {
                            isConnected = true;
                        }
                    }
                    else if (key.Equals("SSID", StringComparison.OrdinalIgnoreCase))
                    {
                        ssid = val;
                    }
                    else if (key.Equals("Band", StringComparison.OrdinalIgnoreCase))
                    {
                        band = val;
                    }
                    else if (key.Equals("Signal", StringComparison.OrdinalIgnoreCase))
                    {
                        signal = val;
                    }
                }
            }

            if (!isConnected && string.IsNullOrEmpty(ssid))
            {
                return (null, null, null);
            }

            return (ssid, band, signal);
        }

        public static string FormatWifiSummaryTag(string? ssid, string? band, string? signal)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ssid)) parts.Add(ssid);
            if (!string.IsNullOrWhiteSpace(band)) parts.Add(band);
            if (!string.IsNullOrWhiteSpace(signal)) parts.Add(signal.EndsWith("%") ? signal : $"{signal}%");

            return parts.Count > 0 ? string.Join(", ", parts) : string.Empty;
        }

        public string GetQuickNetworkSpeedSummary()
        {
            string downStr = FormatHelper.FormatSpeed(_lastDownloadSpeed);
            string upStr = FormatHelper.FormatSpeed(_lastUploadSpeed);
            return $"Network ({_lastActiveAdapter}): {downStr} down, {upStr} up";
        }

        public async Task<string> GetQuickNetworkSpeedSummaryAsync()
        {
            var now = DateTime.UtcNow;
            double elapsedSinceLastSample = (now - _prevNetworkTime).TotalSeconds;

            // If a background sample was taken between 0.3s and 8.0s ago, sample right now
            // across the elapsed delta to calculate microsecond-accurate real-time speed with 0ms wait:
            if (elapsedSinceLastSample >= 0.3 && elapsedSinceLastSample <= 8.0 && !_isFirstNetworkSample)
            {
                SampleNetwork();
                return GetQuickNetworkSpeedSummary();
            }

            // If the app was just launched or the timer was paused/suspended for > 8 seconds,
            // take a quick live 200ms delta sample on the spot for guaranteed accuracy:
            SampleNetwork();
            await Task.Delay(200);
            SampleNetwork();

            return GetQuickNetworkSpeedSummary();
        }

        public Dictionary<string, (string Summary, string Details, double Percent)> SampleAllDisks()
        {
            var result = new Dictionary<string, (string Summary, string Details, double Percent)>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var readyDrives = DriveInfo.GetDrives().Where(d => d.IsReady).ToList();

                // Group drives by physical disk number
                var diskGroups = new Dictionary<int, List<DriveInfo>>();

                foreach (var d in readyDrives)
                {
                    int dNum = NativeMethods.GetStorageDeviceNumber(d.Name);
                    if (dNum < 0)
                    {
                        char c = d.Name.Length > 0 ? char.ToUpperInvariant(d.Name[0]) : 'Z';
                        dNum = -(int)c;
                    }

                    if (!diskGroups.TryGetValue(dNum, out var list))
                    {
                        list = new List<DriveInfo>();
                        diskGroups[dNum] = list;
                    }
                    list.Add(d);
                }

                foreach (var kvp in diskGroups)
                {
                    int dNum = kvp.Key;
                    var drives = kvp.Value;

                    long totalBytes = 0;
                    long freeBytes = 0;
                    foreach (var d in drives)
                    {
                        totalBytes += d.TotalSize;
                        freeBytes += d.AvailableFreeSpace;
                    }
                    long usedBytes = Math.Max(0, totalBytes - freeBytes);
                    double usedPercent = totalBytes > 0 ? (double)usedBytes * 100.0 / totalBytes : 0;

                    string freeFormatted = FormatHelper.FormatDiskSize(freeBytes);
                    string totalFormatted = FormatHelper.FormatDiskSize(totalBytes);
                    string usedFormatted = FormatHelper.FormatDiskSize(usedBytes);

                    bool isExternal = false;
                    string mediaType = "SSD";
                    string modelName = "Disk Drive";

                    if (dNum >= 0)
                    {
                        var meta = HardwareDetailService.GetPhysicalDiskMetadata(dNum);
                        isExternal = !meta.IsInternal;
                        mediaType = meta.MediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase) ? "HDD" : "SSD";
                        modelName = meta.Model;
                    }
                    else
                    {
                        isExternal = drives.Any(d => d.DriveType == DriveType.Removable);
                    }

                    var (readSpeed, writeSpeed) = HardwareDetailService.GetLiveDiskSpeed(dNum);
                    string speedInfo = (readSpeed > 0 || writeSpeed > 0)
                        ? $"Live Speed: Read {FormatHelper.FormatSpeed(readSpeed)}, Write {FormatHelper.FormatSpeed(writeSpeed)}\n"
                        : "Live Speed: Idle (0 B/s Read, 0 B/s Write)\n";

                    if (!isExternal)
                    {
                        string diskKey = dNum == 0 ? "disk" : $"disk_{dNum}";
                        string diskLabel = $"Disk {Math.Max(0, dNum)} (Internal {mediaType})";

                        string summary = $"{diskLabel}: {freeFormatted} free of {totalFormatted} ({usedPercent:F0}% used)";
                        string details = $"Device: {diskLabel}\n" +
                                         $"Model: {modelName}\n" +
                                         $"Total Capacity: {totalFormatted}\n" +
                                         $"Free Space: {freeFormatted}\n" +
                                         $"Used Space: {usedFormatted} ({usedPercent:F1}%)\n" +
                                         speedInfo +
                                         $"Partitions ({drives.Count}): " + string.Join(", ", drives.Select(d => d.Name.TrimEnd('\\')));

                        result[diskKey] = (summary, details, usedPercent);
                    }
                    else
                    {
                        string letters = string.Join(", ", drives.Select(d => d.Name.TrimEnd('\\')));
                        string firstLetter = drives[0].Name.Substring(0, 1).ToUpperInvariant();
                        string usbKey = $"usb_{firstLetter}";
                        string usbLabel = $"USB Storage (External - {letters})";

                        string summary = $"{usbLabel}: {freeFormatted} free of {totalFormatted} ({usedPercent:F0}% used)";
                        string details = $"Device: {usbLabel}\n" +
                                         $"Model: {modelName}\n" +
                                         $"Total Capacity: {totalFormatted}\n" +
                                         $"Free Space: {freeFormatted}\n" +
                                         $"Used Space: {usedFormatted} ({usedPercent:F1}%)\n" +
                                         speedInfo +
                                         $"Partitions ({drives.Count}): {letters}";

                        result[usbKey] = (summary, details, usedPercent);
                    }
                }
            }
            catch { }

            if (!result.ContainsKey("disk"))
            {
                result["disk"] = ("Disk 0 (Internal SSD): Data unavailable", "Could not query drive status.", -1.0);
            }

            return result;
        }

        public (string Summary, string Details, double Percent) SampleDisk()
        {
            var all = SampleAllDisks();
            if (all.TryGetValue("disk", out var d)) return d;
            return ("Disk 0 (Internal SSD): Data unavailable", "Could not query drive status.", -1.0);
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
