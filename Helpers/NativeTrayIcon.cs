using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AccessibleTaskManager.Helpers
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

        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;
        private const uint NIF_INFO = 0x00000010;

        private const uint NIIF_WARNING = 0x00000002;

        private const int WM_USER = 0x0400;
        public const int WM_TRAYICON = WM_USER + 101;

        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_CONTEXTMENU = 0x007B;

        private const uint MF_STRING = 0x00000000;
        private const uint MF_SEPARATOR = 0x00000800;
        private const uint TPM_LEFTALIGN = 0x0000;
        private const uint TPM_BOTTOMALIGN = 0x0020;
        private const uint TPM_RETURNCMD = 0x0100;

        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x0010;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

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

        private readonly IntPtr _hwnd;
        private IntPtr _hIcon = IntPtr.Zero;
        private bool _isCreated = false;
        private readonly Action _onRestore;
        private readonly Action _onSpeakSpeed;
        private readonly Action _onExit;
        private HwndSource? _hwndSource;

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

        private void InitIcon()
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "app.ico");
                if (File.Exists(iconPath))
                {
                    _hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
                }
                if (_hIcon == IntPtr.Zero)
                {
                    _hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION
                }

                var nid = new NOTIFYICONDATA();
                nid.cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>();
                nid.hWnd = _hwnd;
                nid.uID = 1001;
                nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
                nid.uCallbackMessage = (uint)WM_TRAYICON;
                nid.hIcon = _hIcon;
                nid.szTip = "Resource Analyzer for Windows (Ctrl+Win+I for Network)";

                _isCreated = Shell_NotifyIcon(NIM_ADD, ref nid);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"NativeTrayIcon init failed: {ex.Message}");
            }
        }

        public void ShowBalloonTip(string title, string message)
        {
            if (!_isCreated || _hwnd == IntPtr.Zero) return;
            try
            {
                var nid = new NOTIFYICONDATA();
                nid.cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>();
                nid.hWnd = _hwnd;
                nid.uID = 1001;
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
                int eventType = (int)lParam;
                if (eventType == WM_LBUTTONDBLCLK || eventType == WM_LBUTTONUP)
                {
                    _onRestore();
                    handled = true;
                }
                else if (eventType == WM_RBUTTONUP || eventType == WM_CONTEXTMENU)
                {
                    ShowContextMenu();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private void ShowContextMenu()
        {
            IntPtr hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return;

            AppendMenu(hMenu, MF_STRING, 1, "Show Resource Analyzer for Windows");
            AppendMenu(hMenu, MF_SEPARATOR, 0, string.Empty);
            AppendMenu(hMenu, MF_STRING, 2, "Exit");

            GetCursorPos(out POINT pt);
            SetForegroundWindow(_hwnd);
            uint cmd = TrackPopupMenuEx(hMenu, TPM_LEFTALIGN | TPM_BOTTOMALIGN | TPM_RETURNCMD, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            DestroyMenu(hMenu);

            if (cmd == 1) _onRestore();
            else if (cmd == 2) _onExit();
        }

        public void Dispose()
        {
            if (_isCreated && _hwnd != IntPtr.Zero)
            {
                var nid = new NOTIFYICONDATA();
                nid.cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>();
                nid.hWnd = _hwnd;
                nid.uID = 1001;
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
