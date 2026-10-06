using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceAnalyzer.Helpers;
using ResourceAnalyzer.Models;
using ResourceAnalyzer.Views;

namespace ResourceAnalyzer
{
    public partial class MainWindow : Window
    {
        #region Network Ports Tab Handlers

        private async Task RefreshNetworkPortsAsync(bool isFullReset = false, bool announce = false)
        {
            try
            {
                var ports = await _networkPortService.GetNetworkPortsAsync();
                _rawNetworkPortList = ports;

                var filtered = FilterAndSortPorts(ports);
                UpdateNetworkPortCollectionInPlace(filtered, isFullReset);

                int listeningCount = ports.Count(p => p.IsListening);
                int establishedCount = ports.Count(p => p.IsEstablished);
                int totalCount = ports.Count;

                if (!string.IsNullOrWhiteSpace(txtPortSearch?.Text))
                {
                    txtPortCount.Text = $"Showing {filtered.Count} of {totalCount} ports ({listeningCount} listening, {establishedCount} established). Press Delete to end task, Enter for details.";
                }
                else
                {
                    txtPortCount.Text = $"{totalCount} ports & connections ({listeningCount} listening, {establishedCount} established). Press Delete to end task, Enter for details.";
                }

                if (announce)
                {
                    string msg = $"{filtered.Count} network ports and connections.";
                    _speechService.Speak(msg, interrupt: true);
                    txtAnnouncement.Text = msg;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error refreshing network ports: {ex.Message}");
            }
        }

        private List<NetworkPortItem> FilterAndSortPorts(List<NetworkPortItem> source)
        {
            var query = source.AsEnumerable();

            // View filter
            int filterIdx = cmbPortFilter?.SelectedIndex ?? 0;
            query = filterIdx switch
            {
                0 => query.Where(p => p.IsListening),
                2 => query.Where(p => p.IsEstablished),
                _ => query // 1 = All Ports & Connections
            };

            // Search filter
            string search = txtPortSearch?.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(p =>
                    p.LocalPort.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    p.RemotePort.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    p.ProcessName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    p.FriendlyName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    p.ProcessId.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    p.LocalAddress.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    p.RemoteAddress.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    p.Protocol.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    p.State.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            // Sorting
            query = _currentPortSort switch
            {
                "App" => query.OrderBy(p => p.ProcessName).ThenBy(p => p.LocalPort),
                "State" => query.OrderBy(p => p.State).ThenBy(p => p.LocalPort),
                _ => query.OrderBy(p => p.LocalPort).ThenBy(p => p.Protocol) // Default "Port"
            };

            return query.ToList();
        }

        private void UpdateNetworkPortCollectionInPlace(List<NetworkPortItem> newItems, bool isFullReset = false)
        {
            CollectionHelper.SynchronizeInPlace(
                _networkPortItems,
                _networkPortMap,
                newItems,
                p => p.Key,
                (existing, incoming) =>
                {
                    if (existing.State != incoming.State || existing.ProcessName != incoming.ProcessName || existing.FriendlyName != incoming.FriendlyName)
                    {
                        existing.State = incoming.State;
                        existing.ProcessName = incoming.ProcessName;
                        existing.FriendlyName = incoming.FriendlyName;
                        existing.ProcessPath = incoming.ProcessPath;
                        existing.UpdateDisplayText();
                    }
                },
                lstNetworkPorts,
                isFullReset,
                StringComparer.OrdinalIgnoreCase);
        }

        private void BtnSortPort_Click(object sender, RoutedEventArgs e) => SetPortSort("Port");
        private void BtnSortPortApp_Click(object sender, RoutedEventArgs e) => SetPortSort("App");
        private void BtnSortPortState_Click(object sender, RoutedEventArgs e) => SetPortSort("State");

        private void SetPortSort(string sortBy)
        {
            _currentPortSort = sortBy;
            var filtered = FilterAndSortPorts(_rawNetworkPortList);
            UpdateNetworkPortCollectionInPlace(filtered, isFullReset: true);
            _speechService.Speak($"Sorted by {sortBy}.", interrupt: true);
            txtAnnouncement.Text = $"Sorted by {sortBy}.";
        }

        private void BtnRefreshPorts_Click(object sender, RoutedEventArgs e)
        {
            _ = RefreshNetworkPortsAsync(isFullReset: false, announce: true);
        }

        private void CmbPortFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitialized) return;
            var filtered = FilterAndSortPorts(_rawNetworkPortList);
            UpdateNetworkPortCollectionInPlace(filtered, isFullReset: true);
            string viewName = cmbPortFilter.SelectedItem is ComboBoxItem cbi ? (cbi.Content?.ToString() ?? "Filter") : "Filter";
            _speechService.Speak($"View: {viewName}. {filtered.Count} items.", interrupt: true);
            txtAnnouncement.Text = $"View: {viewName}. {filtered.Count} items.";
        }

        private void TxtPortSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (tabNetworkPorts.IsSelected)
            {
                var filtered = FilterAndSortPorts(_rawNetworkPortList);
                UpdateNetworkPortCollectionInPlace(filtered, isFullReset: true);
            }
        }

