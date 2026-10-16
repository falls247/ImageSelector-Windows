using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace ImageSelector;

/// <summary>Cross-process serialized log writes, bounded rotation and PID-level diagnostics.</summary>
internal static class CrashLogger
{
    private static readonly object Sync = new();
    private const string LockName = @"Local\falls247.ImageSelector.log";
    private const long MaxLogBytes = 4L * 1024 * 1024;

    internal static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ImageSelector", "logs", "error.log");

    private static void Append(string message)
    {
        var pid = Environment.ProcessId;
        var prefix = $"[{DateTimeOffset.Now:O}] PID={pid} TID={Environment.CurrentManagedThreadId} ";
        try
        {
            lock (Sync)
            {
                using var mutex = new Mutex(false, LockName);
                var acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(200); }
                    catch (AbandonedMutexException) { acquired = true; }
                    var path = acquired ? LogPath :
                        Path.Combine(Path.GetDirectoryName(LogPath)!, $"error.{pid}.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            if (acquired && File.Exists(path) &&
                                new FileInfo(path).Length >= MaxLogBytes)
                                File.Move(path, path + ".previous", overwrite: true);
                            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                                FileShare.ReadWrite | FileShare.Delete);
                            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                            writer.WriteLine(prefix + message);
                            return;
                        }
                        catch (IOException) when (attempt < 2) { Thread.Sleep(50); }
                    }
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }
        catch { /* diagnostics must not impact application behavior */ }
    }

    internal static void Trace(string message) => Append(message);

    internal static void TraceResources(string context)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            Append($"{context} privateMB={process.PrivateMemorySize64 / 1048576}" +
                $" workingMB={process.WorkingSet64 / 1048576} threads={process.Threads.Count}" +
                $" handles={process.HandleCount} managedMB={GC.GetTotalMemory(false) / 1048576}");
        }
        catch { /* process may have exited */ }
    }

    internal static void Write(string context, Exception exception) =>
        Append($"{context}{Environment.NewLine}{exception}{Environment.NewLine}");
}
