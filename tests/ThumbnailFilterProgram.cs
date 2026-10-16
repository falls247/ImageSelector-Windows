using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageSelector;
using ImageSelector.Models;
using ImageSelector.Ui;
using ImageMetadata = ImageSelector.Models.ImageMetadata;

internal static class ThumbnailFilterProgram
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string windowName);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); app.Shutdown(0); }
            catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(1); }
        };
        return app.Run();
    }

    private static T Field<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static object? Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static Task CallAsync(object target, string name, params object[] args) =>
        (Task)Call(target, name, args)!;
    private static List<ThumbnailItem> Visible(MainWindow window) =>
        Field<List<ThumbnailRow>>(window, "_thumbnailRows").SelectMany(row => row.Items).ToList();
    private static string Current(MainWindow window)
    {
        var index = Field<int>(window, "_currentIndex");
        return index < 0 ? "" : Path.GetFileName(Field<List<string>>(window, "_imageFiles")[index]);
    }
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL " + description);
        Console.WriteLine("PASS " + description);
    }
    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Filter update did not complete");
            await Task.Delay(20);
        }
    }
    private static Task SettleAsync(MainWindow window) => WaitForAsync(() =>
        Field<CancellationTokenSource?>(window, "_annotationFilterLoadCts") is null &&
        !Field<DispatcherTimer>(window, "_thumbnailFilterTimer").IsEnabled);
    private static void SetFilter(MainWindow window, string name, bool enabled)
    {
        var checkbox = (CheckBox)window.FindName(name);
        checkbox.IsChecked = enabled;
        checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }
    private static async Task CancelSelectionAsync(MainWindow window)
    {
        // この試験プロセス自身の、生成した画像の削除確認だけに応答する。
        var confirm = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var hwnd = FindWindow("#32770", "選択済みデータの削除確認");
                if (hwnd != 0)
                {
                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (pid == Environment.ProcessId) { SendMessage(hwnd, 0x0111, 6, 0); return; }
                }
                await Task.Delay(20);
            }
            throw new TimeoutException("Fixture deletion confirmation did not appear");
        });
        await CallAsync(window, "DeleteSelectedCopyArtifactsAsync");
        await confirm;
        await SettleAsync(window);
    }

    private static async Task RunAsync()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "filter-fixtures-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "album");
        var selected = source + "_selected";
        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(other);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8)));
        using (var output = File.Create(Path.Combine(source, "00.png"))) encoder.Save(output);
        File.Copy(Path.Combine(source, "00.png"), Path.Combine(other, "other.png"));
        for (var i = 0; i < 30; i++)
        {
            var name = $"{i:00}.png";
            if (i > 0) File.Copy(Path.Combine(source, "00.png"), Path.Combine(source, name));
            if (i % 2 == 0) File.Copy(Path.Combine(source, name), Path.Combine(selected, name));
            if (i % 2 != 0 && i != 3 && i != 25 && i != 29) continue;
            var annotations = i is 3 or 20 or 24 or 29
                ? new List<ImageAnnotation> { new() { Text = "note-" + i } }
                : i == 4 ? new() { new() { Text = " \t\r\n" } } : new();
            File.WriteAllText(Path.Combine(selected, name + ".json"), i == 26 ? "{broken"
                : JsonSerializer.Serialize(new ImageMetadata { ImageFile = name, Annotations = annotations }));
        }

        var window = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, Width = 1000, Height = 500, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            await CallAsync(window, "LoadFolderAsync", source);
            Check(Visible(window).Count == 30, "initial list contains every image");
            Check(Field<IEnumerable<ThumbnailItem>>(window, "_thumbnailItems").Last().AnnotationStatus == AnnotationStatus.Pending,
                "annotation fixture begins outside realized thumbnail rows");

            SetFilter(window, "SelectedOnlyFilterCheckBox", true);
            Check(Visible(window).Select(item => item.FileName).SequenceEqual(
                Enumerable.Range(0, 15).Select(i => $"{i * 2:00}.png")), "selected-only filter");
            Call(window, "Navigate", 1);
            Check(Current(window) == "02.png", "horizontal navigation skips hidden images");
            var columns = Field<int>(window, "_thumbnailColumns");
            Call(window, "NavigateThumbnailVertical", 1);
            Check(Current(window) == $"{(1 + columns) * 2:00}.png", "vertical navigation follows filtered columns");
            Call(window, "NavigateThumbnailVertical", -1);
            Call(window, "Navigate", -1);
            await CallAsync(window, "SelectCurrentAsync");
            Check(Current(window) == "02.png", "save advances to next filtered image");
            Check(((ListBox)window.FindName("ThumbnailList")).IsEnabled, "saving preserves enabled background theme");

            SetFilter(window, "SelectedOnlyFilterCheckBox", false);
            SetFilter(window, "AnnotatedOnlyFilterCheckBox", true);
            await SettleAsync(window);
            Check(Visible(window).Select(item => item.FileName).SequenceEqual(new[] { "03.png", "20.png", "24.png", "29.png" }),
                "annotation filter finds offscreen JSON and excludes whitespace, empty and invalid JSON");
            Check(!Field<Dictionary<string, List<ImageAnnotation>>>(window, "_drafts").ContainsKey(Path.Combine(source, "25.png")),
                "offscreen filter scan does not retain annotation bodies");
            Check(Current(window) == "03.png", "filter moves current image to matching result");

            async Task ClearAndSaveAnnotationAsync()
            {
                await WaitForAsync(() => Application.Current.Windows.OfType<AnnotationEditor>().Any());
                var editor = Application.Current.Windows.OfType<AnnotationEditor>().Single();
                try
                {
                    ((TextBox)editor.FindName("AnnotationTextBox")).Text = "";
                    Call(editor, "ApplyAnnotationsWithoutClosing");
                    await Task.Delay(250);
                    Check(Current(window) == "03.png", "filter does not switch image while annotation editor remains open");
                    Call(editor, "ConfirmAnnotations");
                }
                finally { if (editor.IsVisible) editor.Close(); }
            }
            var edit = ClearAndSaveAnnotationAsync();
            await CallAsync(window, "OpenAnnotationEditorAsync");
            await edit;
            await SettleAsync(window);
            var saved = JsonSerializer.Deserialize<ImageMetadata>(File.ReadAllText(Path.Combine(selected, "03.png.json")))!;
            Check(saved.Annotations.Count == 0 && File.Exists(Path.Combine(selected, "03.png")),
                "annotation confirmation saves original edited image even when it leaves filter");
            Check(Current(window) == "20.png" && Visible(window).All(item => item.FileName != "03.png"),
                "cleared annotation leaves filtered list after editing completes");

            SetFilter(window, "SelectedOnlyFilterCheckBox", true);
            await SettleAsync(window);
            Check(Visible(window).Select(item => item.FileName).SequenceEqual(new[] { "20.png", "24.png" }), "both filters use AND");
            Check(((TextBlock)window.FindName("PositionText")).Text == "1/2", "position uses filtered count");
            Call(window, "Navigate", 1);
            Call(window, "NavigateThumbnailVertical", 1);
            Check(Current(window) == "24.png", "vertical navigation stops at last filtered row");
            await CallAsync(window, "SelectCurrentAsync");
            Check(Current(window) == "24.png", "saving last filtered result keeps current image");
            Call(window, "Navigate", -1);
            await CancelSelectionAsync(window);
            Check(Visible(window).Count == 1 && Current(window) == "24.png", "selection cancellation removes result and moves to next match");
            await CancelSelectionAsync(window);
            Check(Visible(window).Count == 0 && Current(window) == "", "empty intersection clears current image");
            Check(!((Button)window.FindName("SelectButton")).IsEnabled && ((Image)window.FindName("ImageDisplay")).Source is null,
                "empty result disables save and clears preview");
            Call(window, "Navigate", 1);
            Call(window, "NavigateThumbnailVertical", 1);
            await CallAsync(window, "SelectCurrentAsync");
            await CallAsync(window, "OpenAnnotationEditorAsync");
            SetFilter(window, "SelectedOnlyFilterCheckBox", false);
            await SettleAsync(window);
            Check(Visible(window).Single().FileName == "29.png", "clearing selection filter restores unselected annotation");
            SetFilter(window, "AnnotatedOnlyFilterCheckBox", false);
            Check(Visible(window).Count == 30, "clearing both filters restores complete list");

            await CallAsync(window, "LoadFolderAsync", other);
            var slots = Field<SemaphoreSlim>(window, "_metadataReadSlots");
            for (var i = 0; i < 3; i++) await slots.WaitAsync();
            try
            {
                SetFilter(window, "AnnotatedOnlyFilterCheckBox", true);
                await CallAsync(window, "LoadFolderAsync", source);
                Check(Field<CancellationTokenSource?>(window, "_annotationFilterLoadCts") is not null, "annotation scan can remain pending");
                await CallAsync(window, "LoadFolderAsync", other);
            }
            finally { slots.Release(3); }
            await SettleAsync(window);
            Check(Visible(window).Count == 0 && Field<List<string>>(window, "_imageFiles").All(path => path.StartsWith(other)),
                "folder switch cancels old scan without restoring stale results");
            SetFilter(window, "AnnotatedOnlyFilterCheckBox", false);
            Check(Visible(window).Single().FileName == "other.png", "clearing filter recovers from no matching results in new folder");
        }
        finally
        {
            window.Close();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
