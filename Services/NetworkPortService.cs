using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AccessibleTaskManager.Helpers;
using AccessibleTaskManager.Models;

namespace AccessibleTaskManager.Services
{
    public interface INetworkPortService
    {
        Task<List<NetworkPortItem>> GetNetworkPortsAsync();
    }

    public class NetworkPortService : INetworkPortService
    {
        private readonly ConcurrentDictionary<int, (string ProcessName, string FriendlyName, string ProcessPath, DateTime CachedAt)> _processCache = new();

        public async Task<List<NetworkPortItem>> GetNetworkPortsAsync()
        {
            return await Task.Run(() =>
            {
                var items = new List<NetworkPortItem>();

                try
                {
                    GetTcpV4Connections(items);
                }
                catch { }

                try
                {
                    GetTcpV6Connections(items);
                }
                catch { }

                try
                {
                    GetUdpV4Connections(items);
                }
                catch { }

                try
                {
                    GetUdpV6Connections(items);
                }
                catch { }

                // Resolve process info for each unique PID
                foreach (var item in items)
                {
                    var (pName, fName, path) = ResolveProcessInfo(item.ProcessId);
                    item.ProcessName = pName;
                    item.FriendlyName = fName;
                    item.ProcessPath = path;
                    item.UpdateDisplayText();
                }

                return items;
            });
        }

