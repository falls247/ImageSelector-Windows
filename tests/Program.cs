using ImageSelector;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
    Console.WriteLine("PASS: " + message);
}

static void Reject<T>(Action test, string message) where T : Exception
{
    try { test(); }
    catch (T) { Console.WriteLine("PASS: " + message); return; }
    throw new Exception("FAILED: " + message);
}

var regular = ImageDecodePolicy.Thumbnail(6000, 4000);
Check(regular.Width == 256 && regular.Height == 0, "landscape thumbnail longest edge capped");

var tall = ImageDecodePolicy.Thumbnail(10, 30000);
Check(tall.Height == 256 && tall.Width == 0, "portrait thumbnail height capped");

var wide = ImageDecodePolicy.Thumbnail(30000, 10);
Check(wide.Width == 256 && wide.Height == 0, "panorama thumbnail width capped");

var tiny = ImageDecodePolicy.Thumbnail(1, 1);
Check(tiny.Width == 1 && tiny.Height == 0, "do not upscale small images");

var preview = ImageDecodePolicy.Preview(8000, 6000, 6000, 5000);
Check(preview.Width is > 0 and <= 3072, "preview max edge capped");
Check((long)preview.Width * preview.Width * 6000 / 8000 <= ImageDecodePolicy.PreviewMaxPixels,
    "preview pixel budget capped");

var portraitPreview = ImageDecodePolicy.Preview(3000, 6000, 500, 800);
Check(portraitPreview.Height == 800, "preview fits narrow viewport");

Reject<InvalidDataException>(() => ImageDecodePolicy.Thumbnail(0, 100), "reject zero dimension");
Reject<InvalidDataException>(() => ImageDecodePolicy.Thumbnail(100001, 100), "reject pathological edge");
Reject<InvalidDataException>(() => ImageDecodePolicy.Preview(12000, 9000, 100, 100), "reject >80MP source");
Reject<ArgumentOutOfRangeException>(() => ImageDecodePolicy.Preview(200, 100, double.NaN, 100), "reject non-finite target");

Console.WriteLine("All decode budget checks passed.");
