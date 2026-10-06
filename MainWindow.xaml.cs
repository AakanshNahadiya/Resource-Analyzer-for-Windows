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
using System.Windows.Media;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ResourceAnalyzer.Helpers;
using ResourceAnalyzer.Models;
using ResourceAnalyzer.Services;
using ResourceAnalyzer.Views;

namespace ResourceAnalyzer
{
    public partial class MainWindow : Window
    {
        private readonly IScreenReaderService _speechService;
        private readonly ISystemMonitorService _monitorService;
        private readonly IProcessService _processService;
        private readonly ISettingsService _settingsService;
        private readonly IDataUsageService _dataUsageService;
        private readonly IBatteryUsageService _batteryUsageService;
        private readonly IThemeService _themeService;
        private readonly IHardwareDetailService _hardwareDetailService;
        private readonly IUpdateService _updateService;
        private readonly INetworkPortService _networkPortService;
        private HotkeyService? _hotkeyService;

        private readonly DispatcherTimer _refreshTimer;
        private NativeTrayIcon? _trayIcon;
        private bool _isExplicitExit = false;

        private readonly ObservableCollection<ResourceItem> _resourceItems = new();
        private readonly ObservableCollection<ProcessItem> _processItems = new();
        private readonly ObservableCollection<ProcessItem> _frozenItems = new();
        private readonly ObservableCollection<AppDataUsageItem> _dataUsageItems = new();
        private readonly Dictionary<string, AppDataUsageItem> _dataUsageMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<BatteryUsageItem> _batteryItems = new();
        private readonly ObservableCollection<AppBatteryUsageItem> _appBatteryItems = new();
        private readonly Dictionary<string, AppBatteryUsageItem> _appBatteryMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<NetworkPortItem> _networkPortItems = new();
        private readonly Dictionary<string, NetworkPortItem> _networkPortMap = new(StringComparer.OrdinalIgnoreCase);
        private List<NetworkPortItem> _rawNetworkPortList = new();
        private string _currentPortSort = "Port";
        private string? _latestUpdateUrl;
        private UpdateInfo? _latestUpdateInfo;
        private long _lastTotalDischargeMwh = 0;

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
            _batteryUsageService = new BatteryUsageService();
            _themeService = new ThemeService();
            _hardwareDetailService = new HardwareDetailService();
            _updateService = new UpdateService();
            _networkPortService = new NetworkPortService();

            _refreshTimer = new DispatcherTimer();
            _refreshTimer.Tick += async (s, e) => await OnTimerTickAsync();

            lstResources.ItemsSource = _resourceItems;
            lstProcesses.ItemsSource = _processItems;
            lstFrozenApps.ItemsSource = _frozenItems;
            lstDataUsage.ItemsSource = _dataUsageItems;
            lstBatteryUsage.ItemsSource = _batteryItems;
            lstAppBatteryUsage.ItemsSource = _appBatteryItems;
            lstNetworkPorts.ItemsSource = _networkPortItems;
        }

        private bool _isInitialized = false;

        public async void InitializeApp(bool startMinimized, int initialTabIndex = -1)
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
            ApplySettingsToRuntime();

            // Prewarm hardware static specs in background to make technical dialogs open instantly
            HardwareDetailService.PrewarmHardwareCache();

            // Prewarm today's data usage cache in background for instant hotkey response
            _ = UpdateTodayDataUsageCacheAsync();

            // Build initial resource items
            BuildResourceItemList();

            // Admin privileges status detection
            bool isAdmin = ElevationHelper.IsRunningAsAdmin();
            if (isAdmin)
            {
                Title = "Resource Analyzer for Windows (Administrator)";
                badgeAdmin.Visibility = Visibility.Visible;
                btnHeaderAdmin.Visibility = Visibility.Collapsed;
            }
            else
            {
                Title = "Resource Analyzer for Windows";
                badgeAdmin.Visibility = Visibility.Collapsed;
                btnHeaderAdmin.Visibility = Visibility.Visible;
            }

            string curVer = _updateService.GetCurrentVersion();
            string verTag = curVer.StartsWith("1.0.") ? "Beta" : "Stable";
            txtCurrentVersion.Text = $"Current Version: v{curVer} ({verTag})";

            if (initialTabIndex >= 0 && initialTabIndex < tabMain.Items.Count)
            {
                tabMain.SelectedIndex = initialTabIndex;
            }

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

