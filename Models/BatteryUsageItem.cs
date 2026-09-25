using System;

namespace AccessibleTaskManager.Models
{
    public class BatteryUsageItem
    {
        public DateTime Timestamp { get; set; }
        public TimeSpan Duration { get; set; }
        public string EntryType { get; set; } = string.Empty; // "Active", "ConnectedStandby", "Suspend", "Charging"
        public long DrainMwh { get; set; } // Positive for discharge, negative for charge
        public double DrainPercent { get; set; }
        public bool IsCharging { get; set; }
        public bool HasPercent => DrainPercent >= 0.5 && !IsCharging;
        public string DisplayText { get; set; } = string.Empty;

        public override string ToString() => DisplayText;
    }
}
