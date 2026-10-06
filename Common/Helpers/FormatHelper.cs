using System;

namespace ResourceAnalyzer.Helpers
{
    public static class FormatHelper
    {
        private const long OneKb = 1024;
        private const long OneMb = 1024 * 1024;
        private const long OneGb = 1024 * 1024 * 1024;
        private const long OneTb = 1024L * 1024 * 1024 * 1024;

        /// <summary>
        /// Formats bytes dynamically. Switches between MB and GB automatically based on threshold (1024 MB).
        /// Values below 1024 MB show in MB (e.g. "820 MB").
        /// Values 1024 MB and above show in GB (e.g. "5.42 GB").
        /// Large storage values above 1024 GB show in TB.
        /// </summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) bytes = 0;

            if (bytes < OneMb)
            {
                double kb = (double)bytes / OneKb;
                return $"{kb:F0} KB";
            }
            if (bytes < OneGb)
            {
                double mb = (double)bytes / OneMb;
                return $"{mb:F1} MB";
            }
            if (bytes < OneTb)
            {
                double gb = (double)bytes / OneGb;
                return $"{gb:F2} GB";
            }

            double tb = (double)bytes / OneTb;
            return $"{tb:F2} TB";
        }

        /// <summary>
        /// Formats network speed in human-readable units (B/s, KB/s, MB/s, GB/s).
        /// </summary>
        public static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond < 0) bytesPerSecond = 0;

            if (bytesPerSecond < OneKb)
            {
                return $"{bytesPerSecond:F0} B/s";
            }
            if (bytesPerSecond < OneMb)
            {
                double kbps = bytesPerSecond / OneKb;
                return $"{kbps:F0} KB/s";
            }
            if (bytesPerSecond < OneGb)
            {
                double mbps = bytesPerSecond / OneMb;
                return $"{mbps:F2} MB/s";
            }

            double gbps = bytesPerSecond / OneGb;
            return $"{gbps:F2} GB/s";
        }

        /// <summary>
        /// Formats disk storage capacity accurately with 2 decimal places (e.g. "76.45 GB", "232.29 GB", "475.89 GB").
        /// TB drives show clean TB (e.g. "2 TB" or "1.50 TB").
        /// </summary>
        public static string FormatDiskSize(long bytes)
        {
            if (bytes <= 0) return "0.00 GB";

            if (bytes >= OneTb)
            {
                double tb = (double)bytes / OneTb;
                return (Math.Abs(tb - Math.Round(tb)) < 0.01)
                    ? $"{Math.Round(tb):F0} TB"
                    : $"{tb:F2} TB";
            }

            double gb = (double)bytes / OneGb;
            if (gb >= 1)
            {
                return $"{gb:F2} GB";
            }

            double mb = (double)bytes / OneMb;
            return $"{mb:F1} MB";
        }

        /// <summary>
        /// Formats physical manufacturer hardware size in decimal gigabytes (e.g. "512 GB Hardware", "16 GB Hardware", "1 TB Hardware").
        /// </summary>
        public static string FormatHardwareCapacity(long bytes)
        {
            if (bytes <= 0) return string.Empty;
            double decGb = (double)bytes / 1_000_000_000.0;
            if (decGb >= 900)
            {
                double decTb = decGb / 1000.0;
                return $"{Math.Round(decTb):F0} TB Hardware";
            }
            return $"{Math.Round(decGb):F0} GB Hardware";
        }
    }
}