        private void TxtPortSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down)
            {
                e.Handled = true;
                lstNetworkPorts.Focus();
                if (_networkPortItems.Count > 0 && lstNetworkPorts.SelectedIndex < 0)
                {
                    lstNetworkPorts.SelectedIndex = 0;
                }
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                txtPortSearch.Text = string.Empty;
                lstNetworkPorts.Focus();
            }
        }

        private async void LstNetworkPorts_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                await RefreshNetworkPortsAsync(isFullReset: false, announce: true);
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ShowSelectedPortDetails();
            }
            else if (e.Key == Key.Delete)
            {
                e.Handled = true;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    await TryEndSelectedPortProcessTreeAsync();
                }
                else
                {
                    await TryEndSelectedPortTaskAsync();
                }
            }
            else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                CopySelectedPortDetails();
            }
        }

        private void LstNetworkPorts_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ShowSelectedPortDetails();
        }

        private void ShowSelectedPortDetails()
        {
            if (lstNetworkPorts.SelectedItem is not NetworkPortItem item) return;

            string endpointInfo = item.IsListening
                ? $"Local Address: {item.LocalAddress}\r\nLocal Port: {item.LocalPort} ({item.Protocol})"
                : $"Local Endpoint: {item.LocalAddress}:{item.LocalPort}\r\nRemote Endpoint: {item.RemoteAddress}:{item.RemotePort}\r\nProtocol: {item.Protocol}\r\nState: {item.State}";

            string details = $"Protocol: {item.Protocol}\r\n" +
                             $"Connection State: {item.State}\r\n" +
                             $"{endpointInfo}\r\n" +
                             $"Application Name: {item.ProcessName}\r\n" +
                             $"Description: {(string.IsNullOrEmpty(item.FriendlyName) ? item.ProcessName : item.FriendlyName)}\r\n" +
                             $"Process ID (PID): {item.ProcessId}\r\n" +
                             $"Executable Path: {(string.IsNullOrEmpty(item.ProcessPath) ? "Unknown or System Protected" : item.ProcessPath)}";

            var dlg = new ResourceDetailDialog($"Port {item.LocalPort} ({item.Protocol})", details)
            {
                Owner = this
            };
            dlg.ShowDialog();
        }

        private void CopySelectedPortDetails()
        {
            if (lstNetworkPorts.SelectedItem is not NetworkPortItem item) return;

            try
            {
                Clipboard.SetText(item.DisplayText);
                _speechService.Speak($"Copied port {item.LocalPort} details to clipboard.", interrupt: true);
                txtAnnouncement.Text = $"Copied port {item.LocalPort} details to clipboard.";
            }
            catch { }
        }

        private async Task TryEndSelectedPortTaskAsync()
        {
            if (lstNetworkPorts.SelectedItem is not NetworkPortItem item) return;

            if (item.ProcessId <= 4)
            {
                _speechService.Speak("Cannot end Windows system process.", interrupt: true);
                txtAnnouncement.Text = "Cannot end Windows system process.";
                return;
            }

            bool confirm = _settingsService.CurrentSettings.ConfirmBeforeEndTask;
            if (confirm)
            {
                var dlg = new ConfirmEndTaskDialog($"{item.ProcessName} (PID {item.ProcessId}) on port {item.LocalPort}", item.ProcessId)
                {
                    Owner = this
                };

                bool? result = dlg.ShowDialog();
                if (result != true || !dlg.Confirmed)
                {
                    _speechService.Speak("Task termination canceled.", interrupt: true);
                    return;
                }
            }

            var (success, msg) = await _processService.KillProcessAsync(item.ProcessId, item.ProcessName);
            _speechService.Speak(msg, interrupt: true);
            txtAnnouncement.Text = msg;

            if (success)
            {
                await RefreshNetworkPortsAsync(isFullReset: true);
            }
        }

        private async Task TryEndSelectedPortProcessTreeAsync()
        {
            if (lstNetworkPorts.SelectedItem is not NetworkPortItem item) return;

            if (item.ProcessId <= 4)
            {
                _speechService.Speak("Cannot end Windows system process.", interrupt: true);
                txtAnnouncement.Text = "Cannot end Windows system process.";
                return;
            }

            bool confirm = _settingsService.CurrentSettings.ConfirmBeforeEndTask;
            if (confirm)
            {
                var dlg = new ConfirmEndTaskDialog($"entire process tree of {item.ProcessName} (PID {item.ProcessId}) on port {item.LocalPort}", item.ProcessId)
                {
                    Owner = this
                };

                bool? result = dlg.ShowDialog();
                if (result != true || !dlg.Confirmed)
                {
                    _speechService.Speak("Task termination canceled.", interrupt: true);
                    return;
                }
            }

            var (success, msg) = await _processService.KillProcessTreeAsync(item.ProcessId, item.ProcessName);
            _speechService.Speak(msg, interrupt: true);
            txtAnnouncement.Text = msg;

            if (success)
            {
                await RefreshNetworkPortsAsync(isFullReset: true);
            }
        }

        private async void CtxPortEndTask_Click(object sender, RoutedEventArgs e)
        {
            await TryEndSelectedPortTaskAsync();
        }

        private async void CtxPortEndProcessTree_Click(object sender, RoutedEventArgs e)
        {
            await TryEndSelectedPortProcessTreeAsync();
        }

        private void CtxPortOpenFileLocation_Click(object sender, RoutedEventArgs e)
        {
            if (lstNetworkPorts.SelectedItem is not NetworkPortItem item) return;
            if (!string.IsNullOrEmpty(item.ProcessPath) && File.Exists(item.ProcessPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.ProcessPath}\"") { UseShellExecute = true });
                }
                catch { }
            }
            else
            {
                _speechService.Speak("File location unavailable for this process.", interrupt: true);
                txtAnnouncement.Text = "File location unavailable for this process.";
            }
        }

        private void CtxPortDetails_Click(object sender, RoutedEventArgs e)
        {
            ShowSelectedPortDetails();
        }

        private void CtxPortCopyDetails_Click(object sender, RoutedEventArgs e)
        {
            CopySelectedPortDetails();
        }

        #endregion
    }
}
