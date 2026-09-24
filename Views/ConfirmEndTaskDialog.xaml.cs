using System.Windows;

namespace AccessibleTaskManager.Views
{
    public partial class ConfirmEndTaskDialog : Window
    {
        public bool Confirmed { get; private set; }
        public bool DisableFuturePrompts => chkDontAskAgain.IsChecked == true;

        public ConfirmEndTaskDialog(string processName, int pid)
        {
            InitializeComponent();
            txtPrompt.Text = pid > 0
                ? $"Are you sure you want to end {processName} (PID {pid})?"
                : $"Are you sure you want to end {processName}?";
            btnEndTask.Focus();
        }

        private void BtnEndTask_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Confirmed = false;
            DialogResult = false;
            Close();
        }
    }
}
