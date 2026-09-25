using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Windows;

namespace AccessibleTaskManager.Helpers
{
    /// <summary>
    /// Provides helper methods for detecting and requesting Windows Administrator elevation.
    /// </summary>
    public static class ElevationHelper
    {
        /// <summary>
        /// Checks whether the current process is running with elevated Administrator privileges.
        /// </summary>
        public static bool IsRunningAsAdmin()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Restarts the application requesting Administrator elevation (UAC prompt).
        /// If the user accepts, launches the new elevated process and shuts down this one.
        /// If the user cancels the UAC prompt, returns false and the app continues running.
        /// </summary>
        /// <param name="extraArgs">Optional additional command-line arguments.</param>
        /// <returns>True if the elevated process was successfully started; false if canceled or failed.</returns>
        public static bool RestartAsAdmin(string? extraArgs = null)
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    exePath = Process.GetCurrentProcess().MainModule?.FileName;
                }

                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    return false;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas" // Triggers Windows UAC elevation
                };

                if (!string.IsNullOrWhiteSpace(extraArgs))
                {
                    startInfo.Arguments = extraArgs;
                }

                Process.Start(startInfo);
                Application.Current.Shutdown();
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Win32Exception is thrown when the user clicks "No" on the UAC prompt
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
