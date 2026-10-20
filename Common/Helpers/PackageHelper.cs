using System;
using System.Runtime.InteropServices;

namespace ResourceAnalyzer.Helpers
{
    /// <summary>
    /// Detects whether the current process is running inside an MSIX package container
    /// (e.g. distributed via Microsoft Store) using official Win32 package query APIs.
    /// </summary>
    public static class PackageHelper
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);

        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

        private static bool? _isPackaged;

        /// <summary>
        /// Returns true if the application has a package identity (running as MSIX / Store app).
        /// Returns false if running as a standalone unpackaged .exe (e.g. from GitHub / WinGet).
        /// </summary>
        public static bool IsStorePackage()
        {
            if (_isPackaged.HasValue) return _isPackaged.Value;

            if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            {
                _isPackaged = false;
                return false;
            }

            try
            {
                uint length = 0;
                int result = GetCurrentPackageFullName(ref length, null);
                _isPackaged = (result == ERROR_INSUFFICIENT_BUFFER);
            }
            catch
            {
                _isPackaged = false;
            }

            return _isPackaged.Value;
        }
    }
}
