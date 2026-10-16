using System.IO;
using System.Text;

namespace ImageSelector;

/// <summary>GUIアプリが突然終了しても原因を確認できるよう例外を記録する。</summary>
internal static class CrashLogger
{
    private static readonly object Sync = new();

    internal static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ImageSelector", "logs", "error.log");

    internal static void Trace(string message)
    {
        try
        {
            lock (Sync)
            {
                var path = LogPath;
                var directory = Path.GetDirectoryName(path);
                if (directory is not null) Directory.CreateDirectory(directory);
                File.AppendAllText(path,
                    $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* diagnostics must never crash the app */ }
    }

    internal static void Write(string context, Exception exception)
    {
        try
        {
            lock (Sync)
            {
                var path = LogPath;
                var directory = Path.GetDirectoryName(path);
                if (directory is not null) Directory.CreateDirectory(directory);
                File.AppendAllText(path,
                    $"[{DateTimeOffset.Now:O}] {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // ログ先の容量不足・権限不足が、さらに例外を引き起こさないようにする。
        }
    }
}
