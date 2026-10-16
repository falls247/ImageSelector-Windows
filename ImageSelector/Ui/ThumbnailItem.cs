using System.ComponentModel;
using System.Windows.Media;

namespace ImageSelector.Ui;

public sealed class ThumbnailItem : INotifyPropertyChanged
{
    private bool _isSaved;
    private bool _isCurrent;
    private ImageSource? _thumbnail;

    public ThumbnailItem(string path, bool isSaved)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        _isSaved = isSaved;
    }

    public string Path { get; }
    public string FileName { get; }

    // 読込済みのサムネイルだけ保持。フォルダを切り替えるとインスタンスごと解放する。
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(value, _thumbnail)) return;
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public bool IsLoading { get; set; }

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (value == _isCurrent) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public bool IsSaved
    {
        get => _isSaved;
        set
        {
            if (value == _isSaved) return;
            _isSaved = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSaved)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
