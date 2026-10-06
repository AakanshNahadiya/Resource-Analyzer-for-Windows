using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ResourceAnalyzer.Helpers;
using ResourceAnalyzer.Models;
using ResourceAnalyzer.Views;

namespace ResourceAnalyzer
{
    public partial class MainWindow : Window
    {
        #region Settings Management

        private void LoadSettingsIntoUI()
        {
            _isUpdatingUI = true;
            try
            {
                var s = _settingsService.CurrentSettings;

                // Process Manager Combo
                if (s.HideSystemProcesses)
                {
                    cmbProcessManager.SelectedIndex = s.ConfirmBeforeEndTask ? 0 : 1;
                }
                else
                {
                    cmbProcessManager.SelectedIndex = s.ConfirmBeforeEndTask ? 2 : 3;
                }

                // Process Naming & Speech Format Combo
                if (cmbProcessFormat != null)
                {
                    if (s.ShowProcessExtension && !s.ShowProcessPid)
                    {
                        cmbProcessFormat.SelectedIndex = 0;
                    }
                    else if (s.ShowProcessExtension && s.ShowProcessPid)
                    {
                        cmbProcessFormat.SelectedIndex = 1;
                    }
                    else if (!s.ShowProcessExtension && !s.ShowProcessPid)
                    {
                        cmbProcessFormat.SelectedIndex = 2;
                    }
                    else
                    {
                        cmbProcessFormat.SelectedIndex = 3;
                    }
                }

                UpdateGroupToggleButtonText();

                // Alerts Combo
                if (s.EnableHighRamAlert && s.EnableHighCpuAlert)
                {
                    cmbAlerts.SelectedIndex = 3;
                }
                else if (s.EnableHighCpuAlert)
                {
                    cmbAlerts.SelectedIndex = 2;
                }
                else if (s.EnableHighRamAlert)
                {
                    cmbAlerts.SelectedIndex = 1;
                }
                else
                {
                    cmbAlerts.SelectedIndex = 0;
                }

                txtHighRamValue.Text = s.HighRamLimitValue > 0 ? s.HighRamLimitValue.ToString("0.##") : string.Empty;
                cmbHighRamUnit.SelectedIndex = s.HighRamLimitUnit.Equals("GB", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                txtHighCpuPercent.Text = s.HighCpuLimitPercent > 0 ? s.HighCpuLimitPercent.ToString("0.##") : string.Empty;

                // Visible Resources Preset Combo & Checkboxes
                chkShowCpu.IsChecked = s.ShowCpu;
                chkShowRam.IsChecked = s.ShowRam;
                chkShowGpu.IsChecked = s.ShowGpu;
                chkShowDisplay.IsChecked = s.ShowDisplay;
                chkShowNetwork.IsChecked = s.ShowNetwork;
                chkShowDisk.IsChecked = s.ShowDisk;
                chkShowBattery.IsChecked = s.ShowBattery;

                if (s.ShowCpu && s.ShowRam && s.ShowGpu && s.ShowDisplay && s.ShowNetwork && s.ShowDisk && s.ShowBattery)
                {
                    cmbVisibleResources.SelectedIndex = 0; // All Resources
                }
                else if (s.ShowCpu && s.ShowRam && !s.ShowGpu && s.ShowDisplay && s.ShowNetwork && s.ShowDisk && !s.ShowBattery)
                {
                    cmbVisibleResources.SelectedIndex = 1; // Essential Resources
                }
                else if (s.ShowCpu && s.ShowRam && !s.ShowGpu && !s.ShowDisplay && !s.ShowNetwork && !s.ShowDisk && !s.ShowBattery)
                {
                    cmbVisibleResources.SelectedIndex = 2; // Minimal Resources
                }
                else
                {
                    cmbVisibleResources.SelectedIndex = 3; // Custom Selection...
                }

                // Auto-refresh combo
                cmbRefreshInterval.SelectedIndex = s.RefreshIntervalSeconds switch
                {
                    1 => 0,
                    2 => 1,
                    3 => 2,
                    5 => 3,
                    0 => 4,
                    _ => 1
                };

                // Appearance theme combo
                cmbTheme.SelectedIndex = s.Theme switch
                {
                    "Dark" => 1,
                    "Light" => 2,
                    "High Contrast Black" => 3,
                    _ => 0
                };

                // Startup & Tray combo
                if (s.MinimizeToTray)
                {
                    cmbStartupTray.SelectedIndex = s.StartWithWindows ? 0 : 1;
                }
                else
                {
                    cmbStartupTray.SelectedIndex = s.StartWithWindows ? 2 : 3;
                }

                // Preferences Memory combo
                cmbRememberPrefs.SelectedIndex = s.RememberSortFilter ? 1 : 0;

                // Data usage filters
                cmbDataNetwork.SelectedIndex = s.DataUsageNetworkFilter.Equals("All", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                cmbDataTimeRange.SelectedIndex = s.DataUsageTimeFilter switch
                {
                    "Last Month" => 1,
                    "Last Week" => 2,
                    "Last 24 Hours" => 3,
                    "Today" => 4,
                    _ => 0
                };

                // Battery app display mode
                if (cmbBatteryAppDisplayMode != null)
                {
                    cmbBatteryAppDisplayMode.SelectedIndex = s.BatteryAppDisplayMode switch
                    {
                        "Percentage" => 1,
                        "DrainRate" => 2,
                        _ => 0
                    };
                }

                if (pnlBatteryDisclaimer != null)
                {
                    pnlBatteryDisclaimer.Visibility = s.HideBatteryDisclaimer ? Visibility.Collapsed : Visibility.Visible;
                }
                if (chkDoNotShowBatteryDisclaimer != null)
                {
                    chkDoNotShowBatteryDisclaimer.IsChecked = s.HideBatteryDisclaimer;
                }

                if (s.RememberSortFilter && !string.IsNullOrWhiteSpace(s.SortBy))
                {
                    _currentSort = s.SortBy;
                }
            }
            finally
            {
                _isUpdatingUI = false;
                UpdateAlertPanelsVisibility();
            }
        }

        private void UpdateAlertPanelsVisibility()
        {
            int alertIndex = cmbAlerts?.SelectedIndex ?? 0;

            if (panelHighRamConfig != null)
            {
                panelHighRamConfig.Visibility = (alertIndex == 1 || alertIndex == 3)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (panelHighCpuConfig != null)
            {
                panelHighCpuConfig.Visibility = (alertIndex == 2 || alertIndex == 3)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (panelCustomResources != null)
            {
                panelCustomResources.Visibility = (cmbVisibleResources?.SelectedIndex == 3)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private void ApplySettingsToRuntime()
        {
            ProcessItem.ShowExtension = _settingsService.CurrentSettings.ShowProcessExtension;
            ProcessItem.ShowPid = _settingsService.CurrentSettings.ShowProcessPid;

            int interval = _settingsService.CurrentSettings.RefreshIntervalSeconds;
            if (interval > 0)
            {
                _refreshTimer.Interval = TimeSpan.FromSeconds(interval);
                _refreshTimer.Start();
            }
            else
            {
                _refreshTimer.Stop(); // Paused
            }
        }

        private void CmbProcessManager_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            if (cmbProcessManager.SelectedIndex < 0) return;

            var s = _settingsService.CurrentSettings;
            switch (cmbProcessManager.SelectedIndex)
            {
                case 0:
                    s.HideSystemProcesses = true;
                    s.ConfirmBeforeEndTask = true;
                    break;
                case 1:
                    s.HideSystemProcesses = true;
                    s.ConfirmBeforeEndTask = false;
                    break;
                case 2:
                    s.HideSystemProcesses = false;
                    s.ConfirmBeforeEndTask = true;
                    break;
                case 3:
                    s.HideSystemProcesses = false;
                    s.ConfirmBeforeEndTask = false;
                    break;
            }

            SaveSettingsFromUI(silent: true);
            if (tabProcesses.IsSelected)
            {
                _ = RefreshProcessesAsync(isFullReset: true);
            }
        }

        private void CmbProcessFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            if (cmbProcessFormat.SelectedIndex < 0) return;

            var s = _settingsService.CurrentSettings;
            switch (cmbProcessFormat.SelectedIndex)
            {
                case 0:
                    s.ShowProcessExtension = true;
                    s.ShowProcessPid = false;
                    break;
                case 1:
                    s.ShowProcessExtension = true;
                    s.ShowProcessPid = true;
                    break;
                case 2:
                    s.ShowProcessExtension = false;
                    s.ShowProcessPid = false;
                    break;
                case 3:
                    s.ShowProcessExtension = false;
                    s.ShowProcessPid = true;
                    break;
            }

            ProcessItem.ShowExtension = s.ShowProcessExtension;
            ProcessItem.ShowPid = s.ShowProcessPid;

            foreach (var item in _processItems)
            {
                item.UpdateDisplayText();
            }

            SaveSettingsFromUI(silent: true);
        }

        private void CmbAlerts_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateAlertPanelsVisibility();
            if (_isUpdatingUI) return;
            if (cmbAlerts.SelectedIndex < 0) return;

            var s = _settingsService.CurrentSettings;
            s.EnableHighRamAlert = (cmbAlerts.SelectedIndex == 1 || cmbAlerts.SelectedIndex == 3);
            s.EnableHighCpuAlert = (cmbAlerts.SelectedIndex == 2 || cmbAlerts.SelectedIndex == 3);

            SaveSettingsFromUI(silent: true);
        }

        private void CmbVisibleResources_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateAlertPanelsVisibility();
            if (_isUpdatingUI) return;
            if (cmbVisibleResources.SelectedIndex < 0) return;

            var s = _settingsService.CurrentSettings;
            switch (cmbVisibleResources.SelectedIndex)
            {
                case 0: // All Resources
                    s.ShowCpu = true;
                    s.ShowRam = true;
                    s.ShowGpu = true;
                    s.ShowNetwork = true;
                    s.ShowDisk = true;
                    s.ShowBattery = true;
                    break;
                case 1: // Essential Resources
                    s.ShowCpu = true;
                    s.ShowRam = true;
                    s.ShowGpu = false;
                    s.ShowNetwork = true;
                    s.ShowDisk = true;
                    s.ShowBattery = false;
                    break;
                case 2: // Minimal Resources
                    s.ShowCpu = true;
                    s.ShowRam = true;
                    s.ShowGpu = false;
                    s.ShowNetwork = false;
                    s.ShowDisk = false;
                    s.ShowBattery = false;
                    break;
                case 3: // Custom
                    break;
            }

            if (cmbVisibleResources.SelectedIndex != 3)
            {
                _isUpdatingUI = true;
                try
                {
                    chkShowCpu.IsChecked = s.ShowCpu;
                    chkShowRam.IsChecked = s.ShowRam;
                    chkShowGpu.IsChecked = s.ShowGpu;
                    chkShowDisplay.IsChecked = s.ShowDisplay;
                    chkShowNetwork.IsChecked = s.ShowNetwork;
                    chkShowDisk.IsChecked = s.ShowDisk;
                    chkShowBattery.IsChecked = s.ShowBattery;
                }
                finally
                {
                    _isUpdatingUI = false;
                }
            }

            SaveSettingsFromUI(silent: true);
            BuildResourceItemList();
            _ = RefreshResourcesAsync();
        }

        private void CmbStartupTray_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            if (cmbStartupTray.SelectedIndex < 0) return;

            var s = _settingsService.CurrentSettings;
            switch (cmbStartupTray.SelectedIndex)
            {
                case 0:
                    s.MinimizeToTray = true;
                    s.StartWithWindows = true;
                    break;
                case 1:
                    s.MinimizeToTray = true;
                    s.StartWithWindows = false;
                    break;
                case 2:
                    s.MinimizeToTray = false;
                    s.StartWithWindows = true;
                    break;
                case 3:
                    s.MinimizeToTray = false;
                    s.StartWithWindows = false;
                    break;
            }

            SaveSettingsFromUI(silent: true);
        }

        private void CmbRememberPrefs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            if (cmbRememberPrefs.SelectedIndex < 0) return;

            var s = _settingsService.CurrentSettings;
            s.RememberSortFilter = cmbRememberPrefs.SelectedIndex == 1;

            SaveSettingsFromUI(silent: true);
        }

