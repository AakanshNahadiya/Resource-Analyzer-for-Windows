using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ResourceAnalyzer.Models
{
    public class ResourceItem : INotifyPropertyChanged
    {
        private string _summary = string.Empty;
        private string _details = string.Empty;
        private double _percent = -1;

        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        public double Percent
        {
            get => _percent;
            set
            {
                if (System.Math.Abs(_percent - value) > 0.01)
                {
                    _percent = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasPercent));
                }
            }
        }

        public bool HasPercent => _percent >= 0;

        public string Summary
        {
            get => _summary;
            set
            {
                if (_summary != value)
                {
                    _summary = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public string Details
        {
            get => _details;
            set
            {
                if (_details != value)
                {
                    _details = value;
                    OnPropertyChanged();
                }
            }
        }

        public string DisplayText => _summary;

        public override string ToString() => DisplayText;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
