namespace ImageSelector;

public enum MemoryFailureAction { NotMemoryFailure, SkipImage, ExitApplication }

public static class MemoryFailurePolicy
{
    public const long ProcessPrivateLimit = 1536L * 1024 * 1024;
    private static readonly HashSet<int> NativeMemoryErrors =
    [
        unchecked((int)0x8007000E), // E_OUTOFMEMORY
        unchecked((int)0x800705AF), // ERROR_COMMITMENT_LIMIT
        unchecked((int)0x80070008)  // ERROR_NOT_ENOUGH_MEMORY
    ];

    public static MemoryFailureAction Classify(Exception error, long privateBytes)
    {
        var isNativeMemoryFailure = NativeMemoryErrors.Contains(error.HResult);
        if (error is not OutOfMemoryException && !isNativeMemoryFailure)
            return MemoryFailureAction.NotMemoryFailure;

        if (error is OutOfMemoryException || privateBytes >= ProcessPrivateLimit)
            return MemoryFailureAction.ExitApplication;

        return MemoryFailureAction.SkipImage;
    }
}
