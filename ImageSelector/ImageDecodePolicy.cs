using System.IO;

namespace ImageSelector;

/// <summary>
/// Computes bounded WIC decode dimensions before a bitmap is allocated.
/// BitmapImage uses a single requested dimension to preserve the aspect ratio.
/// </summary>
public static class ImageDecodePolicy
{
    public const long MaxSourcePixels = 80_000_000;
    public const long MaxSourceDecodeBytes = 512L * 1024 * 1024;
    public const int MaxSourceEdge = 50_000;
    public const int PreviewMaxEdge = 3072;
    public const long PreviewMaxPixels = 6_000_000;
    public const int ThumbnailMaxEdge = 256;

    public static DecodeDimensions Preview(int width, int height, double requestedWidth, double requestedHeight) =>
        Calculate(width, height, requestedWidth, requestedHeight, PreviewMaxEdge, PreviewMaxPixels);

    public static DecodeDimensions Thumbnail(int width, int height) =>
        Calculate(width, height, ThumbnailMaxEdge, ThumbnailMaxEdge, ThumbnailMaxEdge,
            (long)ThumbnailMaxEdge * ThumbnailMaxEdge);

    public static void ValidateSource(int width, int height, int bitsPerPixel = 24)
    {
        if (width <= 0 || height <= 0 || width > MaxSourceEdge || height > MaxSourceEdge ||
            (long)width * height > MaxSourcePixels)
            throw new InvalidDataException("画像サイズが安全上限を超えています（最大80MP、1辺50,000px）。");
        if (bitsPerPixel <= 0 || ((long)width * height * bitsPerPixel + 7) / 8 > MaxSourceDecodeBytes)
            throw new InvalidDataException("画像の展開メモリ予算512MBを超えています");
    }

    private static DecodeDimensions Calculate(
        int width, int height, double requestedWidth, double requestedHeight, int maxEdge, long maxPixels)
    {
        ValidateSource(width, height);

        if (!double.IsFinite(requestedWidth) || !double.IsFinite(requestedHeight) ||
            requestedWidth <= 0 || requestedHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedWidth), "デコード目標サイズが無効です。");

        var scale = Math.Min(1.0, Math.Min(
            Math.Min(requestedWidth / width, requestedHeight / height),
            Math.Min((double)maxEdge / Math.Max(width, height),
                Math.Sqrt((double)maxPixels / ((long)width * height)))));
        // WIC maintains aspect ratio from exactly one decode dimension.
        return width >= height
            ? new DecodeDimensions(Math.Max(1, (int)Math.Floor(width * scale)), 0)
            : new DecodeDimensions(0, Math.Max(1, (int)Math.Floor(height * scale)));
    }
}

public readonly record struct DecodeDimensions(int Width, int Height);
