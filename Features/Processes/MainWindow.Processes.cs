using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ResourceAnalyzer.Helpers;
using ResourceAnalyzer.Models;
using ResourceAnalyzer.Views;

namespace ResourceAnalyzer
{
    public partial class MainWindow : Window
    {
        #region Process List Management

        internal static List<ProcessItem> BuildDisplayList(List<ProcessItem> rawList, string sortBy, bool groupProcesses, HashSet<string> expandedGroups)
        {
            if (!groupProcesses)
            {
                return rawList;
            }

            var groups = rawList.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var topLevelItems = new List<(ProcessItem Item, List<ProcessItem>? Children)>();

            foreach (var g in groups)
            {
                int count = g.Count();
                if (count == 1)
                {
                    var single = g.First();
                    single.IsGroupHeader = false;
                    single.IsGroupChild = false;
                    single.GroupName = string.Empty;
                    single.InstanceIndex = 1;
                    single.InstanceTotal = 1;
                    single.UpdateDisplayText();
                    topLevelItems.Add((single, null));
                }
                else
                {
                    string appName = g.Key;
                    long totalMemory = 0;
                    double totalCpu = 0.0;
                    var pids = new List<int>();

                    foreach (var p in g)
                    {
                        totalMemory += p.MemoryBytes;
                        totalCpu += p.CpuPercent;
                        pids.Add(p.Pid);
                    }

                    bool isExpanded = expandedGroups.Contains(appName);
                    bool groupIsFrozen = g.Any(p => p.IsFrozen);
                    bool groupIsActive = g.Any(p => p.IsActiveApp);
                    string groupDesc = g.FirstOrDefault(p => !string.IsNullOrEmpty(p.Description))?.Description ?? string.Empty;

                    var header = new ProcessItem
                    {
                        Pid = 0,
                        Name = appName,
                        Description = groupDesc,
                        IsGroupHeader = true,
                        IsGroupChild = false,
                        IsExpanded = isExpanded,
                        GroupName = appName,
                        GroupChildPids = pids,
                        MemoryBytes = totalMemory,
                        CpuPercent = totalCpu,
                        InstanceIndex = 1,
                        InstanceTotal = count,
                        IsFrozen = groupIsFrozen,
                        IsActiveApp = groupIsActive
                    };
                    header.UpdateDisplayText();

                    List<ProcessItem>? children = null;
                    if (isExpanded)
                    {
                        var sortedChildren = sortBy switch
                        {
                            "CPU" => g.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.MemoryBytes).ToList(),
                            "Name" => g.OrderBy(p => p.Pid).ToList(),
                            _ => g.OrderByDescending(p => p.MemoryBytes).ThenByDescending(p => p.CpuPercent).ToList()
                        };

                        children = new List<ProcessItem>();
                        foreach (var child in sortedChildren)
                        {
                            child.IsGroupHeader = false;
                            child.IsGroupChild = true;
                            child.GroupName = appName;
                            string detail = ResourceAnalyzer.Services.ProcessDetailResolver.ResolveSubProcessDetail(child.Pid, child.Name, child.DisplayName);
                            if (!string.IsNullOrWhiteSpace(detail))
                            {
                                child.SubProcessDetail = detail;
                            }
                            child.UpdateDisplayText();
                            children.Add(child);
                        }
                    }

                    topLevelItems.Add((header, children));
                }
            }

            var sortedTopLevel = sortBy switch
            {
                "CPU" => topLevelItems.OrderByDescending(t => t.Item.CpuPercent).ThenByDescending(t => t.Item.MemoryBytes).ToList(),
                "Name" => topLevelItems.OrderBy(t => t.Item.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
                _ => topLevelItems.OrderByDescending(t => t.Item.MemoryBytes).ThenByDescending(t => t.Item.CpuPercent).ToList()
            };

            var result = new List<ProcessItem>();
            foreach (var (item, children) in sortedTopLevel)
            {
                result.Add(item);
                if (children != null)
                {
                    result.AddRange(children);
                }
            }

            return result;
        }

