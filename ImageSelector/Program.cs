using System.Threading;

namespace ImageSelector;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        CrashLogger.TraceResources("Main start");
        var coordinator = InstanceCoordinator.Enter();
        if (coordinator is null)
        {
            CrashLogger.TraceResources("Main exit as duplicate");
            return 0;
        }

        var code = 0;
        try
        {
            var app = new App(coordinator);
            code = app.Run();
            coordinator.MarkClosing();
            CrashLogger.TraceResources("App.Run returned");
        }
        catch (Exception ex)
        {
            CrashLogger.Write("Main fatal", ex);
            code = 1;
        }
        var delayValue = Environment.GetEnvironmentVariable("IMAGESELECTOR_TEST_EXIT_DELAY_MS");
        if (int.TryParse(delayValue, out var delay) && delay is > 0 and <= 60000)
            Thread.Sleep(delay);
        coordinator.TraceExit();
        // Avoid releasing the named mutex before the runtime and native image codecs
        // have terminated. Environment.Exit terminates process and closes its handles.
        Environment.Exit(code);
        return code;
    }
}
