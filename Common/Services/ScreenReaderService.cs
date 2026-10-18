using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResourceAnalyzer.Services
{
    public interface IScreenReaderService
    {
        bool IsNvdaRunning { get; }
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

        private readonly object _syncLock = new();
        private bool _dllFailed = false;

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