        private async Task RefreshProcessesAsync(bool isFullReset = false, int preferredIndex = -1)
        {
            try
            {
                string term = txtSearch.Text;
                bool groupProcesses = _settingsService.CurrentSettings.GroupProcesses;
                var rawList = await _processService.GetProcessesAsync(term, _currentSort);

                // Check resource overuse alerts on raw processes (only if unfiltered; timer handles full scan when filtered)
                if (string.IsNullOrEmpty(term))
                {
                    CheckResourceAlerts(rawList);
                }

                // Populate dynamic Frozen Applications panel
                var frozenList = rawList.Where(p => p.IsFrozen).ToList();
                if (frozenList.Count > 0)
                {
                    if (pnlFrozenApps.Visibility != Visibility.Visible)
                    {
                        pnlFrozenApps.Visibility = Visibility.Visible;
                        _speechService.Speak($"Warning: {frozenList.Count} frozen application{(frozenList.Count > 1 ? "s" : "")} detected.", interrupt: false);
                    }
                    _frozenItems.Clear();
                    foreach (var f in frozenList)
                    {
                        _frozenItems.Add(f);
                    }
                }
                else
                {
                    if (pnlFrozenApps.Visibility != Visibility.Collapsed)
                    {
                        pnlFrozenApps.Visibility = Visibility.Collapsed;
                    }
                    _frozenItems.Clear();
                }

                var list = BuildDisplayList(rawList, _currentSort, groupProcesses, _expandedGroups);

                int appCount = groupProcesses ? rawList.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() : list.Count;
                txtProcessCount.Text = groupProcesses
                    ? $"Showing {appCount} applications ({rawList.Count} processes). Sorted by {_currentSort}. Press Delete to end task."
                    : $"Showing {list.Count} processes. Sorted by {_currentSort}. Press Delete to end task.";

                if (_processItems.Count == 0)
                {
                    foreach (var p in list)
                    {
                        _processItems.Add(p);
                    }
                    if (_processItems.Count > 0 && lstProcesses.SelectedIndex < 0)
                    {
                        lstProcesses.SelectedIndex = 0;
                    }
                }
                else
                {
                    string prevSelectedKey = (lstProcesses.SelectedItem as ProcessItem)?.ItemKey ?? string.Empty;
                    int prevSelectedIndex = preferredIndex >= 0 ? preferredIndex : lstProcesses.SelectedIndex;
                    bool hadFocus = lstProcesses.IsKeyboardFocusWithin;
                    var freshMap = new Dictionary<string, ProcessItem>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in list)
                    {
                        freshMap[p.ItemKey] = p;
                    }
                    var freshKeys = new HashSet<string>(freshMap.Keys, StringComparer.OrdinalIgnoreCase);

                    // Remove terminated processes or collapsed items
                    for (int i = _processItems.Count - 1; i >= 0; i--)
                    {
                        if (!freshKeys.Contains(_processItems[i].ItemKey))
                        {
                            _processItems.RemoveAt(i);
                        }
                    }

                    // Update existing items in-place (fires PropertyChanged once per item only when changed)
                    var currentKeys = new HashSet<string>(_processItems.Select(p => p.ItemKey), StringComparer.OrdinalIgnoreCase);
                    foreach (var item in _processItems)
                    {
                        if (freshMap.TryGetValue(item.ItemKey, out var fresh))
                        {
                            item.IsGroupHeader = fresh.IsGroupHeader;
                            item.IsGroupChild = fresh.IsGroupChild;
                            item.GroupName = fresh.GroupName;
                            item.GroupChildPids = fresh.GroupChildPids;
                            item.UpdateMetrics(fresh.MemoryBytes, fresh.CpuPercent, fresh.InstanceIndex, fresh.InstanceTotal, fresh.IsExpanded, fresh.IsFrozen, fresh.IsActiveApp, fresh.Description);
                        }
                    }

                    // Append any newly appeared items
                    foreach (var p in list)
                    {
                        if (!currentKeys.Contains(p.ItemKey))
                        {
                            _processItems.Add(p);
                        }
                    }

                    // Only reorder items when user requested a full sort or filter (isFullReset)
                    // During background timer ticks, items remain in place so screen reader focus is never moved
                    if (isFullReset)
                    {
                        for (int targetIndex = 0; targetIndex < list.Count && targetIndex < _processItems.Count; targetIndex++)
                        {
                            string targetKey = list[targetIndex].ItemKey;
                            if (!string.Equals(_processItems[targetIndex].ItemKey, targetKey, StringComparison.OrdinalIgnoreCase))
                            {
                                int currentIndex = -1;
                                for (int j = targetIndex + 1; j < _processItems.Count; j++)
                                {
                                    if (string.Equals(_processItems[j].ItemKey, targetKey, StringComparison.OrdinalIgnoreCase))
                                    {
                                        currentIndex = j;
                                        break;
                                    }
                                }
                                if (currentIndex > targetIndex)
                                {
                                    _processItems.Move(currentIndex, targetIndex);
                                }
                            }
                        }
                    }

                    // Restore selection cleanly with focus lock
                    bool selectionRestored = false;
                    if (!string.IsNullOrEmpty(prevSelectedKey))
                    {
                        var match = _processItems.FirstOrDefault(p => string.Equals(p.ItemKey, prevSelectedKey, StringComparison.OrdinalIgnoreCase));
                        if (match != null)
                        {
                            if (!ReferenceEquals(lstProcesses.SelectedItem, match))
                            {
                                lstProcesses.SelectedItem = match;
                            }
                            if (hadFocus || lstProcesses.IsKeyboardFocusWithin)
                            {
                                var container = lstProcesses.ItemContainerGenerator.ContainerFromItem(match) as ListBoxItem;
                                container?.Focus();
                            }
                            selectionRestored = true;
                        }
                    }

                    if (!selectionRestored && _processItems.Count > 0)
                    {
                        int targetIdx = prevSelectedIndex >= 0 ? prevSelectedIndex : 0;
                        targetIdx = Math.Clamp(targetIdx, 0, _processItems.Count - 1);
                        var targetItem = _processItems[targetIdx];
                        lstProcesses.SelectedIndex = targetIdx;
                        lstProcesses.ScrollIntoView(targetItem);

                        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                        {
                            if (tabProcesses.IsSelected)
                            {
                                lstProcesses.UpdateLayout();
                                var container = (lstProcesses.ItemContainerGenerator.ContainerFromItem(targetItem)
                                    ?? lstProcesses.ItemContainerGenerator.ContainerFromIndex(targetIdx)) as ListBoxItem;
                                if (container != null)
                                {
                                    container.Focus();
                                    Keyboard.Focus(container);
                                }
                                else
                                {
                                    Keyboard.Focus(lstProcesses);
                                }
                            }
                        }));
                    }
                    else if (_processItems.Count > 0 && lstProcesses.SelectedIndex < 0)
                    {
                        lstProcesses.SelectedIndex = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error refreshing processes: {ex.Message}");
            }
        }

