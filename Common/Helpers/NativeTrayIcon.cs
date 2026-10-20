using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ResourceAnalyzer.Helpers
{
    /// <summary>
    /// Pure Win32 system tray icon using Shell_NotifyIcon.
    /// Completely eliminates any dependency on System.Windows.Forms,
    /// saving ~25-35 MB of packaged runtime DLLs.
    /// </summary>
    internal class NativeTrayIcon : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersionOrTimeout;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;
        private const uint NIM_SETFOCUS = 0x00000003;
        private const uint NIM_SETVERSION = 0x00000004;

        private const uint NOTIFYICON_VERSION_4 = 4;

        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;
        private const uint NIF_INFO = 0x00000010;

        private const uint NIIF_WARNING = 0x00000002;

        private const int WM_USER = 0x0400;
        public const int WM_TRAYICON = WM_USER + 101;
        public const string TrayTooltipText = "Resource Analyzer for Windows (Ctrl+Win+I for Network Speed)";

        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_CONTEXTMENU = 0x007B;

        // Modern Shell V4 notifications for accessible keyboard / screen-reader activation
        private const int NIN_SELECT = WM_USER + 0;
        private const int NIN_KEYSELECT = WM_USER + 1;

        private const uint MF_STRING = 0x00000000;
        private const uint MF_SEPARATOR = 0x00000800;
        private const uint TPM_LEFTALIGN = 0x0000;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_BOTTOMALIGN = 0x0020;
        private const uint TPM_RETURNCMD = 0x0100;

        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_POPUP = 0x80000000;
        private const int SW_SHOW = 5;

        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x0010;

        private const int SM_CXSMICON = 49;
        private const int SM_CYSMICON = 50;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string szFileName, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lpTPMParams);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        private static readonly uint WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");

        private readonly IntPtr _hwnd;
        private IntPtr _hIcon = IntPtr.Zero;
        private bool _isCreated = false;
        private bool _isMenuOpen = false;
        private readonly Action _onRestore;
        private readonly Action _onSpeakSpeed;
        private readonly Action _onExit;
        private HwndSource? _hwndSource;

        public bool IsCreated => _isCreated;
        public IntPtr HIcon => _hIcon;

        public NativeTrayIcon(IntPtr hwnd, Action onRestore, Action onSpeakSpeed, Action onExit)
        {
            _hwnd = hwnd;
            _onRestore = onRestore;
            _onSpeakSpeed = onSpeakSpeed;
            _onExit = onExit;

            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);

            InitIcon();
        }

        public void EnsureCreated()
        {
            if (!_isCreated)
            {
                InitIcon();
            }
            else
            {
                EnsureHiddenInNotificationArea();
            }
        }

        private void InitIcon()
        {
            try
            {
                EnsureIconLoaded();

                var nid = CreateNotifyIconData();
                nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
                nid.uCallbackMessage = (uint)WM_TRAYICON;
                nid.hIcon = _hIcon;
                nid.szTip = TrayTooltipText;

                // Attempt NIM_ADD first
                _isCreated = Shell_NotifyIcon(NIM_ADD, ref nid);

                // If adding failed (e.g. icon already registered from prior instance or taskbar reload), try NIM_MODIFY
                if (!_isCreated)
                {
                    _isCreated = Shell_NotifyIcon(NIM_MODIFY, ref nid);
                }

                if (_isCreated)
                {
                    // Promote to modern V4 shell behavior (accurate keyboard & mouse events)
                    var verNid = CreateNotifyIconData();
                    verNid.uVersionOrTimeout = NOTIFYICON_VERSION_4;
                    Shell_NotifyIcon(NIM_SETVERSION, ref verNid);

                    // Ensure icon resides in "Show hidden icons" chevron as standard apps do
                    EnsureHiddenInNotificationArea();
                    _ = Task.Delay(1500).ContinueWith(_ => EnsureHiddenInNotificationArea(), TaskScheduler.Default);
                }
                else
                {
                    // Retry once after 500ms in case Windows Shell/Explorer was temporarily busy during startup
                    _ = Task.Delay(500).ContinueWith(_ =>
                    {
                        try
                        {
                            var retryNid = CreateNotifyIconData();
                            retryNid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
                            retryNid.uCallbackMessage = (uint)WM_TRAYICON;
                            retryNid.hIcon = _hIcon;
                            retryNid.szTip = TrayTooltipText;
                            if (Shell_NotifyIcon(NIM_ADD, ref retryNid) || Shell_NotifyIcon(NIM_MODIFY, ref retryNid))
                            {
                                _isCreated = true;
                                var retryVerNid = CreateNotifyIconData();
                                retryVerNid.uVersionOrTimeout = NOTIFYICON_VERSION_4;
                                Shell_NotifyIcon(NIM_SETVERSION, ref retryVerNid);
                                EnsureHiddenInNotificationArea();
                            }
                        }
                        catch { }
                    }, TaskScheduler.Default);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"NativeTrayIcon init failed: {ex.Message}");
            }
        }

        private void EnsureIconLoaded()
        {
            if (_hIcon != IntPtr.Zero) return;

            try
            {
                int cx = GetSystemMetrics(SM_CXSMICON);
                int cy = GetSystemMetrics(SM_CYSMICON);
                if (cx <= 0) cx = 16;
                if (cy <= 0) cy = 16;

                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "app.ico");
                if (File.Exists(iconPath))
                {
                    _hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, cx, cy, LR_LOADFROMFILE);
                }

                if (_hIcon == IntPtr.Zero)
                {
                    string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        ExtractIconEx(exePath, 0, out _, out _hIcon, 1);
                    }
                }

                if (_hIcon == IntPtr.Zero)
                {
                    _hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
                }
            }
            catch { }
        }

        private NOTIFYICONDATA CreateNotifyIconData()
        {
            var nid = new NOTIFYICONDATA();
            nid.cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>();
            nid.hWnd = _hwnd;
            nid.uID = 1001;
            return nid;
        }

        public static void EnsureHiddenInNotificationArea()
        {
            try
            {
                string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath)) return;

                using var rootKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
                if (rootKey == null) return;

                foreach (string subKeyName in rootKey.GetSubKeyNames())
                {
                    using var subKey = rootKey.OpenSubKey(subKeyName, writable: true);
                    if (subKey != null)
                    {
                        object? val = subKey.GetValue("ExecutablePath");
                        if (val is string regPath && string.Equals(regPath, exePath, StringComparison.OrdinalIgnoreCase))
                        {
                            subKey.SetValue("IsPromoted", 0, RegistryValueKind.DWord);
                            break;
                        }
                    }
                }
            }
            catch
            {
                // Non-critical; fallback to standard Windows placement
            }
        }

        public void ShowBalloonTip(string title, string message)
        {
            if (!_isCreated || _hwnd == IntPtr.Zero) return;
            try
            {
                var nid = CreateNotifyIconData();
                nid.uFlags = NIF_INFO;
                nid.szInfoTitle = title.Length > 63 ? title.Substring(0, 63) : title;
                nid.szInfo = message.Length > 255 ? message.Substring(0, 255) : message;
                nid.dwInfoFlags = NIIF_WARNING;

                Shell_NotifyIcon(NIM_MODIFY, ref nid);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ShowBalloonTip failed: {ex.Message}");
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_TRAYICON)
            {
                // In Shell V4 (uVersion = 4), LOWORD(lParam) contains the notification event code
                // and HIWORD(lParam) contains the icon ID (uID). In legacy mode, lParam is the event code.
                // Masking with 0xFFFF extracts the event code across both modes and architectures.
                int eventType = (int)(lParam.ToInt64() & 0xFFFF);
                if (eventType == WM_LBUTTONDBLCLK || eventType == WM_LBUTTONUP ||
                    eventType == NIN_SELECT || eventType == NIN_KEYSELECT)
                {
                    _onRestore();
                    handled = true;
                }
                else if (eventType == WM_CONTEXTMENU)
                {
                    ShowContextMenu(wParam);
                    handled = true;
                }
            }
            else if (WM_TASKBARCREATED != 0 && (uint)msg == WM_TASKBARCREATED)
            {
                InitIcon();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void ShowContextMenu(IntPtr wParam)
        {
            if (_isMenuOpen) return;
            _isMenuOpen = true;

            try
            {
                IntPtr hMenu = CreatePopupMenu();
                if (hMenu == IntPtr.Zero) return;

                AppendMenu(hMenu, MF_STRING, 1, "Quit");

                int x = 0;
                int y = 0;

                // In Shell V4, wParam contains cursor coordinates: LOWORD = X, HIWORD = Y
                short wx = unchecked((short)(long)wParam);
                short wy = unchecked((short)((long)wParam >> 16));

                if (wx != 0 && wy != 0 && wx != -1 && wy != -1)
                {
                    x = wx;
                    y = wy;
                }
                else
                {
                    GetCursorPos(out POINT pt);
                    x = pt.X;
                    y = pt.Y;
                }

                // When MainWindow is hidden/minimized to the tray, calling SetForegroundWindow(_hwnd)
                // fails or is restricted by Windows because _hwnd is not visible. This leaves Windows
                // Explorer's notification area holding foreground focus, causing screen readers (NVDA/JAWS)
                // to repeat the tray icon button announcement instead of shifting focus to the popup menu.
                // Creating a transient 0x0 tool window allows reliable foreground elevation and clean focus transfer.
                IntPtr hHelper = CreateWindowEx(WS_EX_TOOLWINDOW, "Static", string.Empty, WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                IntPtr menuOwner = _hwnd;

                if (hHelper != IntPtr.Zero)
                {
                    ShowWindow(hHelper, SW_SHOW);
                    SetForegroundWindow(hHelper);
                    menuOwner = hHelper;
                }
                else
                {
                    SetForegroundWindow(_hwnd);
                }

                uint cmd = TrackPopupMenuEx(hMenu, TPM_LEFTALIGN | TPM_BOTTOMALIGN | TPM_RIGHTBUTTON | TPM_RETURNCMD, x, y, menuOwner, IntPtr.Zero);
                DestroyMenu(hMenu);
                PostMessage(menuOwner, 0, IntPtr.Zero, IntPtr.Zero);

                if (hHelper != IntPtr.Zero)
                {
                    DestroyWindow(hHelper);
                }

                if (cmd == 1)
                {
                    _onExit();
                }
                else
                {
                    // Under Shell V4, when a shortcut menu is cancelled by pressing ESC
                    // or clicking outside, the shell requires NIM_SETFOCUS so the notification area regains focus.
                    var nid = CreateNotifyIconData();
                    Shell_NotifyIcon(NIM_SETFOCUS, ref nid);
                }
            }
            finally
            {
                _isMenuOpen = false;
            }
        }

        public void Dispose()
        {
            if (_isCreated && _hwnd != IntPtr.Zero)
            {
                var nid = CreateNotifyIconData();
                Shell_NotifyIcon(NIM_DELETE, ref nid);
                _isCreated = false;
            }

            _hwndSource?.RemoveHook(WndProc);
            _hwndSource = null;

            if (_hIcon != IntPtr.Zero)
            {
                DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
            }
        }
    }
}
