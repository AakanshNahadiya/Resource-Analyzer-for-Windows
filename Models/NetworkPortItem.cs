using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AccessibleTaskManager.Models
{
    /// <summary>
    /// Represents an active network port or connection (TCP or UDP, IPv4 or IPv6)
    /// mapped to an owning process for real-time monitoring and accessibility.
    /// </summary>
    public class NetworkPortItem : INotifyPropertyChanged
    {
        private string _protocol = "TCP";
        private string _localAddress = "";
        private int _localPort;
        private string _remoteAddress = "";
        private int _remotePort;
        private string _state = "Listening";
        private int _processId;
        private string _processName = "";
        private string _friendlyName = "";
        private string _processPath = "";
        private string _displayText = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Protocol
        {
            get => _protocol;
            set { if (_protocol != value) { _protocol = value; OnPropertyChanged(); UpdateDisplayText(); } }
        }

        public string LocalAddress
        {
            get => _localAddress;
            set { if (_localAddress != value) { _localAddress = value; OnPropertyChanged(); UpdateDisplayText(); } }
        }

        public int LocalPort
        {
            get => _localPort;
            set { if (_localPort != value) { _localPort = value; OnPropertyChanged(); UpdateDisplayText(); } }
        }

        public string RemoteAddress
        {
            get => _remoteAddress;
            set { if (_remoteAddress != value) { _remoteAddress = value; OnPropertyChanged(); UpdateDisplayText(); } }
        }

        public int RemotePort
        {
            get => _remotePort;
            set { if (_remotePort != value) { _remotePort = value; OnPropertyChanged(); UpdateDisplayText(); } }
        }

        public string State
        {
            get => _state;
            set { if (_state != value) { _state = value; OnPropertyChanged(); UpdateDisplayText(); } }
        }

        public int ProcessId
        {
            get => _processId;
            set { if (_processId != value) { _processId = value; OnPropertyChanged(); UpdateDisplayText(); } }
        }

        public string ProcessName
        {
            get => _processName;
            set { if (_processName != value) { _processName = value; OnPropertyChanged(); UpdateDisplayText(); } }
        }

        public string FriendlyName
        {
            get => _friendlyName;
            set { if (_friendlyName != value) { _friendlyName = value; OnPropertyChanged(); UpdateDisplayText(); } }
        }

        public string ProcessPath
        {
            get => _processPath;
            set { if (_processPath != value) { _processPath = value; OnPropertyChanged(); } }
        }

        public string DisplayText
        {
            get => _displayText;
            set { if (_displayText != value) { _displayText = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Unique composite key for stationary list reconciliation and diffing.
        /// </summary>
        public string Key => Protocol == "UDP"
            ? $"UDP:{LocalAddress}:{LocalPort}:{ProcessId}"
            : $"TCP:{LocalAddress}:{LocalPort}->{RemoteAddress}:{RemotePort}:{ProcessId}";

        public bool IsListening => State.Equals("Listening", StringComparison.OrdinalIgnoreCase);

        public bool IsEstablished => State.Equals("Established", StringComparison.OrdinalIgnoreCase);

        public void UpdateDisplayText()
        {
            DisplayText = FormatDisplayText();
        }

        public string FormatDisplayText()
        {
            string appLabel = !string.IsNullOrWhiteSpace(FriendlyName) && !FriendlyName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase)
                ? $"{FriendlyName} ({ProcessName}, PID {ProcessId})"
                : $"{ProcessName} (PID {ProcessId})";

            if (IsListening)
            {
                string endpoint = LocalAddress.Contains(':') ? $"[{LocalAddress}]:{LocalPort}" : $"{LocalAddress}:{LocalPort}";
                return $"Port {LocalPort} ({Protocol}) - Listening - {appLabel} [{endpoint}]";
            }
            else if (IsEstablished)
            {
                string remoteEndpoint = RemoteAddress.Contains(':') ? $"[{RemoteAddress}]:{RemotePort}" : $"{RemoteAddress}:{RemotePort}";
                return $"Port {LocalPort} ({Protocol}) -> {remoteEndpoint} - Established - {appLabel}";
            }
            else
            {
                string remoteEndpoint = RemoteAddress.Contains(':') ? $"[{RemoteAddress}]:{RemotePort}" : $"{RemoteAddress}:{RemotePort}";
                return $"Port {LocalPort} ({Protocol}) -> {remoteEndpoint} - {State} - {appLabel}";
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public override string ToString() => DisplayText;
    }
}
