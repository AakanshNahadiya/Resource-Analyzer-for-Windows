using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ResourceAnalyzer.Helpers;
using ResourceAnalyzer.Models;

namespace ResourceAnalyzer.Services
{
    /// <summary>
    /// High-performance resolver for sub-process identity (Window titles, hosted Windows Services, and process roles).
    /// Enables users and screen readers to identify what each child process in an expanded group actually does.
    /// </summary>
    public static class ProcessDetailResolver
    {
        private static readonly object _syncLock = new();

        private static Dictionary<int, string> _cachedServices = new();
        private static DateTime _lastServiceRefresh = DateTime.MinValue;

        private static Dictionary<int, string> _cachedWindowTitles = new();
        private static DateTime _lastWindowRefresh = DateTime.MinValue;

        private static readonly ConcurrentDictionary<int, string> _commandLineRoleCache = new();

        /// <summary>
        /// Resolves an informative, human-readable description for a child sub-process.
        /// Priority:
        /// 1. Top-level window title (e.g. "YouTube - Google Chrome" or "Settings")
        /// 2. Hosted Windows Services (e.g. "Windows Audio [AudioSrv]")
        /// 3. Classified process role from command-line arguments (e.g. "GPU Process", "Web Content (Tab)", "Network Service")
        /// </summary>
        public static string ResolveSubProcessDetail(int pid, string processName, string defaultDisplayName)
        {
            if (pid <= 0) return string.Empty;

            string cleanName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4]
                : processName;

            string appTag = ProcessItem.ShowExtension ? $"{cleanName}.exe" : cleanName;

