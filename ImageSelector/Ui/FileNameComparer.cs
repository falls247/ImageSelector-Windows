using System.Runtime.InteropServices;

namespace ImageSelector.Ui;

internal sealed partial class FileNameComparer : IComparer<string>
{
    internal static FileNameComparer Instance { get; } = new();

    private FileNameComparer() { }

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        return CompareLogical(left, right);
    }

    // Windowsの自然順比較を使い、数字列を数値として扱う。
    [LibraryImport("shlwapi.dll", EntryPoint = "StrCmpLogicalW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CompareLogical(string left, string right);
}
