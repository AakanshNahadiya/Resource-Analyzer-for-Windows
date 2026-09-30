using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using AccessibleTaskManager.Models;

namespace AccessibleTaskManager.Services
{
    public interface ISettingsService
    {
        AppSettings CurrentSettings { get; }
        void Load();
        void Save();
        void ResetToDefaults();
        void ApplyAutoStart(bool enable);
    }

    public class SettingsService : ISettingsService
    {
        private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string AppName = "Resource Analyzer for Windows";

        private readonly string _settingsFilePath;
        public AppSettings CurrentSettings { get; private set; } = new();

        public void ResetToDefaults()
        {
            CurrentSettings = new AppSettings();
            Save();
        }

        public SettingsService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "ResourceAnalyzer");

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            _settingsFilePath = Path.Combine(folder, "settings.json");
            Load();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                    if (loaded != null)
                    {
                        CurrentSettings = loaded;
                        // Auto-heal if previously saved with all false due to init race
                        if (!CurrentSettings.ShowCpu && !CurrentSettings.ShowRam && !CurrentSettings.ShowGpu && !CurrentSettings.ShowNetwork && !CurrentSettings.ShowDisk)
                        {
                            CurrentSettings.ShowCpu = true;
                            CurrentSettings.ShowRam = true;
                            CurrentSettings.ShowGpu = true;
                            CurrentSettings.ShowNetwork = true;
                            CurrentSettings.ShowDisk = true;
                            CurrentSettings.ShowBattery = true;
                            CurrentSettings.MinimizeToTray = true;
                            CurrentSettings.ConfirmBeforeEndTask = true;
                        }
                    }
                }
            }
            catch { }

            // Sync with Windows Settings > Apps > Startup if toggled externally
            SyncWithWindowsStartupStatus();

            // Ensure startup state is registered with Windows
            ApplyAutoStart(CurrentSettings.StartWithWindows);
        }

        private void SyncWithWindowsStartupStatus()
        {
            try
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
                bool inRunKey = runKey?.GetValue(AppName) != null;

                using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, false);
                object? val = approvedKey?.GetValue(AppName);

                if (inRunKey && val is byte[] bytes && bytes.Length > 0)
                {
                    // In Windows 10/11: 0x02 = Enabled, 0x01/0x03 = Disabled in Windows Settings
                    if (bytes[0] == 0x03 || bytes[0] == 0x01)
                    {
                        CurrentSettings.StartWithWindows = false;
                    }
                    else if (bytes[0] == 0x02)
                    {
                        CurrentSettings.StartWithWindows = true;
                    }
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(CurrentSettings, options);
                File.WriteAllText(_settingsFilePath, json);
            }
            catch { }

            ApplyAutoStart(CurrentSettings.StartWithWindows);
        }

        public void ApplyAutoStart(bool enable)
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    try { exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName; } catch { }
                }

                using var runKey = Registry.CurrentUser.CreateSubKey(RunRegistryKey, true);
                using var approvedKey = Registry.CurrentUser.CreateSubKey(StartupApprovedKey, true);

                if (enable)
                {
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        runKey?.SetValue(AppName, $"\"{exePath}\" --minimized");
                        byte[] enabledBytes = new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
                        approvedKey?.SetValue(AppName, enabledBytes, RegistryValueKind.Binary);

                    }
                }
                else
                {
                    if (runKey != null && runKey.GetValue(AppName) != null)
                    {
                        runKey.DeleteValue(AppName, false);
                    }
                    if (approvedKey != null && approvedKey.GetValue(AppName) != null)
                    {
                        approvedKey.DeleteValue(AppName, false);
                    }
                }
            }
            catch { }
        }
    }
}
