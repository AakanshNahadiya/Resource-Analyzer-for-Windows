using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AccessibleTaskManager.Services
{
    public interface IScreenReaderService
    {
        bool IsNvdaRunning { get; }
        bool IsScreenReaderActive { get; }
        void Speak(string text, bool interrupt = true);
    }

    public class ScreenReaderService : IScreenReaderService
    {
        [DllImport("nvdaControllerClient.dll", CharSet = CharSet.Unicode, EntryPoint = "nvdaController_testIfRunning")]
        private static extern int NvdaTestIfRunning();

        [DllImport("nvdaControllerClient.dll", CharSet = CharSet.Unicode, EntryPoint = "nvdaController_speakText")]
        private static extern int NvdaSpeakText(string text);

        [DllImport("nvdaControllerClient.dll", CharSet = CharSet.Unicode, EntryPoint = "nvdaController_cancelSpeech")]
        private static extern int NvdaCancelSpeech();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

        private const uint SPI_GETSCREENREADER = 0x0046;

        private readonly object _syncLock = new();
        private bool _dllFailed = false;
        private DateTime _lastScreenReaderCheck = DateTime.MinValue;
        private bool _cachedScreenReaderActive = false;

        public bool IsNvdaRunning
        {
            get
            {
                if (_dllFailed) return false;
                try
                {
                    return NvdaTestIfRunning() == 0;
                }
                catch
                {
                    _dllFailed = true;
                    return false;
                }
            }
        }

        public bool IsScreenReaderActive
        {
            get
            {
                // If NVDA is running, a screen reader is definitely active
                if (IsNvdaRunning) return true;

                // Cache the system check for 1.5 seconds to avoid frequent Win32 / process calls
                var now = DateTime.UtcNow;
                if ((now - _lastScreenReaderCheck).TotalSeconds < 1.5)
                {
                    return _cachedScreenReaderActive;
                }

                _lastScreenReaderCheck = now;
                _cachedScreenReaderActive = CheckSystemScreenReader();
                return _cachedScreenReaderActive;
            }
        }

        private bool CheckSystemScreenReader()
        {
            // 1. Check Windows SPI_GETSCREENREADER flag (set by Narrator, JAWS, ZoomText, SuperNova, etc.)
            try
            {
                bool isRunning = false;
                if (SystemParametersInfo(SPI_GETSCREENREADER, 0, ref isRunning, 0) && isRunning)
                {
                    return true;
                }
            }
            catch { }

            // 2. Check running screen reader processes in case a screen reader did not set SPI_GETSCREENREADER
            try
            {
                string[] knownScreenReaders = { "narrator", "jfw", "jaws", "nvda", "dolapi" };
                foreach (var name in knownScreenReaders)
                {
                    var procs = Process.GetProcessesByName(name);
                    bool found = procs.Length > 0;
                    foreach (var p in procs) p.Dispose();
                    if (found) return true;
                }
            }
            catch { }

            return false;
        }

        public void Speak(string text, bool interrupt = true)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            // Direct spoken announcements are routed exclusively through NVDA's controller client.
            // Other screen readers (Windows Narrator, JAWS) receive in-app announcements natively
            // via standard WPF UI Automation and the txtAnnouncement LiveRegion without unwanted TTS speech.
            if (!IsNvdaRunning) return;

            lock (_syncLock)
            {
                if (!_dllFailed)
                {
                    try
                    {
                        if (interrupt)
                        {
                            NvdaCancelSpeech();
                        }
                        NvdaSpeakText(text);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"NVDA Controller call failed: {ex.Message}");
                        _dllFailed = true;
                    }
                }
            }
        }
    }
}
