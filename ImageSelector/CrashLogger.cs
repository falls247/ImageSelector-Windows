using System.Diagnostics;
using System.IO;
using System.Text;

namespace ImageSelector;

/// <summary>Best-effort bounded crash diagnostics. Logging must never crash the UI.</summary>
internal static class CrashLogger
{
    private static readonly object Sync = new();
    private const long MaxLogBytes = 4 * 1024 * 1024;

    internal static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ImageSelector", "logs", "error.log");

    private static void Append(string message)
    {
        try
        {
            lock (Sync)
            {
                var path = LogPath;
                var directory = Path.GetDirectoryName(path);
                if (directory is not null) Directory.CreateDirectory(directory);
                // Rotates before append to avoid infinite error spam consuming system disk.
                if (File.Exists(path) && new FileInfo(path).Length >= MaxLogBytes)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* diagnostics are never critical to application control */ }
    }

    internal static void Trace(string message) => Append(message);

    internal static void TraceResources(string context)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            Append($"{context} PID={process.Id} privateMB={process.PrivateMemorySize64 / 1048576}" +
                $" workingMB={process.WorkingSet64 / 1048576} threads={process.Threads.Count} handles={process.HandleCount}" +
                $" managedMB={GC.GetTotalMemory(false) / 1048576}");
        }
        catch { /* best effort */ }
    }

    internal static void Write(string context, Exception exception) =>
        Append($"{context}{Environment.NewLine}{exception}{Environment.NewLine}");
}
