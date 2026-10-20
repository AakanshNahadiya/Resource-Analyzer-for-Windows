using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ResourceAnalyzer.Helpers;
using ResourceAnalyzer.Models;

namespace ResourceAnalyzer.Services
{
    public interface IProcessService
    {
        Task<List<ProcessItem>> GetProcessesAsync(string searchTerm, string sortBy);
        Task<(bool Success, string Message)> KillProcessAsync(int pid, string processName, string? subProcessDetail = null);
        Task<(bool Success, string Message)> KillProcessTreeAsync(int pid, string processName, string? subProcessDetail = null);
        Task<(bool Success, string Message)> RestartProcessAsync(int pid, string processName, string? subProcessDetail = null);
        bool CanTerminateProcess(int pid, string processName, string? subProcessDetail, out string reason);
        string? GetProcessExecutablePath(int pid);
        HashSet<int> GetHungPids();
        int GetActiveForegroundPid();
        string GetProcessDescription(int pid, string processName);
    }

    public class ProcessService : IProcessService
    {
        #region Native Process Information

        public struct NativeProcessInfo
        {
            public int Pid;
            public string Name;
            public long MemoryBytes;
            public long TotalCpuTime100Ns;
            public int SessionId;
        }

        public List<NativeProcessInfo> GetProcessesNative()
        {
            var list = new List<NativeProcessInfo>();
            int bufferSize = 512 * 1024; // 512 KB initial buffer
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);

            try
            {
                int status;
                int returnLength;
                while ((status = NativeMethods.NtQuerySystemInformation(NativeMethods.SystemProcessInformation, buffer, bufferSize, out returnLength)) == NativeMethods.STATUS_INFO_LENGTH_MISMATCH)
                {
                    Marshal.FreeHGlobal(buffer);
                    bufferSize = Math.Max(bufferSize * 2, returnLength + 32768);
                    buffer = Marshal.AllocHGlobal(bufferSize);
                }

                if (status != NativeMethods.STATUS_SUCCESS)
                {
                    return list;
                }

                IntPtr currentPtr = buffer;
                while (true)
                {
                    uint nextEntryOffset = (uint)Marshal.ReadInt32(currentPtr, 0);
                    long userTime = Marshal.ReadInt64(currentPtr, 40);
                    long kernelTime = Marshal.ReadInt64(currentPtr, 48);

                    int pid = Marshal.ReadIntPtr(currentPtr, 80).ToInt32();
                    int sessionId = Marshal.ReadInt32(currentPtr, 100);
                    long workingSet = Marshal.ReadIntPtr(currentPtr, 144).ToInt64();

                    ushort nameLength = (ushort)Marshal.ReadInt16(currentPtr, 56);
                    IntPtr nameBuffer = Marshal.ReadIntPtr(currentPtr, 64);
                    string name;
                    if (pid == 0)
                    {
                        name = "Idle";
                    }
                    else if (nameLength > 0 && nameBuffer != IntPtr.Zero)
                    {
                        name = Marshal.PtrToStringUni(nameBuffer, nameLength / 2) ?? "Unknown";
                    }
                    else if (pid == 4)
                    {
                        name = "System";
                    }
                    else
                    {
                        name = $"Process_{pid}";
                    }

                    list.Add(new NativeProcessInfo
                    {
                        Pid = pid,
                        Name = name,
                        MemoryBytes = workingSet,
                        TotalCpuTime100Ns = userTime + kernelTime,
                        SessionId = sessionId
                    });

                    if (nextEntryOffset == 0) break;
                    currentPtr = IntPtr.Add(currentPtr, (int)nextEntryOffset);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return list;
        }

        #endregion

        private static readonly HashSet<string> CriticalKernelProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "Idle", "System", "Registry", "Memory Compression", "smss", "csrss", "wininit", "services", "lsass", "winlogon", "dwm"
        };

        private static readonly HashSet<string> CriticalServices = new(StringComparer.OrdinalIgnoreCase)
        {
            "RpcSs", "DcomLaunch", "PlugPlay", "LSM", "SamSs", "BrokerInfrastructure"
        };

        private static readonly HashSet<string> ProtectedAntiMalwareProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "MsMpEng", "SecurityHealthService"
        };

        private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            // Core Windows Subsystems & Kernel
            "Idle", "System", "Registry", "Memory Compression", "smss", "csrss", "wininit",
            "services", "lsass", "winlogon", "dwm", "Secure System",

