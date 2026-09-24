using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;
using Application = System.Windows.Application;

namespace AccessibleTaskManager.Services
{
    public interface IThemeService : IDisposable
    {
        string CurrentPreference { get; }
        string ActiveTheme { get; }
        void ApplyTheme(string themePreference);
        event Action? ThemeChanged;
    }

    public class ThemeService : IThemeService
    {
        private string _currentPreference = "System Default";
        private string _activeTheme = "Dark";
        private bool _isHooked = false;

        public string CurrentPreference => _currentPreference;
        public string ActiveTheme => _activeTheme;
        public event Action? ThemeChanged;

        public ThemeService()
        {
            try
            {
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                _isHooked = true;
            }
            catch { }
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (_currentPreference.Equals("System Default", StringComparison.OrdinalIgnoreCase))
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    ApplyTheme("System Default");
                });
            }
        }

        public void ApplyTheme(string themePreference)
        {
            _currentPreference = string.IsNullOrWhiteSpace(themePreference) ? "System Default" : themePreference;
            _activeTheme = ResolveTheme(_currentPreference);

            ApplyColorsForTheme(_activeTheme);
            ThemeChanged?.Invoke();
        }

        private string ResolveTheme(string preference)
        {
            if (preference.Equals("System Default", StringComparison.OrdinalIgnoreCase))
            {
                if (SystemParameters.HighContrast)
                {
                    return "High Contrast Black";
                }

                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                    if (key != null)
                    {
                        var val = key.GetValue("AppsUseLightTheme");
                        if (val is int lightInt && lightInt == 1)
                        {
                            return "Light";
                        }
                    }
                }
                catch { }

                return "Dark";
            }

            return preference;
        }

        private void ApplyColorsForTheme(string theme)
        {
            var res = Application.Current?.Resources;
            if (res == null) return;

            Color winBg, surfaceBg, border, textPrimary, textSecondary, accent, accentHover, selBg, selFg, inputBg, inputFg, inputBorder, progBg, statusAnnounce;

            if (theme.Equals("Light", StringComparison.OrdinalIgnoreCase))
            {
                winBg = Color.FromRgb(241, 245, 249); // slate-100
                surfaceBg = Color.FromRgb(255, 255, 255); // pure white
                border = Color.FromRgb(203, 213, 225); // slate-300
                textPrimary = Color.FromRgb(15, 23, 42); // slate-900
                textSecondary = Color.FromRgb(71, 85, 105); // slate-600
                accent = Color.FromRgb(2, 132, 199); // sky-600
                accentHover = Color.FromRgb(3, 105, 161); // sky-700
                selBg = Color.FromRgb(2, 132, 199);
                selFg = Color.FromRgb(255, 255, 255);
                inputBg = Color.FromRgb(255, 255, 255);
                inputFg = Color.FromRgb(15, 23, 42);
                inputBorder = Color.FromRgb(148, 163, 184); // slate-400
                progBg = Color.FromRgb(226, 232, 240); // slate-200
                statusAnnounce = Color.FromRgb(3, 105, 161);
            }
            else if (theme.Equals("High Contrast Black", StringComparison.OrdinalIgnoreCase))
            {
                winBg = Color.FromRgb(0, 0, 0); // pure black
                surfaceBg = Color.FromRgb(10, 10, 10);
                border = Color.FromRgb(255, 255, 255); // stark white border
                textPrimary = Color.FromRgb(255, 255, 255);
                textSecondary = Color.FromRgb(250, 204, 21); // yellow
                accent = Color.FromRgb(250, 204, 21); // vivid yellow
                accentHover = Color.FromRgb(254, 240, 138);
                selBg = Color.FromRgb(250, 204, 21);
                selFg = Color.FromRgb(0, 0, 0);
                inputBg = Color.FromRgb(0, 0, 0);
                inputFg = Color.FromRgb(255, 255, 255);
                inputBorder = Color.FromRgb(255, 255, 255);
                progBg = Color.FromRgb(38, 38, 38);
                statusAnnounce = Color.FromRgb(250, 204, 21);
            }
            else // Default: Dark
            {
                winBg = Color.FromRgb(15, 23, 42); // slate-900
                surfaceBg = Color.FromRgb(30, 41, 59); // slate-800
                border = Color.FromRgb(51, 65, 85); // slate-700
                textPrimary = Color.FromRgb(248, 250, 252); // slate-50
                textSecondary = Color.FromRgb(148, 163, 184); // slate-400
                accent = Color.FromRgb(56, 189, 248); // sky-400
                accentHover = Color.FromRgb(2, 132, 199); // sky-600
                selBg = Color.FromRgb(2, 132, 199);
                selFg = Color.FromRgb(255, 255, 255);
                inputBg = Color.FromRgb(30, 41, 59);
                inputFg = Color.FromRgb(248, 250, 252);
                inputBorder = Color.FromRgb(71, 85, 105);
                progBg = Color.FromRgb(51, 65, 85);
                statusAnnounce = Color.FromRgb(56, 189, 248);
            }

            SetBrush(res, "WindowBackgroundBrush", winBg);
            SetBrush(res, "SurfaceBackgroundBrush", surfaceBg);
            SetBrush(res, "BorderBrush", border);
            SetBrush(res, "TextPrimaryBrush", textPrimary);
            SetBrush(res, "TextSecondaryBrush", textSecondary);
            SetBrush(res, "AccentBrush", accent);
            SetBrush(res, "AccentHoverBrush", accentHover);
            SetBrush(res, "SelectionBackgroundBrush", selBg);
            SetBrush(res, "SelectionForegroundBrush", selFg);
            SetBrush(res, "InputBackgroundBrush", inputBg);
            SetBrush(res, "InputForegroundBrush", inputFg);
            SetBrush(res, "InputBorderBrush", inputBorder);
            SetBrush(res, "ProgressBarBackgroundBrush", progBg);
            SetBrush(res, "StatusAnnouncementBrush", statusAnnounce);
        }

        private static void SetBrush(ResourceDictionary res, string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze(); // Freezing prevents allocation and maximizes WPF render performance
            res[key] = brush;
        }

        public void Dispose()
        {
            if (_isHooked)
            {
                try
                {
                    SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                }
                catch { }
                _isHooked = false;
            }
        }
    }
}
