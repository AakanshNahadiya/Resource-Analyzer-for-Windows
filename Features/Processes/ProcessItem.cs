using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ResourceAnalyzer.Helpers;

namespace ResourceAnalyzer.Models
{
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
}
