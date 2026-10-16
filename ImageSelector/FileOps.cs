using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace ImageSelector;

/// <summary>
/// Synchronous file operations are only permitted in background work.
/// </summary>
internal static class FileOps
{
    internal static void RequireBackground(string operation)
    {
#if DEBUG
        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            CrashLogger.Trace("UI thread I/O: " + operation);
#endif
    }

    internal static bool FileExists(string path)
    {
        RequireBackground("File.Exists");
        return File.Exists(path);
    }

    internal static bool DirectoryExists(string path)
    {
        RequireBackground("Directory.Exists");
        return Directory.Exists(path);
    }

    internal static void CreateDirectory(string path)
    {
        RequireBackground("Directory.CreateDirectory");
        Directory.CreateDirectory(path);
    }

    internal static void MoveFile(string source, string destination, bool overwrite)
    {
        RequireBackground("File.Move");
        File.Move(source, destination, overwrite);
    }

    internal static void DeleteFile(string path)
    {
        RequireBackground("File.Delete");
        File.Delete(path);
    }

    internal static void DeleteToRecycleBin(string path)
    {
        RequireBackground("RecycleBin.DeleteFile");
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs,
            RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
    }
}
