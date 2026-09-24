using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using AccessibleTaskManager.Helpers;
using AccessibleTaskManager.Models;
using Microsoft.Win32;

namespace AccessibleTaskManager.Services
{
    public interface IStartupService
    {
        List<StartupAppItem> GetStartupApps();
        (bool Success, string Message) ToggleStartupApp(StartupAppItem item);
    }

    public class StartupService : IStartupService
    {
        private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupApprovedRunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string StartupApprovedFolderSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

        private static readonly byte[] EnabledBytes = new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        public List<StartupAppItem> GetStartupApps()
        {
            var list = new List<StartupAppItem>();

            // 1. Current User Registry (HKCU\Run)
            LoadRegistryRunItems(Registry.CurrentUser, "User (HKCU)", isMachineWide: false, list);

            // 2. Local Machine Registry (HKLM\Run)
            LoadRegistryRunItems(Registry.LocalMachine, "All Users (HKLM)", isMachineWide: true, list);

            // 3. User Startup Folder
            LoadFolderStartupItems(Environment.SpecialFolder.Startup, "User Startup Folder", isMachineWide: false, list);

            // 4. Common (All Users) Startup Folder
            LoadFolderStartupItems(Environment.SpecialFolder.CommonStartup, "All Users Startup Folder", isMachineWide: true, list);

            return list;
        }

        private void LoadRegistryRunItems(RegistryKey rootKey, string locationName, bool isMachineWide, List<StartupAppItem> list)
        {
            try
            {
                using var runKey = rootKey.OpenSubKey(RunSubKey, false);
                if (runKey == null) return;

                using var approvedKey = rootKey.OpenSubKey(StartupApprovedRunSubKey, false);

                foreach (string valName in runKey.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(valName)) continue;
                    object? valData = runKey.GetValue(valName);
                    string cmd = valData?.ToString() ?? string.Empty;

                    bool isEnabled = true;
                    if (approvedKey != null)
                    {
                        var approvedData = approvedKey.GetValue(valName) as byte[];
                        if (approvedData != null && approvedData.Length > 0)
                        {
                            // In Windows StartupApproved registry binary data:
                            // Bit 0 of byte 0 is the disabled flag. Even values (0x00, 0x02) = Enabled; Odd values (0x01, 0x03, 0x05) = Disabled.
                            isEnabled = (approvedData[0] & 1) == 0;
                        }
                    }

                    var item = new StartupAppItem
                    {
                        Name = valName,
                        Command = cmd,
                        Location = locationName,
                        RegistryPath = rootKey.Name + "\\" + RunSubKey,
                        IsMachineWide = isMachineWide,
                        IsEnabled = isEnabled
                    };
                    item.UpdateDisplayText();
                    list.Add(item);
                }
            }
            catch { }
        }

        private void LoadFolderStartupItems(Environment.SpecialFolder folder, string locationName, bool isMachineWide, List<StartupAppItem> list)
        {
            try
            {
                string folderPath = Environment.GetFolderPath(folder);
                if (!Directory.Exists(folderPath)) return;

                RegistryKey rootKey = isMachineWide ? Registry.LocalMachine : Registry.CurrentUser;
                using var approvedKey = rootKey.OpenSubKey(StartupApprovedFolderSubKey, false);

                foreach (string file in Directory.GetFiles(folderPath))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                    string displayName = Path.GetFileNameWithoutExtension(fileName);
                    bool isEnabled = true;

                    if (approvedKey != null)
                    {
                        var approvedData = approvedKey.GetValue(fileName) as byte[];
                        if (approvedData != null && approvedData.Length > 0)
                        {
                            isEnabled = (approvedData[0] & 1) == 0;
                        }
                    }

                    var item = new StartupAppItem
                    {
                        Name = displayName,
                        Command = file,
                        Location = locationName,
                        RegistryPath = rootKey.Name + "\\" + StartupApprovedFolderSubKey,
                        IsMachineWide = isMachineWide,
                        IsEnabled = isEnabled
                    };
                    item.UpdateDisplayText();
                    list.Add(item);
                }
            }
            catch { }
        }

        public (bool Success, string Message) ToggleStartupApp(StartupAppItem item)
        {
            bool newStatus = !item.IsEnabled;
            bool isFolderItem = item.Location.Contains("Folder", StringComparison.OrdinalIgnoreCase);
            string subKeyPath = isFolderItem ? StartupApprovedFolderSubKey : StartupApprovedRunSubKey;
            string valueName = isFolderItem ? Path.GetFileName(item.Command) : item.Name;

            RegistryKey rootKey = item.IsMachineWide ? Registry.LocalMachine : Registry.CurrentUser;

            try
            {
                using var key = rootKey.CreateSubKey(subKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree);
                if (key == null)
                {
                    return (false, "Could not open Windows Startup configuration key.");
                }

                if (newStatus)
                {
                    // Enable
                    key.SetValue(valueName, EnabledBytes, RegistryValueKind.Binary);
                }
                else
                {
                    // Disable (0x03 followed by timestamp)
                    byte[] disabledBytes = new byte[12];
                    disabledBytes[0] = 0x03;
                    long fileTime = DateTime.UtcNow.ToFileTimeUtc();
                    byte[] timeBytes = BitConverter.GetBytes(fileTime);
                    Array.Copy(timeBytes, 0, disabledBytes, 4, Math.Min(timeBytes.Length, 8));
                    key.SetValue(valueName, disabledBytes, RegistryValueKind.Binary);
                }

                item.IsEnabled = newStatus;
                return (true, $"{item.Name} is now {(newStatus ? "Enabled" : "Disabled")} for startup.");
            }
            catch (UnauthorizedAccessException)
            {
                return (false, $"Administrator privileges required to modify machine-wide startup app '{item.Name}'. Please switch to Administrator Mode.");
            }
            catch (Exception ex)
            {
                return (false, $"Failed to update startup status: {ex.Message}");
            }
        }
    }
}
