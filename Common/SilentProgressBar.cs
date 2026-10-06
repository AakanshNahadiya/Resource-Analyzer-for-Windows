using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ResourceAnalyzer
{
    /// <summary>
    /// A visual progress bar that completely suppresses UI Automation peer creation.
    /// This prevents screen readers (like NVDA) from treating real-time metric updates
    /// as background progress operations and playing unwanted audio beeps, while preserving
    /// the visual progress bar for sighted users.
    /// </summary>
    public class SilentProgressBar : ProgressBar
    {
        protected override AutomationPeer? OnCreateAutomationPeer()
        {
            return null;
        }
    }
}
