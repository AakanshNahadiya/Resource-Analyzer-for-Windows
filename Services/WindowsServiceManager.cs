using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using AccessibleTaskManager.Models;

namespace AccessibleTaskManager.Services
{
    public interface IWindowsServiceManager
    {
        List<ServiceItem> GetServices();
        Task<(bool Success, string Message)> StartServiceAsync(string serviceName);
        Task<(bool Success, string Message)> StopServiceAsync(string serviceName);
        Task<(bool Success, string Message)> RestartServiceAsync(string serviceName);
    }

    public class WindowsServiceManager : IWindowsServiceManager
    {
        public List<ServiceItem> GetServices()
        {
            var items = new List<ServiceItem>();
            try
            {
                var controllers = ServiceController.GetServices();
                foreach (var sc in controllers)
                {
                    try
                    {
                        string status = sc.Status.ToString();
                        string startup = "Unknown";
                        try
                        {
                            startup = sc.StartType.ToString();
                        }
                        catch { }

                        var item = new ServiceItem
                        {
                            ServiceName = sc.ServiceName,
                            DisplayName = string.IsNullOrWhiteSpace(sc.DisplayName) ? sc.ServiceName : sc.DisplayName,
                            Status = status,
                            StartupType = startup
                        };
                        item.UpdateDisplayText();
                        items.Add(item);
                    }
                    catch { }
                    finally
                    {
                        sc.Dispose();
                    }
                }
            }
            catch { }

            return items.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task<(bool Success, string Message)> StartServiceAsync(string serviceName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var sc = new ServiceController(serviceName);
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        return (true, $"Service '{sc.DisplayName}' is already running.");
                    }

                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
                    return (true, $"Service '{sc.DisplayName}' started successfully.");
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 5) // ERROR_ACCESS_DENIED
                {
                    return (false, $"Access denied. Administrator privileges are required to start '{serviceName}'. Please restart Resource Analyzer in Administrator Mode.");
                }
                catch (Exception ex)
                {
                    return (false, $"Failed to start service: {ex.Message}");
                }
            }).ConfigureAwait(false);
        }

        public async Task<(bool Success, string Message)> StopServiceAsync(string serviceName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var sc = new ServiceController(serviceName);
                    if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        return (true, $"Service '{sc.DisplayName}' is already stopped.");
                    }

                    if (!sc.CanStop)
                    {
                        return (false, $"Service '{sc.DisplayName}' cannot be stopped.");
                    }

                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                    return (true, $"Service '{sc.DisplayName}' stopped successfully.");
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 5) // ERROR_ACCESS_DENIED
                {
                    return (false, $"Access denied. Administrator privileges are required to stop '{serviceName}'. Please restart Resource Analyzer in Administrator Mode.");
                }
                catch (Exception ex)
                {
                    return (false, $"Failed to stop service: {ex.Message}");
                }
            }).ConfigureAwait(false);
        }

        public async Task<(bool Success, string Message)> RestartServiceAsync(string serviceName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var sc = new ServiceController(serviceName);
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        if (!sc.CanStop)
                        {
                            return (false, $"Service '{sc.DisplayName}' cannot be stopped.");
                        }

                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                    }

                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(5));
                    return (true, $"Service '{sc.DisplayName}' restarted successfully.");
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
                {
                    return (false, $"Access denied. Administrator privileges are required to restart '{serviceName}'. Please restart Resource Analyzer in Administrator Mode.");
                }
                catch (Exception ex)
                {
                    return (false, $"Failed to restart service: {ex.Message}");
                }
            }).ConfigureAwait(false);
        }
    }
}
