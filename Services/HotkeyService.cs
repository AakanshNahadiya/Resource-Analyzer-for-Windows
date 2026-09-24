using System;
using System.Windows.Interop;
using AccessibleTaskManager.Helpers;

namespace AccessibleTaskManager.Services
{
    public class HotkeyService : IDisposable
    {
        private const int HOTKEY_NETWORK_ID = 9001;
        private const int HOTKEY_TOGGLE_APP_ID = 9002;

        private IntPtr _hWnd;
        private HwndSource? _hwndSource;
        private readonly Action _onNetworkSpeedRequested;
        private readonly Action _onToggleAppRequested;
        private bool _isRegistered = false;

        public HotkeyService(Action onNetworkSpeedRequested, Action onToggleAppRequested)
        {
            _onNetworkSpeedRequested = onNetworkSpeedRequested;
            _onToggleAppRequested = onToggleAppRequested;
        }

        public void Register(IntPtr hWnd)
        {
            if (_isRegistered) return;

            _hWnd = hWnd;
            _hwndSource = HwndSource.FromHwnd(_hWnd);
            _hwndSource?.AddHook(HwndHook);

            // Register Ctrl + Win + I (Network Speed)
            NativeMethods.RegisterHotKey(_hWnd, HOTKEY_NETWORK_ID, NativeMethods.MOD_CONTROL | NativeMethods.MOD_WIN | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_I);

            // Register Ctrl + Win + T (Show / Toggle App)
            NativeMethods.RegisterHotKey(_hWnd, HOTKEY_TOGGLE_APP_ID, NativeMethods.MOD_CONTROL | NativeMethods.MOD_WIN | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_T);

            _isRegistered = true;
        }

        public void Unregister()
        {
            if (_isRegistered && _hWnd != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(_hWnd, HOTKEY_NETWORK_ID);
                NativeMethods.UnregisterHotKey(_hWnd, HOTKEY_TOGGLE_APP_ID);
                _hwndSource?.RemoveHook(HwndHook);
                _isRegistered = false;
            }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HOTKEY_NETWORK_ID)
                {
                    _onNetworkSpeedRequested?.Invoke();
                    handled = true;
                }
                else if (id == HOTKEY_TOGGLE_APP_ID)
                {
                    _onToggleAppRequested?.Invoke();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Unregister();
        }
    }
}
