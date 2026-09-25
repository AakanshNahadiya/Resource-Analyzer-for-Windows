using System;
using System.Runtime.InteropServices;

namespace AccessibleTaskManager.Helpers
{
    /// <summary>
    /// Centralized Win32 Native P/Invoke methods, structs, and constants.
    /// Follows official Microsoft .NET guidelines for native interop consolidation.
    /// </summary>
    internal static class NativeMethods
    {
        #region Constants

        public const int SW_RESTORE = 9;
        public const int ASFW_ANY = -1;

        public const int SystemProcessInformation = 5;
        public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
        public const int STATUS_SUCCESS = 0;

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;
        public const uint VK_I = 0x49;
        public const uint VK_T = 0x54;

        #endregion

        #region Structs

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public static MEMORYSTATUSEX Create()
            {
                var s = new MEMORYSTATUSEX();
                s.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                return s;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_BATTERY_STATE
        {
            [MarshalAs(UnmanagedType.I1)]
            public bool AcOnLine;
            [MarshalAs(UnmanagedType.I1)]
            public bool BatteryPresent;
            [MarshalAs(UnmanagedType.I1)]
            public bool Charging;
            [MarshalAs(UnmanagedType.I1)]
            public bool Discharging;
            public byte Spare1_0;
            public byte Spare1_1;
            public byte Spare1_2;
            public byte Tag;
            public uint MaxCapacity;
            public uint RemainingCapacity;
            public int Rate; // in mW. Negative = discharging, Positive = charging
            public uint EstimatedTime;
            public uint DefaultAlert1;
            public uint DefaultAlert2;
        }

        #endregion

        #region Kernel32

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        #endregion

        #region User32

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int dwProcessId);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern void SwitchToThisWindow(IntPtr hWnd, bool fUnknown);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsHungAppWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public UIntPtr CommitTotal;
            public UIntPtr CommitLimit;
            public UIntPtr CommitPeak;
            public UIntPtr PhysicalTotal;
            public UIntPtr PhysicalAvailable;
            public UIntPtr SystemCache;
            public UIntPtr KernelTotal;
            public UIntPtr KernelPaged;
            public UIntPtr KernelNonpaged;
            public UIntPtr PageSize;
            public uint HandleCount;
            public uint ProcessCount;
            public uint ThreadCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

        #endregion

        #region Psapi

        [DllImport("psapi.dll")]
        public static extern int EmptyWorkingSet(IntPtr hwProc);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);

        #endregion

        #region Ntdll

        public const int SystemMemoryListInformation = 80;
        public const int MemoryPurgeStandbyList = 4;
        public const int MemoryEmptyWorkingSets = 2;

        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(
            int systemInformationClass,
            IntPtr systemInformation,
            int systemInformationLength,
            out int returnLength);

        [DllImport("ntdll.dll")]
        public static extern int NtSetSystemInformation(
            int systemInformationClass,
            IntPtr systemInformation,
            int systemInformationLength);

        #endregion

        #region Powrprof

        public const int SystemBatteryState = 5;

        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern uint CallNtPowerInformation(
            int informationLevel,
            IntPtr lpInputBuffer,
            uint nInputBufferSize,
            out SYSTEM_BATTERY_STATE lpOutputBuffer,
            uint nOutputBufferSize);

        #endregion

        #region Iphlpapi

        public const int AF_INET = 2;
        public const int AF_INET6 = 23;
        public const int TCP_TABLE_OWNER_PID_ALL = 5;
        public const int UDP_TABLE_OWNER_PID = 1;
        public const uint ERROR_INSUFFICIENT_BUFFER = 122;
        public const uint NO_ERROR = 0;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable,
            ref int pdwSize,
            bool bOrder,
            int ulAf,
            int tableClass,
            uint reserved = 0);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedUdpTable(
            IntPtr pUdpTable,
            ref int pdwSize,
            bool bOrder,
            int ulAf,
            int tableClass,
            uint reserved = 0);

        #endregion
    }
}
