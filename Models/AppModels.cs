using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using AccessibleTaskManager.Helpers;

namespace AccessibleTaskManager.Models
{
    #region Application Settings

    public class AppSettings
    {
        public bool ConfirmBeforeEndTask { get; set; } = true;
        public bool ShowCpu { get; set; } = true;
        public bool ShowRam { get; set; } = true;
        public bool ShowGpu { get; set; } = true;
        public bool ShowNetwork { get; set; } = true;
        public bool ShowDisk { get; set; } = true;
        public bool ShowBattery { get; set; } = true;

        public bool HideSystemProcesses { get; set; } = true;
        public bool ShowProcessExtension { get; set; } = true;
        public bool ShowProcessPid { get; set; } = false;
        public bool GroupProcesses { get; set; } = true;

        /// <summary>
        /// Refresh interval in seconds: 1, 2, 3, 5, or 0 (paused).
        /// </summary>
        public int RefreshIntervalSeconds { get; set; } = 2;

        public bool StartWithWindows { get; set; } = true;
        public bool MinimizeToTray { get; set; } = true;
        public bool AnnounceOnStartup { get; set; } = true;
        public bool RememberSortFilter { get; set; } = false;

        public string SortBy { get; set; } = "Memory"; // "Memory", "CPU", "Name"

        // High resource alerts
        public bool EnableHighRamAlert { get; set; } = false;
        public double HighRamLimitValue { get; set; } = 0;
        public string HighRamLimitUnit { get; set; } = "MB"; // "MB" or "GB"

        public bool EnableHighCpuAlert { get; set; } = false;
        public double HighCpuLimitPercent { get; set; } = 0;

        // Data usage filters
        public string DataUsageNetworkFilter { get; set; } = "Current"; // "Current" or "All"
        public string DataUsageTimeFilter { get; set; } = "Full"; // "Full", "Last Month", "Last Week", "Last 24 Hours", "Today"

        // Battery settings
        public string BatteryAppDisplayMode { get; set; } = "Combined"; // "Combined", "Percentage", "DrainRate"
        public bool HideBatteryDisclaimer { get; set; } = false;

        // Theme setting
        public string Theme { get; set; } = "System Default"; // "System Default", "Dark", "Light", "High Contrast Black"
    }

    #endregion

    #region Resource Metric Item