            // Host Processes & Infrastructure
            "svchost", "conhost", "dllhost", "sihost", "taskhostw", "RuntimeBroker",
            "fontdrvhost", "WmiPrvSE", "dasHost", "WUDFHost", "backgroundTaskHost",

            // Search & Shell Background Services
            "SearchIndexer", "SearchHost", "SearchFilterHost", "SearchProtocolHost",
            "StartMenuExperienceHost", "ShellExperienceHost", "ShellHost",
            "ApplicationFrameHost", "SystemSettings", "SystemSettingsBroker",
            "TextInputHost", "LockApp", "ctfmon", "spoolsv", "smartscreen",
            "SecurityHealthService", "SecurityHealthSystray", "DefenderSessionHelper",
            "audiodg",

            // Windows Update, Diagnostics & Maintenance
            "compattelrunner", "MoUsoCoreWorker", "TiWorker", "TrustedInstaller",
            "sppsvc", "wlanext", "vds", "vssvc", "wbengine", "msdtc",

            // Windows 11 Widgets & Phone Link Background Hosts
            "WidgetBoard", "WidgetService", "MicrosoftStartFeedProvider",
            "PhoneExperienceHost", "YourPhoneAppProxyHost",
            "CrossDeviceResume", "CrossDeviceService", "FileCoAuth", "AppActions",

