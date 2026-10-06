using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ResourceAnalyzer.Helpers;
using ResourceAnalyzer.Services;

using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace ResourceAnalyzer.Views
{
    public class DetailItem : INotifyPropertyChanged
    {
        private string _text = string.Empty;
        public string Text
        {
            get => _text;
            set
            {
                if (_text != value)
                {
                    _text = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => Text;
    }

    public partial class ResourceDetailDialog : Window
    {
        private readonly ObservableCollection<DetailItem> _detailItems = new();
        private string _fullDetailsText;
        private readonly IHardwareDetailService? _hardwareDetailService;
        private readonly IScreenReaderService? _speechService;
        private readonly string? _resourceId;
        private readonly DispatcherTimer? _liveTimer;

        public ResourceDetailDialog(
            string title,
            string details,
            IHardwareDetailService? hardwareDetailService = null,
            IScreenReaderService? speechService = null,
            string? resourceId = null,
            int refreshIntervalSeconds = 0)
        {
            InitializeComponent();
            _fullDetailsText = details;
            _hardwareDetailService = hardwareDetailService;
            _speechService = speechService;
            _resourceId = resourceId;

            Title = $"{title} Technical Specifications";
            txtTitle.Text = $"{title} Technical Specifications";

            lstDetails.ItemsSource = _detailItems;
            PopulateDetails(details);

            if (string.Equals(resourceId, "ram", StringComparison.OrdinalIgnoreCase) && _hardwareDetailService != null)
            {
                btnFlushMemory.Visibility = Visibility.Visible;
            }

            if (refreshIntervalSeconds > 0 && _hardwareDetailService != null && !string.IsNullOrEmpty(_resourceId))
            {
                _liveTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(refreshIntervalSeconds)
                };
                _liveTimer.Tick += async (s, e) =>
                {
                    try
                    {
                        string fresh = await _hardwareDetailService.GetHardwareDetailsAsync(_resourceId);
                        UpdateDetailsInPlace(fresh);
                    }
                    catch { }
                };
                _liveTimer.Start();
            }

            Closed += (s, e) => _liveTimer?.Stop();
            Loaded += (s, e) => lstDetails.Focus();
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    Close();
                }
            };
        }

        private void PopulateDetails(string details)
        {
            _fullDetailsText = details;
            var lines = details.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToList();

            _detailItems.Clear();
            foreach (var l in lines)
            {
                _detailItems.Add(new DetailItem { Text = l });
            }

            if (_detailItems.Count > 0)
            {
                lstDetails.SelectedIndex = 0;
            }
        }

        private void UpdateDetailsInPlace(string details)
        {
            _fullDetailsText = details;
            var newLines = details.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l))
                .ToList();

            int savedIndex = lstDetails.SelectedIndex;

            // In-place update of existing items so ListBoxItem containers and keyboard focus are never destroyed
            int commonCount = Math.Min(_detailItems.Count, newLines.Count);
            for (int i = 0; i < commonCount; i++)
            {
                if (_detailItems[i].Text != newLines[i])
                {
                    _detailItems[i].Text = newLines[i];
                }
            }

            // Append newly added lines if any
            if (newLines.Count > _detailItems.Count)
            {
                for (int i = commonCount; i < newLines.Count; i++)
                {
                    _detailItems.Add(new DetailItem { Text = newLines[i] });
                }
            }
            // Remove excess items from the tail if line count decreased
            else if (_detailItems.Count > newLines.Count)
            {
                for (int i = _detailItems.Count - 1; i >= commonCount; i--)
                {
                    _detailItems.RemoveAt(i);
                }
            }

            // Preserve selection index so cursor position is never reset
            if (savedIndex >= 0 && _detailItems.Count > 0)
            {
                int targetIndex = Math.Min(savedIndex, _detailItems.Count - 1);
                if (lstDetails.SelectedIndex != targetIndex)
                {
                    lstDetails.SelectedIndex = targetIndex;
                }
            }
        }

        private void LstDetails_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                CopyFullDetails();
            }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.None && btnFlushMemory.Visibility == Visibility.Visible && btnFlushMemory.IsEnabled)
            {
                e.Handled = true;
                FlushMemory();
            }
        }

        private void BtnFlushMemory_Click(object sender, RoutedEventArgs e)
        {
            FlushMemory();
        }

        private async void FlushMemory()
        {
            if (_hardwareDetailService == null || !btnFlushMemory.IsEnabled)
                return;

            btnFlushMemory.IsEnabled = false;
            btnFlushMemory.Content = "Flushing Cache...";
            _speechService?.Speak("Flushing standby memory and trimming working sets...", interrupt: true);

            try
            {
                var (trimmedCount, freedBytes, updatedDetails) = await _hardwareDetailService.FlushMemoryCacheAsync();
                PopulateDetails(updatedDetails);
                lstDetails.Focus();

                string freedMsg;
                if (freedBytes > 0)
                {
                    freedMsg = $"Memory cache flushed. Reclaimed {FormatHelper.FormatBytes(freedBytes)} across {trimmedCount} processes.";
                }
                else
                {
                    freedMsg = $"Memory cache flushed. Optimized {trimmedCount} processes.";
                }

                _speechService?.Speak(freedMsg, interrupt: true);
                btnFlushMemory.Content = "Flushed!";
            }
            catch (Exception ex)
            {
                _speechService?.Speak($"Failed to flush memory cache: {ex.Message}", interrupt: true);
                btnFlushMemory.Content = "Flush Failed";
            }
            finally
            {
                await Task.Delay(2000);
                btnFlushMemory.Content = "Flush Memory Cache";
                btnFlushMemory.IsEnabled = true;
            }
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            CopyFullDetails();
        }

        private void CopyFullDetails()
        {
            try
            {
                Clipboard.SetText(_fullDetailsText);
                btnCopy.Content = "Copied!";
            }
            catch { }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