    public class ResourceItem : INotifyPropertyChanged
    {
        private string _summary = string.Empty;
        private string _details = string.Empty;
        private double _percent = -1;

        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        public double Percent
        {
            get => _percent;
            set
            {
                if (System.Math.Abs(_percent - value) > 0.01)
                {
                    _percent = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasPercent));
                }
            }
        }

        public bool HasPercent => _percent >= 0;

        public string Summary
        {
            get => _summary;
            set
            {
                if (_summary != value)
                {
                    _summary = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public string Details
        {
            get => _details;
            set
            {
                if (_details != value)
                {
                    _details = value;
                    OnPropertyChanged();
                }
            }
        }

        public string DisplayText => _summary;

        public override string ToString() => DisplayText;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #endregion

    #region Process Item

    public class ProcessItem : INotifyPropertyChanged
    {
        private long _memoryBytes;
        private double _cpuPercent;
        private string _displayText = string.Empty;
        private bool _isFrozen;
        private bool _isActiveApp;
        private string _description = string.Empty;

        public static bool ShowExtension { get; set; } = true;
        public static bool ShowPid { get; set; } = false;

        public int Pid { get; set; }
        public string Name { get; set; } = string.Empty;

        public bool IsFrozen
        {
            get => _isFrozen;
            set
            {
                if (_isFrozen != value)
                {
                    _isFrozen = value;
                    OnPropertyChanged();
                    UpdateDisplayText();
                }
            }
        }

        public bool IsActiveApp
        {
            get => _isActiveApp;
            set
            {
                if (_isActiveApp != value)
                {
                    _isActiveApp = value;
                    OnPropertyChanged();
                    UpdateDisplayText();
                }
            }
        }

        public string Description
        {
            get => _description;
            set
            {
                if (_description != value)
                {
                    _description = value;
                    OnPropertyChanged();
                    UpdateDisplayText();
                }
            }
        }

        public string DisplayName
        {
            get
            {
                string exeName = ShowExtension && !Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? $"{Name}.exe"
                    : Name;

                if (!string.IsNullOrWhiteSpace(_description) && !_description.Equals(Name, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{_description} ({exeName})";
                }
                return exeName;
            }
        }

        public long MemoryBytes
        {
            get => _memoryBytes;
            set
            {
                if (_memoryBytes != value)
                {
                    _memoryBytes = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(MemoryFormatted));
                    UpdateDisplayText();
                }
            }
        }

        public string MemoryFormatted => FormatHelper.FormatBytes(_memoryBytes);

        public double CpuPercent
        {
            get => _cpuPercent;
            set
            {
                if (Math.Abs(_cpuPercent - value) > 0.001)
                {
                    _cpuPercent = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CpuFormatted));
                    OnPropertyChanged(nameof(HasCpuPercent));
                    UpdateDisplayText();
                }
            }
        }

        public bool HasCpuPercent => _cpuPercent >= 0.5;

        public bool IsGroupHeader { get; set; }
        public bool IsGroupChild { get; set; }
        public bool IsExpanded { get; set; }
        public string GroupName { get; set; } = string.Empty;
        public List<int> GroupChildPids { get; set; } = new();

        public string ItemKey
        {
            get
            {
                if (IsGroupHeader) return $"group_{GroupName.ToLowerInvariant()}";
                return $"proc_{Pid}";
            }
        }

        private int _instanceIndex = 1;
        private int _instanceTotal = 1;

        public int InstanceIndex
        {
            get => _instanceIndex;
            set
            {
                if (_instanceIndex != value)
                {
                    _instanceIndex = value;
                    OnPropertyChanged();
                    UpdateDisplayText();
                }
            }
        }

        public int InstanceTotal
        {
            get => _instanceTotal;
            set
            {
                if (_instanceTotal != value)
                {
                    _instanceTotal = value;
                    OnPropertyChanged();
                    UpdateDisplayText();
                }
            }
        }

        public void UpdateMetrics(
            long memoryBytes,
            double cpuPercent,
            int instanceIndex = 1,
            int instanceTotal = 1,
            bool isExpanded = false,
            bool isFrozen = false,
            bool isActiveApp = false,
            string description = "")
        {
            bool memChanged = _memoryBytes != memoryBytes;
            bool cpuChanged = Math.Abs(_cpuPercent - cpuPercent) > 0.001;
            bool instChanged = _instanceIndex != instanceIndex || _instanceTotal != instanceTotal;
            bool expChanged = IsExpanded != isExpanded;
            bool frozenChanged = _isFrozen != isFrozen;
            bool activeChanged = _isActiveApp != isActiveApp;
            bool descChanged = !string.IsNullOrEmpty(description) && _description != description;

            if (memChanged || cpuChanged || instChanged || expChanged || frozenChanged || activeChanged || descChanged)
            {
                _memoryBytes = memoryBytes;
                _cpuPercent = cpuPercent;
                _instanceIndex = instanceIndex;
                _instanceTotal = instanceTotal;
                IsExpanded = isExpanded;
                _isFrozen = isFrozen;
                _isActiveApp = isActiveApp;
                if (!string.IsNullOrEmpty(description)) _description = description;

                if (memChanged)
                {
                    OnPropertyChanged(nameof(MemoryBytes));
                    OnPropertyChanged(nameof(MemoryFormatted));
                }
                if (cpuChanged)
                {
                    OnPropertyChanged(nameof(CpuPercent));
                    OnPropertyChanged(nameof(CpuFormatted));
                }
                if (instChanged)
                {
                    OnPropertyChanged(nameof(InstanceIndex));
                    OnPropertyChanged(nameof(InstanceTotal));
                }
                if (expChanged)
                {
                    OnPropertyChanged(nameof(IsExpanded));
                }
                if (frozenChanged)
                {
                    OnPropertyChanged(nameof(IsFrozen));
                }
                if (activeChanged)
                {
                    OnPropertyChanged(nameof(IsActiveApp));
                }
                UpdateDisplayText();
            }
        }

        public string CpuFormatted
        {
            get
            {
                if (_cpuPercent <= 0.0) return "0.0%";
                if (_cpuPercent < 0.1) return "<0.1%";
                return $"{_cpuPercent:F1}%";
            }
        }

        public string DisplayText
        {
            get => _displayText;
            private set
            {
                if (_displayText != value)
                {
                    _displayText = value;
                    OnPropertyChanged();
                }
            }
        }

        public void UpdateDisplayText()
        {
            string name = DisplayName;
            string cpuStr = CpuFormatted;
            string prefix = string.Empty;
            if (_isFrozen)
            {
                prefix = "[FROZEN - Not Responding] ";
            }
            else if (_isActiveApp)
            {
                prefix = "[Active App] ";
            }

            if (IsGroupHeader)
            {
                string state = IsExpanded ? "expanded" : "collapsed";
                string instWord = _instanceTotal == 1 ? "instance" : "instances";
                DisplayText = $"{prefix}{name} ({_instanceTotal} {instWord}, {state}) - RAM: {MemoryFormatted}, CPU: {cpuStr}";
            }
            else if (IsGroupChild)
            {
                DisplayText = $"  {prefix}{name} (PID: {Pid}) - RAM: {MemoryFormatted}, CPU: {cpuStr}";
            }
            else
            {
                string instStr = _instanceTotal > 1 ? $" ({_instanceIndex} of {_instanceTotal})" : string.Empty;
                if (ShowPid)
                {
                    DisplayText = $"{prefix}{name}{instStr} (PID: {Pid}) - RAM: {MemoryFormatted}, CPU: {cpuStr}";
                }
                else
                {
                    DisplayText = $"{prefix}{name}{instStr} - RAM: {MemoryFormatted}, CPU: {cpuStr}";
                }
            }
        }

        public override string ToString() => DisplayText;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #endregion

    #region App Data Usage Item

    public class AppDataUsageItem : INotifyPropertyChanged
    {
        private long _bytesReceived;
        private long _bytesSent;
        private double _usagePercent;
        private string _appName = string.Empty;
        private string _rawIdentifier = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string AppName
        {
            get => _appName;
            set
            {
                if (_appName != value)
                {
                    _appName = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public string RawIdentifier
        {
            get => _rawIdentifier;
            set
            {
                if (_rawIdentifier != value)
                {
                    _rawIdentifier = value;
                    OnPropertyChanged();
                }
            }
        }

        public long BytesReceived
        {
            get => _bytesReceived;
            set
            {
                if (_bytesReceived != value)
                {
                    _bytesReceived = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TotalBytes));
                    OnPropertyChanged(nameof(ReceivedFormatted));
                    OnPropertyChanged(nameof(TotalFormatted));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public long BytesSent
        {
            get => _bytesSent;
            set
            {
                if (_bytesSent != value)
                {
                    _bytesSent = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TotalBytes));
                    OnPropertyChanged(nameof(SentFormatted));
                    OnPropertyChanged(nameof(TotalFormatted));
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public double UsagePercent
        {
            get => _usagePercent;
            set
            {
                if (Math.Abs(_usagePercent - value) > 0.01)
                {
                    _usagePercent = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasPercent));
                }
            }
        }

        public bool HasPercent => _usagePercent >= 0.5;

        public long TotalBytes => _bytesReceived + _bytesSent;

        public string ReceivedFormatted => FormatHelper.FormatBytes(_bytesReceived);
        public string SentFormatted => FormatHelper.FormatBytes(_bytesSent);
        public string TotalFormatted => FormatHelper.FormatBytes(TotalBytes);

        public string DisplayText => $"{AppName} - Total: {TotalFormatted} (Down: {ReceivedFormatted}, Up: {SentFormatted})";

        public override string ToString() => DisplayText;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public static string CleanAppName(string rawId)
        {
            if (string.IsNullOrWhiteSpace(rawId)) return "System & Deleted Applications";

            try
            {
                string path = rawId.Trim();

                // Handle "System\..." paths (e.g. System\IPv6 Control Message)
                if (path.StartsWith(@"System\", StringComparison.OrdinalIgnoreCase))
                {
                    return path.Substring(7);
                }

                // Remove device prefix if present (e.g. \device\harddiskvolume3\...)
                if (path.StartsWith(@"\device\", StringComparison.OrdinalIgnoreCase))
                {
                    int thirdSlash = path.IndexOf('\\', 8);
                    if (thirdSlash >= 0)
                    {
                        path = path.Substring(thirdSlash + 1);
                    }
                }

                string fileName = Path.GetFileName(path);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    // Clean known extension (.exe)
                    if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        return fileName.Substring(0, fileName.Length - 4);
                    }

                    // Check for UWP / Windows Store Package Family Names (e.g. Name_PublisherHash)
                    int underscoreIdx = fileName.LastIndexOf('_');
                    if (underscoreIdx > 0 && (fileName.Length - underscoreIdx - 1) >= 8)
                    {
                        string baseName = fileName.Substring(0, underscoreIdx);
                        if (baseName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
                        {
                            baseName = baseName.Substring("Microsoft.".Length);
                        }
                        return baseName;
                    }

                    return fileName;
                }
            }
            catch { }

            return rawId;
        }
    }

    #endregion


}