        private static ushort SwapPort(uint port)
        {
            return (ushort)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));
        }

        private static string MapTcpState(int state)
        {
            return state switch
            {
                1 => "Closed",
                2 => "Listening",
                3 => "SynSent",
                4 => "SynReceived",
                5 => "Established",
                6 => "FinWait1",
                7 => "FinWait2",
                8 => "CloseWait",
                9 => "Closing",
                10 => "LastAck",
                11 => "TimeWait",
                12 => "DeleteTcb",
                _ => "Unknown"
            };
        }

        private void GetTcpV4Connections(List<NetworkPortItem> list)
        {
            int size = 0;
            uint ret = NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, true, NativeMethods.AF_INET, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0);
            if (size == 0) return;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                ret = NativeMethods.GetExtendedTcpTable(buffer, ref size, true, NativeMethods.AF_INET, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret != NativeMethods.NO_ERROR) return;

                int entries = Marshal.ReadInt32(buffer);
                IntPtr rowPtr = IntPtr.Add(buffer, 4);

                for (int i = 0; i < entries; i++)
                {
                    int stateNum = Marshal.ReadInt32(rowPtr, 0);
                    uint localAddr = (uint)Marshal.ReadInt32(rowPtr, 4);
                    uint localPort = (uint)Marshal.ReadInt32(rowPtr, 8);
                    uint remoteAddr = (uint)Marshal.ReadInt32(rowPtr, 12);
                    uint remotePort = (uint)Marshal.ReadInt32(rowPtr, 16);
                    int pid = Marshal.ReadInt32(rowPtr, 20);

                    string stateStr = MapTcpState(stateNum);
                    ushort lPort = SwapPort(localPort);
                    ushort rPort = SwapPort(remotePort);

                    string localIpStr = new IPAddress(localAddr).ToString();
                    string remoteIpStr = new IPAddress(remoteAddr).ToString();

                    list.Add(new NetworkPortItem
                    {
                        Protocol = "TCP",
                        LocalAddress = localIpStr,
                        LocalPort = lPort,
                        RemoteAddress = remoteIpStr,
                        RemotePort = rPort,
                        State = stateStr,
                        ProcessId = pid
                    });

                    rowPtr = IntPtr.Add(rowPtr, 24);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private void GetTcpV6Connections(List<NetworkPortItem> list)
        {
            int size = 0;
            uint ret = NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, true, NativeMethods.AF_INET6, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0);
            if (size == 0) return;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                ret = NativeMethods.GetExtendedTcpTable(buffer, ref size, true, NativeMethods.AF_INET6, NativeMethods.TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret != NativeMethods.NO_ERROR) return;

                int entries = Marshal.ReadInt32(buffer);
                IntPtr rowPtr = IntPtr.Add(buffer, 4);
                byte[] localAddrBytes = new byte[16];
                byte[] remoteAddrBytes = new byte[16];

                for (int i = 0; i < entries; i++)
                {
                    Marshal.Copy(rowPtr, localAddrBytes, 0, 16);
                    // uint localScope = (uint)Marshal.ReadInt32(rowPtr, 16);
                    uint localPort = (uint)Marshal.ReadInt32(rowPtr, 20);

                    Marshal.Copy(IntPtr.Add(rowPtr, 24), remoteAddrBytes, 0, 16);
                    // uint remoteScope = (uint)Marshal.ReadInt32(rowPtr, 40);
                    uint remotePort = (uint)Marshal.ReadInt32(rowPtr, 44);

                    int stateNum = Marshal.ReadInt32(rowPtr, 48);
                    int pid = Marshal.ReadInt32(rowPtr, 52);

                    string stateStr = MapTcpState(stateNum);
                    ushort lPort = SwapPort(localPort);
                    ushort rPort = SwapPort(remotePort);

                    string localIpStr = new IPAddress(localAddrBytes).ToString();
                    string remoteIpStr = new IPAddress(remoteAddrBytes).ToString();

                    list.Add(new NetworkPortItem
                    {
                        Protocol = "TCP",
                        LocalAddress = localIpStr,
                        LocalPort = lPort,
                        RemoteAddress = remoteIpStr,
                        RemotePort = rPort,
                        State = stateStr,
                        ProcessId = pid
                    });

                    rowPtr = IntPtr.Add(rowPtr, 56);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private void GetUdpV4Connections(List<NetworkPortItem> list)
        {
            int size = 0;
            uint ret = NativeMethods.GetExtendedUdpTable(IntPtr.Zero, ref size, true, NativeMethods.AF_INET, NativeMethods.UDP_TABLE_OWNER_PID, 0);
            if (size == 0) return;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                ret = NativeMethods.GetExtendedUdpTable(buffer, ref size, true, NativeMethods.AF_INET, NativeMethods.UDP_TABLE_OWNER_PID, 0);
                if (ret != NativeMethods.NO_ERROR) return;

                int entries = Marshal.ReadInt32(buffer);
                IntPtr rowPtr = IntPtr.Add(buffer, 4);

                for (int i = 0; i < entries; i++)
                {
                    uint localAddr = (uint)Marshal.ReadInt32(rowPtr, 0);
                    uint localPort = (uint)Marshal.ReadInt32(rowPtr, 4);
                    int pid = Marshal.ReadInt32(rowPtr, 8);

                    ushort lPort = SwapPort(localPort);
                    string localIpStr = new IPAddress(localAddr).ToString();

                    list.Add(new NetworkPortItem
                    {
                        Protocol = "UDP",
                        LocalAddress = localIpStr,
                        LocalPort = lPort,
                        RemoteAddress = "*",
                        RemotePort = 0,
                        State = "Listening",
                        ProcessId = pid
                    });

                    rowPtr = IntPtr.Add(rowPtr, 12);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private void GetUdpV6Connections(List<NetworkPortItem> list)
        {
            int size = 0;
            uint ret = NativeMethods.GetExtendedUdpTable(IntPtr.Zero, ref size, true, NativeMethods.AF_INET6, NativeMethods.UDP_TABLE_OWNER_PID, 0);
            if (size == 0) return;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                ret = NativeMethods.GetExtendedUdpTable(buffer, ref size, true, NativeMethods.AF_INET6, NativeMethods.UDP_TABLE_OWNER_PID, 0);
                if (ret != NativeMethods.NO_ERROR) return;

                int entries = Marshal.ReadInt32(buffer);
                IntPtr rowPtr = IntPtr.Add(buffer, 4);
                byte[] localAddrBytes = new byte[16];

                for (int i = 0; i < entries; i++)
                {
                    Marshal.Copy(rowPtr, localAddrBytes, 0, 16);
                    // uint localScope = (uint)Marshal.ReadInt32(rowPtr, 16);
                    uint localPort = (uint)Marshal.ReadInt32(rowPtr, 20);
                    int pid = Marshal.ReadInt32(rowPtr, 24);

                    ushort lPort = SwapPort(localPort);
                    string localIpStr = new IPAddress(localAddrBytes).ToString();

                    list.Add(new NetworkPortItem
                    {
                        Protocol = "UDP",
                        LocalAddress = localIpStr,
                        LocalPort = lPort,
                        RemoteAddress = "*",
                        RemotePort = 0,
                        State = "Listening",
                        ProcessId = pid
                    });

                    rowPtr = IntPtr.Add(rowPtr, 28);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private (string ProcessName, string FriendlyName, string ProcessPath) ResolveProcessInfo(int pid)
        {
            if (pid == 0)
                return ("System Idle Process", "System Idle Process", "");
            if (pid == 4)
                return ("System", "System / NT Kernel", "");

            if (_processCache.TryGetValue(pid, out var cached) && (DateTime.Now - cached.CachedAt).TotalSeconds < 30)
            {
                return (cached.ProcessName, cached.FriendlyName, cached.ProcessPath);
            }

            string pName = $"PID {pid}";
            string fName = "";
            string pPath = "";

            try
            {
                using var p = Process.GetProcessById(pid);
                pName = p.ProcessName + ".exe";
                fName = p.ProcessName;

                try
                {
                    pPath = p.MainModule?.FileName ?? "";
                    if (!string.IsNullOrEmpty(pPath))
                    {
                        var vi = FileVersionInfo.GetVersionInfo(pPath);
                        if (!string.IsNullOrWhiteSpace(vi.FileDescription))
                        {
                            fName = vi.FileDescription.Trim();
                        }
                    }
                }
                catch
                {
                    // MainModule can fail for protected system processes or un-elevated rights; process name still works
                }
            }
            catch
            {
                pName = $"Unknown ({pid})";
                fName = pName;
            }

            var entry = (pName, fName, pPath, DateTime.Now);
            _processCache[pid] = entry;
            return (pName, fName, pPath);
        }
    }
}
