using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using AccessibleTaskManager.Helpers;
using AccessibleTaskManager.Models;
using AccessibleTaskManager.Services;
using AccessibleTaskManager.Views;

namespace AccessibleTaskManager
{
    public partial class MainWindow : Window
    {
        private readonly IScreenReaderService _speechService;
        private readonly ISystemMonitorService _monitorService;
        private readonly IProcessService _processService;
        private readonly ISettingsService _settingsService;
        private readonly IDataUsageService _dataUsageService;
        private readonly IThemeService _themeService;
        private readonly IHardwareDetailService _hardwareDetailService;
        private HotkeyService? _hotkeyService;

        private readonly DispatcherTimer _refreshTimer;
        private NativeTrayIcon? _trayIcon;
        private bool _isExplicitExit = false;

        private readonly ObservableCollection<ResourceItem> _resourceItems = new();
        private readonly ObservableCollection<ProcessItem> _processItems = new();
        private readonly ObservableCollection<AppDataUsageItem> _dataUsageItems = new();
        private readonly Dictionary<string, ResourceItem> _resourceMap = new();
        private readonly Dictionary<string, DateTime> _lastAlertTimes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _expandedGroups = new(StringComparer.OrdinalIgnoreCase);

        private string _currentSort = "Memory";
        private bool _isRefreshing = false;
        private bool _isUpdatingUI = true;
        private int _timerTickCount = 0;

        public MainWindow()
        {
            InitializeComponent();

            _speechService = new ScreenReaderService();
            _monitorService = new SystemMonitorService();
            _processService = new ProcessService();
            _settingsService = new SettingsService();
            _dataUsageService = new DataUsageService();
            _themeService = new ThemeService();
            _hardwareDetailService = new HardwareDetailService();

            _refreshTimer = new DispatcherTimer();
            _refreshTimer.Tick += async (s, e) => await OnTimerTickAsync();

            lstResources.ItemsSource = _resourceItems;
            lstProcesses.ItemsSource = _processItems;
            lstDataUsage.ItemsSource = _dataUsageItems;
        }

        private bool _isInitialized = false;

