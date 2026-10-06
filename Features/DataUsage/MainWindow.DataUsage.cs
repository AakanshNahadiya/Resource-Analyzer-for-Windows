using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceAnalyzer.Helpers;
using ResourceAnalyzer.Models;

namespace ResourceAnalyzer
{
    public partial class MainWindow : Window
    {
        #region Data Usage Management

        private async Task RefreshDataUsageAsync(bool announce = false)
        {
            try
            {
                bool currentOnly = cmbDataNetwork.SelectedIndex != 1; // 0 = Current (Default), 1 = All Networks
                string timeRange = cmbDataTimeRange.SelectedIndex switch
                {
                    0 => "Full",
                    1 => "Last Month",
                    2 => "Last Week",
                    3 => "Last 24 Hours",
                    4 => "Today",
                    _ => "Full"
                };

                string search = txtDataSearch.Text;
                var (apps, totalRecv, totalSent) = await _dataUsageService.GetDataUsageAsync(timeRange, currentOnly, search);

                long grandTotal = totalRecv + totalSent;
                string summary = $"Showing {apps.Count} applications. Total: {FormatHelper.FormatBytes(grandTotal)} (Down: {FormatHelper.FormatBytes(totalRecv)}, Up: {FormatHelper.FormatBytes(totalSent)}).";
                txtDataSummary.Text = summary;

                _dataUsageItems.Clear();
                _dataUsageMap.Clear();
                foreach (var a in apps)
                {
                    _dataUsageItems.Add(a);
                    _dataUsageMap[a.AppName] = a;
                }

                if (_dataUsageItems.Count > 0 && lstDataUsage.SelectedIndex < 0)
                {
                    lstDataUsage.SelectedIndex = 0;
                }

                if (announce)
                {
                    _speechService.Speak(summary, interrupt: true);
                    txtAnnouncement.Text = summary;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error querying data usage: {ex.Message}");
                txtDataSummary.Text = "Unable to load data usage on this connection.";
            }
        }

        private async Task RefreshDataUsageRealtimeAsync()
        {
            try
            {
                bool currentOnly = cmbDataNetwork.SelectedIndex != 1; // 0 = Current (Default), 1 = All Networks
                string timeRange = cmbDataTimeRange.SelectedIndex switch
                {
                    0 => "Full",
                    1 => "Last Month",
                    2 => "Last Week",
                    3 => "Last 24 Hours",
                    4 => "Today",
                    _ => "Full"
                };

                string search = txtDataSearch.Text;
                var (apps, totalRecv, totalSent) = await _dataUsageService.GetDataUsageAsync(timeRange, currentOnly, search);

                long grandTotal = totalRecv + totalSent;
                string summary = $"Showing {apps.Count} applications. Total: {FormatHelper.FormatBytes(grandTotal)} (Down: {FormatHelper.FormatBytes(totalRecv)}, Up: {FormatHelper.FormatBytes(totalSent)}).";
                txtDataSummary.Text = summary;

                UpdateDataUsageCollectionInPlace(apps);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in realtime data usage scan: {ex.Message}");
            }
        }

        private void UpdateDataUsageCollectionInPlace(List<AppDataUsageItem> newItems)
        {
            CollectionHelper.SynchronizeInPlace(
                _dataUsageItems,
                _dataUsageMap,
                newItems,
                a => a.AppName,
                (existing, incoming) =>
                {
                    existing.BytesReceived = incoming.BytesReceived;
                    existing.BytesSent = incoming.BytesSent;
                    existing.UsagePercent = incoming.UsagePercent;
                },
                lstDataUsage,
                isFullReset: false,
                StringComparer.OrdinalIgnoreCase);
        }

        private void CmbDataFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI) return;
            _ = RefreshDataUsageAsync(announce: false);
            SaveSettingsFromUI(silent: true);
        }

        private void TxtDataSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (tabDataUsage.IsSelected)
            {
                _ = RefreshDataUsageAsync(announce: false);
            }
        }

        private void TxtDataSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down)
            {
                e.Handled = true;
                lstDataUsage.Focus();
                if (_dataUsageItems.Count > 0 && lstDataUsage.SelectedIndex < 0)
                {
                    lstDataUsage.SelectedIndex = 0;
                }
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                txtDataSearch.Text = string.Empty;
                lstDataUsage.Focus();
            }
        }

        private void LstDataUsage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                _ = RefreshDataUsageAsync(announce: true);
            }
        }

        #endregion
    }
}
