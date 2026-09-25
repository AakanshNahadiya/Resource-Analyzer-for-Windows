using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AccessibleTaskManager.Models
{
    public class AppBatteryUsageItem : INotifyPropertyChanged
    {
        private string _processName = string.Empty;
        private string _displayName = string.Empty;
        private int _pid;
        private int _estimatedPowerMw;
        private double _energyConsumedMwh;
        private double _batteryPercent;
        private string _powerImpact = "Low";
        private bool _isForeground;
        private string _displayMode = "Combined";
        private string _displayText = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string ProcessName
        {
            get => _processName;
            set
            {
                if (_processName != value)
                {
                    _processName = value;
                    OnPropertyChanged();
                }
            }
        }

        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (_displayName != value)
                {
                    _displayName = value;
                    OnPropertyChanged();
                    UpdateDisplayText();
                }
            }
        }

        public int Pid
        {
            get => _pid;
            set
            {
                if (_pid != value)
                {
                    _pid = value;
                    OnPropertyChanged();
                }
            }
        }

        public int EstimatedPowerMw
        {
            get => _estimatedPowerMw;
            set
            {
                if (_estimatedPowerMw != value)
                {
                    _estimatedPowerMw = value;
                    OnPropertyChanged();
                    UpdateDisplayText();
                }
            }
        }

        public double EnergyConsumedMwh
        {
            get => _energyConsumedMwh;
            set
            {
                if (Math.Abs(_energyConsumedMwh - value) > 0.01)
                {
                    _energyConsumedMwh = value;
                    OnPropertyChanged();
                    UpdateDisplayText();
                }
            }
        }

        public double BatteryPercent
        {
            get => _batteryPercent;
            set
            {
                if (Math.Abs(_batteryPercent - value) > 0.01)
                {
                    _batteryPercent = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasPercent));
                    UpdateDisplayText();
                }
            }
        }

        public bool HasPercent => _batteryPercent >= 0.5;

        public string PowerImpact
        {
            get => _powerImpact;
            set
            {
                if (_powerImpact != value)
                {
                    _powerImpact = value;
                    OnPropertyChanged();
                    UpdateDisplayText();
                }
            }
        }

        public bool IsForeground
        {
            get => _isForeground;
            set
            {
                if (_isForeground != value)
                {
                    _isForeground = value;
                    OnPropertyChanged();
                    UpdateDisplayText();
                }
            }
        }

        public string DisplayMode
        {
            get => _displayMode;
            set
            {
                if (_displayMode != value)
                {
                    _displayMode = value;
                    OnPropertyChanged();
                    UpdateDisplayText();
                }
            }
        }

        public string DisplayText
        {
            get => _displayText;
            private set
            {
                if (_displayText != value)
                {
                    _displayText = value;
                    OnPropertyChanged();
                }
            }
        }

        public void UpdateMetrics(
            int estimatedPowerMw,
            double energyConsumedMwh,
            double batteryPercent,
            string powerImpact,
            bool isForeground,
            string displayMode)
        {
            bool powerChanged = _estimatedPowerMw != estimatedPowerMw;
            bool energyChanged = Math.Abs(_energyConsumedMwh - energyConsumedMwh) > 0.01;
            bool percentChanged = Math.Abs(_batteryPercent - batteryPercent) > 0.01;
            bool impactChanged = _powerImpact != powerImpact;
            bool fgChanged = _isForeground != isForeground;
            bool modeChanged = _displayMode != displayMode;

            if (powerChanged || energyChanged || percentChanged || impactChanged || fgChanged || modeChanged)
            {
                _estimatedPowerMw = estimatedPowerMw;
                _energyConsumedMwh = energyConsumedMwh;
                _batteryPercent = batteryPercent;
                _powerImpact = powerImpact;
                _isForeground = isForeground;
                _displayMode = displayMode;

                if (powerChanged) OnPropertyChanged(nameof(EstimatedPowerMw));
                if (energyChanged) OnPropertyChanged(nameof(EnergyConsumedMwh));
                if (percentChanged)
                {
                    OnPropertyChanged(nameof(BatteryPercent));
                    OnPropertyChanged(nameof(HasPercent));
                }
                if (impactChanged) OnPropertyChanged(nameof(PowerImpact));
                if (fgChanged) OnPropertyChanged(nameof(IsForeground));
                if (modeChanged) OnPropertyChanged(nameof(DisplayMode));

                UpdateDisplayText();
            }
        }

        public void UpdateDisplayText()
        {
            string fgText = _isForeground ? " [Active Window]" : "";
            string name = string.IsNullOrEmpty(_displayName) ? _processName : _displayName;
            string pctStr = _batteryPercent > 0.001 && _batteryPercent < 0.1 ? "< 0.1%" : $"{_batteryPercent:F1}%";

            switch (_displayMode)
            {
                case "Percentage":
                    DisplayText = $"{name}: {pctStr} of battery used ({_energyConsumedMwh:N1} mWh){fgText}";
                    break;

                case "DrainRate":
                    DisplayText = $"{name}: {_estimatedPowerMw:N0} mW - {_powerImpact} Impact{fgText}";
                    break;

                case "Combined":
                default:
                    DisplayText = $"{name}: {pctStr} of battery used ({_energyConsumedMwh:N1} mWh) - Live: {_estimatedPowerMw:N0} mW ({_powerImpact}){fgText}";
                    break;
            }
        }

        public override string ToString() => DisplayText;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
