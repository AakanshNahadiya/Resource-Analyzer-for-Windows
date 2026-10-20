using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace ResourceAnalyzer.Helpers
{
    public static class SafeClipboard
    {
        public static bool TrySetText(string text, int retries = 5, int delayMs = 30)
        {
            if (string.IsNullOrEmpty(text)) return false;

            for (int i = 0; i < retries; i++)
            {
                try
                {
                    Clipboard.SetDataObject(text, copy: true);
                    return true;
                }
                catch (COMException)
                {
                    if (i < retries - 1) Thread.Sleep(delayMs);
                }
                catch
                {
                    if (i < retries - 1) Thread.Sleep(delayMs);
                }
            }

            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
