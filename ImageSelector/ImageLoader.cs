using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace ImageSelector;

/// <summary>
/// File-backed decoding always occurs on background workers. WIC must never use an
/// uncapped source or hold a remote file open after the completed bitmap is frozen.
/// </summary>
public static class ImageLoader
{
    public const long MaxFileBytes = 256L * 1024 * 1024;
    private const int CopyBufferSize = 128 * 1024;
    private static readonly ConcurrentDictionary<string, byte> BlockedRemoteFolders =
        new(StringComparer.OrdinalIgnoreCase);

    public static Task<BitmapSource> PreviewAsync(
        string path, double width, double height, CancellationToken token) =>
        LoadAsync(path, width, height, false, token);

    public static Task<BitmapSource> ThumbnailAsync(string path, CancellationToken token) =>
        LoadAsync(path, 256, 256, true, token);

    private static async Task<BitmapSource> LoadAsync(
        string path, double width, double height, bool thumbnail, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // FileInfo and DriveInfo are on a background worker to keep UI responsive.
        var remote = await Task.Run(() => PathKind.IsRemote(path), token)
            .WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        if (!remote)
            return await Task.Run(() => DecodeLocal(path, width, height, thumbnail, token), token).ConfigureAwait(false);

        var folder = Path.GetDirectoryName(path) ?? path;
        if (BlockedRemoteFolders.ContainsKey(folder))
            throw new TimeoutException("フォルダが応答しません。アプリを再起動するか別のフォルダを選択してください");

        // A timeout prevents more work from queuing against a hanging WSL/UNC share.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return await Task.Run(() => LoadRemoteCoreAsync(path, width, height, thumbnail, timeout.Token),
                timeout.Token).WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            timeout.Cancel();
            BlockedRemoteFolders.TryAdd(folder, 0);
            throw new TimeoutException("フォルダが10秒以内に応答しません。新しい画像読み込みを停止しました");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            BlockedRemoteFolders.TryAdd(folder, 0);
            throw new TimeoutException("フォルダが10秒以内に応答しません。新しい画像読み込みを停止しました");
        }
    }

    private static async Task<BitmapSource> LoadRemoteCoreAsync(
        string path, double width, double height, bool thumbnail, CancellationToken token)
    {
        // FileStream construction is backgrounded as even the initial open may hang.
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        CheckFileLength(file.Length);
        using var memory = new MemoryStream((int)file.Length);
        var buffer = new byte[CopyBufferSize];
        int read;
        while ((read = await file.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            token.ThrowIfCancellationRequested();
            if (memory.Length + read > MaxFileBytes)
                throw new InvalidDataException("画像ファイルが256MBの上限を超えています");
            memory.Write(buffer, 0, read);
        }
        memory.Position = 0;
        token.ThrowIfCancellationRequested();
        return await Task.Run(() => DecodeStream(memory, width, height, thumbnail, token),
            token).ConfigureAwait(false);
    }

    private static BitmapSource DecodeLocal(
        string path, double width, double height, bool thumbnail, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, CopyBufferSize, FileOptions.SequentialScan);
        CheckFileLength(stream.Length);
        return DecodeStream(stream, width, height, thumbnail, token);
    }

    private static void CheckFileLength(long length)
    {
        if (length > MaxFileBytes)
            throw new InvalidDataException("画像ファイルが256MBの上限を超えています");
        if (length <= 0)
            throw new InvalidDataException("画像ファイルが空です");
    }

    private static BitmapSource DecodeStream(
        Stream stream, double width, double height, bool thumbnail, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Keep the metadata decoder in the same worker and on the same open stream.
        // Do not use DelayCreation: it can defer metadata reads until after disposal.
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count == 0) throw new FileFormatException("画像フレームがありません");
        var frame = decoder.Frames[0];
        var sourceWidth = frame.PixelWidth;
        var sourceHeight = frame.PixelHeight;
        var bitsPerPixel = frame.Format.BitsPerPixel;
        ImageDecodePolicy.ValidateSource(sourceWidth, sourceHeight, bitsPerPixel);

        var target = thumbnail
            ? ImageDecodePolicy.Thumbnail(sourceWidth, sourceHeight)
            : ImageDecodePolicy.Preview(sourceWidth, sourceHeight, width, height);

        token.ThrowIfCancellationRequested();
        stream.Seek(0, SeekOrigin.Begin);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        // StreamSource has no URI: IgnoreImageCache throws ArgumentNullException in WPF.
        bitmap.CreateOptions = BitmapCreateOptions.None;
        if (target.Width > 0) bitmap.DecodePixelWidth = target.Width;
        if (target.Height > 0) bitmap.DecodePixelHeight = target.Height;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        token.ThrowIfCancellationRequested();
        return bitmap;
    }
}
