using ImageSelector;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static void Assert(bool okay, string description)
{
    if (!okay) throw new Exception("FAILED " + description);
    Console.WriteLine("PASS " + description);
}
static void CreatePng(string file, int width, int height)
{
    var stride = checked(width * 4);
    var pixels = new byte[checked(stride * height)];
    for (var i = 0; i < pixels.Length; i += 4)
    {
        pixels[i] = (byte)((i / 4) % 251);
        pixels[i + 1] = 91;
        pixels[i + 2] = 205;
        pixels[i + 3] = 255;
    }
    var source = BitmapSource.Create(width, height, 96, 96,
        PixelFormats.Bgra32, null, pixels, stride);
    source.Freeze();
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(source));
    using var output = File.Create(file);
    encoder.Save(output);
}
static void CreateJpeg(string file)
{
    var data = new byte[64 * 32 * 4];
    for (var i = 0; i < data.Length; i += 4)
    {
        data[i] = 125; data[i + 1] = 210; data[i + 2] = 50; data[i + 3] = 255;
    }
    var bitmap = BitmapSource.Create(64, 32, 96, 96, PixelFormats.Bgra32, null, data, 64 * 4);
    bitmap.Freeze();
    var encoder = new JpegBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var output = File.Create(file);
    encoder.Save(output);
}
var folder = Path.Combine(Path.GetTempPath(), "ImageSelector-decode-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var png = Path.Combine(folder, "test.png");
    var jpeg = Path.Combine(folder, "test.jpg");
    var tall = Path.Combine(folder, "tall.png");
    var wide = Path.Combine(folder, "wide.png");
    CreatePng(png, 96, 64);
    CreateJpeg(jpeg);
    CreatePng(tall, 10, 30000);
    CreatePng(wide, 30000, 10);

    // Exercise all declared image extensions, not only PNG and JPEG.
    var pixelsOther = new byte[96 * 64 * 4];
    for (var n = 0; n < pixelsOther.Length; n += 4)
    {
        pixelsOther[n] = 70;
        pixelsOther[n + 1] = 140;
        pixelsOther[n + 2] = 220;
        pixelsOther[n + 3] = 255;
    }
    var baseRaster = BitmapSource.Create(96, 64, 96, 96,
        PixelFormats.Bgra32, null, pixelsOther, 96 * 4);
    baseRaster.Freeze();
    var bmp = Path.Combine(folder, "test.bmp");
    var gif = Path.Combine(folder, "test.gif");
    var tiff = Path.Combine(folder, "test.tiff");
    foreach (var sample in new (BitmapEncoder Encoder, string File)[]
    {
        (new BmpBitmapEncoder(), bmp), (new GifBitmapEncoder(), gif), (new TiffBitmapEncoder(), tiff)
    })
    {
        sample.Encoder.Frames.Add(BitmapFrame.Create(baseRaster));
        using var output = File.Create(sample.File);
        sample.Encoder.Save(output);
    }

    // 16-bit grayscale PNG exercises non-8-bit source format inspection.
    var gray = Path.Combine(folder, "gray16.png");
    var grayData = new byte[64 * 32 * 2];
    for (var i = 0; i < grayData.Length; i += 2)
    {
        grayData[i] = (byte)(i & 0xff);
        grayData[i + 1] = 128;
    }
    var grayBitmap = BitmapSource.Create(64, 32, 96, 96,
        PixelFormats.Gray16, null, grayData, 64 * 2);
    grayBitmap.Freeze();
    var grayEncoder = new PngBitmapEncoder();
    grayEncoder.Frames.Add(BitmapFrame.Create(grayBitmap));
    using (var output = File.Create(gray)) grayEncoder.Save(output);

    foreach (var file in new[] { png, jpeg, tall, wide, gray, bmp, gif, tiff })
    {
        var preview = await ImageLoader.PreviewAsync(file, 600, 400, CancellationToken.None);
        Assert(preview.IsFrozen && preview.PixelWidth > 0 && preview.PixelHeight > 0,
            Path.GetFileName(file) + " preview is frozen and nonempty");
        var thumb = await ImageLoader.ThumbnailAsync(file, CancellationToken.None);
        Assert(thumb.IsFrozen && Math.Max(thumb.PixelWidth, thumb.PixelHeight) <= 256,
            Path.GetFileName(file) + " thumb is bounded");
    }

    var tiny = Path.Combine(folder, "tiny.png");
    CreatePng(tiny, 1, 1);
    var tinyPreview = await ImageLoader.PreviewAsync(tiny, 10, 10, CancellationToken.None);
    Assert(tinyPreview.PixelWidth == 1 && tinyPreview.PixelHeight == 1, "1x1 picture decoded");

    var invalid = Path.Combine(folder, "corrupt.jpg");
    await File.WriteAllTextAsync(invalid, "not a valid JPEG");
    try
    {
        await ImageLoader.PreviewAsync(invalid, 100, 100, CancellationToken.None);
        throw new Exception("FAILED corrupt image did not throw");
    }
    catch (Exception ex) when (ex is not Exception { Message: "FAILED corrupt image did not throw" })
    {
        Console.WriteLine("PASS corrupt file rejected: " + ex.GetType().Name);
    }

    using (var stream = File.Create(Path.Combine(folder, "oversized.png")))
        stream.SetLength(ImageLoader.MaxFileBytes + 1);
    try
    {
        await ImageLoader.ThumbnailAsync(Path.Combine(folder, "oversized.png"), CancellationToken.None);
        throw new Exception("FAILED oversized image not rejected");
    }
    catch (InvalidDataException) { Console.WriteLine("PASS >256MB rejected before decoding"); }
    Console.WriteLine("All real WIC decode checks passed.");
}
finally
{
    Directory.Delete(folder, recursive: true);
}
