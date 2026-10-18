using System.ComponentModel;
using System.Threading;
using System.Windows.Media;

namespace ImageSelector.Ui;

public enum AnnotationStatus
{
    Pending,
    Empty,
    Present,
    Invalid
}

public sealed class ThumbnailItem : INotifyPropertyChanged
{
    private bool _isSaved;
    private bool _isCurrent;
    private AnnotationStatus _annotationStatus;
    private ImageSource? _thumbnail;

    public ThumbnailItem(string path, bool isSaved, DateTime lastWriteTimeUtc, bool hasMetadata = false)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        LastWriteTimeUtc = lastWriteTimeUtc;
        _isSaved = isSaved;
        _annotationStatus = hasMetadata ? AnnotationStatus.Pending : AnnotationStatus.Empty;
    }

    public string Path { get; }
    public string FileName { get; }
    public DateTime LastWriteTimeUtc { get; }

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
    public int RealizedCount { get; set; }
    public CancellationTokenSource? LoadCts { get; set; }

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

    public AnnotationStatus AnnotationStatus
    {
        get => _annotationStatus;
        set
        {
            if (value == _annotationStatus) return;
            _annotationStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AnnotationStatus)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
