using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceAnalyzer.Helpers;
using ResourceAnalyzer.Models;
using ResourceAnalyzer.Views;

namespace ResourceAnalyzer
{
    public partial class MainWindow : Window
    {
        #region Battery Usage Tab Handlers

        private void CmbBatteryTimeRange_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            _ = RefreshBatteryUsageAsync(announce: false);
        }

        private void ChkSinceLastCharge_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUI) return;
            _batteryUsageService.ResetAppEnergy();
            _ = RefreshBatteryUsageAsync(announce: true);
        }

        private void ChkSinceLastCharge_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUI) return;
            _batteryUsageService.ResetAppEnergy();
            _ = RefreshBatteryUsageAsync(announce: true);
        }

        private void BtnRefreshBattery_Click(object sender, RoutedEventArgs e)
        {
            _ = RefreshBatteryUsageAsync(announce: true);
        }

        private void BtnDismissBatteryDisclaimer_Click(object sender, RoutedEventArgs e)
        {
            if (chkDoNotShowBatteryDisclaimer?.IsChecked == true)
            {
                _settingsService.CurrentSettings.HideBatteryDisclaimer = true;
                _settingsService.Save();
            }
            pnlBatteryDisclaimer.Visibility = Visibility.Collapsed;
            _speechService.Speak("Battery accuracy disclaimer dismissed.", interrupt: true);
        }

        private static string FormatRelativeTime(DateTime pastTime)
        {
            var span = DateTime.Now - pastTime;
            if (span.TotalSeconds < 0) return "just now";
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}h {span.Minutes}m ago";
            return $"{(int)span.TotalDays}d ago";
        }

        private async Task RefreshBatteryUsageAsync(bool announce = false)
        {
            try
            {
                string timeRange = cmbBatteryTimeRange?.SelectedIndex switch
                {
                    0 => "Full",
                    1 => "Last Month",
                    2 => "Last Week",
                    3 => "Last 24 Hours",
                    4 => "Today",
                    _ => "Full"
                };

                bool sinceLastCharge = chkSinceLastCharge?.IsChecked == true;
                var (sessions, totalDischarge, totalDischargePct, activeDur, standbyDur, hasBattery, lastDisconnect) =
                    await _batteryUsageService.GetBatteryUsageAsync(timeRange, sinceLastCharge);

                if (!hasBattery)
                {
                    string noBatt = "No battery detected (Desktop PC or AC only).";
                    txtBatterySummary.Text = noBatt;
                    _batteryItems.Clear();
                    _appBatteryItems.Clear();
                    _appBatteryMap.Clear();
                    if (announce)
                    {
                        _speechService.Speak(noBatt, interrupt: true);
                        txtAnnouncement.Text = noBatt;
                    }
                    return;
                }

                _lastTotalDischargeMwh = totalDischarge;

                string activeStr = activeDur.TotalHours >= 1 ? $"{(int)activeDur.TotalHours}h {activeDur.Minutes}m" : $"{activeDur.Minutes}m";
                string standbyStr = standbyDur.TotalHours >= 1 ? $"{(int)standbyDur.TotalHours}h {standbyDur.Minutes}m" : $"{standbyDur.Minutes}m";

                string relTime = lastDisconnect.HasValue ? $", {FormatRelativeTime(lastDisconnect.Value)}" : "";
                string filterDesc = sinceLastCharge
                    ? (lastDisconnect.HasValue ? $" (Since last charge: {lastDisconnect.Value:dd-MMM HH:mm}{relTime})" : " (Since last charge)")
                    : $" ({timeRange})";

                string summary = $"Showing {sessions.Count} battery sessions{filterDesc}. Total Discharge: {totalDischarge:N0} mWh ({totalDischargePct:F1}%). Active: {activeStr}, Standby: {standbyStr}.";
                txtBatterySummary.Text = summary;

                _batteryItems.Clear();
                foreach (var s in sessions)
                {
                    _batteryItems.Add(s);
                }

                if (_batteryItems.Count > 0 && lstBatteryUsage.SelectedIndex < 0)
                {
                    lstBatteryUsage.SelectedIndex = 0;
                }

                // Refresh real-time application battery usage with full reset when requested
                await RefreshAppBatteryUsageAsync(isFullReset: announce);

                if (announce)
                {
                    _speechService.Speak(summary, interrupt: true);
                    txtAnnouncement.Text = summary;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error querying battery usage: {ex.Message}");
                txtBatterySummary.Text = "Unable to load battery energy usage.";
            }
        }

        private async Task RefreshAppBatteryUsageAsync(bool isFullReset = false)
        {
            try
            {
                var liveState = _batteryUsageService.GetLiveBatteryState();
                if (!liveState.HasBattery)
                {
                    _appBatteryItems.Clear();
                    _appBatteryMap.Clear();
                    return;
                }

                var rawProcs = await _processService.GetProcessesAsync(string.Empty, "CPU", hideSystemProcesses: false);

                IntPtr fgHwnd = NativeMethods.GetForegroundWindow();
                int fgPid = 0;
                if (fgHwnd != IntPtr.Zero)
                {
                    NativeMethods.GetWindowThreadProcessId(fgHwnd, out fgPid);
                }

                int refreshSecs = _settingsService.CurrentSettings.RefreshIntervalSeconds;
                double elapsedSecs = refreshSecs > 0 ? refreshSecs : 2.0;

                _batteryUsageService.AccumulateAppEnergy(
                    rawProcs,
                    liveState.DischargeRateMw,
                    liveState.IsDischarging,
                    elapsedSecs,
                    fgPid);

                string displayMode = _settingsService.CurrentSettings.BatteryAppDisplayMode ?? "Combined";
                var updatedApps = _batteryUsageService.GetAppBatteryUsage(
                    rawProcs,
                    liveState.DischargeRateMw,
                    liveState.IsDischarging,
                    fgPid,
                    displayMode);

                UpdateAppBatteryCollectionInPlace(updatedApps, isFullReset);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error refreshing app battery usage: {ex.Message}");
            }
        }

        private void UpdateAppBatteryCollectionInPlace(List<AppBatteryUsageItem> newItems, bool isFullReset = false)
        {
            var currentSelected = lstAppBatteryUsage.SelectedItem as AppBatteryUsageItem;
            string? selectedKey = currentSelected?.ProcessName;

            if (_appBatteryItems.Count == 0)
            {
                _appBatteryMap.Clear();
                foreach (var it in newItems)
                {
                    _appBatteryItems.Add(it);
                    _appBatteryMap[it.ProcessName] = it;
                }
                if (_appBatteryItems.Count > 0 && lstAppBatteryUsage.SelectedIndex < 0)
                {
                    lstAppBatteryUsage.SelectedIndex = 0;
                }
                return;
            }

            var newKeySet = new HashSet<string>(newItems.Select(i => i.ProcessName), StringComparer.OrdinalIgnoreCase);

            for (int i = _appBatteryItems.Count - 1; i >= 0; i--)
            {
                if (!newKeySet.Contains(_appBatteryItems[i].ProcessName))
                {
                    _appBatteryMap.Remove(_appBatteryItems[i].ProcessName);
                    _appBatteryItems.RemoveAt(i);
                }
            }

            // Update existing items in-place without moving them
            foreach (var incoming in newItems)
            {
                if (_appBatteryMap.TryGetValue(incoming.ProcessName, out var existing))
                {
                    existing.DisplayName = incoming.DisplayName;
                    existing.Pid = incoming.Pid;
                    existing.UpdateMetrics(
                        incoming.EstimatedPowerMw,
                        incoming.EnergyConsumedMwh,
                        incoming.BatteryPercent,
                        incoming.PowerImpact,
                        incoming.IsForeground,
                        incoming.DisplayMode);
                }
                else
                {
                    _appBatteryItems.Add(incoming);
                    _appBatteryMap[incoming.ProcessName] = incoming;
                }
            }

            // Only reorder items when user requested a full reset (F5 Refresh or Filter change).
            // During periodic background timer ticks, items remain in place so screen reader focus is never moved.
            if (isFullReset)
            {
                for (int targetIndex = 0; targetIndex < newItems.Count && targetIndex < _appBatteryItems.Count; targetIndex++)
                {
                    string targetKey = newItems[targetIndex].ProcessName;
                    if (!string.Equals(_appBatteryItems[targetIndex].ProcessName, targetKey, StringComparison.OrdinalIgnoreCase))
                    {
                        int currentIndex = -1;
                        for (int j = targetIndex + 1; j < _appBatteryItems.Count; j++)
                        {
                            if (string.Equals(_appBatteryItems[j].ProcessName, targetKey, StringComparison.OrdinalIgnoreCase))
                            {
                                currentIndex = j;
                                break;
                            }
                        }
                        if (currentIndex > targetIndex)
                        {
                            _appBatteryItems.Move(currentIndex, targetIndex);
                        }
                    }
                }
            }

            if (selectedKey != null && _appBatteryMap.TryGetValue(selectedKey, out var reselect))
            {
                if (!ReferenceEquals(lstAppBatteryUsage.SelectedItem, reselect))
                {
                    lstAppBatteryUsage.SelectedItem = reselect;
                }
            }
            else if (_appBatteryItems.Count > 0 && lstAppBatteryUsage.SelectedIndex < 0)
            {
                lstAppBatteryUsage.SelectedIndex = 0;
            }
        }

        private void LstBatteryUsage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                FocusListBoxItem(lstAppBatteryUsage);
            }
            else if (e.Key == Key.F5)
            {
                e.Handled = true;
                _ = RefreshBatteryUsageAsync(announce: true);
            }
            else if (e.Key == Key.F6)
            {
                e.Handled = true;
                FocusListBoxItem(lstAppBatteryUsage);
            }
            else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                if (lstBatteryUsage.SelectedItem is BatteryUsageItem item)
                {
                    try
                    {
                        System.Windows.Clipboard.SetText(item.DisplayText);
                        _speechService.Speak("Copied battery session details to clipboard.", interrupt: true);
                        txtAnnouncement.Text = "Copied battery session details to clipboard.";
                    }
                    catch { }
                }
            }
        }

        private void LstAppBatteryUsage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Shift)
            {
                e.Handled = true;
                FocusListBoxItem(lstBatteryUsage);
            }
            else if (e.Key == Key.F5)
            {
                e.Handled = true;
                _ = RefreshBatteryUsageAsync(announce: true);
            }
            else if (e.Key == Key.F6)
            {
                e.Handled = true;
                FocusListBoxItem(lstBatteryUsage);
            }
            else if (e.Key == Key.Delete)
            {
                e.Handled = true;
                EndSelectedAppBatteryTask();
            }
            else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                CopySelectedAppBatteryDetails();
            }
        }

        private void CtxAppBatteryEndTask_Click(object sender, RoutedEventArgs e)
        {
            EndSelectedAppBatteryTask();
        }

        private void CtxAppBatteryCopy_Click(object sender, RoutedEventArgs e)
        {
            CopySelectedAppBatteryDetails();
        }

        private void CopySelectedAppBatteryDetails()
        {
            if (lstAppBatteryUsage.SelectedItem is AppBatteryUsageItem item)
            {
                try
                {
                    System.Windows.Clipboard.SetText(item.DisplayText);
                    _speechService.Speak($"Copied {item.DisplayName} battery details to clipboard.", interrupt: true);
                    txtAnnouncement.Text = $"Copied {item.DisplayName} battery details to clipboard.";
                }
                catch { }
            }
        }

        private async void EndSelectedAppBatteryTask()
        {
            if (lstAppBatteryUsage.SelectedItem is not AppBatteryUsageItem item)
            {
                _speechService.Speak("No application selected.", interrupt: true);
                return;
            }

            if (item.Pid <= 4)
            {
                _speechService.Speak("Cannot terminate system process.", interrupt: true);
                return;
            }

            bool confirm = _settingsService.CurrentSettings.ConfirmBeforeEndTask;
            if (confirm)
            {
                var dlg = new ConfirmEndTaskDialog($"{item.DisplayName} (PID {item.Pid})", item.Pid)
                {
                    Owner = this
                };

                bool? result = dlg.ShowDialog();
                if (result != true || !dlg.Confirmed)
                {
                    _speechService.Speak("Task termination canceled.", interrupt: true);
                    return;
                }
            }

            var (success, msg) = await _processService.KillProcessTreeAsync(item.Pid, item.DisplayName);
            _speechService.Speak(msg, interrupt: true);
            txtAnnouncement.Text = msg;

            await RefreshAppBatteryUsageAsync(isFullReset: true);
        }

        #endregion
    }
}