            // Common Hardware & Driver Background Daemons
            "RtkAudUService64", "SECOMN64", "ServiceCoordinator", "RtkBtManServ",
            "RstMwService", "SAClient", "SDXHelper", "vmcompute", "vmmemCmZygote",
            "igfxEMN"
        };

        private readonly Dictionary<int, (long TotalCpuTime100Ns, DateTime SampleTime)> _cpuHistory = new();
        private readonly object _syncLock = new();

        private static int _lastExternalForegroundPid;

        public static int GetActiveForegroundPid()
        {
            try
            {
                IntPtr hwnd = NativeMethods.GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.GetWindowThreadProcessId(hwnd, out int fgPid);
                    if (fgPid > 0 && fgPid != Environment.ProcessId)
                    {
                        _lastExternalForegroundPid = fgPid;
                    }
                }
            }
            catch { }
            return _lastExternalForegroundPid;
        }

        public static HashSet<int> GetHungPids()
        {
            var hungPids = new HashSet<int>();
            try
            {
                NativeMethods.EnumWindows((hWnd, lParam) =>
                {
                    if (NativeMethods.IsWindowVisible(hWnd) && NativeMethods.IsHungAppWindow(hWnd))
                    {
                        NativeMethods.GetWindowThreadProcessId(hWnd, out int pid);
                        if (pid > 0)
                        {
                            hungPids.Add(pid);
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return hungPids;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _descriptionCache = new(StringComparer.OrdinalIgnoreCase)
        {
            // Core Windows Subsystems & Kernel
            ["Idle"] = "System Idle Process",
            ["System"] = "System Kernel",
            ["Registry"] = "System Registry",
            ["Memory Compression"] = "Memory Compression",
            ["smss"] = "Session Manager Subsystem",
            ["csrss"] = "Client Server Runtime Subsystem",
            ["wininit"] = "Windows Initialization Process",
            ["services"] = "Service Control Manager",
            ["lsass"] = "Local Security Authority Subsystem Service",
            ["winlogon"] = "Windows Logon Application",
            ["dwm"] = "Desktop Window Manager",
            ["svchost"] = "Host Process for Windows Services",
            ["fontdrvhost"] = "Usermode Font Driver Host",
            ["sihost"] = "Shell Infrastructure Host",
            ["taskhostw"] = "Host Process for Windows Tasks",
            ["RuntimeBroker"] = "Runtime Broker",
            ["dllhost"] = "COM Surrogate",
            ["conhost"] = "Console Window Host",
            ["spoolsv"] = "Print Spooler",
            ["MsMpEng"] = "Antimalware Service Executable",
            ["SecurityHealthService"] = "Windows Security Health Service",

            // Common Desktop & System Applications
            ["explorer"] = "Windows Explorer",
            ["taskmgr"] = "Task Manager",
            ["cmd"] = "Command Prompt",
            ["powershell"] = "Windows PowerShell",
            ["pwsh"] = "PowerShell 7",
            ["notepad"] = "Notepad",
            ["devenv"] = "Visual Studio",
            ["code"] = "Visual Studio Code",
            ["chrome"] = "Google Chrome",
            ["msedge"] = "Microsoft Edge",
            ["firefox"] = "Mozilla Firefox",
            ["brave"] = "Brave Browser",
            ["spotify"] = "Spotify",
            ["discord"] = "Discord",
            ["slack"] = "Slack",
            ["teams"] = "Microsoft Teams",
            ["steam"] = "Steam",
            ["epicgameslauncher"] = "Epic Games Launcher"
        };

        public static string GetProcessDescription(int pid, string processName)
        {
            if (_descriptionCache.TryGetValue(processName, out var cached))
            {
                return cached;
            }

            try
            {
                using var proc = Process.GetProcessById(pid);
                string? path = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path))
                {
                    var fvi = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrWhiteSpace(fvi.FileDescription))
                    {
                        string desc = fvi.FileDescription.Trim();
                        _descriptionCache[processName] = desc;
                        return desc;
                    }
                }
            }
            catch
            {
                // Access denied on live process handle in Session 0.
                // Fall back to reading the file directly from System32 on disk for standard users (Fix 7)
                try
                {
                    string exeName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? processName
                        : $"{processName}.exe";
                    string sysPath = System.IO.Path.Combine(Environment.SystemDirectory, exeName);
                    if (System.IO.File.Exists(sysPath))
                    {
                        var fvi = FileVersionInfo.GetVersionInfo(sysPath);
                        if (!string.IsNullOrWhiteSpace(fvi.FileDescription))
                        {
                            string desc = fvi.FileDescription.Trim();
                            _descriptionCache[processName] = desc;
                            return desc;
                        }
                    }
                }
                catch { }
            }

            _descriptionCache[processName] = processName;
            return processName;
        }

        public bool CanTerminateProcess(int pid, string processName, string? subProcessDetail, out string reason)
        {
            string cleanName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4]
                : processName;

            // 1. Critical Kernel Processes & System PIDs (PID 4, smss, csrss, wininit, etc.)
            if ((pid > 0 && pid <= 4) || CriticalKernelProcesses.Contains(cleanName) || CriticalKernelProcesses.Contains(processName))
            {
                reason = $"{cleanName} is a critical Windows system process and cannot be terminated.";
                return false;
            }

            // 2. Protected Process Light (PPL) Anti-Malware Services (MsMpEng, Defender)
            if (ProtectedAntiMalwareProcesses.Contains(cleanName) || ProtectedAntiMalwareProcesses.Contains(processName))
            {
                reason = $"{cleanName} is a protected Windows antimalware process (PPL) and cannot be terminated.";
                return false;
            }

            // 3. Critical Foundational System Services (BSOD Protection for svchost)
            string? servicesToCheck = subProcessDetail;
            if (string.IsNullOrWhiteSpace(servicesToCheck) && pid > 4 &&
                (cleanName.Equals("svchost", StringComparison.OrdinalIgnoreCase) ||
                 processName.Equals("svchost", StringComparison.OrdinalIgnoreCase)))
            {
                var services = ProcessDetailResolver.GetActiveServices();
                if (services.TryGetValue(pid, out var autoSvc))
                {
                    servicesToCheck = autoSvc;
                }
            }

            if (!string.IsNullOrWhiteSpace(servicesToCheck))
            {
                foreach (var svc in CriticalServices)
                {
                    if (servicesToCheck.IndexOf(svc, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        reason = $"{cleanName} hosts the critical Windows service '{svc}' and cannot be terminated without crashing Windows.";
                        return false;
                    }
                }
            }

            // 4. Windows Native IsProcessCritical API Check (Flags processes that trigger Bug Check on exit)
            if (pid > 4)
            {
                IntPtr hQuery = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hQuery != IntPtr.Zero)
                {
                    try
                    {
                        if (NativeMethods.IsProcessCritical(hQuery, out bool isCritical) && isCritical)
                        {
                            reason = $"{cleanName} is designated by Windows as a critical system process and cannot be terminated.";
                            return false;
                        }
                    }
                    finally
                    {
                        NativeMethods.CloseHandle(hQuery);
                    }
                }
            }

            // 5. Native Permission Probe: Check PROCESS_TERMINATE rights
            if (pid > 4)
            {
                IntPtr hTerm = NativeMethods.OpenProcess(NativeMethods.PROCESS_TERMINATE, false, pid);
                if (hTerm != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(hTerm);
                }
                else
                {
                    int lastError = Marshal.GetLastWin32Error();
                    if (lastError == 5) // ERROR_ACCESS_DENIED
                    {
                        if (ElevationHelper.IsRunningAsAdmin())
                        {
                            reason = $"{cleanName} is protected by Windows kernel security policies and cannot be terminated.";
                            return false;
                        }
                    }
                }
            }

            reason = string.Empty;
            return true;
        }

        HashSet<int> IProcessService.GetHungPids() => GetHungPids();
        int IProcessService.GetActiveForegroundPid() => GetActiveForegroundPid();
        string IProcessService.GetProcessDescription(int pid, string processName) => GetProcessDescription(pid, processName);

        public async Task<List<ProcessItem>> GetProcessesAsync(string searchTerm, string sortBy)
        {
            return await Task.Run(() =>
            {
                var result = new List<ProcessItem>();
                var activePids = new HashSet<int>();
                var now = DateTime.UtcNow;
                int processorCount = Math.Max(1, Environment.ProcessorCount);

                // Use high-performance NtQuerySystemInformation (0.2ms total execution time, 0 handle allocations)
                var rawProcesses = GetProcessesNative();
                var hungPids = GetHungPids();
                int activeFgPid = GetActiveForegroundPid();

                // If native query returned empty for any reason, gracefully fall back to Process.GetProcesses()
                if (rawProcesses.Count == 0)
                {
                    try
                    {
                        foreach (var p in Process.GetProcesses())
                        {
                            try
                            {
                                rawProcesses.Add(new NativeProcessInfo
                                {
                                    Pid = p.Id,
                                    Name = p.ProcessName,
                                    MemoryBytes = p.WorkingSet64,
                                    TotalCpuTime100Ns = p.TotalProcessorTime.Ticks,
                                    SessionId = p.SessionId
                                });
                            }
                            catch { }
                            finally
                            {
                                p.Dispose();
                            }
                        }
                    }
                    catch { }
                }

                foreach (var p in rawProcesses)
                {
                    int pid = p.Pid;
                    activePids.Add(pid);

                    string name = p.Name;
                    string cleanName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? name[..^4]
                        : name;

                    // Ultra-efficient CPU Calculation via 100ns deltas
                    double cpuPercent = 0.0;
                    lock (_syncLock)
                    {
                        if (_cpuHistory.TryGetValue(pid, out var prev))
                        {
                            double elapsedMs = (now - prev.SampleTime).TotalMilliseconds;
                            if (elapsedMs >= 50)
                            {
                                if (elapsedMs <= 5000)
                                {
                                    long cpuUsed100Ns = p.TotalCpuTime100Ns - prev.TotalCpuTime100Ns;
                                    if (cpuUsed100Ns > 0)
                                    {
                                        double cpuUsedMs = cpuUsed100Ns / 10000.0;
                                        cpuPercent = Math.Clamp((cpuUsedMs / (elapsedMs * processorCount)) * 100.0, 0.0, 100.0);
                                    }
                                }
                                _cpuHistory[pid] = (p.TotalCpuTime100Ns, now);
                            }
                        }
                        else
                        {
                            _cpuHistory[pid] = (p.TotalCpuTime100Ns, now);
                        }
                    }

                    bool isFrozen = hungPids.Contains(pid);
                    bool isActiveApp = (pid == activeFgPid);
                    string description = GetProcessDescription(pid, cleanName);

                    var item = new ProcessItem
                    {
                        Pid = pid,
                        Name = cleanName,
                        Description = description,
                        MemoryBytes = p.MemoryBytes,
                        CpuPercent = cpuPercent,
                        IsFrozen = isFrozen,
                        IsActiveApp = isActiveApp
                    };
                    item.UpdateDisplayText();

                    result.Add(item);
                }

                // Clean up terminated processes from CPU history
                lock (_syncLock)
                {
                    var stale = _cpuHistory.Keys.Where(id => !activePids.Contains(id)).ToList();
                    foreach (var id in stale)
                    {
                        _cpuHistory.Remove(id);
                    }
                }

                // Filter search term
                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    string term = searchTerm.Trim();
                    result = result.Where(p =>
                        p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                        p.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                        p.Pid.ToString().Contains(term, StringComparison.OrdinalIgnoreCase)
                    ).ToList();
                }

                // Sort
                result = sortBy switch
                {
                    "CPU" => result.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.MemoryBytes).ToList(),
                    "Name" => result.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
                    _ => result.OrderByDescending(p => p.MemoryBytes).ThenByDescending(p => p.CpuPercent).ToList(), // "Memory" default
                };

                // Annotate instance counts for multi-process applications (e.g., Chromium browsers)
                var nameGroups = result.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
                foreach (var group in nameGroups)
                {
                    int total = group.Count();
                    if (total > 1)
                    {
                        int idx = 1;
                        foreach (var item in group)
                        {
                            item.InstanceTotal = total;
                            item.InstanceIndex = idx++;
                            item.UpdateDisplayText();
                        }
                    }
                }

                return result;
            });
        }

        public async Task<(bool Success, string Message)> KillProcessAsync(int pid, string processName, string? subProcessDetail = null)
        {
            return await Task.Run(() =>
            {
                if (!CanTerminateProcess(pid, processName, subProcessDetail, out string cantTerminateReason))
                {
                    return (false, cantTerminateReason);
                }

                try
                {
                    using var proc = Process.GetProcessById(pid);
                    proc.Kill(); // Terminate single process
                    return (true, $"Ended {processName}");
                }
                catch (ArgumentException)
                {
                    return (false, $"{processName} is no longer running.");
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    if (ElevationHelper.IsRunningAsAdmin())
                    {
                        return (false, $"Access denied. {processName} is protected by Windows kernel security policies and cannot be terminated.");
                    }
                    return (false, $"Access denied. Administrator privileges may be required to end {processName}.");
                }
                catch (Exception ex)
                {
                    return (false, $"Failed to end {processName}: {ex.Message}");
                }
            });
        }

        public async Task<(bool Success, string Message)> KillProcessTreeAsync(int pid, string processName, string? subProcessDetail = null)
        {
            return await Task.Run(() =>
            {
                if (!CanTerminateProcess(pid, processName, subProcessDetail, out string cantTerminateReason))
                {
                    return (false, cantTerminateReason);
                }

                try
                {
                    using var proc = Process.GetProcessById(pid);
                    proc.Kill(entireProcessTree: true); // Native .NET 10 process tree termination without spawning external taskkill.exe
                    return (true, $"Terminated process tree for {processName} (PID {pid}) and all child processes.");
                }
                catch (ArgumentException)
                {
                    return (false, $"{processName} is no longer running.");
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    if (ElevationHelper.IsRunningAsAdmin())
                    {
                        return (false, $"Access denied. {processName} is protected by Windows kernel security policies and cannot be terminated.");
                    }
                    return (false, $"Access denied. Administrator privileges may be required to end {processName}.");
                }
                catch (Exception ex)
                {
                    return (false, $"Failed to end process tree for {processName}: {ex.Message}");
                }
            });
        }

        public async Task<(bool Success, string Message)> RestartProcessAsync(int pid, string processName, string? subProcessDetail = null)
        {
            return await Task.Run(() =>
            {
                if (!CanTerminateProcess(pid, processName, subProcessDetail, out string cantRestartReason))
                {
                    return (false, cantRestartReason);
                }

                string? exePath = GetProcessExecutablePath(pid);
                if (string.IsNullOrEmpty(exePath) || !System.IO.File.Exists(exePath))
                {
                    return (false, $"Cannot restart {processName}: executable file path could not be determined.");
                }

                // Native process tree termination
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(3000);
                }
                catch (ArgumentException)
                {
                    // Process already exited
                }
                catch (Exception ex)
                {
                    return (false, $"Could not stop {processName}: {ex.Message}");
                }

                // Brief grace period for socket and file release
                System.Threading.Thread.Sleep(300);

                // Start fresh instance
                try
                {
                    var startPsi = new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true,
                        WorkingDirectory = System.IO.Path.GetDirectoryName(exePath) ?? string.Empty
                    };
                    Process.Start(startPsi);
                    return (true, $"Restarted {processName}");
                }
                catch (Exception ex)
                {
                    return (false, $"Terminated {processName}, but failed to launch a new instance: {ex.Message}");
                }
            });
        }

        public static string? GetProcessExecutablePath(int pid)
        {
            if (pid <= 4) return null;

            try
            {
                using var proc = Process.GetProcessById(pid);
                string? path = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                {
                    return path;
                }
            }
            catch { }

            try
            {
                IntPtr hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hProcess != IntPtr.Zero)
                {
                    try
                    {
                        uint capacity = 1024;
                        var sb = new System.Text.StringBuilder((int)capacity);
                        if (NativeMethods.QueryFullProcessImageName(hProcess, 0, sb, ref capacity))
                        {
                            string path = sb.ToString();
                            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                            {
                                return path;
                            }
                        }
                    }
                    finally
                    {
                        NativeMethods.CloseHandle(hProcess);
                    }
                }
            }
            catch { }

            return null;
        }

        string? IProcessService.GetProcessExecutablePath(int pid) => GetProcessExecutablePath(pid);
    }
}