        private void TriggerResourceAlert(string title, string msg)
        {
            // Play alert exclamation chime (user confirmed sound is fine)
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }

            // Check if application window is actively focused and visible on screen
            bool isWindowActiveAndVisible = IsActive && Visibility == Visibility.Visible && WindowState != WindowState.Minimized;

            if (isWindowActiveAndVisible || _trayIcon == null)
            {
                // App is active and focused on screen: speak directly to NVDA / LiveRegion, skip tray balloon
                _speechService.Speak(msg, interrupt: true);
                txtAnnouncement.Text = msg;
            }
            else
            {
                // App is minimized to system tray or in background:
                // Show native Windows tray balloon tip. Windows Shell automatically displays the
                // notification popup visually for sighted users and announces it to NVDA, JAWS,
                // and Narrator via Windows OS accessibility.
                // We skip _speechService.Speak here so NVDA announces the alert ONLY ONCE!
                _trayIcon.ShowBalloonTip(title, msg);
            }
        }

        private void CheckResourceAlerts(List<ProcessItem> list)
        {
            var s = _settingsService.CurrentSettings;
            bool checkRam = s.EnableHighRamAlert && s.HighRamLimitValue > 0;
            bool checkCpu = s.EnableHighCpuAlert && s.HighCpuLimitPercent > 0;

            if (!checkRam && !checkCpu) return;

            double ramLimitBytes = s.HighRamLimitUnit.Equals("GB", StringComparison.OrdinalIgnoreCase)
                ? s.HighRamLimitValue * 1024.0 * 1024.0 * 1024.0
                : s.HighRamLimitValue * 1024.0 * 1024.0;
            double cpuLimitPercent = s.HighCpuLimitPercent;
            var now = DateTime.UtcNow;

            // Exclude PID 0 (System Idle Process) and PID 4 (System Kernel) - these are not actionable applications
            var actionableProcesses = list.Where(p =>
                p.Pid > 4 &&
                !p.Name.Equals("Idle", StringComparison.OrdinalIgnoreCase) &&
                !p.Name.Equals("System Idle Process", StringComparison.OrdinalIgnoreCase) &&
                !p.Name.Equals("System", StringComparison.OrdinalIgnoreCase));

            // Group by process name so that multi-instance applications (like Chrome, Edge, etc.)
            // have their combined total resource usage evaluated against the limit.
            var appGroups = actionableProcesses.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

            (string Title, string Message, string AlertKey)? pendingAlertToFire = null;

            foreach (var group in appGroups)
            {
                long totalRamBytes = group.Sum(p => p.MemoryBytes);
                double totalCpuPercent = group.Sum(p => p.CpuPercent);
                var first = group.First();
                int count = group.Count();
                string baseName = !string.IsNullOrWhiteSpace(first.DisplayName) ? first.DisplayName : (!string.IsNullOrWhiteSpace(first.Name) ? first.Name : group.Key);
                string appTitle = count > 1 ? $"{baseName} ({count} instances)" : baseName;

                // Check RAM
                if (checkRam && totalRamBytes > ramLimitBytes)
                {
                    string ramKey = $"RAM_{group.Key}";
                    if (!_lastAlertTimes.TryGetValue(ramKey, out var lastTime) || (now - lastTime).TotalSeconds >= 60)
                    {
                        if (pendingAlertToFire == null)
                        {
                        string limitStr = $"{s.HighRamLimitValue} {s.HighRamLimitUnit}";
                        string usedStr = FormatHelper.FormatBytes(totalRamBytes);
                        string msg = $"{appTitle} is exceeding maximum RAM usage ({usedStr}, limit: {limitStr}).";
                            pendingAlertToFire = ("High RAM Usage Alert", msg, ramKey);
                        }
                    }
                }

                // Check CPU
                string cpuKey = $"CPU_{group.Key}";
                if (checkCpu && totalCpuPercent >= cpuLimitPercent)
                {
                    _consecutiveCpuAlertHits.TryGetValue(cpuKey, out int hits);
                    hits++;
                    _consecutiveCpuAlertHits[cpuKey] = hits;

                    // Require at least 2 consecutive high CPU checks (~2 seconds) so brief transient launch bursts don't trigger false alarms
                    if (hits >= 2)
                    {
                        if (!_lastAlertTimes.TryGetValue(cpuKey, out var lastTime) || (now - lastTime).TotalSeconds >= 60)
                        {
                            if (pendingAlertToFire == null)
                            {
                            string msg = $"{appTitle} is exceeding maximum CPU usage ({totalCpuPercent:F1}%, limit: {cpuLimitPercent:F0}%).";
                                pendingAlertToFire = ("High CPU Usage Alert", msg, cpuKey);
                            }
                        }
                    }
                }
                else
                {
                    // Reset hit counter if CPU drops below limit
                    _consecutiveCpuAlertHits.Remove(cpuKey);
                }
            }

            // Prune stale CPU hit counters for terminated applications
            if (_consecutiveCpuAlertHits.Count > 0)
            {
                var activeCpuKeys = new HashSet<string>(appGroups.Select(g => $"CPU_{g.Key}"), StringComparer.OrdinalIgnoreCase);
                foreach (var k in _consecutiveCpuAlertHits.Keys.Where(k => !activeCpuKeys.Contains(k)).ToList())
                {
                    _consecutiveCpuAlertHits.Remove(k);
                }
            }

            // Staggered Delivery: Fire at most ONE alert per check cycle with a minimum 4-second gap between any alerts.
            // Guarantees:
            // 1. Each application maintains its own independent 60-second cooldown starting from when it was actually alerted.
            // 2. Multiple high-resource applications never collide or cancel each other's speech.
            // 3. Windows Shell tray notification balloons are delivered reliably without being dropped.
            if (pendingAlertToFire != null)
            {
                if ((now - _lastGlobalAlertTime).TotalSeconds >= 4)
                {
                    _lastAlertTimes[pendingAlertToFire.Value.AlertKey] = now;
                    _lastGlobalAlertTime = now;
                    TriggerResourceAlert(pendingAlertToFire.Value.Title, pendingAlertToFire.Value.Message);
                }
            }
        }

        /*
                foreach (var k in stale)
                {
                    _consecutiveCpuAlertHits.Remove(k);
                }
            }
        }

        */
        private async Task TryEndSelectedProcessAsync()
        {
            if (lstProcesses.SelectedItem is not ProcessItem item)
            {
                _speechService.Speak("No process selected.", interrupt: true);
                return;
            }

            int savedIndex = lstProcesses.SelectedIndex;
            bool confirm = _settingsService.CurrentSettings.ConfirmBeforeEndTask;

            if (item.IsGroupHeader)
            {
                if (item.Name.Equals("svchost", StringComparison.OrdinalIgnoreCase) ||
                    item.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string safeMsg = "svchost.exe hosts critical Windows system services and the entire group cannot be terminated.";
                    _speechService.Speak(safeMsg, interrupt: true);
                    txtAnnouncement.Text = safeMsg;
                    return;
                }

                if (confirm)
                {
                    string promptTarget = $"all {item.InstanceTotal} instances of {item.DisplayName}";
                    var dlg = new ConfirmEndTaskDialog(promptTarget, pid: -1)
                    {
                        Owner = this
                    };

                    bool? result = dlg.ShowDialog();
                    if (result != true || !dlg.Confirmed)
                    {
                        _speechService.Speak("Task termination canceled.", interrupt: true);
                        return;
                    }

                    if (dlg.DisableFuturePrompts)
                    {
                        _settingsService.CurrentSettings.ConfirmBeforeEndTask = false;
                        if (cmbProcessManager != null)
                        {
                            cmbProcessManager.SelectedIndex = 1;
                        }
                        _settingsService.Save();
                    }
                }

                int killedCount = 0;
                var pidsToKill = item.GroupChildPids.ToList();
                foreach (int pid in pidsToKill)
                {
                    var (ok, _) = await _processService.KillProcessAsync(pid, item.DisplayName);
                    if (ok) killedCount++;
                }

                string msg = $"Ended {killedCount} of {pidsToKill.Count} instances of {item.DisplayName}.";
                _speechService.Speak(msg, interrupt: true);
                txtAnnouncement.Text = msg;

                await RefreshProcessesAsync(isFullReset: true, preferredIndex: savedIndex);
                return;
            }

            if (!_processService.CanTerminateProcess(item.Pid, item.Name, item.SubProcessDetail, out string blockReason))
            {
                _speechService.Speak(blockReason, interrupt: true);
                txtAnnouncement.Text = blockReason;
                return;
            }

            if (confirm)
            {
                var dlg = new ConfirmEndTaskDialog(item.DisplayName, item.Pid)
                {
                    Owner = this
                };

                bool? result = dlg.ShowDialog();
                if (result != true || !dlg.Confirmed)
                {
                    _speechService.Speak("Task termination canceled.", interrupt: true);
                    return;
                }

                if (dlg.DisableFuturePrompts)
                {
                    _settingsService.CurrentSettings.ConfirmBeforeEndTask = false;
                    if (cmbProcessManager != null)
                    {
                        cmbProcessManager.SelectedIndex = 1;
                    }
                    _settingsService.Save();
                }
            }

            var (success, singleMsg) = await _processService.KillProcessAsync(item.Pid, item.DisplayName, item.SubProcessDetail);
            _speechService.Speak(singleMsg, interrupt: true);
            txtAnnouncement.Text = singleMsg;

            await RefreshProcessesAsync(isFullReset: true, preferredIndex: savedIndex);
        }

        private async Task TryEndSelectedProcessTreeAsync()
        {
            if (lstProcesses.SelectedItem is not ProcessItem item)
            {
                _speechService.Speak("No process selected.", interrupt: true);
                return;
            }

            int savedIndex = lstProcesses.SelectedIndex;
            bool confirm = _settingsService.CurrentSettings.ConfirmBeforeEndTask;

            if (item.IsGroupHeader)
            {
                if (item.Name.Equals("svchost", StringComparison.OrdinalIgnoreCase) ||
                    item.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string safeMsg = "svchost.exe hosts critical Windows system services and the entire group cannot be terminated.";
                    _speechService.Speak(safeMsg, interrupt: true);
                    txtAnnouncement.Text = safeMsg;
                    return;
                }

                if (confirm)
                {
                    string promptTarget = $"entire process trees of all {item.InstanceTotal} instances of {item.DisplayName}";
                    var dlg = new ConfirmEndTaskDialog(promptTarget, pid: -1)
                    {
                        Owner = this
                    };

                    bool? result = dlg.ShowDialog();
                    if (result != true || !dlg.Confirmed)
                    {
                        _speechService.Speak("Task termination canceled.", interrupt: true);
                        return;
                    }

                    if (dlg.DisableFuturePrompts)
                    {
                        _settingsService.CurrentSettings.ConfirmBeforeEndTask = false;
                        if (cmbProcessManager != null)
                        {
                            cmbProcessManager.SelectedIndex = 1;
                        }
                        _settingsService.Save();
                    }
                }

                int killedCount = 0;
                var pidsToKill = item.GroupChildPids.ToList();
                foreach (int pid in pidsToKill)
                {
                    var (ok, _) = await _processService.KillProcessTreeAsync(pid, item.DisplayName);
                    if (ok) killedCount++;
                }

                string msg = $"Ended process trees for {killedCount} of {pidsToKill.Count} instances of {item.DisplayName}.";
                _speechService.Speak(msg, interrupt: true);
                txtAnnouncement.Text = msg;

                await RefreshProcessesAsync(isFullReset: true, preferredIndex: savedIndex);
                return;
            }

            if (!_processService.CanTerminateProcess(item.Pid, item.Name, item.SubProcessDetail, out string blockReason))
            {
                _speechService.Speak(blockReason, interrupt: true);
                txtAnnouncement.Text = blockReason;
                return;
            }

            if (confirm)
            {
                var dlg = new ConfirmEndTaskDialog($"{item.DisplayName} (PID {item.Pid}) and child processes", item.Pid)
                {
                    Owner = this
                };

                bool? result = dlg.ShowDialog();
                if (result != true || !dlg.Confirmed)
                {
                    _speechService.Speak("Task termination canceled.", interrupt: true);
                    return;
                }

                if (dlg.DisableFuturePrompts)
                {
                    _settingsService.CurrentSettings.ConfirmBeforeEndTask = false;
                    if (cmbProcessManager != null)
                    {
                        cmbProcessManager.SelectedIndex = 1;
                    }
                    _settingsService.Save();
                }
            }

            var (success, singleMsg) = await _processService.KillProcessTreeAsync(item.Pid, item.DisplayName, item.SubProcessDetail);
            _speechService.Speak(singleMsg, interrupt: true);
            txtAnnouncement.Text = singleMsg;

            await RefreshProcessesAsync(isFullReset: true, preferredIndex: savedIndex);
        }

        private async Task TryRestartSelectedProcessAsync()
        {
            if (lstProcesses.SelectedItem is not ProcessItem item) return;

            int savedIndex = lstProcesses.SelectedIndex;
            int targetPid = item.Pid;
            if (item.IsGroupHeader)
            {
                if (item.Name.Equals("svchost", StringComparison.OrdinalIgnoreCase) ||
                    item.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string safeMsg = "svchost.exe hosts critical Windows system services and cannot be restarted.";
                    _speechService.Speak(safeMsg, interrupt: true);
                    txtAnnouncement.Text = safeMsg;
                    return;
                }

                if (item.GroupChildPids.Count > 0)
                {
                    targetPid = item.GroupChildPids[0];
                }
                else
                {
                    _speechService.Speak($"Cannot restart {item.DisplayName}: no running instances found.", interrupt: true);
                    return;
                }
            }

            if (!_processService.CanTerminateProcess(targetPid, item.Name, item.SubProcessDetail, out string blockReason))
            {
                _speechService.Speak(blockReason, interrupt: true);
                txtAnnouncement.Text = blockReason;
                return;
            }

            var (success, msg) = await _processService.RestartProcessAsync(targetPid, item.DisplayName, item.SubProcessDetail);
            _speechService.Speak(msg, interrupt: true);
            txtAnnouncement.Text = msg;

            await RefreshProcessesAsync(isFullReset: true, preferredIndex: savedIndex);
        }

        #endregion

        #region Process UI & Keyboard Handlers

        private async void LstProcesses_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                e.Handled = true;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    await TryEndSelectedProcessTreeAsync();
                }
                else
                {
                    await TryEndSelectedProcessAsync();
                }
            }
            else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                CtxCopyDetails_Click(sender, e);
            }
            else if (e.Key == Key.R && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                await TryRestartSelectedProcessAsync();
            }
            else if (e.Key == Key.Right)
            {
                if (lstProcesses.SelectedItem is ProcessItem item && item.IsGroupHeader)
                {
                    e.Handled = true;
                    if (!_expandedGroups.Contains(item.GroupName))
                    {
                        _expandedGroups.Add(item.GroupName);
                        _speechService.Speak($"{item.DisplayName} expanded, {item.InstanceTotal} instances.", interrupt: true);
                        await RefreshProcessesAsync(isFullReset: true);
                    }
                }
            }
            else if (e.Key == Key.Left)
            {
                if (lstProcesses.SelectedItem is ProcessItem item)
                {
                    if (item.IsGroupHeader && _expandedGroups.Contains(item.GroupName))
                    {
                        e.Handled = true;
                        _expandedGroups.Remove(item.GroupName);
                        _speechService.Speak($"{item.DisplayName} collapsed.", interrupt: true);
                        await RefreshProcessesAsync(isFullReset: true);
                    }
                    else if (item.IsGroupChild)
                    {
                        e.Handled = true;
                        var parent = _processItems.FirstOrDefault(p => p.IsGroupHeader && string.Equals(p.GroupName, item.GroupName, StringComparison.OrdinalIgnoreCase));
                        if (parent != null)
                        {
                            lstProcesses.SelectedItem = parent;
                            lstProcesses.ScrollIntoView(parent);
                            _speechService.Speak(parent.DisplayText, interrupt: true);
                        }
                    }
                }
            }
            else if (e.Key == Key.Enter)
            {
                if (lstProcesses.SelectedItem is ProcessItem item && item.IsGroupHeader)
                {
                    e.Handled = true;
                    if (_expandedGroups.Contains(item.GroupName))
                    {
                        _expandedGroups.Remove(item.GroupName);
                        _speechService.Speak($"{item.DisplayName} collapsed.", interrupt: true);
                    }
                    else
                    {
                        _expandedGroups.Add(item.GroupName);
                        _speechService.Speak($"{item.DisplayName} expanded, {item.InstanceTotal} instances.", interrupt: true);
                    }
                    await RefreshProcessesAsync(isFullReset: true);
                }
            }
            else if (e.Key == Key.F5)
            {
                e.Handled = true;
                _ = RefreshAllAsync(announce: true);
            }
            else if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            {
                char pressedChar = '\0';
                if (e.Key >= Key.A && e.Key <= Key.Z)
                {
                    pressedChar = (char)('A' + (e.Key - Key.A));
                }
                else if (e.Key >= Key.D0 && e.Key <= Key.D9)
                {
                    pressedChar = (char)('0' + (e.Key - Key.D0));
                }
                else if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9)
                {
                    pressedChar = (char)('0' + (e.Key - Key.NumPad0));
                }

                if (pressedChar != '\0' && _processItems.Count > 0)
                {
                    int currentIdx = lstProcesses.SelectedIndex;
                    int count = _processItems.Count;
                    int startIdx = (currentIdx >= 0) ? (currentIdx + 1) % count : 0;

                    bool found = false;
                    for (int i = 0; i < count; i++)
                    {
                        int checkIdx = (startIdx + i) % count;
                        var item = _processItems[checkIdx];

                        if (ProcessMatchesLetter(item, pressedChar))
                        {
                            found = true;
                            e.Handled = true;
                            lstProcesses.SelectedIndex = checkIdx;
                            lstProcesses.ScrollIntoView(item);

                            var container = (ListBoxItem)lstProcesses.ItemContainerGenerator.ContainerFromIndex(checkIdx);
                            if (container == null)
                            {
                                lstProcesses.UpdateLayout();
                                container = (ListBoxItem)lstProcesses.ItemContainerGenerator.ContainerFromIndex(checkIdx);
                            }
                            container?.Focus();

                            _speechService.Speak(item.DisplayText, interrupt: true);
                            txtAnnouncement.Text = item.DisplayText;
                            break;
                        }
                    }

                    if (!found)
                    {
                        e.Handled = true;
                        string notFoundMsg = $"No process starting with {pressedChar}.";
                        _speechService.Speak(notFoundMsg, interrupt: true);
                        txtAnnouncement.Text = notFoundMsg;
                    }
                }
            }
        }

        internal static bool ProcessMatchesLetter(ProcessItem item, char letter)
        {
            string charStr = letter.ToString();

            // 1. Direct name and description prefix matches
            if (item.Name.StartsWith(charStr, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrWhiteSpace(item.Description) && item.Description.TrimStart().StartsWith(charStr, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrWhiteSpace(item.DisplayName) && item.DisplayName.TrimStart().StartsWith(charStr, StringComparison.OrdinalIgnoreCase)) return true;
            if (item.IsGroupChild && !string.IsNullOrWhiteSpace(item.SubProcessDetail) && item.SubProcessDetail.TrimStart().StartsWith(charStr, StringComparison.OrdinalIgnoreCase)) return true;

            // 2. Word boundary matches in DisplayName (e.g. 'E' for "Microsoft Edge", 'C' for "Google Chrome")
            string full = item.DisplayName;
            if (!string.IsNullOrWhiteSpace(full))
            {
                // Strip trademark symbols so (R), (TM), (C) do not trigger false letter matches
                string cleaned = full.Replace("(R)", " ", StringComparison.OrdinalIgnoreCase)
                                     .Replace("(TM)", " ", StringComparison.OrdinalIgnoreCase)
                                     .Replace("(C)", " ", StringComparison.OrdinalIgnoreCase)
                                     .Replace("®", " ")
                                     .Replace("™", " ")
                                     .Replace("©", " ");

                var parts = cleaned.Split(new[] { ' ', '(', ')', '[', ']', '-', '/', '\\', '_' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (string.Equals(part, "R", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(part, "TM", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (part.StartsWith(charStr, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private async void LstProcesses_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstProcesses.SelectedItem is ProcessItem item && item.IsGroupHeader)
            {
                if (_expandedGroups.Contains(item.GroupName))
                {
                    _expandedGroups.Remove(item.GroupName);
                    _speechService.Speak($"{item.DisplayName} collapsed.", interrupt: true);
                }
                else
                {
                    _expandedGroups.Add(item.GroupName);
                    _speechService.Speak($"{item.DisplayName} expanded, {item.InstanceTotal} instances.", interrupt: true);
                }
                await RefreshProcessesAsync(isFullReset: true);
            }
        }

        private async void CtxEndTask_Click(object sender, RoutedEventArgs e)
        {
            await TryEndSelectedProcessAsync();
        }

        private void CtxOpenFileLocation_Click(object sender, RoutedEventArgs e)
        {
            if (lstProcesses.SelectedItem is ProcessItem item)
            {
                int targetPid = item.Pid;
                if (item.IsGroupHeader && item.GroupChildPids.Count > 0)
                {
                    targetPid = item.GroupChildPids[0];
                }

                if (targetPid > 0)
                {
                    try
                    {
                        var proc = Process.GetProcessById(targetPid);
                        string? path = proc.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        {
                            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                            _speechService.Speak($"Opened file location for {item.DisplayName}.", interrupt: true);
                            return;
                        }
                    }
                    catch { }
                }

                _speechService.Speak($"File location is unavailable for {item.DisplayName}.", interrupt: true);
            }
        }

        private void CtxSearchWeb_Click(object sender, RoutedEventArgs e)
        {
            if (lstProcesses.SelectedItem is ProcessItem item)
            {
                try
                {
                    string query = Uri.EscapeDataString($"{item.Name} Windows process");
                    Process.Start(new ProcessStartInfo($"https://www.google.com/search?q={query}") { UseShellExecute = true });
                    _speechService.Speak($"Searching online for {item.Name}.", interrupt: true);
                }
                catch { }
            }
        }

        private void CtxCopyDetails_Click(object sender, RoutedEventArgs e)
        {
            if (lstProcesses.SelectedItem is ProcessItem item)
            {
                try
                {
                    if (!SafeClipboard.TrySetText(item.DisplayText)) return;
                    _speechService.Speak($"Copied {item.Name} details to clipboard.", interrupt: true);
                    txtAnnouncement.Text = $"Copied {item.Name} details to clipboard.";
                }
                catch { }
            }
        }

        private async void CtxEndProcessTree_Click(object sender, RoutedEventArgs e)
        {
            await TryEndSelectedProcessTreeAsync();
        }

        private async void CtxRestartTask_Click(object sender, RoutedEventArgs e)
        {
            await TryRestartSelectedProcessAsync();
        }

        private async void LstFrozenApps_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete || e.Key == Key.Enter)
            {
                e.Handled = true;
                if (lstFrozenApps.SelectedItem is ProcessItem item)
                {
                    int savedIndex = lstProcesses.SelectedIndex;
                    var (success, msg) = await _processService.KillProcessTreeAsync(item.Pid, item.DisplayName);
                    _speechService.Speak(msg, interrupt: true);
                    txtAnnouncement.Text = msg;
                    await RefreshProcessesAsync(isFullReset: true, preferredIndex: savedIndex);
                }
            }
        }

        private async void LstFrozenApps_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstFrozenApps.SelectedItem is ProcessItem item)
            {
                int savedIndex = lstProcesses.SelectedIndex;
                var (success, msg) = await _processService.KillProcessTreeAsync(item.Pid, item.DisplayName);
                _speechService.Speak(msg, interrupt: true);
                txtAnnouncement.Text = msg;
                await RefreshProcessesAsync(isFullReset: true, preferredIndex: savedIndex);
            }
        }

        private async void CtxKillFrozen_Click(object sender, RoutedEventArgs e)
        {
            if (lstFrozenApps.SelectedItem is ProcessItem item)
            {
                int savedIndex = lstProcesses.SelectedIndex;
                var (success, msg) = await _processService.KillProcessTreeAsync(item.Pid, item.DisplayName);
                _speechService.Speak(msg, interrupt: true);
                txtAnnouncement.Text = msg;
                await RefreshProcessesAsync(isFullReset: true, preferredIndex: savedIndex);
            }
        }

        private void SetSort(string sortBy)
        {
            _currentSort = sortBy;
            tabProcesses.IsSelected = true;
            _ = RefreshProcessesAsync(isFullReset: true);
            _speechService.Speak($"Sorted by {sortBy}.", interrupt: true);
            txtAnnouncement.Text = $"Sorted by {sortBy}.";

            if (_settingsService.CurrentSettings.RememberSortFilter)
            {
                _settingsService.CurrentSettings.SortBy = sortBy;
                _settingsService.Save();
            }
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (tabProcesses.IsSelected)
            {
                _ = RefreshProcessesAsync(isFullReset: true);
            }
        }

        private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down)
            {
                e.Handled = true;
                lstProcesses.Focus();
                if (_processItems.Count > 0 && lstProcesses.SelectedIndex < 0)
                {
                    lstProcesses.SelectedIndex = 0;
                }
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                txtSearch.Text = string.Empty;
                lstProcesses.Focus();
            }
        }

        private void BtnSortMem_Click(object sender, RoutedEventArgs e) => SetSort("Memory");
        private void BtnSortCpu_Click(object sender, RoutedEventArgs e) => SetSort("CPU");
        private void BtnSortName_Click(object sender, RoutedEventArgs e) => SetSort("Name");
        private void BtnToggleGroup_Click(object sender, RoutedEventArgs e) => ToggleProcessGrouping();

        private void ToggleProcessGrouping()
        {
            var s = _settingsService.CurrentSettings;
            s.GroupProcesses = !s.GroupProcesses;
            _settingsService.Save();

            UpdateGroupToggleButtonText();

            string status = s.GroupProcesses ? "Process grouping enabled." : "Process grouping disabled.";
            _speechService.Speak(status, interrupt: true);
            txtAnnouncement.Text = status;

            _ = RefreshProcessesAsync(isFullReset: true);
        }

        private void UpdateGroupToggleButtonText()
        {
            if (btnToggleGroup != null)
            {
                bool grouped = _settingsService.CurrentSettings.GroupProcesses;
                btnToggleGroup.Content = grouped ? "Group: On (Ctrl+G)" : "Group: Off (Ctrl+G)";
            }
        }

        #endregion
    }
}
