namespace ImageSelector.Ui;

/// <summary>仮想化する単位。各行に表示するタイルだけを生成する。</summary>
public sealed class ThumbnailRow
{
    public ThumbnailRow(IReadOnlyList<ThumbnailItem> items) => Items = items;
    public IReadOnlyList<ThumbnailItem> Items { get; }
}
