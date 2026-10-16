using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace ImageSelector;

/// <summary>
/// Prevents a new WPF process from decoding until the previous Windows process exited.
/// The mutex is intentionally kept owned until the process terminates.
/// </summary>
internal sealed class InstanceCoordinator
{
    private const string MutexName = @"Local\falls247.ImageSelector.single-instance";
    private const string ReadyName = @"Local\falls247.ImageSelector.ready";
    private const string ActivationName = @"Local\falls247.ImageSelector.activate";

    private readonly Mutex _mutex;
    private bool _ownsMutex;
    private readonly EventWaitHandle _ready;
    private readonly EventWaitHandle _activate;

    private InstanceCoordinator(Mutex mutex, bool ownsMutex, EventWaitHandle ready, EventWaitHandle activate)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
        _ready = ready;
        _activate = activate;
    }

    internal EventWaitHandle ActivationEvent => _activate;

    internal static InstanceCoordinator? Enter()
    {
        var mutex = new Mutex(false, MutexName);
        var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyName);
        var activation = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationName);
        var owned = false;
        try
        {
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }

            if (!owned && ready.WaitOne(0))
            {
                // Give the active GUI permission to come forward from a taskbar relaunch.
                NativeWindowFocus.AllowSetForegroundWindow(uint.MaxValue);
                activation.Set();
                CrashLogger.Trace("Instance detection: active window signalled");
                return null;
            }
            if (!owned)
            {
                CrashLogger.Trace("Instance detection: waiting for old process exit");
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    NotifyBlocked(FindPreviousProcessId());
                    return null;
                }
            }

            ready.Reset();
            var previous = FindPreviousProcess();
            if (previous is not null)
            {
                using (previous)
                {
                    CrashLogger.Trace("Instance detection: waiting for PID=" + previous.Id);
                    if (!previous.WaitForExit(5000))
                    {
                        NotifyBlocked(previous.Id);
                        // Newly acquired mutex must be released if process continues to exist.
                        mutex.ReleaseMutex();
                        owned = false;
                        return null;
                    }
                }
            }
            CrashLogger.TraceResources("Instance acquired");
            return new InstanceCoordinator(mutex, owned, ready, activation);
        }
        finally
        {
            if (!owned)
            {
                activation.Dispose();
                ready.Dispose();
                mutex.Dispose();
            }
        }
    }

    private static Process? FindPreviousProcess()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            try
            {
                if (process.Id == current.Id || process.SessionId != current.SessionId ||
                    process.HasExited || process.StartTime >= current.StartTime)
                {
                    process.Dispose();
                    continue;
                }
                return process;
            }
            catch { process.Dispose(); }
        }
        return null;
    }

    private static int FindPreviousProcessId()
    {
        using var previous = FindPreviousProcess();
        return previous?.Id ?? -1;
    }

    private static void NotifyBlocked(int pid)
    {
        CrashLogger.Trace("Instance timed out: old PID=" + pid);
        System.Windows.MessageBox.Show(
            $"前回のImageSelector（PID {pid}）が終了していません。\nしばらく待ってから起動してください。",
            "Image Selector - 起動を待機", System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
    }

    internal void MarkReady()
    {
        _ready.Set();
        CrashLogger.Trace("Instance ready");
    }

    internal void MarkClosing()
    {
        _ready.Reset();
        CrashLogger.Trace("Instance closing");
    }

    // Never release/dispose ownership here: the kernel mutex must remain owned
    // through .NET, native WIC, CLR and single-file host process teardown.
    internal void TraceExit() => CrashLogger.TraceResources("Instance process exit pending");
}

internal static partial class NativeWindowFocus
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(uint processId);
}
