using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ResourceAnalyzer.Models;
using ResourceAnalyzer.Views;

namespace ResourceAnalyzer
{
    public partial class MainWindow : Window
    {
        private bool _isOpeningResourceDetail = false;

        private void BuildResourceItemList()
        {
            _resourceItems.Clear();
            _resourceMap.Clear();

            var settings = _settingsService.CurrentSettings;

            if (settings.ShowCpu) AddResourceItem("cpu", "CPU");
            if (settings.ShowRam) AddResourceItem("ram", "RAM");
            if (settings.ShowGpu) AddResourceItem("gpu", "GPU");
            if (settings.ShowDisplay) AddResourceItem("display", "Display");
            if (settings.ShowNetwork) AddResourceItem("network", "Network");
            if (settings.ShowDisk) AddResourceItem("disk", "Disk 0 (Internal SSD)");
            if (settings.ShowBattery && _monitorService.HasBattery) AddResourceItem("battery", "Battery");

            if (_resourceItems.Count > 0 && lstResources.SelectedIndex < 0)
            {
                lstResources.SelectedIndex = 0;
            }
        }

        private void AddResourceItem(string id, string name)
        {
            var item = new ResourceItem
            {
                Id = id,
                Name = name,
                Summary = $"{name}: Loading..."
            };
            _resourceItems.Add(item);
            _resourceMap[id] = item;
        }

        private async Task RefreshResourcesAsync()
        {
            try
            {
                var metrics = await _monitorService.SampleMetricsAsync();

                foreach (var kvp in metrics)
                {
                    if (_resourceMap.TryGetValue(kvp.Key, out var item))
                    {
                        item.Summary = kvp.Value.Summary;
                        item.Details = kvp.Value.Details;
                        item.Percent = kvp.Value.Percent;
                        if (kvp.Key.StartsWith("disk", StringComparison.OrdinalIgnoreCase) ||
                            kvp.Key.StartsWith("usb", StringComparison.OrdinalIgnoreCase))
                        {
                            int colonIdx = kvp.Value.Summary.IndexOf(':');
                            if (colonIdx > 0)
                            {
                                item.Name = kvp.Value.Summary.Substring(0, colonIdx).Trim();
                            }
                        }
                    }
                    else if (_settingsService.CurrentSettings.ShowDisk &&
                             (kvp.Key.StartsWith("disk_", StringComparison.OrdinalIgnoreCase) ||
                              kvp.Key.StartsWith("usb_", StringComparison.OrdinalIgnoreCase)))
                    {
                        string itemName = kvp.Key;
                        int colonIdx = kvp.Value.Summary.IndexOf(':');
                        if (colonIdx > 0)
                        {
                            itemName = kvp.Value.Summary.Substring(0, colonIdx).Trim();
                        }

                        var newItem = new ResourceItem
                        {
                            Id = kvp.Key,
                            Name = itemName,
                            Summary = kvp.Value.Summary,
                            Details = kvp.Value.Details,
                            Percent = kvp.Value.Percent
                        };

                        int insertIndex = _resourceItems.Count;
                        int lastDiskIndex = -1;
                        for (int i = 0; i < _resourceItems.Count; i++)
                        {
                            if (_resourceItems[i].Id.StartsWith("disk", StringComparison.OrdinalIgnoreCase) ||
                                _resourceItems[i].Id.StartsWith("usb", StringComparison.OrdinalIgnoreCase))
                            {
                                lastDiskIndex = i;
                            }
                        }

                        if (lastDiskIndex >= 0)
                        {
                            insertIndex = lastDiskIndex + 1;
                        }

                        _resourceItems.Insert(insertIndex, newItem);
                        _resourceMap[kvp.Key] = newItem;
                    }
                }

                // Remove unplugged USB drives or detached secondary disks
                var itemsToRemove = new List<ResourceItem>();
                foreach (var item in _resourceItems)
                {
                    if ((item.Id.StartsWith("usb_", StringComparison.OrdinalIgnoreCase) ||
                         item.Id.StartsWith("disk_", StringComparison.OrdinalIgnoreCase)) &&
                        !metrics.ContainsKey(item.Id))
                    {
                        itemsToRemove.Add(item);
                    }
                }

                foreach (var item in itemsToRemove)
                {
                    _resourceItems.Remove(item);
                    _resourceMap.Remove(item.Id);
                }

                if (lstResources.SelectedIndex < 0 && _resourceItems.Count > 0)
                {
                    lstResources.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error sampling resources: {ex.Message}");
            }
        }

        private void LstResources_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ShowSelectedResourceDetail();
            }
            else if (e.Key == Key.F5)
            {
                e.Handled = true;
                _ = RefreshAllAsync(announce: true);
            }
        }

        private void LstResources_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ShowSelectedResourceDetail();
        }

        private async void ShowSelectedResourceDetail()
        {
            if (_isOpeningResourceDetail) return;
            _isOpeningResourceDetail = true;

            try
            {
                if (lstResources.SelectedItem is ResourceItem item)
                {
                    string details = await _hardwareDetailService.GetHardwareDetailsAsync(item.Id);
                    var dlg = new ResourceDetailDialog(item.Name, details, _hardwareDetailService, _speechService, item.Id, _settingsService.CurrentSettings.RefreshIntervalSeconds)
                    {
                        Owner = this
                    };
                    dlg.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error opening hardware details: {ex.Message}");
            }
            finally
            {
                _isOpeningResourceDetail = false;
            }
        }
    }
}
