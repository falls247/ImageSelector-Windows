using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace ImageSelector;

public partial class App : Application
{
    private readonly InstanceCoordinator _coordinator;
    private RegisteredWaitHandle? _activationRegistration;
    private bool _showingError;

    internal App(InstanceCoordinator coordinator)
    {
        _coordinator = coordinator;
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
        base.OnStartup(e);
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _coordinator.ActivationEvent,
            (_, timedOut) =>
            {
                if (timedOut) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (MainWindow is not Window window || !window.IsVisible) return;
                    if (window.WindowState == WindowState.Minimized)
                        window.WindowState = WindowState.Normal;
                    window.Activate();
                }), DispatcherPriority.Background);
            }, null, Timeout.Infinite, false);

        // Only the kernel mutex owner ever constructs the WPF main window.
        var main = new MainWindow();
        MainWindow = main;
        main.ContentRendered += (_, _) => _coordinator.MarkReady();
        main.Closing += (_, _) => _coordinator.MarkClosing();
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _coordinator.MarkClosing();
        _activationRegistration?.Unregister(null);
        CrashLogger.TraceResources("App.OnExit");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLogger.Write("Application.DispatcherUnhandledException", e.Exception);
        // Do not silently continue in a truly exhausted managed heap.
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
        finally { _showingError = false; }
    }
}
