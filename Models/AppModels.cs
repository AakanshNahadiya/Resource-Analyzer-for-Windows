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

        public static bool ShowExtension { get; set; } = true;
        public static bool ShowPid { get; set; } = false;

        public int Pid { get; set; }
        public string Name { get; set; } = string.Empty;

        public string DisplayName => ShowExtension && !Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? $"{Name}.exe"
            : Name;

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
                    UpdateDisplayText();
                }
            }
        }

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

        public void UpdateMetrics(long memoryBytes, double cpuPercent, int instanceIndex = 1, int instanceTotal = 1, bool isExpanded = false)
        {
            bool memChanged = _memoryBytes != memoryBytes;
            bool cpuChanged = Math.Abs(_cpuPercent - cpuPercent) > 0.001;
            bool instChanged = _instanceIndex != instanceIndex || _instanceTotal != instanceTotal;
            bool expChanged = IsExpanded != isExpanded;

            if (memChanged || cpuChanged || instChanged || expChanged)
            {
                _memoryBytes = memoryBytes;
                _cpuPercent = cpuPercent;
                _instanceIndex = instanceIndex;
                _instanceTotal = instanceTotal;
                IsExpanded = isExpanded;

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

            if (IsGroupHeader)
            {
                string state = IsExpanded ? "expanded" : "collapsed";
                string instWord = _instanceTotal == 1 ? "instance" : "instances";
                DisplayText = $"{name} ({_instanceTotal} {instWord}, {state}) - RAM: {MemoryFormatted}, CPU: {cpuStr}";
            }
            else if (IsGroupChild)
            {
                DisplayText = $"  {name} (PID: {Pid}) - RAM: {MemoryFormatted}, CPU: {cpuStr}";
            }
            else
            {
                string instStr = _instanceTotal > 1 ? $" ({_instanceIndex} of {_instanceTotal})" : string.Empty;
                if (ShowPid)
                {
                    DisplayText = $"{name}{instStr} (PID: {Pid}) - RAM: {MemoryFormatted}, CPU: {cpuStr}";
                }
                else
                {
                    DisplayText = $"{name}{instStr} - RAM: {MemoryFormatted}, CPU: {cpuStr}";
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

    public class AppDataUsageItem
    {
        public string AppName { get; set; } = string.Empty;
        public string RawIdentifier { get; set; } = string.Empty;
        public long BytesReceived { get; set; }
        public long BytesSent { get; set; }
        public long TotalBytes => BytesReceived + BytesSent;

        public string ReceivedFormatted => FormatHelper.FormatBytes(BytesReceived);
        public string SentFormatted => FormatHelper.FormatBytes(BytesSent);
        public string TotalFormatted => FormatHelper.FormatBytes(TotalBytes);

        public string DisplayText => $"{AppName} - Total: {TotalFormatted} (Down: {ReceivedFormatted}, Up: {SentFormatted})";

        public override string ToString() => DisplayText;

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



    #region Service Item

    public class ServiceItem : INotifyPropertyChanged
    {
        private string _status = "Unknown";
        private string _startupType = "Unknown";
        private string _displayText = string.Empty;

        public string ServiceName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;

        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    UpdateDisplayText();
                    OnPropertyChanged();
                }
            }
        }

        public string StartupType
        {
            get => _startupType;
            set
            {
                if (_startupType != value)
                {
                    _startupType = value;
                    UpdateDisplayText();
                    OnPropertyChanged();
                }
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
            string disp = string.IsNullOrWhiteSpace(DisplayName) ? ServiceName : DisplayName;
            DisplayText = $"{disp} ({ServiceName}) - Status: {Status}, Startup: {StartupType}";
        }

        public override string ToString() => DisplayText;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    #endregion
}