        private void AlertSettingInputChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            SaveSettingsFromUI(silent: true);
        }

        private void CmbHighRamUnit_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            SaveSettingsFromUI(silent: true);
        }

        private void ResourceVisibilityChanged(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUI) return;
            SaveSettingsFromUI(silent: true);
            BuildResourceItemList();
            _ = RefreshResourcesAsync();
        }

        private void CmbRefreshInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            if (cmbRefreshInterval.SelectedIndex < 0) return;

            int seconds = cmbRefreshInterval.SelectedIndex switch
            {
                0 => 1,
                1 => 2,
                2 => 3,
                3 => 5,
                4 => 0,
                _ => 2
            };

            _settingsService.CurrentSettings.RefreshIntervalSeconds = seconds;
            ApplySettingsToRuntime();
            _settingsService.Save();
        }

        private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            string selectedTheme = cmbTheme.SelectedIndex switch
            {
                1 => "Dark",
                2 => "Light",
                3 => "High Contrast Black",
                _ => "System Default"
            };
            _settingsService.CurrentSettings.Theme = selectedTheme;
            _themeService.ApplyTheme(selectedTheme);
            SaveSettingsFromUI(silent: true);
        }

        private void BtnResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            _settingsService.ResetToDefaults();
            _isUpdatingUI = true;
            try
            {
                LoadSettingsIntoUI();
            }
            finally
            {
                _isUpdatingUI = false;
            }
            ApplySettingsToRuntime();
            foreach (var item in _processItems)
            {
                item.UpdateDisplayText();
            }
            BuildResourceItemList();
            _themeService.ApplyTheme(_settingsService.CurrentSettings.Theme);
            _speechService.Speak("All settings have been reset to default values.", interrupt: true);
            txtAnnouncement.Text = "Settings reset to defaults.";
        }

        private void SaveSettingsFromUI(bool silent)
        {
            var s = _settingsService.CurrentSettings;

            // Process manager options
            if (cmbProcessManager != null && cmbProcessManager.SelectedIndex >= 0)
            {
                switch (cmbProcessManager.SelectedIndex)
                {
                    case 0:
                        s.HideSystemProcesses = true;
                        s.ConfirmBeforeEndTask = true;
                        break;
                    case 1:
                        s.HideSystemProcesses = true;
                        s.ConfirmBeforeEndTask = false;
                        break;
                    case 2:
                        s.HideSystemProcesses = false;
                        s.ConfirmBeforeEndTask = true;
                        break;
                    case 3:
                        s.HideSystemProcesses = false;
                        s.ConfirmBeforeEndTask = false;
                        break;
                }
            }

            // Process format options
            if (cmbProcessFormat != null && cmbProcessFormat.SelectedIndex >= 0)
            {
                switch (cmbProcessFormat.SelectedIndex)
                {
                    case 0:
                        s.ShowProcessExtension = true;
                        s.ShowProcessPid = false;
                        break;
                    case 1:
                        s.ShowProcessExtension = true;
                        s.ShowProcessPid = true;
                        break;
                    case 2:
                        s.ShowProcessExtension = false;
                        s.ShowProcessPid = false;
                        break;
                    case 3:
                        s.ShowProcessExtension = false;
                        s.ShowProcessPid = true;
                        break;
                }
            }

            UpdateGroupToggleButtonText();

            // Visible resources
            if (cmbVisibleResources != null && cmbVisibleResources.SelectedIndex >= 0)
            {
                if (cmbVisibleResources.SelectedIndex == 3)
                {
                    s.ShowCpu = chkShowCpu.IsChecked == true;
                    s.ShowRam = chkShowRam.IsChecked == true;
                    s.ShowGpu = chkShowGpu.IsChecked == true;
                    s.ShowDisplay = chkShowDisplay.IsChecked == true;
                    s.ShowNetwork = chkShowNetwork.IsChecked == true;
                    s.ShowDisk = chkShowDisk.IsChecked == true;
                    s.ShowBattery = chkShowBattery.IsChecked == true;
                }
                else
                {
                    switch (cmbVisibleResources.SelectedIndex)
                    {
                        case 0:
                            s.ShowCpu = true; s.ShowRam = true; s.ShowGpu = true; s.ShowDisplay = true; s.ShowNetwork = true; s.ShowDisk = true; s.ShowBattery = true;
                            break;
                        case 1:
                            s.ShowCpu = true; s.ShowRam = true; s.ShowGpu = false; s.ShowDisplay = true; s.ShowNetwork = true; s.ShowDisk = true; s.ShowBattery = false;
                            break;
                        case 2:
                            s.ShowCpu = true; s.ShowRam = true; s.ShowGpu = false; s.ShowDisplay = false; s.ShowNetwork = false; s.ShowDisk = false; s.ShowBattery = false;
                            break;
                    }
                }
            }

            // Resource Alerts
            if (cmbAlerts != null && cmbAlerts.SelectedIndex >= 0)
            {
                s.EnableHighRamAlert = (cmbAlerts.SelectedIndex == 1 || cmbAlerts.SelectedIndex == 3);
                s.EnableHighCpuAlert = (cmbAlerts.SelectedIndex == 2 || cmbAlerts.SelectedIndex == 3);
            }

            if (txtHighRamValue != null)
            {
                if (double.TryParse(txtHighRamValue.Text.Trim(), out double ramLimit) && ramLimit > 0)
                {
                    s.HighRamLimitValue = ramLimit;
                }
                else
                {
                    s.HighRamLimitValue = 0;
                }
            }

            if (cmbHighRamUnit != null)
            {
                s.HighRamLimitUnit = cmbHighRamUnit.SelectedIndex == 1 ? "GB" : "MB";
            }

            if (txtHighCpuPercent != null)
            {
                if (double.TryParse(txtHighCpuPercent.Text.Trim(), out double cpuLimit) && cpuLimit > 0)
                {
                    s.HighCpuLimitPercent = cpuLimit;
                }
                else
                {
                    s.HighCpuLimitPercent = 0;
                }
            }

            // Startup and Tray
            if (cmbStartupTray != null && cmbStartupTray.SelectedIndex >= 0)
            {
                switch (cmbStartupTray.SelectedIndex)
                {
                    case 0:
                        s.MinimizeToTray = true;
                        s.StartWithWindows = true;
                        break;
                    case 1:
                        s.MinimizeToTray = true;
                        s.StartWithWindows = false;
                        break;
                    case 2:
                        s.MinimizeToTray = false;
                        s.StartWithWindows = true;
                        break;
                    case 3:
                        s.MinimizeToTray = false;
                        s.StartWithWindows = false;
                        break;
                }
            }

            // Preferences Memory
            if (cmbRememberPrefs != null && cmbRememberPrefs.SelectedIndex >= 0)
            {
                s.RememberSortFilter = cmbRememberPrefs.SelectedIndex == 1;
            }

            // Auto-refresh interval
            if (cmbRefreshInterval != null && cmbRefreshInterval.SelectedIndex >= 0)
            {
                s.RefreshIntervalSeconds = cmbRefreshInterval.SelectedIndex switch
                {
                    0 => 1,
                    1 => 2,
                    2 => 3,
                    3 => 5,
                    4 => 0,
                    _ => 2
                };
            }

            // Theme
            if (cmbTheme != null && cmbTheme.SelectedIndex >= 0)
            {
                s.Theme = cmbTheme.SelectedIndex switch
                {
                    1 => "Dark",
                    2 => "Light",
                    3 => "High Contrast Black",
                    _ => "System Default"
                };
            }

            // Data usage filters
            if (cmbDataNetwork != null && cmbDataTimeRange != null)
            {
                s.DataUsageNetworkFilter = cmbDataNetwork.SelectedIndex == 1 ? "All" : "Current";
                s.DataUsageTimeFilter = cmbDataTimeRange.SelectedIndex switch
                {
                    1 => "Last Month",
                    2 => "Last Week",
                    3 => "Last 24 Hours",
                    4 => "Today",
                    _ => "Full"
                };
            }

            // Battery app display mode
            if (cmbBatteryAppDisplayMode != null && cmbBatteryAppDisplayMode.SelectedIndex >= 0)
            {
                s.BatteryAppDisplayMode = cmbBatteryAppDisplayMode.SelectedIndex switch
                {
                    1 => "Percentage",
                    2 => "DrainRate",
                    _ => "Combined"
                };
            }

            _settingsService.Save();

            if (!silent)
            {
                _speechService.Speak("Settings saved.", interrupt: true);
                txtAnnouncement.Text = "Settings saved.";
            }
        }

        private void CmbBatteryAppDisplayMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            if (cmbBatteryAppDisplayMode == null || cmbBatteryAppDisplayMode.SelectedIndex < 0) return;

            string mode = cmbBatteryAppDisplayMode.SelectedIndex switch
            {
                1 => "Percentage",
                2 => "DrainRate",
                _ => "Combined"
            };

            _settingsService.CurrentSettings.BatteryAppDisplayMode = mode;
            SaveSettingsFromUI(silent: true);

            foreach (var item in _appBatteryItems)
            {
                item.DisplayMode = mode;
                item.UpdateDisplayText();
            }

            string announcement = mode switch
            {
                "Percentage" => "Battery app display set to Percentage Only.",
                "DrainRate" => "Battery app display set to Live Drain Rate Only.",
                _ => "Battery app display set to Combined Percentage and Live Drain Rate."
            };
            _speechService.Speak(announcement, interrupt: true);
            txtAnnouncement.Text = announcement;

            _ = RefreshAppBatteryUsageAsync(isFullReset: true);
        }

        #endregion

        #region Administrator Elevation

        private void BtnRestartAdmin_Click(object sender, RoutedEventArgs e)
        {
            RestartAsAdministratorWithConfirmation();
        }

        private void RestartAsAdministratorWithConfirmation()
        {
            if (ElevationHelper.IsRunningAsAdmin())
            {
                _speechService.Speak("Resource Analyzer is already running with Administrator privileges.", interrupt: true);
                txtAnnouncement.Text = "Already running as Administrator.";
                return;
            }

            var result = MessageBox.Show(
                "Restart Resource Analyzer for Windows with Administrator privileges?\n\nThis will allow you to end protected processes and access advanced system telemetry.",
                "Restart as Administrator",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                bool success = ElevationHelper.RestartAsAdmin($"--tab {tabMain.SelectedIndex}");
                if (!success)
                {
                    _speechService.Speak("Administrator elevation was canceled.", interrupt: true);
                    txtAnnouncement.Text = "Administrator elevation canceled.";
                }
            }
        }

        #endregion

        #region Windows Startup Apps Settings

        private void OpenWindowsStartupSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true });
                _speechService.Speak("Opening Windows Startup Apps settings.", interrupt: true);
                txtAnnouncement.Text = "Opened Windows Startup Apps settings.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open Windows startup settings: {ex.Message}");
                _speechService.Speak("Could not open Windows settings.", interrupt: true);
                txtAnnouncement.Text = "Could not open Windows settings.";
            }
        }

        private void BtnOpenStartupSettings_Click(object sender, RoutedEventArgs e)
        {
            OpenWindowsStartupSettings();
        }

        private async void CopySystemSnapshotToClipboard()
        {
            try
            {
                _speechService.Speak("Gathering system diagnostic snapshot...", interrupt: true);
                txtAnnouncement.Text = "Gathering system diagnostic snapshot...";

                string snapshot = await _hardwareDetailService.GenerateSystemSnapshotAsync();
                Clipboard.SetText(snapshot);

                _speechService.Speak("System snapshot copied to clipboard.", interrupt: true);
                txtAnnouncement.Text = "System snapshot copied to clipboard.";
            }
            catch (Exception ex)
            {
                _speechService.Speak($"Failed to copy system snapshot: {ex.Message}", interrupt: true);
                txtAnnouncement.Text = $"Failed to copy system snapshot: {ex.Message}";
            }
        }

        private void BtnCopySystemSnapshot_Click(object sender, RoutedEventArgs e)
        {
            CopySystemSnapshotToClipboard();
        }

        #endregion

        #region Windows Services Manager Launcher

        private void OpenWindowsServicesManager()
        {
            try
            {
                string servicesMscPath = Path.Combine(Environment.SystemDirectory, "services.msc");
                Process.Start(new ProcessStartInfo(servicesMscPath) { UseShellExecute = true });
                _speechService.Speak("Opening Windows Services Manager.", interrupt: true);
                txtAnnouncement.Text = "Opened Windows Services Manager.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open Windows services manager: {ex.Message}");
                _speechService.Speak("Could not open Windows Services Manager.", interrupt: true);
                txtAnnouncement.Text = "Could not open Windows Services Manager.";
            }
        }

        private void BtnOpenServicesManager_Click(object sender, RoutedEventArgs e)
        {
            OpenWindowsServicesManager();
        }

        #endregion

        #region Application Updates

        private async void BtnCheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            btnCheckForUpdates.IsEnabled = false;
            txtUpdateStatus.Text = "Checking for updates from GitHub...";
            _speechService.Speak("Checking for updates.", interrupt: true);

            var info = await _updateService.CheckForUpdatesAsync();
            btnCheckForUpdates.IsEnabled = true;

            txtUpdateStatus.Text = info.Message;
            _speechService.Speak(info.Message, interrupt: true);

            if (info.HasUpdate)
            {
                _latestUpdateInfo = info;
                _latestUpdateUrl = !string.IsNullOrEmpty(info.DownloadUrl) ? info.DownloadUrl : info.HtmlUrl;
                btnDownloadUpdate.Visibility = Visibility.Visible;
                btnDownloadUpdate.Content = $"Download and Install (v{info.LatestVersion})";
                btnDownloadUpdate.Focus();

                var dlg = new UpdateAvailableDialog(info, _updateService, _speechService)
                {
                    Owner = this
                };
                dlg.ShowDialog();
            }
            else
            {
                _latestUpdateInfo = null;
                btnDownloadUpdate.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnDownloadUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_latestUpdateInfo != null && _latestUpdateInfo.HasUpdate)
            {
                var dlg = new UpdateAvailableDialog(_latestUpdateInfo, _updateService, _speechService)
                {
                    Owner = this
                };
                dlg.ShowDialog();
            }
            else if (!string.IsNullOrEmpty(_latestUpdateUrl))
            {
                _updateService.OpenUrl(_latestUpdateUrl);
                _speechService.Speak("Opening update download link in your browser.", interrupt: true);
            }
        }

        #endregion
    }
}
