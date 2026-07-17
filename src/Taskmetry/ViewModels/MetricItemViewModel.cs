using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Taskmetry.ViewModels;

public sealed partial class MetricItemViewModel : ObservableObject
{
    public MetricItemViewModel(string key, string label, string accentColor)
    {
        Key = key;
        Label = label;
        AccentBrush = new SolidColorBrush(Color.Parse(accentColor));
    }

    public string Key { get; }
    public string Label { get; }
    public IBrush AccentBrush { get; }

    [ObservableProperty]
    private string _valueNumber = "—";

    [ObservableProperty]
    private string _unitText = string.Empty;

    [ObservableProperty]
    private IBrush _statusBrush = new SolidColorBrush(Color.Parse("#55E6C1"));

    [ObservableProperty]
    private string _detailText = "データを待っています";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isVisible = true;

    public void SetPercent(double? value)
    {
        if (value is null)
        {
            Progress = 0;
            ValueNumber = "—";
            UnitText = string.Empty;
            StatusBrush = new SolidColorBrush(Color.Parse("#71809B"));
            return;
        }

        Progress = Math.Clamp(value.Value, 0, 100);
        ValueNumber = $"{Progress:0}";
        UnitText = "%";
        StatusBrush = new SolidColorBrush(Color.Parse(Progress switch
        {
            >= 85 => "#FF5D67",
            >= 65 => "#FFBF47",
            _ => "#55E6C1",
        }));
    }
}
