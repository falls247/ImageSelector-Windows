using System.Windows;
using System.Windows.Threading;

namespace ImageSelector;

public partial class App : Application
{
    private bool _showingError;

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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLogger.Write("Application.DispatcherUnhandledException", e.Exception);
        // フォルダ選択や画像デコード等で未知の例外が出てもUIからログを確認可能にする。
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
