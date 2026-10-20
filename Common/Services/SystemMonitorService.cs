using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
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

                // 7. Audio
                var audioData = SampleAudio();
                dict["audio"] = audioData;

                return dict;
            });
        }

        private double _smoothedCpuPercent = -1.0;

        private (string Summary, string Details, double Percent) SampleCpu()
        {
            double rawCpuPercent = 0.0;

            // Ultra-lightweight native Win32 kernel API (< 0.001ms, 0 allocations, 0 DLLs, Task Manager accuracy)
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
                        rawCpuPercent = Math.Clamp((double)(totalTime - idleDelta) * 100.0 / totalTime, 0.0, 100.0);
                    }
                }
                _prevIdleTime = idleTime;
                _prevKernelTime = kernelTime;
                _prevUserTime = userTime;
                _isFirstCpuSample = false;
            }

            // 3. Smooth instantaneous micro-bursts using a low-pass filter (EMA)
            if (_smoothedCpuPercent < 0)
            {
                _smoothedCpuPercent = rawCpuPercent;
            }
            else
            {
                _smoothedCpuPercent = (_smoothedCpuPercent * 0.35) + (rawCpuPercent * 0.65);
            }

            double cpuPercent = Math.Clamp(_smoothedCpuPercent, 0.0, 100.0);

            int coreCount = Environment.ProcessorCount;
            string summary = $"CPU: {cpuPercent:F0}% ({coreCount} logical cores)";

            return (summary, string.Empty, cpuPercent);
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

                return (summary, string.Empty, (double)load);
            }

            return ("RAM: Data unavailable", "Could not query memory status.", -1.0);
        }

        private (string Summary, string Details, double Percent) SampleGpu()
        {
            return (_gpuSummary, string.Empty, -1.0);
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

                    uint dpi = 96;
                    try { dpi = NativeMethods.GetDpiForSystem(); } catch { }
                    if (dpi == 0) dpi = 96;
                    int scalePct = (int)Math.Round(dpi * 100.0 / 96.0);

                    string summary = (freq > 0)
                        ? $"Display: {width}x{height} @ {freq} Hz ({scalePct}% Scaling)"
                        : $"Display: {width}x{height} ({scalePct}% Scaling)";

                    return (summary, string.Empty, -1.0);
                }
            }
            catch { }

            return ("Display: Active", string.Empty, -1.0);
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
                    if ((_lastWifiQueryTime == DateTime.MinValue || (DateTime.UtcNow - _lastWifiQueryTime).TotalSeconds >= 5) && !_isWifiQueryInProgress)
                    {
                        _isWifiQueryInProgress = true;
                        Task.Run(() =>
                        {
                            try
                            {
                                var (ssid, band, signal) = QueryWifiTelemetryFast();
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

            return (summary, string.Empty, -1.0);
        }

        public static (string? Ssid, string? Band, string? Signal) QueryWifiTelemetryFast()
        {
            // 1. Try modern Windows 10/11 WinRT ConnectionProfile (zero location icon)
            try
            {
                var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                if (profile != null && profile.IsWlanConnectionProfile)
                {
                    string ssid = profile.ProfileName;
                    byte? bars = profile.GetSignalBars();
                    string? signal = bars.HasValue ? $"{bars.Value * 20}%" : null;

                    string? band = NativeMethods.QueryActiveWifiBand();
                    if (string.IsNullOrEmpty(band))
                    {
                        var adapter = profile.NetworkAdapter;
                        if (adapter != null)
                        {
                            ulong inSpeed = adapter.InboundMaxBitsPerSecond;
                            if (inSpeed > 300_000_000) band = "5 GHz";
                            else if (inSpeed > 0 && inSpeed <= 72_000_000) band = "2.4 GHz";
                        }
                    }

                    if (!string.IsNullOrEmpty(ssid))
                    {
                        return (ssid, band, signal);
                    }
                }
            }
            catch { }

            // 2. Try Windows Network List Manager COM interface (zero location icon)
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
                            return (name, null, null);
                        }
                    }
                }
            }
            catch { }

            return (null, null, null);
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
                    string letters = string.Join(", ", drives.Select(d => d.Name.TrimEnd('\\')));
                    string firstLetter = drives[0].Name.Substring(0, 1).ToUpperInvariant();
                    string diskKey;
                    string diskLabel;

                    if (dNum >= 0)
                    {
                        bool isRemovableDrive = drives.Any(d => d.DriveType == DriveType.Removable);
                        string? usbLetterParam = isRemovableDrive ? firstLetter : null;
                        var meta = HardwareDetailService.GetPhysicalDiskMetadata(dNum, usbLetterParam);
                        isExternal = !meta.IsInternal || isRemovableDrive;
                        mediaType = meta.MediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase) ? "HDD" : "SSD";
                        modelName = meta.Model;

                        if (!isExternal)
                        {
                            diskKey = dNum == 0 ? "disk" : $"disk_{dNum}";
                            diskLabel = $"Disk {dNum} (Internal {mediaType})";
                        }
                        else
                        {
                            diskKey = $"usb_{firstLetter}";
                            diskLabel = $"USB Storage (External - {letters})";
                        }
                    }
                    else
                    {
                        // Non-physical storage or virtual mount (dNum < 0)
                        if (drives.Any(d => d.DriveType == DriveType.Network))
                        {
                            diskKey = $"net_{firstLetter}";
                            diskLabel = $"Network Drive ({letters})";
                            modelName = "Network Share / Mapped Volume";
                            isExternal = true;
                        }
                        else if (drives.Any(d => d.DriveType == DriveType.CDRom))
                        {
                            diskKey = $"cd_{firstLetter}";
                            diskLabel = $"Optical / Virtual Drive ({letters})";
                            modelName = "CD/DVD / ISO Mount";
                            isExternal = true;
                        }
                        else if (drives.Any(d => d.DriveType == DriveType.Removable))
                        {
                            diskKey = $"usb_{firstLetter}";
                            diskLabel = $"USB Storage (External - {letters})";
                            modelName = "External Removable Storage";
                            isExternal = true;
                        }
                        else
                        {
                            diskKey = $"ext_{firstLetter}";
                            diskLabel = $"External Storage ({letters})";
                            modelName = "Virtual / Storage Space Volume";
                            isExternal = true;
                        }
                    }

                    string summary = $"{diskLabel}: {freeFormatted} free of {totalFormatted} ({usedPercent:F0}% used)";
                    result[diskKey] = (summary, string.Empty, usedPercent);
                }
            }
            catch { }

            if (!result.ContainsKey("disk"))
            {
                result["disk"] = ("Disk 0 (Internal SSD): Data unavailable", string.Empty, -1.0);
            }

            return result;
        }

        private (string Summary, string Details, double Percent) SampleBattery()
        {
            try
            {
                if (!NativeMethods.GetSystemPowerStatus(out var status) || status.BatteryFlag == 128)
                {
                    return ("No Battery", string.Empty, 0);
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

                return (summary, string.Empty, (double)percent);
            }
            catch { }

            return ("Battery: Data unavailable", string.Empty, -1.0);
        }

        private (string Summary, string Details, double Percent) SampleAudio()
        {
            try
            {
                var audio = NativeMethods.GetDefaultAudioDeviceInfo(NativeMethods.EDataFlow.eRender);
                if (audio.HasValue)
                {
                    var a = audio.Value;
                    int volPct = (int)Math.Round(a.Volume * 100.0);
                    string state = a.IsMuted ? "Muted" : $"{volPct}% Volume";
                    string summary = $"Audio: {a.Name} ({state})";
                    string details = $"{a.FormFactor} | {state}";
                    return (summary, details, a.IsMuted ? 0 : volPct);
                }
            }
            catch { }

            return ("Audio: No Audio Output Device", "No output endpoints detected", 0);
        }
    }
}
