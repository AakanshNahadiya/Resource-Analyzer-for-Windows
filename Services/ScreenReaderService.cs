using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;

namespace AccessibleTaskManager.Services
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
        private SpeechSynthesizer? _synth;
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

            lock (_syncLock)
            {
                if (!_dllFailed)
                {
                    try
                    {
                        if (NvdaTestIfRunning() == 0)
                        {
                            if (interrupt)
                            {
                                NvdaCancelSpeech();
                            }
                            int res = NvdaSpeakText(text);
                            if (res == 0)
                            {
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"NVDA Controller call failed: {ex.Message}");
                        _dllFailed = true;
                    }
                }

                // Fallback to Windows SAPI (for JAWS, Narrator, or when NVDA client DLL is inactive)
                try
                {
                    _synth ??= new SpeechSynthesizer();
                    if (interrupt)
                    {
                        _synth.SpeakAsyncCancelAll();
                    }
                    _synth.SpeakAsync(text);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"SAPI speech failed: {ex.Message}");
                }
            }
        }
    }
}
