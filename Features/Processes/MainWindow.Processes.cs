using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

        private async Task RefreshProcessesAsync(bool isFullReset = false)
        {
            try
            {
                string term = txtSearch.Text;
                bool hideSystem = _settingsService.CurrentSettings.HideSystemProcesses;
                bool groupProcesses = _settingsService.CurrentSettings.GroupProcesses;
                var rawList = await _processService.GetProcessesAsync(term, _currentSort, hideSystem);

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

                        // Restore selection cleanly with focus lock
                        if (!string.IsNullOrEmpty(prevSelectedKey))
                        {
                            var match = _processItems.FirstOrDefault(p => string.Equals(p.ItemKey, prevSelectedKey, StringComparison.OrdinalIgnoreCase));
                            if (match != null)
                            {
                                if (!ReferenceEquals(lstProcesses.SelectedItem, match))
                                {
                                    lstProcesses.SelectedItem = match;
                                }
                                if (lstProcesses.IsKeyboardFocusWithin)
                                {
                                    var container = lstProcesses.ItemContainerGenerator.ContainerFromItem(match) as ListBoxItem;
                                    container?.Focus();
                                }
                            }
                        }
                        else if (_processItems.Count > 0 && lstProcesses.SelectedIndex < 0)
                        {
                            lstProcesses.SelectedIndex = 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error refreshing processes: {ex.Message}");
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

            // Group by process name so that multi-instance applications (like Chrome, Edge, etc.)
            // have their combined total resource usage evaluated against the limit.
            var appGroups = list.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var group in appGroups)
            {
                long totalRamBytes = group.Sum(p => p.MemoryBytes);
                double totalCpuPercent = group.Sum(p => p.CpuPercent);
                var first = group.First();
                int count = group.Count();
                string appTitle = count > 1 ? $"{first.DisplayName} ({count} instances)" : first.DisplayName;

                // Check RAM
                if (checkRam && totalRamBytes > ramLimitBytes)
                {
                    string ramKey = $"RAM_{group.Key}";
                    if (!_lastAlertTimes.TryGetValue(ramKey, out var lastTime) || (now - lastTime).TotalSeconds >= 60)
                    {
                        _lastAlertTimes[ramKey] = now;
                        string limitStr = $"{s.HighRamLimitValue} {s.HighRamLimitUnit}";
                        string usedStr = FormatHelper.FormatBytes(totalRamBytes);
                        string msg = $"{appTitle} is exceeding maximum RAM usage ({usedStr}, limit: {limitStr}).";

                        try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
                        _trayIcon?.ShowBalloonTip("High RAM Usage Alert", msg);
                        _speechService.Speak(msg, interrupt: true);
                        txtAnnouncement.Text = msg;
                    }
                }

                // Check CPU
                if (checkCpu && totalCpuPercent >= cpuLimitPercent)
                {
                    string cpuKey = $"CPU_{group.Key}";
                    if (!_lastAlertTimes.TryGetValue(cpuKey, out var lastTime) || (now - lastTime).TotalSeconds >= 60)
                    {
                        _lastAlertTimes[cpuKey] = now;
                        string msg = $"{appTitle} is exceeding maximum CPU usage ({totalCpuPercent:F1}%, limit: {cpuLimitPercent:F0}%).";

                        try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
                        _trayIcon?.ShowBalloonTip("High CPU Usage Alert", msg);
                        _speechService.Speak(msg, interrupt: true);
                        txtAnnouncement.Text = msg;
                    }
                }
            }
        }

        private async Task TryEndSelectedProcessAsync()
        {
            if (lstProcesses.SelectedItem is not ProcessItem item)
            {
                _speechService.Speak("No process selected.", interrupt: true);
                return;
            }

            bool confirm = _settingsService.CurrentSettings.ConfirmBeforeEndTask;

            if (item.IsGroupHeader)
            {
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
                            bool hideSys = _settingsService.CurrentSettings.HideSystemProcesses;
                            cmbProcessManager.SelectedIndex = hideSys ? 1 : 3;
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

                await RefreshProcessesAsync(isFullReset: true);
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
                        bool hideSys = _settingsService.CurrentSettings.HideSystemProcesses;
                        cmbProcessManager.SelectedIndex = hideSys ? 1 : 3;
                    }
                    _settingsService.Save();
                }
            }

            var (success, singleMsg) = await _processService.KillProcessAsync(item.Pid, item.DisplayName);
            _speechService.Speak(singleMsg, interrupt: true);
            txtAnnouncement.Text = singleMsg;

            await RefreshProcessesAsync(isFullReset: true);
        }

        private async Task TryEndSelectedProcessTreeAsync()
        {
            if (lstProcesses.SelectedItem is not ProcessItem item)
            {
                _speechService.Speak("No process selected.", interrupt: true);
                return;
            }

            bool confirm = _settingsService.CurrentSettings.ConfirmBeforeEndTask;

            if (item.IsGroupHeader)
            {
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
                            bool hideSys = _settingsService.CurrentSettings.HideSystemProcesses;
                            cmbProcessManager.SelectedIndex = hideSys ? 1 : 3;
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

                await RefreshProcessesAsync(isFullReset: true);
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
                        bool hideSys = _settingsService.CurrentSettings.HideSystemProcesses;
                        cmbProcessManager.SelectedIndex = hideSys ? 1 : 3;
                    }
                    _settingsService.Save();
                }
            }

            var (success, singleMsg) = await _processService.KillProcessTreeAsync(item.Pid, item.DisplayName);
            _speechService.Speak(singleMsg, interrupt: true);
            txtAnnouncement.Text = singleMsg;

            await RefreshProcessesAsync(isFullReset: true);
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
                    Clipboard.SetText(item.DisplayText);
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

        private async void LstFrozenApps_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete || e.Key == Key.Enter)
            {
                e.Handled = true;
                if (lstFrozenApps.SelectedItem is ProcessItem item)
                {
                    var (success, msg) = await _processService.KillProcessTreeAsync(item.Pid, item.DisplayName);
                    _speechService.Speak(msg, interrupt: true);
                    txtAnnouncement.Text = msg;
                    await RefreshProcessesAsync(isFullReset: true);
                }
            }
        }

        private async void LstFrozenApps_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstFrozenApps.SelectedItem is ProcessItem item)
            {
                var (success, msg) = await _processService.KillProcessTreeAsync(item.Pid, item.DisplayName);
                _speechService.Speak(msg, interrupt: true);
                txtAnnouncement.Text = msg;
                await RefreshProcessesAsync(isFullReset: true);
            }
        }

        private async void CtxKillFrozen_Click(object sender, RoutedEventArgs e)
        {
            if (lstFrozenApps.SelectedItem is ProcessItem item)
            {
                var (success, msg) = await _processService.KillProcessTreeAsync(item.Pid, item.DisplayName);
                _speechService.Speak(msg, interrupt: true);
                txtAnnouncement.Text = msg;
                await RefreshProcessesAsync(isFullReset: true);
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
