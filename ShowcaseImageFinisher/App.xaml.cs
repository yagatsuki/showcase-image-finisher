using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ShowcaseImageFinisher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) WriteCrashLog(ex);
        };
        base.OnStartup(e);
        try
        {
            // Apply the saved theme before the window exists: the
            // Standard theme must start without the Styles dictionary.
            ThemeManager.Apply(SettingsStore.Load().Theme);
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            MessageBox.Show(
                "起動時エラーが発生しました。詳細を startup-error.log に保存しました。\n\n" + ex.Message,
                "Showcase Image Finisher", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        MessageBox.Show(
            "起動時エラーが発生しました。詳細を startup-error.log に保存しました。\n\n" + e.Exception.Message,
            "Showcase Image Finisher", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
        e.Handled = true;
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { /* last resort: nowhere left to report */ }
    }
}