            if (_settingsService.CurrentSettings.AnnounceOnStartup && !startMinimized)
            {
                if (isAdmin)
                {
                    string tabName = tabMain.SelectedItem is TabItem ti ? (ti.Header?.ToString() ?? "") : "";
                    string msg = initialTabIndex > 0
                        ? $"Resource Analyzer for Windows, running with Administrator privileges. {tabName}. Ready."
                        : "Resource Analyzer for Windows, running with Administrator privileges. Ready.";
                    _speechService.Speak(msg, interrupt: false);
                    txtAnnouncement.Text = "Running with Administrator privileges. Ready.";
                }
                else
                {
                    _speechService.Speak("Resource Analyzer for Windows. Ready.", interrupt: false);
                    txtAnnouncement.Text = "Ready.";
                }
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

                int targetTabIndex = -1;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i].Equals("--tab", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out int idx))
                    {
                        targetTabIndex = idx;
                    }
                }

                InitializeApp(startMinimized, targetTabIndex);
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

        private DateTime _lastNetworkSpeedHotkeyTime = DateTime.MinValue;
        private int _networkSpeedHotkeyTriggerId = 0;
        private long _cachedTodayDataUsageBytes = 0;
        private DateTime _lastDataUsageCacheTime = DateTime.MinValue;

        private async Task UpdateTodayDataUsageCacheAsync()
        {
            try
            {
                var (recv, sent) = await _dataUsageService.GetTodayTotalUsageAsync();
                long total = recv + sent;
                if (total > 0)
                {
                    _cachedTodayDataUsageBytes = total;
                    _lastDataUsageCacheTime = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to update today's data usage cache: {ex.Message}");
            }
        }

        private async void OnNetworkSpeedHotkeyTriggered()
        {
            var now = DateTime.UtcNow;
            double elapsedMs = (now - _lastNetworkSpeedHotkeyTime).TotalMilliseconds;
            _lastNetworkSpeedHotkeyTime = now;

            if (elapsedMs < 300)
            {
                _lastNetworkSpeedHotkeyTime = DateTime.MinValue;
                int triggerId = ++_networkSpeedHotkeyTriggerId;

                try
                {
                    string msg;
                    if (_cachedTodayDataUsageBytes > 0)
                    {
                        msg = $"Data used today: {FormatHelper.FormatBytes(_cachedTodayDataUsageBytes)}.";
                    }
                    else
                    {
                        var (inBytes, outBytes) = await _dataUsageService.GetTodayTotalUsageAsync();
                        _cachedTodayDataUsageBytes = inBytes + outBytes;
                        msg = $"Data used today: {FormatHelper.FormatBytes(_cachedTodayDataUsageBytes)}.";
                    }
                    _speechService.Speak(msg, interrupt: true);
                    txtAnnouncement.Text = msg;
                    txtStatus.Text = msg;

                    // Trigger non-blocking background refresh to keep cache fresh
                    _ = UpdateTodayDataUsageCacheAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to announce today's data: {ex.Message}");
                }
                return;
            }

            int currentTriggerId = ++_networkSpeedHotkeyTriggerId;
            string speedSummary = await _monitorService.GetQuickNetworkSpeedSummaryAsync();
            if (currentTriggerId != _networkSpeedHotkeyTriggerId)
            {
                return;
            }

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
                else if (tabBatteryUsage.IsSelected)
                {
                    if (lstAppBatteryUsage.IsKeyboardFocusWithin)
                    {
                        FocusListBoxItem(lstAppBatteryUsage);
                    }
                    else
                    {
                        FocusListBoxItem(lstBatteryUsage);
                    }
                }
                else if (tabNetworkPorts.IsSelected)
                {
                    if (!string.IsNullOrEmpty(txtPortSearch.Text))
                    {
                        txtPortSearch.Focus();
                        txtPortSearch.SelectAll();
                    }
                    else
                    {
                        FocusListBoxItem(lstNetworkPorts);
                    }
                }
                else if (tabSettings.IsSelected)
                {
                    cmbProcessManager?.Focus();
                }
            }));
        }

        private void SelectAndFocusTab(int index)
        {
            if (index >= 0 && index < tabMain.Items.Count)
            {
                tabMain.SelectedIndex = index;
                if (tabMain.Items[index] is TabItem item)
                {
                    item.IsSelected = true;
                    item.Focus();
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    {
                        item.Focus();
                    }));
                }
            }
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
                else if (tabBatteryUsage.IsSelected)
                {
                    await RefreshBatteryUsageAsync(announce: false);
                }
                else if (tabNetworkPorts.IsSelected)
                {
                    await RefreshNetworkPortsAsync(isFullReset: false, announce: false);
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
            _timerTickCount++;

            // Always sample network throughput in background (costs < 0.05ms) so live speed hotkey and Tab 1 stay current
            _monitorService.SampleNetwork();

            // Sample physical disk throughput in background so live disk read/write speeds stay current
            HardwareDetailService.SampleAllDiskSpeeds();

            // Silently refresh today's data usage cache every 15 seconds in background
            if ((DateTime.UtcNow - _lastDataUsageCacheTime).TotalSeconds >= 15)
            {
                _ = UpdateTodayDataUsageCacheAsync();
            }

            // When minimized to tray or hidden, sleep quietly without background CPU work
            if (Visibility != Visibility.Visible || WindowState == WindowState.Minimized)
            {
                var s = _settingsService.CurrentSettings;
                bool alertsEnabled = (s.EnableHighRamAlert && s.HighRamLimitValue > 0) || (s.EnableHighCpuAlert && s.HighCpuLimitPercent > 0);

                // Only scan processes while minimized if user enabled high usage alerts (throttled to once every 10 seconds)
                if (alertsEnabled && _timerTickCount % 5 == 0)
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
            else if (tabDataUsage.IsSelected)
            {
                // Throttle Tab 3 auto-scan to 15 seconds to avoid CPU waste
                int currentInterval = Math.Max(1, _settingsService.CurrentSettings.RefreshIntervalSeconds);
                int ticksPer15Sec = Math.Max(1, 15 / currentInterval);
                if (_timerTickCount % ticksPer15Sec == 0)
                {
                    await RefreshDataUsageRealtimeAsync();
                }
            }
            else if (tabBatteryUsage.IsSelected)
            {
                await RefreshAppBatteryUsageAsync();
            }
            else if (tabNetworkPorts.IsSelected)
            {
                await RefreshNetworkPortsAsync(isFullReset: false);
            }

            // Periodic background check for resource overuse alerts when not on Tab 2 or when Tab 2 is filtered
            var settings = _settingsService.CurrentSettings;
            bool overuseAlertsEnabled = (settings.EnableHighRamAlert && settings.HighRamLimitValue > 0) || (settings.EnableHighCpuAlert && settings.HighCpuLimitPercent > 0);
            if (overuseAlertsEnabled && (!tabProcesses.IsSelected || !string.IsNullOrEmpty(txtSearch.Text)))
            {
                if (_timerTickCount % 2 == 0)
                {
                    try
                    {
                        var alertList = await _processService.GetProcessesAsync(string.Empty, "Memory", settings.HideSystemProcesses);
                        CheckResourceAlerts(alertList);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Background alert scan error: {ex.Message}");
                    }
                }
            }

            // Periodic memory trimming (every 10 seconds / 5 ticks) to keep working set tightly bounded (~25-35 MB)
            if (_timerTickCount % 5 == 0)
            {
                TrimProcessMemory();
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

            // Admin elevation hotkey: Ctrl + Shift + A
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.A)
            {
                e.Handled = true;
                RestartAsAdministratorWithConfirmation();
                return;
            }

            // Windows Startup Apps settings hotkey: Ctrl + Shift + S
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S)
            {
                e.Handled = true;
                OpenWindowsStartupSettings();
                return;
            }

            // System diagnostic snapshot hotkey: Ctrl + Shift + C
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C)
            {
                e.Handled = true;
                CopySystemSnapshotToClipboard();
                return;
            }

            // Arrow key navigation across Tab headers (guarantees seamless 1 <-> 2 <-> 3 <-> 4 <-> 5 <-> 6 circular navigation even if multi-row wrapped)
            if (Keyboard.Modifiers == ModifierKeys.None)
            {
                TabItem? focusedTabItem = Keyboard.FocusedElement as TabItem;
                if (focusedTabItem == null && Keyboard.FocusedElement is DependencyObject depObj)
                {
                    DependencyObject? parent = depObj;
                    while (parent != null)
                    {
                        if (parent is TabItem ti && tabMain.Items.Contains(ti))
                        {
                            focusedTabItem = ti;
                            break;
                        }
                        if (parent is TabControl) break;
                        parent = VisualTreeHelper.GetParent(parent);
                    }
                }

                if (focusedTabItem != null && tabMain.Items.Contains(focusedTabItem))
                {
                    int currentIndex = tabMain.Items.IndexOf(focusedTabItem);
                    if (currentIndex < 0) currentIndex = tabMain.SelectedIndex;

                    if (e.Key == Key.Right)
                    {
                        e.Handled = true;
                        int next = (currentIndex + 1) % tabMain.Items.Count;
                        SelectAndFocusTab(next);
                        return;
                    }
                    else if (e.Key == Key.Left)
                    {
                        e.Handled = true;
                        int prev = (currentIndex - 1 + tabMain.Items.Count) % tabMain.Items.Count;
                        SelectAndFocusTab(prev);
                        return;
                    }
                    else if (e.Key == Key.Down)
                    {
                        e.Handled = true;
                        FocusCurrentTabContent();
                        return;
                    }
                    else if (e.Key == Key.Up)
                    {
                        // Suppress WPF TabPanel default row-jumping on Up arrow
                        e.Handled = true;
                        return;
                    }
                    else if (e.Key == Key.Home)
                    {
                        e.Handled = true;
                        SelectAndFocusTab(0);
                        return;
                    }
                    else if (e.Key == Key.End)
                    {
                        e.Handled = true;
                        SelectAndFocusTab(tabMain.Items.Count - 1);
                        return;
                    }
                }
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
                    tabBatteryUsage.IsSelected = true;
                    _speechService.Speak("Tab 4: Battery Usage", interrupt: true);
                    FocusCurrentTabContent();
                    return;
                }
                else if (e.Key == Key.D5 || e.Key == Key.NumPad5)
                {
                    e.Handled = true;
                    tabNetworkPorts.IsSelected = true;
                    _speechService.Speak("Tab 5: Network Ports", interrupt: true);
                    FocusCurrentTabContent();
                    return;
                }
                else if (e.Key == Key.D6 || e.Key == Key.NumPad6)
                {
                    e.Handled = true;
                    tabSettings.IsSelected = true;
                    _speechService.Speak("Tab 6: Settings", interrupt: true);
                    cmbProcessManager?.Focus();
                    return;
                }
                else if (e.Key == Key.F)
                {
                    e.Handled = true;
                    if (tabNetworkPorts.IsSelected)
                    {
                        txtPortSearch.Focus();
                        txtPortSearch.SelectAll();
                        _speechService.Speak("Search network ports. Type port number, process name, or IP.", interrupt: true);
                    }
                    else if (tabDataUsage.IsSelected)
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
                else if (e.Key == Key.P)
                {
                    e.Handled = true;
                    SetSort("CPU");
                    return;
                }
                else if (e.Key == Key.O)
                {
                    if (tabNetworkPorts.IsSelected)
                    {
                        e.Handled = true;
                        SetPortSort("Port");
                        return;
                    }
                }
                else if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
                {
                    if (tabNetworkPorts.IsSelected)
                    {
                        e.Handled = true;
                        SetPortSort("State");
                        return;
                    }
                }
                else if (e.Key == Key.C)
                {
                    e.Handled = true;
                    if (tabNetworkPorts.IsSelected && lstNetworkPorts.SelectedItem != null)
                    {
                        CopySelectedPortDetails();
                    }
                    else if (tabProcesses.IsSelected && lstProcesses.SelectedItem != null)
                    {
                        CtxCopyDetails_Click(sender, e);
                    }
                    else
                    {
                        SetSort("CPU");
                    }
                    return;
                }
                else if (e.Key == Key.N)
                {
                    e.Handled = true;
                    if (tabNetworkPorts.IsSelected)
                    {
                        SetPortSort("App");
                    }
                    else
                    {
                        SetSort("Name");
                    }
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



        private async void TabMain_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source is not System.Windows.Controls.TabControl) return;

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
                await RefreshDataUsageAsync();
            }
            else if (tabBatteryUsage.IsSelected)
            {
                await RefreshBatteryUsageAsync();
            }
            else if (tabNetworkPorts.IsSelected)
            {
                await RefreshNetworkPortsAsync(isFullReset: true);
            }
        }



        #endregion




    }
}