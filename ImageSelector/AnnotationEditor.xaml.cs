using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ImageSelector.Models;

namespace ImageSelector;

public partial class AnnotationEditor : Window
{
    private readonly ObservableCollection<EditableAnnotation> _entries = new();
    private bool _switchingSelection;
    private int _focusRequestVersion;
    private readonly Action<IReadOnlyList<ImageAnnotation>> _applyDraft;

    public List<ImageAnnotation> Result { get; private set; } = new();
    public bool SaveAndSelectRequested { get; private set; }

    public AnnotationEditor(
        IEnumerable<ImageAnnotation> existing,
        Action<IReadOnlyList<ImageAnnotation>> applyDraft)
    {
        _applyDraft = applyDraft;
        InitializeComponent();
        foreach (var item in existing)
        {
            _entries.Add(new EditableAnnotation(item.Clone()));
        }
        EntriesList.ItemsSource = _entries;
        if (_entries.Count > 0) EntriesList.SelectedIndex = 0;
        UpdateButtons();
        Loaded += (_, _) => FocusShortcutMode("ショートカット待機中（M / F / C / E / H）");
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as Button)?.Tag as string;
        if (tag is null) return;
        AddEntry(tag);
    }

    private void AddEntry(string tag)
    {
        ImageAnnotation entry = tag switch
        {
            "male" => new() { Type = "dialogue", Speaker = "male" },
            "female" => new() { Type = "dialogue", Speaker = "female" },
            "caption" => new() { Type = "caption" },
            "sound_effect" => new() { Type = "sound_effect" },
            "speech_bubble" => new() { Type = "speech_bubble" },
            _ => throw new InvalidOperationException("Unknown annotation type")
        };
        var added = new EditableAnnotation(entry);
        _entries.Add(added);
        EntriesList.SelectedItem = added;
        EntriesList.ScrollIntoView(added);
        AnnotationTextBox.Focus();
    }

    private void EntriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _switchingSelection = true;
        var selected = EntriesList.SelectedItem as EditableAnnotation;
        AnnotationTextBox.IsEnabled = selected is not null;
        AnnotationTextBox.Text = selected?.Entry.Text ?? "";
        EditingLabel.Text = selected is null ? "行を追加して編集" : $"{selected.DisplayType}の本文";
        _switchingSelection = false;
        UpdateButtons();
    }

    private void AnnotationTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_switchingSelection) return;
        if (EntriesList.SelectedItem is EditableAnnotation selected)
        {
            selected.Entry.Text = AnnotationTextBox.Text;
            selected.NotifyTextChanged();
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int direction)
    {
        var index = EntriesList.SelectedIndex;
        var nextIndex = index + direction;
        if (index < 0 || nextIndex < 0 || nextIndex >= _entries.Count) return;
        _entries.Move(index, nextIndex);
        EntriesList.SelectedIndex = nextIndex;
        UpdateButtons();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var index = EntriesList.SelectedIndex;
        if (index < 0) return;
        _entries.RemoveAt(index);
        if (_entries.Count > 0) EntriesList.SelectedIndex = Math.Min(index, _entries.Count - 1);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (MoveUpButton is null) return;
        var index = EntriesList.SelectedIndex;
        MoveUpButton.IsEnabled = index > 0;
        MoveDownButton.IsEnabled = index >= 0 && index < _entries.Count - 1;
        RemoveButton.IsEnabled = index >= 0;
    }

    // 選択肢を追加した後は本文入力へ移る。ユーザーが明示的に本文へ戻った場合は、
    // 先に予約した待機領域へのフォーカス復帰を無効化する。
    private void AnnotationTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _focusRequestVersion++;
    }

    // Alt+Enterや「注釈だけ反映」の直後はショートカット待機へ戻す。
    // ボタンのフォーカス処理が完了したあとにも一度適用することで復帰を安定させる。
    private void FocusShortcutMode(string status)
    {
        EditingStatusText.Text = status;
        var request = ++_focusRequestVersion;
        FocusManager.SetFocusedElement(this, ShortcutFocusTarget);
        Keyboard.Focus(ShortcutFocusTarget);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (request == _focusRequestVersion && IsVisible)
            {
                FocusManager.SetFocusedElement(this, ShortcutFocusTarget);
                Keyboard.Focus(ShortcutFocusTarget);
            }
        }), DispatcherPriority.Background);
    }

    private static Key NormalizeKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        _ => e.Key
    };

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = NormalizeKey(e);
        var modifiers = Keyboard.Modifiers;

        // 本文でのEscは注釈画面を閉じず、文字入力を終了する。
        // 日本語IMEによる変換中はEscがIMEの変換取り消しに使われる場合がある。
        if (key == Key.Escape && AnnotationTextBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            FocusShortcutMode("文字入力を解除。M / F / C / E / H で続けて追加可能");
            return;
        }

        if (key == Key.Return && modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ConfirmAnnotations();
            return;
        }

        if (key == Key.Return && modifiers == ModifierKeys.Alt)
        {
            e.Handled = true;
            ApplyAnnotationsWithoutClosing();
            return;
        }

        // 本文入力中のM/F/C/E/Hはショートカットとして扱わない。
        // それ以外のボタン・リスト・待機領域のどこにフォーカスがあっても操作できる。
        if (modifiers != ModifierKeys.None || AnnotationTextBox.IsKeyboardFocusWithin)
            return;

        string? kind = key switch
        {
            Key.M => "male",
            Key.F => "female",
            Key.C => "caption",
            Key.E => "sound_effect",
            Key.H => "speech_bubble",
            _ => null
        };
        if (kind is not null)
        {
            e.Handled = true;
            AddEntry(kind);
        }
    }

    private List<ImageAnnotation> ReadCurrentEntries() => _entries
        .Where(x => !string.IsNullOrWhiteSpace(x.Entry.Text))
        .Select(x => x.Entry.Clone())
        .ToList();

    // Alt+Enter / ボタン: 未保存下書きを親ウィンドウに反映。ダイアログは開いたまま。
    private void ApplyAnnotationsWithoutClosing()
    {
        var snapshot = ReadCurrentEntries();
        _applyDraft(snapshot);
        FocusShortcutMode($"注釈 {snapshot.Count} 件を下書きに反映。M / F / C / E / H で続けて追加可能");
    }

    private void SaveDraft_Click(object sender, RoutedEventArgs e) => ApplyAnnotationsWithoutClosing();
    private void SaveAndSelect_Click(object sender, RoutedEventArgs e) => ConfirmAnnotations();

    private void ConfirmAnnotations()
    {
        Result = ReadCurrentEntries();
        SaveAndSelectRequested = true;
        DialogResult = true;
    }

    public sealed class EditableAnnotation : INotifyPropertyChanged
    {
        public ImageAnnotation Entry { get; }
        public string DisplayType => Entry.Type switch
        {
            "dialogue" when Entry.Speaker == "male" => "男性セリフ",
            "dialogue" when Entry.Speaker == "female" => "女性セリフ",
            "caption" => "キャプション",
            "sound_effect" => "効果音",
            "speech_bubble" => "吹き出し",
            _ => "その他"
        };
        public string Preview => string.IsNullOrWhiteSpace(Entry.Text)
            ? "（未入力）"
            : Entry.Text.Replace('\r', ' ').Replace('\n', ' ');

        public EditableAnnotation(ImageAnnotation entry) => Entry = entry;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void NotifyTextChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
    }
}
