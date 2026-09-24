using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace AccessibleTaskManager.Views
{
    public partial class ResourceDetailDialog : Window
    {
        private readonly string _fullDetailsText;

        public ResourceDetailDialog(string title, string details)
        {
            InitializeComponent();
            _fullDetailsText = details;

            Title = $"{title} Technical Specifications";
            txtTitle.Text = $"{title} Technical Specifications";

            var lines = details.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            lstDetails.ItemsSource = lines;
            if (lines.Count > 0)
            {
                lstDetails.SelectedIndex = 0;
            }

            Loaded += (s, e) => lstDetails.Focus();
        }

        private void LstDetails_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                CopyFullDetails();
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
