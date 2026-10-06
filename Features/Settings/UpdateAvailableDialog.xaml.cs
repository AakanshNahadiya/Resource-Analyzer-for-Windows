using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ResourceAnalyzer.Services;

namespace ResourceAnalyzer.Views
{
    public partial class UpdateAvailableDialog : Window
    {
        private readonly UpdateInfo _updateInfo;
        private readonly IUpdateService _updateService;
        private readonly IScreenReaderService? _speechService;
        private readonly CancellationTokenSource _cts = new();
        private bool _isDownloading = false;

        public UpdateAvailableDialog(UpdateInfo updateInfo, IUpdateService updateService, IScreenReaderService? speechService = null)
        {
            InitializeComponent();
            _updateInfo = updateInfo;
            _updateService = updateService;
            _speechService = speechService;

            txtTitle.Text = $"Update Available: v{_updateInfo.LatestVersion}";
            txtVersionInfo.Text = $"Resource Analyzer for Windows v{_updateInfo.LatestVersion} is available (Current version: v{_updateInfo.CurrentVersion}).";

            if (!string.IsNullOrWhiteSpace(_updateInfo.ReleaseNotes))
            {
                txtReleaseNotes.Text = _updateInfo.ReleaseNotes;
            }
            else if (!string.IsNullOrWhiteSpace(_updateInfo.ReleaseName))
            {
                txtReleaseNotes.Text = _updateInfo.ReleaseName;
            }
            else
            {
                txtReleaseNotes.Text = "No release notes provided for this version.";
            }

            Loaded += (s, e) => btnDownloadInstall.Focus();

            KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter && !_isDownloading)
                {
                    e.Handled = true;
                    BtnDownloadInstall_Click(btnDownloadInstall, new RoutedEventArgs());
                }
                else if (e.Key == System.Windows.Input.Key.Escape)
                {
                    e.Handled = true;
                    BtnNotNow_Click(btnNotNow, new RoutedEventArgs());
                }
            };
        }

        private async void BtnDownloadInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloading) return;

            string? downloadUrl = _updateInfo.DownloadUrl;
            if (string.IsNullOrEmpty(downloadUrl))
            {
                // Fallback to browser if installer asset is not directly available
                _updateService.OpenUrl(_updateInfo.HtmlUrl);
                Close();
                return;
            }

            _isDownloading = true;
            btnDownloadInstall.IsEnabled = false;
            btnNotNow.IsEnabled = false;
            pbDownload.Visibility = Visibility.Visible;
            pbDownload.Value = 0;

            txtDownloadStatus.Text = "Downloading update from GitHub...";
            _speechService?.Speak("Downloading update from GitHub.", interrupt: true);

            var progress = new Progress<int>(percent =>
            {
                pbDownload.Value = percent;
                txtDownloadStatus.Text = $"Downloading update... {percent}%";
                if (percent % 25 == 0 && percent > 0 && percent < 100)
                {
                    _speechService?.Speak($"Download progress: {percent} percent.", interrupt: false);
                }
            });

            try
            {
                string? installerPath = await _updateService.DownloadInstallerAsync(downloadUrl, progress, _cts.Token);
                if (!string.IsNullOrEmpty(installerPath) && File.Exists(installerPath))
                {
                    txtDownloadStatus.Text = "Download complete. Starting installer...";
                    _speechService?.Speak("Download complete. Starting installer.", interrupt: true);

                    await Task.Delay(600);

                    bool launched = _updateService.LaunchInstaller(installerPath);
                    if (launched)
                    {
                        // Cleanly shut down the application so the installer can replace files
                        Application.Current.Shutdown();
                        return;
                    }
                    else
                    {
                        MessageBox.Show(this, "Failed to launch the downloaded installer.", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else
                {
                    txtDownloadStatus.Text = "Failed to download update. Please try again or visit GitHub.";
                    _speechService?.Speak("Failed to download update.", interrupt: true);
                    btnDownloadInstall.IsEnabled = true;
                    btnNotNow.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                txtDownloadStatus.Text = $"Download error: {ex.Message}";
                _speechService?.Speak("Update download error.", interrupt: true);
                btnDownloadInstall.IsEnabled = true;
                btnNotNow.IsEnabled = true;
            }
            finally
            {
                _isDownloading = false;
            }
        }

        private void BtnNotNow_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloading)
            {
                _cts.Cancel();
            }
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_isDownloading)
            {
                _cts.Cancel();
            }
            base.OnClosed(e);
        }
    }
}
