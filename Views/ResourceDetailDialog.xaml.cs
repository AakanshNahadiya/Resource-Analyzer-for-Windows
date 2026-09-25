using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AccessibleTaskManager.Helpers;
using AccessibleTaskManager.Services;

namespace AccessibleTaskManager.Views
{
    public partial class ResourceDetailDialog : Window
    {
        private string _fullDetailsText;
        private readonly IHardwareDetailService? _hardwareDetailService;
        private readonly IScreenReaderService? _speechService;
        private readonly string? _resourceId;

        public ResourceDetailDialog(
            string title,
            string details,
            IHardwareDetailService? hardwareDetailService = null,
            IScreenReaderService? speechService = null,
            string? resourceId = null)
        {
            InitializeComponent();
            _fullDetailsText = details;
            _hardwareDetailService = hardwareDetailService;
            _speechService = speechService;
            _resourceId = resourceId;

            Title = $"{title} Technical Specifications";
            txtTitle.Text = $"{title} Technical Specifications";

            PopulateDetails(details);

            if (string.Equals(resourceId, "ram", StringComparison.OrdinalIgnoreCase) && _hardwareDetailService != null)
            {
                btnFlushMemory.Visibility = Visibility.Visible;
            }

            Loaded += (s, e) => lstDetails.Focus();
        }

        private void PopulateDetails(string details)
        {
            _fullDetailsText = details;
            var lines = details.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            lstDetails.ItemsSource = lines;
            if (lines.Count > 0)
            {
                lstDetails.SelectedIndex = 0;
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
                btnFlushMemory.Content = "_Flush Memory Cache (F)";
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
