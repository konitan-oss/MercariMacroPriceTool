using System.ComponentModel;

namespace MercariMacroPriceTool.App;

public class ListingRow : INotifyPropertyChanged
{
    private bool _isChecked;
    private int _runCount;

    public string? ItemId { get; set; }
    public string? Title { get; set; }
    public int Price { get; set; }
    public string FormattedPrice { get; set; } = string.Empty;
    public string? ItemUrl { get; set; }
    public string? StatusText { get; set; }
    public int LikeCount { get; set; }
    public string LastDownAtText { get; set; } = string.Empty;
    public string LastDownLabelText { get; set; } = string.Empty;
    public int LastDownAmount { get; set; }
    public int RunCount
    {
        get => _runCount;
        set
        {
            if (_runCount != value)
            {
                _runCount = value;
                OnPropertyChanged(nameof(RunCount));
            }
        }
    }
    public string ResultStatus { get; set; } = "未実行";
    public string LastMessage { get; set; } = string.Empty;
    public string ExecutedAt { get; set; } = string.Empty;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                OnPropertyChanged(nameof(IsChecked));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
