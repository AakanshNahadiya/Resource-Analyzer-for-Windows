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

            if (_updateInfo.HasUpdate)
            {
                Title = "Update Available - Resource Analyzer for Windows";
                txtTitle.Text = $"Update Available: v{_updateInfo.LatestVersion}";
                txtVersionInfo.Text = $"Resource Analyzer for Windows v{_updateInfo.LatestVersion} is available (Current version: v{_updateInfo.CurrentVersion}).";
                txtDownloadStatus.Text = "Would you like to download and install this update now?";

                scrollReleaseNotes.Visibility = Visibility.Visible;
                pnlDownloadProgress.Visibility = Visibility.Visible;

                btnDownloadInstall.Visibility = Visibility.Visible;
                btnSeeWhatsNew.Visibility = Visibility.Visible;
                btnNotNow.Visibility = Visibility.Visible;

                btnViewReleaseNotes.Visibility = Visibility.Collapsed;
                btnClose.Visibility = Visibility.Collapsed;

                Height = 390;

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
            }
            else
            {
                Title = "Up to Date - Resource Analyzer for Windows";
                txtTitle.Text = "Resource Analyzer is Up to Date";
                txtVersionInfo.Text = $"You have the latest version of Resource Analyzer for Windows (Version v{_updateInfo.CurrentVersion}).";

                // Completely collapse the release notes and download prompt sections to avoid misleading text
                scrollReleaseNotes.Visibility = Visibility.Collapsed;
                pnlDownloadProgress.Visibility = Visibility.Collapsed;

                // Download, Not Now, and See What's New are ONLY present when an update is available
                btnDownloadInstall.Visibility = Visibility.Collapsed;
                btnSeeWhatsNew.Visibility = Visibility.Collapsed;
                btnNotNow.Visibility = Visibility.Collapsed;

                // Up to Date shows only View Release Notes and Close
                btnViewReleaseNotes.Visibility = Visibility.Visible;
                btnClose.Visibility = Visibility.Visible;

                Height = 180;

                Loaded += (s, e) => btnViewReleaseNotes.Focus();
            }

            KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape)
                {
                    e.Handled = true;
                    if (_isDownloading)
                    {
                        _cts.Cancel();
                    }
                    Close();
                }
            };
        }

        private void BtnSeeWhatsNew_Click(object sender, RoutedEventArgs e)
        {
            string url = _updateInfo.GetEffectiveReleaseUrl();
            _updateService.OpenUrl(url);
            _speechService?.Speak("Opening release notes on GitHub.", interrupt: true);
        }

        private void BtnViewReleaseNotes_Click(object sender, RoutedEventArgs e)
        {
            string url = _updateInfo.GetEffectiveReleaseUrl();
            _updateService.OpenUrl(url);
            _speechService?.Speak("Opening release notes on GitHub.", interrupt: true);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
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
                string? installerPath = await _updateService.DownloadInstallerAsync(downloadUrl, _updateInfo.ExpectedSha256, progress, _cts.Token);
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
            catch (System.Security.SecurityException)
            {
                txtDownloadStatus.Text = "Security Error: Checksum verification failed. The installer was deleted for your safety.";
                _speechService?.Speak("Security alert. Downloaded installer failed checksum verification and was deleted for your safety.", interrupt: true);
                MessageBox.Show(this, "The downloaded update did not match the official SHA-256 checksum from GitHub and was deleted for your safety.", "Security Verification Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                btnDownloadInstall.IsEnabled = true;
                btnNotNow.IsEnabled = true;
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