        public async void InitializeApp(bool startMinimized)
        {
            if (_isInitialized) return;
            _isInitialized = true;

            // Ensure Win32 window HWND is created even if starting minimized/hidden
            var helper = new WindowInteropHelper(this);
            IntPtr hwnd = helper.EnsureHandle();

            InitTrayIcon(hwnd);
            InitHotkey(hwnd);

            _themeService.ApplyTheme(_settingsService.CurrentSettings.Theme);
            LoadSettingsIntoUI();
            ApplySettingsToServices();

            // Build initial resource items
            BuildResourceItemList();

            if (startMinimized)
            {
                WindowState = WindowState.Minimized;
                Visibility = Visibility.Hidden;
            }
            else
            {
                Show();
                FocusCurrentTabContent();
            }

            await RefreshAllAsync(announce: false);
            TrimProcessMemory();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_isInitialized)
            {
                string[] args = Environment.GetCommandLineArgs();
                bool startMinimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
                InitializeApp(startMinimized);
            }
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _settingsService.CurrentSettings.MinimizeToTray)
            {
                Hide();
                TrimProcessMemory();
                _speechService.Speak("Resource Analyzer for Windows minimized to system tray. Press Control Windows I anytime to hear network speed.", interrupt: true);
            }
        }

        private void InitHotkey(IntPtr hwnd)
        {
            try
            {
                _hotkeyService?.Dispose();
                _hotkeyService = new HotkeyService(OnNetworkSpeedHotkeyTriggered, OnToggleAppHotkeyTriggered);
                _hotkeyService.Register(hwnd);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to register global hotkey: {ex.Message}");
            }
        }

        private void OnToggleAppHotkeyTriggered()
        {
            if (Visibility == Visibility.Visible && WindowState != WindowState.Minimized && IsActive)
            {
                if (_settingsService.CurrentSettings.MinimizeToTray)
                {
                    WindowState = WindowState.Minimized;
                    Hide();
                    TrimProcessMemory();
                    _speechService.Speak("Resource Analyzer for Windows minimized.", interrupt: true);
                }
                else
                {
                    WindowState = WindowState.Minimized;
                }
            }
            else
            {
                RestoreFromTray();
            }
        }

        private async void OnNetworkSpeedHotkeyTriggered()
        {
            string speedSummary = await _monitorService.GetQuickNetworkSpeedSummaryAsync();
            _speechService.Speak(speedSummary, interrupt: true);
            txtAnnouncement.Text = speedSummary;
        }

        private void InitTrayIcon(IntPtr hwnd)
        {
            try
            {
                _trayIcon?.Dispose();
                _trayIcon = new NativeTrayIcon(hwnd, RestoreFromTray, OnNetworkSpeedHotkeyTriggered, ExitApplication);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Tray icon init failed: {ex.Message}");
            }
        }

        public static void TrimProcessMemory()
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Forced, false);
                NativeMethods.EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            }
            catch { }
        }

        public void RestoreFromTray()
        {
            Show();
            Visibility = Visibility.Visible;
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            var helper = new WindowInteropHelper(this);
            IntPtr hwnd = helper.Handle;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(hwnd);
                NativeMethods.SwitchToThisWindow(hwnd, true);
            }

            Activate();
            Topmost = true;
            Topmost = false;

            FocusCurrentTabContent();

            _speechService.Speak("Resource Analyzer for Windows restored.", interrupt: true);
            _ = RefreshAllAsync(announce: false);
        }

        private void FocusCurrentTabContent()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (tabResources.IsSelected)
                {
                    FocusListBoxItem(lstResources);
                }
                else if (tabProcesses.IsSelected)
                {
                    if (!string.IsNullOrEmpty(txtSearch.Text))
                    {
                        txtSearch.Focus();
                        txtSearch.SelectAll();
                    }
                    else
                    {
                        FocusListBoxItem(lstProcesses);
                    }
                }
                else if (tabDataUsage.IsSelected)
                {
                    if (!string.IsNullOrEmpty(txtDataSearch.Text))
                    {
                        txtDataSearch.Focus();
                        txtDataSearch.SelectAll();
                    }
                    else
                    {
                        FocusListBoxItem(lstDataUsage);
                    }
                }
                else if (tabSettings.IsSelected)
                {
                    cmbProcessManager?.Focus();
                }
            }));
        }

        private void FocusListBoxItem(System.Windows.Controls.ListBox listBox)
        {
            if (listBox == null) return;
            listBox.Focus();

            if (listBox.Items.Count > 0)
            {
                if (listBox.SelectedIndex < 0) listBox.SelectedIndex = 0;
                listBox.ScrollIntoView(listBox.SelectedItem);

                var container = listBox.ItemContainerGenerator.ContainerFromItem(listBox.SelectedItem) as ListBoxItem;
                if (container != null)
                {
                    container.Focus();
                    Keyboard.Focus(container);
                }
                else
                {
                    Keyboard.Focus(listBox);
                }
            }
            else
            {
                Keyboard.Focus(listBox);
            }
        }

        private void ExitApplication()
        {
            _isExplicitExit = true;
            _trayIcon?.Dispose();
            _hotkeyService?.Dispose();
            _themeService?.Dispose();
            Close();
            System.Windows.Application.Current.Shutdown();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExplicitExit && _settingsService.CurrentSettings.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                TrimProcessMemory();
                _speechService.Speak("Resource Analyzer for Windows minimized to system tray. Press Control Windows I anytime to hear network speed.", interrupt: true);
            }
            else
            {
                _trayIcon?.Dispose();
                _hotkeyService?.Dispose();
            }
        }

        #region Resource List Management

        private void BuildResourceItemList()
        {
            _resourceItems.Clear();
            _resourceMap.Clear();

            var settings = _settingsService.CurrentSettings;

            if (settings.ShowCpu) AddResourceItem("cpu", "CPU");
            if (settings.ShowRam) AddResourceItem("ram", "RAM");
            if (settings.ShowGpu) AddResourceItem("gpu", "GPU");
            if (settings.ShowNetwork) AddResourceItem("network", "Network");
            if (settings.ShowDisk) AddResourceItem("disk", "Disk");
            if (settings.ShowBattery && _monitorService.HasBattery) AddResourceItem("battery", "Battery");

            if (_resourceItems.Count > 0 && lstResources.SelectedIndex < 0)
            {
                lstResources.SelectedIndex = 0;
            }
        }

        private void AddResourceItem(string id, string name)
        {
            var item = new ResourceItem
            {
                Id = id,
                Name = name,
                Summary = $"{name}: Loading..."
            };
            _resourceItems.Add(item);
            _resourceMap[id] = item;
        }

        private async Task RefreshResourcesAsync()
        {
            try
            {
                var metrics = await _monitorService.SampleMetricsAsync();

                foreach (var kvp in metrics)
                {
                    if (_resourceMap.TryGetValue(kvp.Key, out var item))
                    {
                        item.Summary = kvp.Value.Summary;
                        item.Details = kvp.Value.Details;
                        item.Percent = kvp.Value.Percent;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error sampling resources: {ex.Message}");
            }
        }

        #endregion

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

                    var header = new ProcessItem
                    {
                        Pid = 0,
                        Name = appName,
                        IsGroupHeader = true,
                        IsGroupChild = false,
                        IsExpanded = isExpanded,
                        GroupName = appName,
                        GroupChildPids = pids,
                        MemoryBytes = totalMemory,
                        CpuPercent = totalCpu,
                        InstanceIndex = 1,
                        InstanceTotal = count
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

                // Check resource overuse alerts on raw processes
                CheckResourceAlerts(rawList);

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
                            item.UpdateMetrics(fresh.MemoryBytes, fresh.CpuPercent, fresh.InstanceIndex, fresh.InstanceTotal, fresh.IsExpanded);
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

                        // Restore selection cleanly
                        if (!string.IsNullOrEmpty(prevSelectedKey))
                        {
                            var match = _processItems.FirstOrDefault(p => string.Equals(p.ItemKey, prevSelectedKey, StringComparison.OrdinalIgnoreCase));
                            if (match != null)
                            {
                                lstProcesses.SelectedItem = match;
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

            foreach (var p in list)
            {
                // Check RAM
                if (checkRam && p.MemoryBytes > ramLimitBytes)
                {
                    string ramKey = $"RAM_{p.Name}";
                    if (!_lastAlertTimes.TryGetValue(ramKey, out var lastTime) || (now - lastTime).TotalSeconds >= 60)
                    {
                        _lastAlertTimes[ramKey] = now;
                        string limitStr = $"{s.HighRamLimitValue} {s.HighRamLimitUnit}";
                        string usedStr = FormatHelper.FormatBytes(p.MemoryBytes);
                        string msg = $"{p.DisplayName} is exceeding maximum RAM usage ({usedStr}, limit: {limitStr}).";

                        _trayIcon?.ShowBalloonTip("High RAM Usage Alert", msg);
                        _speechService.Speak(msg, interrupt: false);
                        txtAnnouncement.Text = msg;
                    }
                }

                // Check CPU
                if (checkCpu && p.CpuPercent >= cpuLimitPercent)
                {
                    string cpuKey = $"CPU_{p.Name}";
                    if (!_lastAlertTimes.TryGetValue(cpuKey, out var lastTime) || (now - lastTime).TotalSeconds >= 60)
                    {
                        _lastAlertTimes[cpuKey] = now;
                        string msg = $"{p.DisplayName} is exceeding maximum CPU usage ({p.CpuPercent:F1}%, limit: {cpuLimitPercent:F0}%).";

                        _trayIcon?.ShowBalloonTip("High CPU Usage Alert", msg);
                        _speechService.Speak(msg, interrupt: false);
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

        #endregion

        #region Data Usage Management

        private async Task RefreshDataUsageAsync(bool announce = false)
        {
            try
            {
                bool currentOnly = cmbDataNetwork.SelectedIndex != 1; // 0 = Current (Default), 1 = All Networks
                string timeRange = cmbDataTimeRange.SelectedIndex switch
                {
                    0 => "Full",
                    1 => "Last Month",
                    2 => "Last Week",
                    3 => "Last 24 Hours",
                    4 => "Today",
                    _ => "Full"
                };

                string search = txtDataSearch.Text;
                var (apps, totalRecv, totalSent) = await _dataUsageService.GetDataUsageAsync(timeRange, currentOnly, search);

                long grandTotal = totalRecv + totalSent;
                string summary = $"Showing {apps.Count} applications. Total: {FormatHelper.FormatBytes(grandTotal)} (Down: {FormatHelper.FormatBytes(totalRecv)}, Up: {FormatHelper.FormatBytes(totalSent)}).";
                txtDataSummary.Text = summary;

                _dataUsageItems.Clear();
                foreach (var a in apps)
                {
                    _dataUsageItems.Add(a);
                }

                if (_dataUsageItems.Count > 0 && lstDataUsage.SelectedIndex < 0)
                {
                    lstDataUsage.SelectedIndex = 0;
                }

                if (announce)
                {
                    _speechService.Speak(summary, interrupt: true);
                    txtAnnouncement.Text = summary;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error querying data usage: {ex.Message}");
                txtDataSummary.Text = "Unable to load data usage on this connection.";
            }
        }

        private void CmbDataFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            _ = RefreshDataUsageAsync(announce: false);
            SaveSettingsFromUI(silent: true);
        }

        private void TxtDataSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (tabDataUsage.IsSelected)
            {
                _ = RefreshDataUsageAsync(announce: false);
            }
        }

        private void TxtDataSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down)
            {
                e.Handled = true;
                lstDataUsage.Focus();
                if (_dataUsageItems.Count > 0 && lstDataUsage.SelectedIndex < 0)
                {
                    lstDataUsage.SelectedIndex = 0;
                }
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                txtDataSearch.Text = string.Empty;
                lstDataUsage.Focus();
            }
        }

        private void LstDataUsage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                _ = RefreshDataUsageAsync(announce: true);
            }
        }

        #endregion

        #region Refresh & Keyboard Handlers

        private async Task RefreshAllAsync(bool announce = false)
        {
            if (_isRefreshing) return;
            _isRefreshing = true;

            try
            {
                if (tabResources.IsSelected)
                {
                    await RefreshResourcesAsync();
                }
                else if (tabProcesses.IsSelected)
                {
                    await RefreshProcessesAsync(isFullReset: true);
                }
                else if (tabDataUsage.IsSelected)
                {
                    await RefreshDataUsageAsync(announce: false);
                }

                if (announce)
                {
                    _speechService.Speak("Refreshed.", interrupt: true);
                    txtAnnouncement.Text = "Refreshed.";
                }
            }
            finally
            {
                _isRefreshing = false;
            }
        }

        private async Task OnTimerTickAsync()
        {
            // Always sample network throughput in background (costs < 0.05ms) so live speed hotkey and Tab 1 stay current
            _monitorService.SampleNetwork();

            // When minimized to tray or hidden, sleep quietly without background CPU work
            if (Visibility != Visibility.Visible || WindowState == WindowState.Minimized)
            {
                var s = _settingsService.CurrentSettings;
                bool alertsEnabled = (s.EnableHighRamAlert && s.HighRamLimitValue > 0) || (s.EnableHighCpuAlert && s.HighCpuLimitPercent > 0);

                // Only scan processes while minimized if user enabled high usage alerts (throttled to once every 10 seconds)
                if (alertsEnabled && ++_timerTickCount % 5 == 0)
                {
                    try
                    {
                        bool hideSystem = s.HideSystemProcesses;
                        var list = await _processService.GetProcessesAsync(string.Empty, _currentSort, hideSystem);
                        CheckResourceAlerts(list);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Background process scan error: {ex.Message}");
                    }
                }
                return;
            }

            // Silent background refresh for active tab only
            if (tabResources.IsSelected)
            {
                await RefreshResourcesAsync();
            }
            else if (tabProcesses.IsSelected)
            {
                await RefreshProcessesAsync(isFullReset: false);
            }

            // Periodic memory trimming (every 10 seconds / 5 ticks) to keep working set tightly bounded (~25-35 MB)
            if (++_timerTickCount % 5 == 0)
            {
                TrimProcessMemory();
            }
        }

        private void LstResources_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ShowSelectedResourceDetail();
            }
            else if (e.Key == Key.F5)
            {
                e.Handled = true;
                _ = RefreshAllAsync(announce: true);
            }
        }

        private void LstResources_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ShowSelectedResourceDetail();
        }

        private async void ShowSelectedResourceDetail()
        {
            if (lstResources.SelectedItem is ResourceItem item)
            {
                string details = await _hardwareDetailService.GetHardwareDetailsAsync(item.Id);
                var dlg = new ResourceDetailDialog(item.Name, details)
                {
                    Owner = this
                };
                dlg.ShowDialog();
            }
        }

        private async void LstProcesses_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                e.Handled = true;
                await TryEndSelectedProcessAsync();
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
                    System.Windows.Clipboard.SetText(item.DisplayText);
                    _speechService.Speak($"Copied {item.Name} details to clipboard.", interrupt: true);
                    txtAnnouncement.Text = $"Copied {item.Name} details to clipboard.";
                }
                catch { }
            }
        }

        private void ShowShortcutsDialog()
        {
            var dlg = new KeyboardShortcutsDialog
            {
                Owner = this
            };
            dlg.ShowDialog();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // F1 or Shift + / (?) opens Keyboard Shortcuts Help
            if (e.Key == Key.F1 || ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift && (e.Key == Key.OemQuestion || e.Key == Key.Divide)))
            {
                e.Handled = true;
                ShowShortcutsDialog();
                return;
            }

            // Global in-app hotkeys
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.D1 || e.Key == Key.NumPad1)
                {
                    e.Handled = true;
                    tabResources.IsSelected = true;
                    _speechService.Speak("Tab 1: Resources", interrupt: true);
                    FocusCurrentTabContent();
                    return;
                }
                else if (e.Key == Key.D2 || e.Key == Key.NumPad2)
                {
                    e.Handled = true;
                    tabProcesses.IsSelected = true;
                    _speechService.Speak("Tab 2: Processes", interrupt: true);
                    FocusCurrentTabContent();
                    return;
                }
                else if (e.Key == Key.D3 || e.Key == Key.NumPad3)
                {
                    e.Handled = true;
                    tabDataUsage.IsSelected = true;
                    _speechService.Speak("Tab 3: Data Usage", interrupt: true);
                    FocusCurrentTabContent();
                    return;
                }
                else if (e.Key == Key.D4 || e.Key == Key.NumPad4)
                {
                    e.Handled = true;
                    tabSettings.IsSelected = true;
                    _speechService.Speak("Tab 4: Settings", interrupt: true);
                    cmbProcessManager?.Focus();
                    return;
                }
                else if (e.Key == Key.F)
                {
                    e.Handled = true;
                    if (tabDataUsage.IsSelected)
                    {
                        txtDataSearch.Focus();
                        txtDataSearch.SelectAll();
                        _speechService.Speak("Search data usage applications.", interrupt: true);
                    }
                    else
                    {
                        tabProcesses.IsSelected = true;
                        txtSearch.Focus();
                        txtSearch.SelectAll();
                        _speechService.Speak("Search processes. Type filter text.", interrupt: true);
                    }
                    return;
                }
                else if (e.Key == Key.M)
                {
                    e.Handled = true;
                    SetSort("Memory");
                    return;
                }
                else if (e.Key == Key.C)
                {
                    e.Handled = true;
                    SetSort("CPU");
                    return;
                }
                else if (e.Key == Key.N)
                {
                    e.Handled = true;
                    SetSort("Name");
                    return;
                }
                else if (e.Key == Key.G)
                {
                    e.Handled = true;
                    ToggleProcessGrouping();
                    return;
                }
            }
            else if (e.Key == Key.F5)
            {
                e.Handled = true;
                _ = RefreshAllAsync(announce: true);
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

        private async void TabMain_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source is not System.Windows.Controls.TabControl) return;

            if (tabResources.IsSelected)
            {
                await RefreshResourcesAsync();
                lstResources.Focus();
            }
            else if (tabProcesses.IsSelected)
            {
                await RefreshProcessesAsync(isFullReset: true);
                if (string.IsNullOrEmpty(txtSearch.Text))
                {
                    lstProcesses.Focus();
                }
            }
            else if (tabDataUsage.IsSelected)
            {
                await RefreshDataUsageAsync();
                if (string.IsNullOrEmpty(txtDataSearch.Text))
                {
                    lstDataUsage.Focus();
                }
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
            if (cmbProcessGrouping != null)
            {
                cmbProcessGrouping.SelectedIndex = s.GroupProcesses ? 0 : 1;
            }

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

        private void CmbProcessGrouping_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            if (cmbProcessGrouping.SelectedIndex < 0) return;

            var s = _settingsService.CurrentSettings;
            s.GroupProcesses = cmbProcessGrouping.SelectedIndex == 0;
            _settingsService.Save();

            UpdateGroupToggleButtonText();
            if (tabProcesses.IsSelected)
            {
                _ = RefreshProcessesAsync(isFullReset: true);
            }
        }

        #endregion

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

                // Process Grouping Combo
                if (cmbProcessGrouping != null)
                {
                    cmbProcessGrouping.SelectedIndex = s.GroupProcesses ? 0 : 1;
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
                chkShowNetwork.IsChecked = s.ShowNetwork;
                chkShowDisk.IsChecked = s.ShowDisk;
                chkShowBattery.IsChecked = s.ShowBattery;

                if (s.ShowCpu && s.ShowRam && s.ShowGpu && s.ShowNetwork && s.ShowDisk && s.ShowBattery)
                {
                    cmbVisibleResources.SelectedIndex = 0; // All Resources
                }
                else if (s.ShowCpu && s.ShowRam && !s.ShowGpu && s.ShowNetwork && s.ShowDisk && !s.ShowBattery)
                {
                    cmbVisibleResources.SelectedIndex = 1; // Essential Resources
                }
                else if (s.ShowCpu && s.ShowRam && !s.ShowGpu && !s.ShowNetwork && !s.ShowDisk && !s.ShowBattery)
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

        private void ApplySettingsToServices()
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
            ApplySettingsToServices();
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
            ApplySettingsToServices();
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

            // Process Grouping
            if (cmbProcessGrouping != null && cmbProcessGrouping.SelectedIndex >= 0)
            {
                s.GroupProcesses = cmbProcessGrouping.SelectedIndex == 0;
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
                    s.ShowNetwork = chkShowNetwork.IsChecked == true;
                    s.ShowDisk = chkShowDisk.IsChecked == true;
                    s.ShowBattery = chkShowBattery.IsChecked == true;
                }
                else
                {
                    switch (cmbVisibleResources.SelectedIndex)
                    {
                        case 0:
                            s.ShowCpu = true; s.ShowRam = true; s.ShowGpu = true; s.ShowNetwork = true; s.ShowDisk = true; s.ShowBattery = true;
                            break;
                        case 1:
                            s.ShowCpu = true; s.ShowRam = true; s.ShowGpu = false; s.ShowNetwork = true; s.ShowDisk = true; s.ShowBattery = false;
                            break;
                        case 2:
                            s.ShowCpu = true; s.ShowRam = true; s.ShowGpu = false; s.ShowNetwork = false; s.ShowDisk = false; s.ShowBattery = false;
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

            _settingsService.Save();

            if (!silent)
            {
                _speechService.Speak("Settings saved.", interrupt: true);
                txtAnnouncement.Text = "Settings saved.";
            }
        }

        #endregion
    }

    /// <summary>
    /// A visual progress bar that completely suppresses UI Automation peer creation.
    /// This prevents screen readers (like NVDA) from treating real-time metric updates
    /// as background progress operations and playing unwanted audio beeps, while preserving
    /// the visual progress bar for sighted users.
    /// </summary>
    public class SilentProgressBar : System.Windows.Controls.ProgressBar
    {
        protected override System.Windows.Automation.Peers.AutomationPeer? OnCreateAutomationPeer()
        {
            return null;
        }
    }

}