            // 1. Check for visible window title
            var titles = GetActiveWindowTitles();
            if (titles.TryGetValue(pid, out var title) && !string.IsNullOrWhiteSpace(title))
            {
                if (title.Contains(cleanName, StringComparison.OrdinalIgnoreCase) ||
                    title.Contains(defaultDisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    return title;
                }
                return $"{title} ({appTag})";
            }

            // 2. Check for hosted Windows services (e.g. svchost.exe)
            var services = GetActiveServices();
            if (services.TryGetValue(pid, out var svcDesc) && !string.IsNullOrWhiteSpace(svcDesc))
            {
                return svcDesc;
            }

            // 3. Check for classified role (Chromium, Electron, or script runners)
            if (_commandLineRoleCache.TryGetValue(pid, out var cachedRole))
            {
                return $"{cachedRole} ({appTag})";
            }

            string? cmdLine = GetProcessCommandLine(pid);
            if (!string.IsNullOrWhiteSpace(cmdLine))
            {
                string? role = ExtractProcessRole(cmdLine, cleanName);
                if (!string.IsNullOrEmpty(role))
                {
                    _commandLineRoleCache[pid] = role;
                    return $"{role} ({appTag})";
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Retrieves visible top-level window titles mapped to their process IDs (cached for 2 seconds).
        /// </summary>
        public static Dictionary<int, string> GetActiveWindowTitles()
        {
            lock (_syncLock)
            {
                if ((DateTime.UtcNow - _lastWindowRefresh).TotalSeconds < 2.0 && _cachedWindowTitles.Count > 0)
                {
                    return _cachedWindowTitles;
                }

                var titles = new Dictionary<int, string>();
                try
                {
                    NativeMethods.EnumWindows((hWnd, lParam) =>
                    {
                        try
                        {
                            if (NativeMethods.IsWindowVisible(hWnd))
                            {
                                NativeMethods.GetWindowThreadProcessId(hWnd, out int pid);
                                if (pid > 4 && !titles.ContainsKey(pid))
                                {
                                    int length = NativeMethods.GetWindowTextLength(hWnd);
                                    if (length > 0 && length < 1024)
                                    {
                                        var sb = new StringBuilder(length + 1);
                                        NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
                                        string t = sb.ToString().Trim();
                                        if (!string.IsNullOrWhiteSpace(t) &&
                                            !t.Equals("Default IME", StringComparison.OrdinalIgnoreCase) &&
                                            !t.Equals("MSCTFIME UI", StringComparison.OrdinalIgnoreCase))
                                        {
                                            titles[pid] = t;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                        return true;
                    }, IntPtr.Zero);
                }
                catch { }

                _cachedWindowTitles = titles;
                _lastWindowRefresh = DateTime.UtcNow;
                return _cachedWindowTitles;
            }
        }

        /// <summary>
        /// Retrieves all running Windows services grouped by their hosting process ID (cached for 5 seconds).
        /// </summary>
        public static Dictionary<int, string> GetActiveServices()
        {
            lock (_syncLock)
            {
                if ((DateTime.UtcNow - _lastServiceRefresh).TotalSeconds < 5.0 && _cachedServices.Count > 0)
                {
                    return _cachedServices;
                }

                var map = new Dictionary<int, List<string>>();
                IntPtr hScm = IntPtr.Zero;
                try
                {
                    hScm = NativeMethods.OpenSCManager(null, null, NativeMethods.SC_MANAGER_ENUMERATE_SERVICE);
                    if (hScm != IntPtr.Zero)
                    {
                        int resumeHandle = 0;
                        NativeMethods.EnumServicesStatusEx(
                            hScm,
                            NativeMethods.SC_ENUM_PROCESS_INFO,
                            NativeMethods.SERVICE_WIN32,
                            NativeMethods.SERVICE_STATE_ALL,
                            IntPtr.Zero,
                            0,
                            out int bytesNeeded,
                            out _,
                            ref resumeHandle,
                            null);

                        if (bytesNeeded > 0)
                        {
                            IntPtr buffer = Marshal.AllocHGlobal(bytesNeeded);
                            try
                            {
                                resumeHandle = 0;
                                if (NativeMethods.EnumServicesStatusEx(
                                    hScm,
                                    NativeMethods.SC_ENUM_PROCESS_INFO,
                                    NativeMethods.SERVICE_WIN32,
                                    NativeMethods.SERVICE_STATE_ALL,
                                    buffer,
                                    bytesNeeded,
                                    out _,
                                    out int returned,
                                    ref resumeHandle,
                                    null))
                                {
                                    int structSize = Marshal.SizeOf<NativeMethods.ENUM_SERVICE_STATUS_PROCESS>();
                                    IntPtr currentPtr = buffer;

                                    for (int i = 0; i < returned; i++)
                                    {
                                        var status = Marshal.PtrToStructure<NativeMethods.ENUM_SERVICE_STATUS_PROCESS>(currentPtr);
                                        int pid = status.ServiceStatusProcess.dwProcessId;
                                        if (pid > 0)
                                        {
                                            if (!map.TryGetValue(pid, out var list))
                                            {
                                                list = new List<string>();
                                                map[pid] = list;
                                            }

                                            string disp = !string.IsNullOrWhiteSpace(status.lpDisplayName) ? status.lpDisplayName.Trim() : status.lpServiceName;
                                            string svc = status.lpServiceName ?? string.Empty;
                                            string entry = !string.IsNullOrEmpty(svc) && !disp.Equals(svc, StringComparison.OrdinalIgnoreCase)
                                                ? $"{disp} [{svc}]"
                                                : disp;

                                            list.Add(entry);
                                        }
                                        currentPtr = IntPtr.Add(currentPtr, structSize);
                                    }
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(buffer);
                            }
                        }
                    }
                }
                catch { }
                finally
                {
                    if (hScm != IntPtr.Zero)
                    {
                        NativeMethods.CloseServiceHandle(hScm);
                    }
                }

                var finalMap = new Dictionary<int, string>();
                foreach (var (pid, list) in map)
                {
                    finalMap[pid] = string.Join(", ", list);
                }

                _cachedServices = finalMap;
                _lastServiceRefresh = DateTime.UtcNow;
                return _cachedServices;
            }
        }

        /// <summary>
        /// Reads process command-line arguments using NtQueryInformationProcess (ProcessCommandLineInformation).
        /// </summary>
        public static string? GetProcessCommandLine(int pid)
        {
            if (pid <= 4) return null;

            IntPtr hProcess = IntPtr.Zero;
            try
            {
                hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hProcess == IntPtr.Zero) return null;

                int status = NativeMethods.NtQueryInformationProcess(
                    hProcess,
                    NativeMethods.ProcessCommandLineInformation,
                    IntPtr.Zero,
                    0,
                    out int returnLength);

                if (returnLength <= 0) return null;

                IntPtr buffer = Marshal.AllocHGlobal(returnLength);
                try
                {
                    status = NativeMethods.NtQueryInformationProcess(
                        hProcess,
                        NativeMethods.ProcessCommandLineInformation,
                        buffer,
                        returnLength,
                        out _);

                    if (status == 0)
                    {
                        var unicodeStr = Marshal.PtrToStructure<NativeMethods.UNICODE_STRING>(buffer);
                        if (unicodeStr.Length > 0 && unicodeStr.Buffer != IntPtr.Zero)
                        {
                            return Marshal.PtrToStringUni(unicodeStr.Buffer, unicodeStr.Length / 2);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch { }
            finally
            {
                if (hProcess != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(hProcess);
                }
            }

            return null;
        }

        /// <summary>
        /// Parses command-line arguments to classify Chromium, Electron, or script runner roles.
        /// </summary>
        public static string? ExtractProcessRole(string? commandLine, string processName)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return null;

            // Chromium / Electron architectures (Chrome, Edge, Brave, VS Code, Discord, Slack, etc.)
            if (commandLine.Contains("--type=renderer", StringComparison.OrdinalIgnoreCase))
            {
                return "Web Content (Tab)";
            }
            if (commandLine.Contains("--type=gpu-process", StringComparison.OrdinalIgnoreCase))
            {
                return "GPU Process";
            }
            if (commandLine.Contains("--type=utility", StringComparison.OrdinalIgnoreCase))
            {
                if (commandLine.Contains("audio.mojom.AudioService", StringComparison.OrdinalIgnoreCase))
                {
                    return "Audio Service";
                }
                if (commandLine.Contains("network.mojom.NetworkService", StringComparison.OrdinalIgnoreCase))
                {
                    return "Network Service";
                }
                if (commandLine.Contains("storage.mojom.StorageService", StringComparison.OrdinalIgnoreCase))
                {
                    return "Storage Service";
                }
                return "Utility Worker";
            }
            if (commandLine.Contains("--type=extension", StringComparison.OrdinalIgnoreCase))
            {
                return "Browser Extension";
            }
            if (commandLine.Contains("--type=crashpad-handler", StringComparison.OrdinalIgnoreCase))
            {
                return "Crash Reporter";
            }

            // Script runners (python, node, pwsh, cmd)
            if (processName.Equals("python", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("pythonw", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("node", StringComparison.OrdinalIgnoreCase))
            {
                var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 1; i < parts.Length; i++)
                {
                    string arg = parts[i].Trim('"', '\'');
                    if (arg.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ||
                        arg.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                        arg.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                    {
                        return System.IO.Path.GetFileName(arg);
                    }
                }
            }

            return null;
        }
    }
}
