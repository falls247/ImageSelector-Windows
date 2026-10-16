using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ImageSelector;

public partial class App : Application
{
    private const string InstanceName = @"Local\falls247.ImageSelector.single-instance";
    private const string ActivationName = @"Local\falls247.ImageSelector.activate";
    private bool _showingError;
    private Mutex? _instanceMutex;
    private bool _ownsInstance;
    private EventWaitHandle? _activationEvent;
    private EventWaitHandle? _exitEvent;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                CrashLogger.Write("AppDomain.UnhandledException (fatal)", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLogger.Write("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // A pinned taskbar EXE can be launched before the previous process has exited.
        // Only one process in a Windows logon session may decode pictures at once.
        _instanceMutex = new Mutex(false, InstanceName);
        try
        {
            _ownsInstance = _instanceMutex.WaitOne(TimeSpan.FromSeconds(2));
        }
        catch (AbandonedMutexException)
        {
            _ownsInstance = true;
        }

        if (!_ownsInstance)
        {
            // Ask the already running window to come forward; do not start a second WPF host.
            using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationName);
            activate.Set();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        CrashLogger.TraceResources("Startup acquired instance mutex");
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
        var activation = _activationEvent;
        var stopping = _exitEvent;
        _ = Task.Run(() =>
        {
            try
            {
                while (WaitHandle.WaitAny(new WaitHandle[] { activation, stopping }) == 0)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (MainWindow is not Window window || !window.IsVisible) return;
                        if (window.WindowState == WindowState.Minimized)
                            window.WindowState = WindowState.Normal;
                        window.Activate();
                    }), DispatcherPriority.Background);
                }
            }
            catch (ObjectDisposedException) { /* process is exiting */ }
        });

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exitEvent?.Set();
        // Mutex ownership belongs to the dispatcher thread; release it only here.
        if (_ownsInstance) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLogger.Write("Application.DispatcherUnhandledException", e.Exception);
        // OOM must not be swallowed: continuing can starve the entire Windows desktop.
        if (e.Exception is OutOfMemoryException)
        {
            e.Handled = false;
            return;
        }
        e.Handled = true;
        if (_showingError) return;
        try
        {
            _showingError = true;
            MessageBox.Show(
                $"アプリ内部でエラーが発生しました。\n\n{e.Exception.Message}\n\n" +
                $"詳細ログ: {CrashLogger.LogPath}\n\n" +
                "継続できない場合はアプリを終了し、ログを確認してください。",
                "Image Selector - エラー",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _showingError = false;
        }
    }
}
