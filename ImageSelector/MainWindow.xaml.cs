using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Threading;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ImageSelector.Models;
using ImageSelector.Ui;
using ImageMetadata = ImageSelector.Models.ImageMetadata;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.VisualBasic.FileIO;
using System.Security;
using System.Runtime.InteropServices;

namespace ImageSelector;

public partial class MainWindow : Window
{
    // 対象は選択したフォルダの直下のみ。サブフォルダは再帰検索しない。
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"
    };

    private readonly DispatcherTimer _notificationTimer;
    private readonly DispatcherTimer _resizeTimer = new();
    private List<string> _imageFiles = new();
    private readonly ObservableCollection<ThumbnailItem> _thumbnailItems = new();
    private List<ThumbnailRow> _thumbnailRows = new();
    private readonly DispatcherTimer _thumbnailLayoutTimer = new();
    private readonly SemaphoreSlim _thumbnailDecodeSlots = new(1, 1);
    private const int ThumbnailCacheLimit = 160;
    private readonly Queue<ThumbnailItem> _thumbnailCacheOrder = new();
    private readonly SemaphoreSlim _previewDecodeSlots = new(1, 1);
    private CancellationTokenSource _thumbnailLoadCts = new();
    private CancellationTokenSource? _previewLoadCts;
    private CancellationTokenSource? _folderLoadCts;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _isClosing;
    private bool _folderPickerOpen;
    private bool _folderLoadInProgress;
    private ThumbnailItem? _highlightedThumbnail;
    private int _thumbnailColumns = 1;
    private string? _currentFolder;
    private int _currentIndex;
    private int _selectedCount;
    private bool _fileOperationInProgress;
    private readonly Dictionary<string, List<ImageAnnotation>> _drafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unsavedDrafts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = false
    };

    public MainWindow()
    {
        InitializeComponent();
        ThumbnailList.ItemsSource = _thumbnailRows;

        _thumbnailLayoutTimer.Interval = TimeSpan.FromMilliseconds(120);
        _thumbnailLayoutTimer.Tick += (_, _) =>
        {
            _thumbnailLayoutTimer.Stop();
            RebuildThumbnailRows();
        };

        _notificationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _notificationTimer.Tick += (_, _) =>
        {
            _notificationTimer.Stop();
            NoticeBorder.Visibility = Visibility.Collapsed;
        };

        _resizeTimer.Interval = TimeSpan.FromMilliseconds(250);
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            if (!_isClosing && !_fileOperationInProgress && _imageFiles.Count > 0) RenderCurrentImage();
        };

        UpdateControls();
    }

    private void ImageViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isClosing) return;
        // リサイズ中はデコードし直さず、操作が一段落してから表示解像度を合わせる。
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // コマンドラインにフォルダが指定されていたら、そのフォルダを開く。
        var pathArgument = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(pathArgument) && Directory.Exists(pathArgument))
        {
            _ = LoadFolderAsync(pathArgument);
            return;
        }

        // Do not automatically invoke the Shell folder picker on startup:
        // a blocked network/WSL folder can otherwise stall the desktop immediately.
        PlaceholderText.Text = "「フォルダを開く」または Ctrl+O で画像フォルダを選択";
    }

    private void ShowFolderPicker()
    {
        if (_isClosing || _folderPickerOpen || _fileOperationInProgress) return;
        _folderPickerOpen = true;
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "画像が入っているフォルダを選択",
                Multiselect = false
            };
            if (_currentFolder is not null && Directory.Exists(_currentFolder))
                dialog.InitialDirectory = _currentFolder;

            if (dialog.ShowDialog(this) == true && !_isClosing)
                _ = LoadFolderAsync(dialog.FolderName);
        }
        finally
        {
            _folderPickerOpen = false;
        }
    }

    // Do not wait for decoders or directory enumeration on the dispatcher during exit.
    private void Window_Closed(object sender, EventArgs e)
    {
        _isClosing = true;
        CrashLogger.TraceResources("Window closed (before cleanup)");
        _notificationTimer.Stop();
        _resizeTimer.Stop();
        _thumbnailLayoutTimer.Stop();
        _folderLoadCts?.Cancel();
        _lifetimeCts.Cancel();
        _previewLoadCts?.Cancel();
        _thumbnailLoadCts.Cancel();
        ImageDisplay.Source = null;
        ThumbnailList.ItemsSource = null;
        foreach (var item in _thumbnailItems) item.Thumbnail = null;
        _thumbnailCacheOrder.Clear();
        _thumbnailRows.Clear();
        _thumbnailItems.Clear();
        _imageFiles.Clear();
        _drafts.Clear();
        _unsavedDrafts.Clear();
    }

    // 選択フォルダの兄弟ディレクトリに保存。例: D:\photos -> D:\photos_selected
    private static string GetSelectedFolder(string sourceFolder)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceFolder));
        var folderName = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(folderName))
            folderName = "root";
        var parent = Directory.GetParent(normalized)?.FullName ?? normalized;
        return Path.Combine(parent, folderName + "_selected");
    }

    private async Task LoadFolderAsync(string folder)
    {
        if (_isClosing) return;
        _folderLoadCts?.Cancel();
        var request = new CancellationTokenSource();
        _folderLoadCts = request;
        _folderLoadInProgress = true;
        UpdateControls();
        var token = request.Token;
        try
        {
            CrashLogger.Trace("LoadFolder started: " + folder);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var fullFolder = Path.GetFullPath(folder);
            // Directory enumeration and selected-copy scan can block on a slow disk/share.
            var (images, existingSelectedFiles) = await Task.Run(() =>
            {
                var found = Directory.EnumerateFiles(fullFolder, "*", System.IO.SearchOption.TopDirectoryOnly)
                    .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .ToList();
                token.ThrowIfCancellationRequested();
                var selectedFolder = GetSelectedFolder(fullFolder);
                var selected = Directory.Exists(selectedFolder)
                    ? Directory.EnumerateFiles(selectedFolder, "*", System.IO.SearchOption.TopDirectoryOnly)
                        .Select(path => Path.GetFileName(path) ?? string.Empty)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                token.ThrowIfCancellationRequested();
                return (found, selected);
            }, token);
            if (_isClosing || token.IsCancellationRequested) return;

            CrashLogger.Trace($"LoadFolder enumerated: {images.Count} images");
            _previewLoadCts?.Cancel();
            var previousThumbnailLoad = _thumbnailLoadCts;
            previousThumbnailLoad.Cancel();
            _thumbnailLoadCts = new CancellationTokenSource();
            _thumbnailCacheOrder.Clear();
            ImageDisplay.Source = null;
            _currentFolder = fullFolder;
            _drafts.Clear();
            _unsavedDrafts.Clear();
            _invalidMetadata.Clear();
            _imageFiles = images;
            _currentIndex = 0;
            _highlightedThumbnail = null;

            _thumbnailItems.Clear();
            foreach (var image in images)
                _thumbnailItems.Add(new ThumbnailItem(image,
                    existingSelectedFiles.Contains(Path.GetFileName(image))));
            _selectedCount = _thumbnailItems.Count(item => item.IsSaved);
            RebuildThumbnailRows();
            FolderPathText.Text = fullFolder;
            FolderPathText.ToolTip = fullFolder;
            Title = $"Image Selector — 注釈付き選別 — {Path.GetFileName(Path.TrimEndingDirectorySeparator(fullFolder))}";
            _notificationTimer.Stop();
            NoticeBorder.Visibility = Visibility.Collapsed;
            RenderCurrentImage();
            CrashLogger.Trace($"LoadFolder UI ready: {images.Count} files, {clock.ElapsedMilliseconds} ms");
            if (images.Count == 0)
                ShowNotification("対応画像が見つからないフォルダ", NotificationKind.Information);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsFatalMemoryFailure(ex))
        {
            ShutDownAfterMemoryExhaustion("Folder enumeration", ex);
        }
        catch (Exception ex)
        {
            if (_isClosing || token.IsCancellationRequested) return;
            CrashLogger.Write("LoadFolder: " + folder, ex);
            ShowNotification($"フォルダを開けない: {ex.Message}", NotificationKind.Error);
            MessageBox.Show(this,
                $"フォルダ読み込みに失敗しました。\n\n{ex.Message}\n\n詳細ログ: {CrashLogger.LogPath}",
                "Image Selector - 読み込みエラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_folderLoadCts, request))
            {
                _folderLoadCts = null;
                _folderLoadInProgress = false;
                UpdateControls();
            }
            request.Dispose();
        }
    }

    private static bool IsFatalMemoryFailure(Exception exception)
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        return MemoryFailurePolicy.Classify(exception, p.PrivateMemorySize64) ==
            MemoryFailureAction.ExitApplication;
    }

    private static void ShutDownAfterMemoryExhaustion(string operation, Exception exception)
    {
        CrashLogger.Write(operation + ": fatal memory exhaustion", exception);
        MessageBox.Show(
            "メモリ不足のため安全に終了します。\n詳細ログ: " + CrashLogger.LogPath,
            "Image Selector - メモリ不足", MessageBoxButton.OK, MessageBoxImage.Error);
        Application.Current.Shutdown(-1);
    }

    // 画像のデコードはバックグラウンド。フォルダを開いた後、一覧を待たずに最初の画像を要求する。
    private void RenderCurrentImage()
    {
        if (_isClosing) return;
        _previewLoadCts?.Cancel();
        _previewLoadCts = new CancellationTokenSource();
        var token = _previewLoadCts.Token;

        ImageDisplay.Source = null;
        if (_imageFiles.Count > 0 && (_currentIndex < 0 || _currentIndex >= _imageFiles.Count))
            _currentIndex = Math.Clamp(_currentIndex, 0, _imageFiles.Count - 1);

        if (_imageFiles.Count == 0)
        {
            PlaceholderText.Text = _currentFolder is null
                ? "フォルダを選択すると画像を表示"
                : "このフォルダに対応画像がない";
            PlaceholderText.Visibility = Visibility.Visible;
            CurrentFileNameText.Text = "画像なし";
            _highlightedThumbnail = null;
            UpdateControls();
            return;
        }

        var path = _imageFiles[_currentIndex];
        CurrentFileNameText.Text = Path.GetFileName(path);
        // 一度表示した画像はサムネイルを先に出し、フル解像度に非同期で差し替える。
        var cachedThumbnail = _thumbnailItems[_currentIndex].Thumbnail;
        ImageDisplay.Source = cachedThumbnail;
        PlaceholderText.Text = "画像を読み込み中…";
        PlaceholderText.Visibility = cachedThumbnail is null ? Visibility.Visible : Visibility.Collapsed;
        SyncThumbnailSelection();
        UpdateControls();

        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, ImageViewport.ActualWidth * dpi.DpiScaleX * 1.25);
        var height = Math.Max(1, ImageViewport.ActualHeight * dpi.DpiScaleY * 1.25);
        _ = LoadPreviewAsync(path, width, height, token);
    }

    private async Task LoadPreviewAsync(string path, double targetWidth, double targetHeight, CancellationToken token)
    {
        var acquired = false;
        try
        {
            await _previewDecodeSlots.WaitAsync(token);
            acquired = true;
            var bitmap = await ImageLoader.PreviewAsync(path, targetWidth, targetHeight, token);
            if (_isClosing || token.IsCancellationRequested || _imageFiles.Count == 0 ||
                !string.Equals(_imageFiles[_currentIndex], path, StringComparison.OrdinalIgnoreCase)) return;

            ImageDisplay.Source = bitmap;
            PlaceholderText.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            // 移動先の画像だけを表示する。
        }
        catch (Exception ex) when (IsFatalMemoryFailure(ex))
        {
            ShutDownAfterMemoryExhaustion("Preview decode", ex);
        }
        catch (Exception ex)
        {
            if (_isClosing || token.IsCancellationRequested) return;
            CrashLogger.Write("LoadPreviewAsync: " + path, ex);
            PlaceholderText.Text = $"画像を表示できない\n{ex.Message}\n\n→ で次の画像へ";
            PlaceholderText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (acquired) _previewDecodeSlots.Release();
        }
    }

    // 行単位の仮想化。WrapPanel直結では全500枚の要素が一度に生成されてしまう。
    private void RebuildThumbnailRows()
    {
        if (ThumbnailList is null || ThumbSizeSlider is null) return;
        var tileWidth = 98.0 * ThumbSizeSlider.Value / 100.0 + 14.0;
        var available = Math.Max(1, ThumbnailList.ActualWidth - 20);
        _thumbnailColumns = Math.Max(1, (int)(available / tileWidth));
        var rows = new List<ThumbnailRow>((_thumbnailItems.Count + _thumbnailColumns - 1) / _thumbnailColumns);
        for (var i = 0; i < _thumbnailItems.Count; i += _thumbnailColumns)
        {
            var count = Math.Min(_thumbnailColumns, _thumbnailItems.Count - i);
            var items = new List<ThumbnailItem>(count);
            for (var j = 0; j < count; j++) items.Add(_thumbnailItems[i + j]);
            rows.Add(new ThumbnailRow(items));
        }
        _thumbnailRows = rows;
        ThumbnailList.ItemsSource = _thumbnailRows;
        SyncThumbnailSelection();
    }

    private void SyncThumbnailSelection()
    {
        if (_thumbnailItems.Count == 0) return;
        var item = _thumbnailItems[_currentIndex];
        if (!ReferenceEquals(item, _highlightedThumbnail))
        {
            if (_highlightedThumbnail is not null) _highlightedThumbnail.IsCurrent = false;
            item.IsCurrent = true;
            _highlightedThumbnail = item;
        }
        if (ThumbnailList.Visibility != Visibility.Visible || _thumbnailRows.Count == 0) return;
        var rowIndex = Math.Min(_currentIndex / _thumbnailColumns, _thumbnailRows.Count - 1);
        ThumbnailList.ScrollIntoView(_thumbnailRows[rowIndex]);
    }

    private void ThumbnailList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isClosing || Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 1) return;
        _thumbnailLayoutTimer.Stop();
        _thumbnailLayoutTimer.Start();
    }

    private void ThumbnailTile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing || _folderLoadInProgress || _fileOperationInProgress || sender is not FrameworkElement { DataContext: ThumbnailItem item }) return;
        var index = _thumbnailItems.IndexOf(item);
        if (index < 0 || index == _currentIndex) return;
        _currentIndex = index;
        RenderCurrentImage();
        e.Handled = true;
    }

    // 仮想化セルのサムネイルを1件ずつデコードする。
    private async void ThumbnailImage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isClosing || sender is not Image { DataContext: ThumbnailItem item }) return;
        if (item.Thumbnail is not null || item.IsLoading) return;
        var token = _thumbnailLoadCts.Token;
        item.IsLoading = true;
        var acquired = false;
        try
        {
            await _thumbnailDecodeSlots.WaitAsync(token);
            acquired = true;
            var thumbnail = await ImageLoader.ThumbnailAsync(item.Path, token);
            if (!_isClosing && !token.IsCancellationRequested)
            {
                item.Thumbnail = thumbnail;
                _thumbnailCacheOrder.Enqueue(item);
                if (_thumbnailCacheOrder.Count > ThumbnailCacheLimit)
                    _thumbnailCacheOrder.Dequeue().Thumbnail = null;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsFatalMemoryFailure(ex))
        {
            ShutDownAfterMemoryExhaustion("Thumbnail decode", ex);
        }
        catch (Exception ex)
        {
            if (!_isClosing && !token.IsCancellationRequested)
                CrashLogger.Write("ThumbnailImage_Loaded: " + item.Path, ex);
        }
        finally
        {
            if (acquired) _thumbnailDecodeSlots.Release();
            item.IsLoading = false;
        }
    }

    private void UpdateControls()
    {
        if (_isClosing) return;
        var hasImage = _imageFiles.Count > 0;
        var busy = _fileOperationInProgress || _folderLoadInProgress;
        // Cached selection state: avoid redundant synchronous I/O for every keypress.
        var hasSelectedCopy = hasImage && _thumbnailItems[_currentIndex].IsSaved;

        PreviousButton.IsEnabled = hasImage && !busy && _currentIndex > 0;
        NextButton.IsEnabled = hasImage && !busy && _currentIndex < _imageFiles.Count - 1;
        SelectButton.IsEnabled = hasImage && !busy;
        DeleteButton.IsEnabled = hasImage && !busy;
        DeleteSelectedCopyButton.IsEnabled = hasImage && !busy && hasSelectedCopy;
        AnnotateButton.IsEnabled = hasImage && !busy;
        OpenFolderButton.IsEnabled = !_fileOperationInProgress;
        ThumbnailList.IsEnabled = hasImage && !busy;

        PositionText.Text = hasImage ? $"{_currentIndex + 1}/{_imageFiles.Count}" : "0/0";

        if (_currentFolder is null)
        {
            SelectedCountText.Text = "選択済み: 0 件";
            AnnotationCountText.Text = "注釈: 0 件";
            return;
        }

        SelectedCountText.Text = $"選択済み {_selectedCount}/{_imageFiles.Count}件";
        if (hasImage)
        {
            var currentPath = _imageFiles[_currentIndex];
            var count = GetAnnotations(currentPath).Count;
            AnnotationCountText.Text = $"注釈 {count}件"
                + (_unsavedDrafts.Contains(currentPath) ? "*" : "");
        }
        else
        {
            AnnotationCountText.Text = "注釈: 0 件";
        }
    }

    private void Navigate(int direction)
    {
        if (_isClosing || _folderLoadInProgress || _fileOperationInProgress || _imageFiles.Count == 0)
        {
            return;
        }

        var next = _currentIndex + direction;
        if (next < 0 || next >= _imageFiles.Count)
        {
            return; // 端で止まる。先頭や末尾には循環しない。
        }

        _currentIndex = next;
        RenderCurrentImage();
    }

    // 上下キーは現在表示されているタイルの列数を使い、同じ列の前後の行へ移動。
    // サイドバー幅やサムネイル倍率を変更直後の場合は、まず行レイアウトを更新する。
    private void NavigateThumbnailVertical(int rowDirection)
    {
        if (_isClosing || _folderLoadInProgress || _fileOperationInProgress || _imageFiles.Count == 0 ||
            ThumbnailList.Visibility != Visibility.Visible)
            return;

        if (_thumbnailLayoutTimer.IsEnabled)
        {
            _thumbnailLayoutTimer.Stop();
            RebuildThumbnailRows();
        }

        var next = _currentIndex + rowDirection * _thumbnailColumns;
        // 最終行が途中で終わるとき、真下にタイルがなければ移動しない。
        if (next < 0 || next >= _imageFiles.Count)
            return;

        _currentIndex = next;
        RenderCurrentImage(); // 選択中のタイル・スクロール位置も同期する
    }

    private async Task OpenAnnotationEditorAsync()
    {
        if (_isClosing || _folderLoadInProgress || _fileOperationInProgress || _imageFiles.Count == 0) return;

        var current = _imageFiles[_currentIndex];
        // Alt+Enter は編集中の注釈だけをメモリ上の下書きへ反映し、画面は閉じない。
        // Ctrl+Enter のみウィンドウを閉じ、画像とJSONの保存へ進む。
        var editor = new AnnotationEditor(
            GetAnnotations(current),
            annotations =>
            {
                _drafts[current] = annotations.Select(a => a.Clone()).ToList();
                _unsavedDrafts.Add(current);
                UpdateControls();
            }) { Owner = this };

        if (editor.ShowDialog() == true)
        {
            _drafts[current] = editor.Result.Select(a => a.Clone()).ToList();
            _unsavedDrafts.Add(current);
            UpdateControls();
            if (editor.SaveAndSelectRequested)
                await SelectCurrentAsync();
        }
    }

    // 保存済みJSONを再読込することで、アプリを再起動した後も注釈を再編集できる。
    private List<ImageAnnotation> GetAnnotations(string source)
    {
        if (_drafts.TryGetValue(source, out var draft)) return draft;
        if (_currentFolder is null) return new();

        var jsonPath = Path.Combine(GetSelectedFolder(_currentFolder), Path.GetFileName(source) + ".json");
        if (!File.Exists(jsonPath))
        {
            _drafts[source] = new();
            return _drafts[source];
        }

        try
        {
            if (new FileInfo(jsonPath).Length > 2 * 1024 * 1024)
                throw new InvalidDataException("注釈JSONが上限2MBを超えています");
            var data = JsonSerializer.Deserialize<ImageMetadata>(File.ReadAllText(jsonPath, Encoding.UTF8), JsonOptions);
            if (data is null || data.SchemaVersion != 1 || data.ImageFile != Path.GetFileName(source))
            {
                throw new JsonException("JSONの形式または画像ファイル名が一致しない");
            }
            if (data.Annotations?.Any(x => x is null) == true)
                throw new JsonException("注釈配列に null の要素があります");
            _drafts[source] = (data.Annotations ?? new()).Select(x => x.Clone()).ToList();
        }
        catch (Exception ex)
        {
            // 破損JSONを空データで無断上書きしない。Enterで保存する前に修正が必要。
            CrashLogger.Write("GetAnnotations: " + jsonPath, ex);
            ShowNotification($"注釈JSONを読めない: {ex.Message}", NotificationKind.Error);
            _drafts[source] = new();
            _invalidMetadata.Add(source);
        }
        return _drafts[source];
    }

    private readonly HashSet<string> _invalidMetadata = new(StringComparer.OrdinalIgnoreCase);

    private async Task SelectCurrentAsync()
    {
        if (_isClosing || _folderLoadInProgress || _fileOperationInProgress || _currentFolder is null || _imageFiles.Count == 0) return;

        var source = _imageFiles[_currentIndex];
        if (_invalidMetadata.Contains(source))
        {
            ShowNotification("既存JSONを読み取れないため上書きを停止。JSONを修復または退避してから再実行", NotificationKind.Error);
            return;
        }

        var selectedFolder = GetSelectedFolder(_currentFolder);
        var fileName = Path.GetFileName(source);
        var target = Path.Combine(selectedFolder, fileName);
        var targetJson = target + ".json";
        string? tempImage = null;
        string? tempJson = null;
        var createdImage = false;

        var metadata = new ImageMetadata
        {
            SchemaVersion = 1,
            ImageFile = fileName,
            Annotations = GetAnnotations(source).Select(x => x.Clone()).ToList(),
            SavedAt = DateTimeOffset.Now
        };
        var contents = JsonSerializer.Serialize(metadata, JsonOptions);
        var token = _lifetimeCts.Token;

        _fileOperationInProgress = true;
        UpdateControls();
        try
        {
            Directory.CreateDirectory(selectedFolder);

            // 先にJSONの一時ファイルを完成させる。失敗時に画像だけ残さないため。
            tempJson = Path.Combine(selectedFolder, $".{Guid.NewGuid():N}.json.tmp");
            await File.WriteAllTextAsync(tempJson, contents, new UTF8Encoding(false), token);

            if (!File.Exists(target))
            {
                // 画像を同じフォルダ内の一時ファイルへコピーしてから正式名へ移動。
                tempImage = Path.Combine(selectedFolder, $".{Guid.NewGuid():N}.image.tmp");
                await using (var input = new FileStream(
                    source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 1024 * 1024, options: FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(
                    tempImage, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 1024 * 1024, options: FileOptions.Asynchronous))
                {
                    await input.CopyToAsync(output, token);
                    await output.FlushAsync(token);
                }
                File.Move(tempImage, target, overwrite: false);
                tempImage = null;
                createdImage = true;
            }

            // 既存JSONは原子リネームで更新。既存画像のバイト列は変更しない。
            File.Move(tempJson, targetJson, overwrite: true);
            tempJson = null;
            if (createdImage) _selectedCount++;
            _thumbnailItems[_currentIndex].IsSaved = true;
            _unsavedDrafts.Remove(source);
            var status = createdImage ? "画像＋注釈JSONを保存" : "既存画像の注釈JSONを更新";
            NavigateAfterSelection();
            ShowNotification($"{status}: {fileName}（{metadata.Annotations.Count} 件）", NotificationKind.Success);
        }
        catch (OperationCanceledException) when (_isClosing)
        {
            // Window is closing; temporary files are removed in finally.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (createdImage)
            {
                try { File.Delete(target); }
                catch (IOException) { /* ロールバックできない場合、再試行で救済する */ }
                catch (UnauthorizedAccessException) { /* 同上 */ }
            }
            _selectedCount = _imageFiles.Count(path =>
                File.Exists(Path.Combine(selectedFolder, Path.GetFileName(path))));
            ShowNotification($"保存失敗（画像を進めない）: {ex.Message}", NotificationKind.Error);
        }
        finally
        {
            foreach (var temporary in new[] { tempImage, tempJson })
            {
                if (temporary is null) continue;
                try { File.Delete(temporary); }
                catch (IOException) { /* 使用中ファイルの削除不可 */ }
                catch (UnauthorizedAccessException) { /* 削除権限なし */ }
            }
            _fileOperationInProgress = false;
            if (!_isClosing) UpdateControls();
        }
    }

    private void DeleteSelectedCopyArtifacts()
    {
        if (_isClosing || _folderLoadInProgress || _fileOperationInProgress || _currentFolder is null || _imageFiles.Count == 0) return;

        var source = _imageFiles[_currentIndex];
        var fileName = Path.GetFileName(source);
        var selectedFolder = GetSelectedFolder(_currentFolder);
        var selectedImage = Path.Combine(selectedFolder, fileName);
        var selectedJson = selectedImage + ".json";
        var hadImage = File.Exists(selectedImage);
        var hadJson = File.Exists(selectedJson);

        if (!hadImage && !hadJson)
        {
            ShowNotification("この画像には削除対象の 選択済みデータがありません", NotificationKind.Information);
            return;
        }

        var imageLabel = hadImage ? selectedImage : "なし";
        var jsonLabel = hadJson ? selectedJson : "なし";
        var decision = MessageBox.Show(
            this,
            $"選択済み保存先の選択済みデータを削除しますか？\n\n画像: {imageLabel}\nJSON: {jsonLabel}",
            "選択済みデータの削除確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (decision != MessageBoxResult.Yes) return;

        _fileOperationInProgress = true;
        UpdateControls();
        try
        {
            if (hadImage) File.Delete(selectedImage);
            if (hadJson) File.Delete(selectedJson);

            _drafts[source] = new List<ImageAnnotation>();
            _unsavedDrafts.Remove(source);
            _invalidMetadata.Remove(source);
            _thumbnailItems[_currentIndex].IsSaved = false;
            _selectedCount = Math.Max(0, _selectedCount - (hadImage ? 1 : 0));
            UpdateControls();
            ShowNotification($"選択済み保存先の画像とJSONを削除: {fileName}", NotificationKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            ShowNotification($"選択済みデータの削除に失敗: {ex.Message}", NotificationKind.Error);
        }
        finally
        {
            _fileOperationInProgress = false;
            UpdateControls();
        }
    }

    // Deleteは元フォルダの画像だけをごみ箱へ移動する。
    // selectedフォルダ内のコピー画像・サイドカーJSONは維持する。
    private void DeleteCurrentOriginal()
    {
        if (_isClosing || _folderLoadInProgress || _fileOperationInProgress || _currentFolder is null || _imageFiles.Count == 0) return;

        var source = _imageFiles[_currentIndex];
        var fileName = Path.GetFileName(source);
        var selectedImage = Path.Combine(GetSelectedFolder(_currentFolder), fileName);
        var hasSavedCopy = File.Exists(selectedImage);
        var hasUnsavedDraft = _unsavedDrafts.Contains(source);
        var additionalWarnings = (hasSavedCopy
                ? "\n選択済み保存先のコピー画像・注釈JSONは残します。" : "")
            + (hasUnsavedDraft
                ? "\nこの画像の未保存注釈は破棄されます。" : "");

        // Noが既定。キーリピートで削除ダイアログが連続しないようにKeyDown側でも制御。
        var decision = MessageBox.Show(
            this,
            $"元画像「{fileName}」をごみ箱へ移動しますか？\n\n元画像の場所: {source}{additionalWarnings}",
            "元画像の削除確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (decision != MessageBoxResult.Yes) return;

        // 削除中は画像切替・保存・フォルダ変更を抑制。
        _fileOperationInProgress = true;
        UpdateControls();
        try
        {
            if (!File.Exists(source)) throw new FileNotFoundException("元画像が見つからない", source);

            // サムネイルとメイン画像の参照を解除してからWindowsのごみ箱へ移動。
            ImageDisplay.Source = null;
            FileSystem.DeleteFile(
                source,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
            if (File.Exists(source)) throw new IOException("元画像が残っているため、削除完了を確認できない");

            // データ構造の更新は削除成功を確認してから一括実行。
            var removedIndex = _currentIndex;
            _drafts.Remove(source);
            _unsavedDrafts.Remove(source);
            _invalidMetadata.Remove(source);
            _imageFiles.RemoveAt(removedIndex);
            _thumbnailItems.RemoveAt(removedIndex);
            _currentIndex = Math.Min(removedIndex, Math.Max(0, _imageFiles.Count - 1));
            _highlightedThumbnail = null;
            RebuildThumbnailRows();

            var selectedFolder = GetSelectedFolder(_currentFolder);
            _selectedCount = _imageFiles.Count(path =>
                File.Exists(Path.Combine(selectedFolder, Path.GetFileName(path))));
            RenderCurrentImage();
            ShowNotification($"元画像をごみ箱へ移動: {fileName}", NotificationKind.Success);
        }
        catch (OperationCanceledException)
        {
            RenderCurrentImage();
            ShowNotification("元画像の削除をキャンセル", NotificationKind.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or SecurityException)
        {
            RenderCurrentImage();
            ShowNotification($"元画像の削除に失敗: {ex.Message}", NotificationKind.Error);
        }
        finally
        {
            _fileOperationInProgress = false;
            UpdateControls();
        }
    }

    private void NavigateAfterSelection()
    {
        if (_currentIndex < _imageFiles.Count - 1)
        {
            _currentIndex++;
            RenderCurrentImage();
        }
        else
        {
            // 最終画像はそのまま表示し、選択済み状態を更新する。
            UpdateControls();
        }
    }

    private enum NotificationKind { Success, Information, Error }

    private void ShowNotification(string message, NotificationKind kind)
    {
        NoticeText.Text = message;
        NoticeBorder.Background = new SolidColorBrush(kind switch
        {
            NotificationKind.Success => Color.FromRgb(6, 95, 70),
            NotificationKind.Information => Color.FromRgb(30, 64, 175),
            _ => Color.FromRgb(153, 27, 27)
        });
        NoticeBorder.Visibility = Visibility.Visible;
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Altを含むショートカットはWPFでKey.Systemに変換される場合がある。
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key
        };
        if (key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (!_fileOperationInProgress) ShowFolderPicker();
            e.Handled = true;
            return;
        }

        if (key == Key.Delete && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            if (!e.IsRepeat) DeleteSelectedCopyArtifacts();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        switch (key)
        {
            case Key.Left:
                Navigate(-1);
                e.Handled = true;
                break;
            case Key.Right:
                Navigate(1);
                e.Handled = true;
                break;
            case Key.Up:
                NavigateThumbnailVertical(-1);
                e.Handled = true;
                break;
            case Key.Down:
                NavigateThumbnailVertical(1);
                e.Handled = true;
                break;
            case Key.T:
                ShowThumbnailsCheckBox.IsChecked = ShowThumbnailsCheckBox.IsChecked != true;
                UpdateThumbnailVisibility();
                e.Handled = true;
                break;
            case Key.Space:
                _ = OpenAnnotationEditorAsync();
                e.Handled = true;
                break;
            case Key.Delete:
                if (!e.IsRepeat) DeleteCurrentOriginal();
                e.Handled = true;
                break;
            case Key.Return:
                _ = SelectCurrentAsync();
                e.Handled = true;
                break;
        }
    }

    private void ShowThumbnailsCheckBox_Click(object sender, RoutedEventArgs e) => UpdateThumbnailVisibility();

    private void UpdateThumbnailVisibility()
    {
        ThumbnailList.Visibility = ShowThumbnailsCheckBox.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        ThumbSizeSlider.IsEnabled = ShowThumbnailsCheckBox.IsChecked == true;
        if (ThumbnailList.Visibility == Visibility.Visible && _thumbnailItems.Count > 0)
            SyncThumbnailSelection();
    }

    private void ThumbSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isClosing) return;
        if (ThumbScaleText is not null)
            ThumbScaleText.Text = $"{(int)Math.Round(e.NewValue)}%";
        if (_thumbnailLayoutTimer is not null)
        {
            _thumbnailLayoutTimer.Stop();
            _thumbnailLayoutTimer.Start();
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e) => ShowFolderPicker();
    private void PreviousButton_Click(object sender, RoutedEventArgs e) => Navigate(-1);
    private void NextButton_Click(object sender, RoutedEventArgs e) => Navigate(1);
    private void SelectButton_Click(object sender, RoutedEventArgs e) => _ = SelectCurrentAsync();
    private void AnnotateButton_Click(object sender, RoutedEventArgs e) => _ = OpenAnnotationEditorAsync();
    private void DeleteButton_Click(object sender, RoutedEventArgs e) => DeleteCurrentOriginal();
    private void DeleteSelectedCopyButton_Click(object sender, RoutedEventArgs e) => DeleteSelectedCopyArtifacts();
}
