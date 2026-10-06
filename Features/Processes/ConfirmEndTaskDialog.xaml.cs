using System.Windows;

namespace ResourceAnalyzer.Views
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

            KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    e.Handled = true;
                    BtnEndTask_Click(btnEndTask, new RoutedEventArgs());
                }
                else if (e.Key == System.Windows.Input.Key.Escape)
                {
                    e.Handled = true;
                    BtnCancel_Click(btnCancel, new RoutedEventArgs());
                }
            };
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
