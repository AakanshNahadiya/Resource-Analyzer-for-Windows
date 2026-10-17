using System;
using System.Runtime.InteropServices;

namespace ResourceAnalyzer.Helpers
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

        #region Process & DLL Security Hardening (Microsoft SDL / CWE-427)

        public const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
        public const uint LOAD_LIBRARY_SEARCH_APPLICATION_DIR = 0x00000200;
        public const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;
        public const uint LOAD_LIBRARY_SEARCH_USER_DIRS = 0x00000400;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetDefaultDllDirectories(uint DirectoryFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetDllDirectory(string lpPathName);

        /// <summary>
        /// Hardens process against DLL preloading and hijacking attacks (CWE-427)
        /// in accordance with Microsoft Security Development Lifecycle guidelines.
        /// Removes the Current Working Directory (CWD) from the DLL search order.
        /// </summary>
        public static void InitializeProcessSecurity()
        {
            try
            {
                SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
                SetDllDirectory(string.Empty);
            }
            catch
            {
                // Graceful fallback on non-standard Windows builds
            }
        }

        #endregion

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        [StructLayout(LayoutKind.Sequential)]
        public struct STORAGE_DEVICE_NUMBER
        {
            public int DeviceType;
            public int DeviceNumber;
            public int PartitionNumber;
        }

        public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
        public const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x00070050;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            Microsoft.Win32.SafeHandles.SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            out STORAGE_DEVICE_NUMBER lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            Microsoft.Win32.SafeHandles.SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        public static int GetStorageDeviceNumber(string driveLetter)
        {
            try
            {
                string path = @"\\.\" + driveLetter.TrimEnd('\\');
                using var handle = CreateFile(path, 0, 1 | 2 /* FILE_SHARE_READ | FILE_SHARE_WRITE */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
                if (!handle.IsInvalid)
                {
                    if (DeviceIoControl(handle, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0,
                        out STORAGE_DEVICE_NUMBER sdn, (uint)Marshal.SizeOf(typeof(STORAGE_DEVICE_NUMBER)),
                        out _, IntPtr.Zero))
                    {
                        return sdn.DeviceNumber;
                    }
                }
            }
            catch { }
            return -1;
        }

        public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

        [StructLayout(LayoutKind.Sequential)]
        public struct STORAGE_PROPERTY_QUERY
        {
            public uint PropertyId;
            public uint QueryType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public byte[] AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVICE_SEEK_PENALTY_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            [MarshalAs(UnmanagedType.I1)]
            public bool IncursSeekPenalty;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DEVICE_TRIM_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            [MarshalAs(UnmanagedType.I1)]
            public bool TrimEnabled;
        }

        public static bool? QueryDiskSeekPenalty(int diskIndex)
        {
            if (diskIndex < 0) return null;
            try
            {
                string path = $@"\\.\PhysicalDrive{diskIndex}";
                using var handle = CreateFile(path, 0, 1 | 2 | 4, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (!handle.IsInvalid)
                {
                    var query = new STORAGE_PROPERTY_QUERY
                    {
                        PropertyId = 7, // StorageDeviceSeekPenaltyProperty
                        QueryType = 0,  // PropertyStandardQuery
                        AdditionalParameters = new byte[1]
                    };
                    int qSize = Marshal.SizeOf(typeof(STORAGE_PROPERTY_QUERY));
                    IntPtr inBuf = Marshal.AllocHGlobal(qSize);
                    Marshal.StructureToPtr(query, inBuf, false);
                    int outSize = Marshal.SizeOf(typeof(DEVICE_SEEK_PENALTY_DESCRIPTOR));
                    IntPtr outBuf = Marshal.AllocHGlobal(outSize);
                    try
                    {
                        if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, inBuf, (uint)qSize, outBuf, (uint)outSize, out _, IntPtr.Zero))
                        {
                            var desc = Marshal.PtrToStructure<DEVICE_SEEK_PENALTY_DESCRIPTOR>(outBuf);
                            return desc.IncursSeekPenalty;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(inBuf);
                        Marshal.FreeHGlobal(outBuf);
                    }
                }
            }
            catch { }
            return null;
        }

        public static bool? QueryDiskTrimSupport(int diskIndex)
        {
            if (diskIndex < 0) return null;
            try
            {
                string path = $@"\\.\PhysicalDrive{diskIndex}";
                using var handle = CreateFile(path, 0, 1 | 2 | 4, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (!handle.IsInvalid)
                {
                    var query = new STORAGE_PROPERTY_QUERY
                    {
                        PropertyId = 8, // StorageDeviceTrimProperty
                        QueryType = 0,  // PropertyStandardQuery
                        AdditionalParameters = new byte[1]
                    };
                    int qSize = Marshal.SizeOf(typeof(STORAGE_PROPERTY_QUERY));
                    IntPtr inBuf = Marshal.AllocHGlobal(qSize);
                    Marshal.StructureToPtr(query, inBuf, false);
                    int outSize = Marshal.SizeOf(typeof(DEVICE_TRIM_DESCRIPTOR));
                    IntPtr outBuf = Marshal.AllocHGlobal(outSize);
                    try
                    {
                        if (DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, inBuf, (uint)qSize, outBuf, (uint)outSize, out _, IntPtr.Zero))
                        {
                            var desc = Marshal.PtrToStructure<DEVICE_TRIM_DESCRIPTOR>(outBuf);
                            return desc.TrimEnabled;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(inBuf);
                        Marshal.FreeHGlobal(outBuf);
                    }
                }
            }
            catch { }
            return null;
        }

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(
            IntPtr hProcess,
            int dwFlags,
            System.Text.StringBuilder lpExeName,
            ref uint lpdwSize);

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

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

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

        public const int ProcessCommandLineInformation = 60;

        [StructLayout(LayoutKind.Sequential)]
        public struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [DllImport("ntdll.dll")]
        public static extern int NtQueryInformationProcess(
            IntPtr processHandle,
            int processInformationClass,
            IntPtr processInformation,
            int processInformationLength,
            out int returnLength);

        #endregion

        #region Advapi32

        public const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
        public const int SC_ENUM_PROCESS_INFO = 0;
        public const int SERVICE_WIN32 = 0x00000030;
        public const int SERVICE_STATE_ALL = 0x00000003;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ENUM_SERVICE_STATUS_PROCESS
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpServiceName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpDisplayName;
            public SERVICE_STATUS_PROCESS ServiceStatusProcess;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_STATUS_PROCESS
        {
            public int dwServiceType;
            public int dwCurrentState;
            public int dwControlsAccepted;
            public int dwWin32ExitCode;
            public int dwServiceSpecificExitCode;
            public int dwCheckPoint;
            public int dwWaitHint;
            public int dwProcessId;
            public int dwServiceFlags;
        }

        [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumServicesStatusEx(
            IntPtr hSCManager,
            int infoLevel,
            int dwServiceType,
            int dwServiceState,
            IntPtr lpServices,
            int cbBufSize,
            out int pcbBytesNeeded,
            out int lpServicesReturned,
            ref int lpResumeHandle,
            string? pszGroupName);

        #endregion

        #region Powrprof

        public const int SystemBatteryState = 5;
        public const int ProcessorInformation = 11;

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESSOR_POWER_INFORMATION
        {
            public uint Number;
            public uint MaxMhz;
            public uint CurrentMhz;
            public uint MhzLimit;
            public uint MaxIdleState;
            public uint CurrentIdleState;
        }

        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern uint CallNtPowerInformation(
            int informationLevel,
            IntPtr lpInputBuffer,
            uint nInputBufferSize,
            out SYSTEM_BATTERY_STATE lpOutputBuffer,
            uint nOutputBufferSize);

        [DllImport("powrprof.dll", EntryPoint = "CallNtPowerInformation", SetLastError = true)]
        public static extern uint CallNtPowerInformationRaw(
            int informationLevel,
            IntPtr lpInputBuffer,
            uint nInputBufferSize,
            IntPtr lpOutputBuffer,
            uint nOutputBufferSize);

        public static (uint MaxMhz, uint CurrentMhz, uint MhzLimit)? GetProcessorPowerLimits()
        {
            try
            {
                int procCount = Environment.ProcessorCount;
                if (procCount <= 0) procCount = 1;
                int structSize = Marshal.SizeOf(typeof(PROCESSOR_POWER_INFORMATION));
                int totalSize = procCount * structSize;
                IntPtr buffer = Marshal.AllocHGlobal(totalSize);
                try
                {
                    uint ret = CallNtPowerInformationRaw(ProcessorInformation, IntPtr.Zero, 0, buffer, (uint)totalSize);
                    if (ret == 0)
                    {
                        var info = Marshal.PtrToStructure<PROCESSOR_POWER_INFORMATION>(buffer);
                        return (info.MaxMhz, info.CurrentMhz, info.MhzLimit);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch { }
            return null;
        }

        [DllImport("powrprof.dll", SetLastError = true)]
        public static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr LocalFree(IntPtr hMem);

        #endregion

        #region Display

        public const int ENUM_CURRENT_SETTINGS = -1;
        public const int SM_CMONITORS = 80;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public uint type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;
            public uint colorEncoding;
            public uint bitsPerColorChannel;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public uint refreshRateNumerator;
            public uint refreshRateDenominator;
            public uint scanLineOrdering;
            [MarshalAs(UnmanagedType.Bool)]
            public bool targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
            public byte[] modeInfo;
        }

        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(
            uint flags,
            out uint numPathArrayElements,
            out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
            ref uint numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

        public static (bool Supported, bool Enabled, uint BitsPerChannel)? QueryDisplayHdrStatus()
        {
            try
            {
                int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
                if (err == 0 && pathCount > 0)
                {
                    var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                    var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                    err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                    if (err == 0)
                    {
                        for (int i = 0; i < pathCount; i++)
                        {
                            var packet = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                            packet.header.type = 9; // DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO
                            packet.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO));
                            packet.header.adapterId = paths[i].targetInfo.adapterId;
                            packet.header.id = paths[i].targetInfo.id;
                            if (DisplayConfigGetDeviceInfo(ref packet) == 0)
                            {
                                bool supported = (packet.value & 0x1) != 0;
                                bool enabled = (packet.value & 0x2) != 0;
                                return (supported, enabled, packet.bitsPerColorChannel);
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

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

        #region Wlanapi

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanOpenHandle(
            uint dwClientVersion,
            IntPtr pReserved,
            out uint pdwNegotiatedVersion,
            out IntPtr phClientHandle);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanCloseHandle(
            IntPtr hClientHandle,
            IntPtr pReserved);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanEnumInterfaces(
            IntPtr hClientHandle,
            IntPtr pReserved,
            out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern uint WlanQueryInterface(
            IntPtr hClientHandle,
            ref Guid pInterfaceGuid,
            int opCode,
            IntPtr pReserved,
            out uint pdwDataSize,
            out IntPtr ppData,
            out int pWlanOpcodeValueType);

        [DllImport("wlanapi.dll", SetLastError = true)]
        public static extern void WlanFreeMemory(IntPtr pMemory);

        public static string? QueryActiveWifiBand()
        {
            try
            {
                if (WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr handle) == 0)
                {
                    try
                    {
                        if (WlanEnumInterfaces(handle, IntPtr.Zero, out IntPtr ifList) == 0)
                        {
                            try
                            {
                                int count = Marshal.ReadInt32(ifList);
                                if (count > 0)
                                {
                                    byte[] guidBytes = new byte[16];
                                    Marshal.Copy(new IntPtr(ifList.ToInt64() + 8), guidBytes, 0, 16);
                                    Guid guid = new Guid(guidBytes);

                                    // OpCode 8 = wlan_intf_opcode_channel_number
                                    if (WlanQueryInterface(handle, ref guid, 8, IntPtr.Zero, out _, out IntPtr data, out _) == 0)
                                    {
                                        try
                                        {
                                            int channel = Marshal.ReadInt32(data);
                                            if (channel is >= 1 and <= 14)
                                                return "2.4 GHz";
                                            if (channel is >= 32 and <= 177)
                                                return "5 GHz";
                                            if (channel > 177)
                                                return "6 GHz";
                                        }
                                        finally
                                        {
                                            WlanFreeMemory(data);
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                WlanFreeMemory(ifList);
                            }
                        }
                    }
                    finally
                    {
                        WlanCloseHandle(handle, IntPtr.Zero);
                    }
                }
            }
            catch { }
            return null;
        }

        #endregion
    }
}
