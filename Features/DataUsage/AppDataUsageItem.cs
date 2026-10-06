using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using ResourceAnalyzer.Helpers;

namespace ResourceAnalyzer.Models
{
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
                    OnPropertyChanged(nameof(ReceivedFormatted));
                    OnPropertyChanged(nameof(TotalBytes));
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
                    OnPropertyChanged(nameof(SentFormatted));
                    OnPropertyChanged(nameof(TotalBytes));
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
            if (string.IsNullOrWhiteSpace(rawId)) return "System";

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
}
