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
    private static void SetSort(MainWindow window, ThumbnailSortOrder order)
    {
        var button = (Button)window.FindName("ThumbnailSortButton");
        var selectedItem = button.ContextMenu.Items.Cast<MenuItem>().Single(item => Equals(item.Tag, order));
        selectedItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(button.ContextMenu.Items.Cast<MenuItem>().Count(item => item.IsChecked) == 1 && selectedItem.IsChecked,
            "sort menu checks only the active order: " + order);
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
        await RunSortChecksAsync();
    }

    private static async Task RunSortChecksAsync()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "sort-fixtures-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "album");
        var selected = source + "_selected";
        var other = Path.Combine(root, "other");
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(empty);
        var epoch = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8)));
        using var encodedImage = new MemoryStream();
        encoder.Save(encodedImage);
        var imageBytes = encodedImage.ToArray();
        var fixtures = new[] { ("z.png", 0), ("image10.png", 3), ("b.png", 1), ("image2.png", 2), ("A.png", 1) };
        foreach (var (name, days) in fixtures)
        {
            var path = Path.Combine(source, name);
            File.WriteAllBytes(path, imageBytes);
            File.SetLastWriteTimeUtc(path, epoch.AddDays(days));
            if (name is "A.png" or "b.png" or "z.png") File.Copy(path, Path.Combine(selected, name));
            if (name is "b.png" or "image2.png")
                File.WriteAllText(Path.Combine(selected, name + ".json"), JsonSerializer.Serialize(new ImageMetadata
                {
                    ImageFile = name, Annotations = new() { new() { Text = "sort note" } }
                }));
        }
        foreach (var name in new[] { "a.png", "z.png" })
        {
            var path = Path.Combine(other, name);
            File.Copy(Path.Combine(source, "z.png"), path);
            File.SetLastWriteTimeUtc(path, epoch.AddDays(name == "a.png" ? 1 : 0));
        }

        var window = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, Width = 1000, Height = 500, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            await CallAsync(window, "LoadFolderAsync", source);
            Check(Visible(window).Select(item => item.FileName).SequenceEqual(new[] { "A.png", "b.png", "image10.png", "image2.png", "z.png" })
                && Current(window) == "A.png", "default order is case-insensitive filename ascending");
            Check(Visible(window).All(item => item.LastWriteTimeUtc == epoch.AddDays(fixtures.Single(fixture => fixture.Item1 == item.FileName).Item2)),
                "date sorting uses original image modification timestamps");

            var button = (Button)window.FindName("ThumbnailSortButton");
            var panel = (WrapPanel)button.Parent;
            ((Grid)window.Content).ColumnDefinitions[0].Width = new GridLength(210);
            window.UpdateLayout();
            var legend = (TextBlock)panel.Children[1];
            var buttonOrigin = button.TranslatePoint(new Point(), panel);
            Check(button.ActualWidth <= 44 && button.ActualHeight <= 18
                && buttonOrigin.X >= legend.TranslatePoint(new Point(legend.ActualWidth, 0), panel).X
                && buttonOrigin.X + button.ActualWidth <= panel.ActualWidth
                && buttonOrigin.Y == legend.TranslatePoint(new Point(), panel).Y,
                "compact sort button fits to the right of legends at minimum sidebar width");
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(button.ContextMenu.IsOpen && ReferenceEquals(button.ContextMenu.PlacementTarget, button),
                "sort button opens its menu");
            button.ContextMenu.IsOpen = false;

            var orders = new[]
            {
                (ThumbnailSortOrder.FileNameDescending, new[] { "z.png", "image2.png", "image10.png", "b.png", "A.png" }),
                (ThumbnailSortOrder.DateAscending, new[] { "z.png", "A.png", "b.png", "image2.png", "image10.png" }),
                (ThumbnailSortOrder.DateDescending, new[] { "image10.png", "image2.png", "A.png", "b.png", "z.png" }),
                (ThumbnailSortOrder.FileNameAscending, new[] { "A.png", "b.png", "image10.png", "image2.png", "z.png" })
            };
            foreach (var (order, expected) in orders)
            {
                SetSort(window, order);
                Check(Visible(window).Select(item => item.FileName).SequenceEqual(expected), "thumbnail order: " + order);
                Check(Current(window) == "A.png" && Visible(window).Single(item => item.IsCurrent).FileName == "A.png",
                    "sort preserves current image and highlight: " + order);
                Check(Field<List<string>>(window, "_imageFiles").SequenceEqual(
                    Field<IEnumerable<ThumbnailItem>>(window, "_thumbnailItems").Select(item => item.Path)),
                    "sort keeps image and thumbnail indexes aligned: " + order);
            }
            SetSort(window, ThumbnailSortOrder.FileNameAscending);
            SetSort(window, ThumbnailSortOrder.DateDescending);
            Call(window, "Navigate", 1);
            Check(Current(window) == "b.png", "navigation follows date order");
            await CallAsync(window, "SelectCurrentAsync");
            Check(Current(window) == "z.png", "save advances in date order");
            SetFilter(window, "SelectedOnlyFilterCheckBox", true);
            Check(Visible(window).Select(item => item.FileName).SequenceEqual(new[] { "A.png", "b.png", "z.png" }),
                "selected filter retains date order");
            SetFilter(window, "AnnotatedOnlyFilterCheckBox", true);
            await SettleAsync(window);
            Check(Visible(window).Single().FileName == "b.png" && Current(window) == "b.png", "both filters work with date order");
            SetFilter(window, "SelectedOnlyFilterCheckBox", false);
            await SettleAsync(window);
            Check(Visible(window).Select(item => item.FileName).SequenceEqual(new[] { "image2.png", "b.png" }),
                "annotation filter retains date order");
            Call(window, "Navigate", -1);
            await CallAsync(window, "SelectCurrentAsync");
            Check(Current(window) == "b.png" && File.Exists(Path.Combine(selected, "image2.png")),
                "sorted save writes the displayed image and advances to next match");
            SetFilter(window, "SelectedOnlyFilterCheckBox", true);
            await SettleAsync(window);
            await CancelSelectionAsync(window);
            Check(Current(window) == "image2.png" && Visible(window).Single().FileName == "image2.png",
                "removing the last sorted match falls back to previous visible image");
            SetSort(window, ThumbnailSortOrder.FileNameDescending);
            Check(Visible(window).Single().FileName == "image2.png", "sort preserves active filters");
            SetFilter(window, "SelectedOnlyFilterCheckBox", false);
            SetFilter(window, "AnnotatedOnlyFilterCheckBox", false);
            SetSort(window, ThumbnailSortOrder.DateAscending);
            await CallAsync(window, "LoadFolderAsync", other);
            Check(Visible(window).Select(item => item.FileName).SequenceEqual(new[] { "z.png", "a.png" }) && Current(window) == "z.png",
                "new folder retains chosen order and displays its first image");
            await CallAsync(window, "LoadFolderAsync", empty);
            SetSort(window, ThumbnailSortOrder.FileNameDescending);
            Check(Visible(window).Count == 0 && Current(window) == "", "sorting empty folder keeps preview empty");
        }
        finally
        {
            window.Close();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
