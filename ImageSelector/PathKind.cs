using System.IO;

namespace ImageSelector;

internal static class PathKind
{
    internal static bool IsRemote(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrWhiteSpace(root) &&
                new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
