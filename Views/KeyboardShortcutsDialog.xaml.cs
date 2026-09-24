using System.Collections.Generic;
using System.Windows;

namespace AccessibleTaskManager.Views
{
    public partial class KeyboardShortcutsDialog : Window
    {
        public KeyboardShortcutsDialog()
        {
            InitializeComponent();

            var shortcuts = new List<string>
            {
                "Ctrl + 1 : Switch to Tab 1 (Resources)",
                "Ctrl + 2 : Switch to Tab 2 (Processes)",
                "Ctrl + 3 : Switch to Tab 3 (Windows Services)",
                "Ctrl + 4 : Switch to Tab 4 (Data Usage)",
                "Ctrl + 5 : Switch to Tab 5 (Settings)",
                "Ctrl + Shift + S : Open Windows Startup Apps Settings",
                "Ctrl + Shift + A : Restart Resource Analyzer in Administrator Mode",
                "F1 or Shift + / : Open this Keyboard Shortcuts Help dialog",
                "F5 : Refresh current tab data and announce status",
                "Enter : View detailed technical hardware information (Resources tab)",
                "Ctrl + F : Search and filter items in the active tab",
                "Down Arrow (in Search) : Move directly into the filtered list",
                "Escape (in Search) : Clear search filter and focus list",
                "Ctrl + M : Sort processes by Memory (RAM) usage",
                "Ctrl + C : Sort processes by CPU usage",
                "Ctrl + N : Sort processes alphabetically by Name",
                "Ctrl + G : Toggle grouping multiple process instances into applications",
                "Right Arrow : Expand grouped application to see sub-processes (Processes tab)",
                "Left Arrow : Collapse grouped application or return to parent (Processes tab)",
                "Delete : End selected task (with confirmation prompt)",
                "Enter or Space (Services tab) : Start or Stop selected Windows Service",
                "Ctrl + R (Services tab) : Restart selected Windows Service",
                "Ctrl + Win + T : Global shortcut to show or toggle Resource Analyzer for Windows",
                "Ctrl + Win + I : Global shortcut to hear network speed anytime from anywhere",
                "Escape : Close this dialog"
            };

            lstShortcuts.ItemsSource = shortcuts;
            if (shortcuts.Count > 0)
            {
                lstShortcuts.SelectedIndex = 0;
            }

            Loaded += (s, e) => lstShortcuts.Focus();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
