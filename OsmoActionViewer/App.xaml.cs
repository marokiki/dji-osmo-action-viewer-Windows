using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace OsmoActionViewer;

public partial class App : Application
{
    private static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OsmoActionViewer", "app.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("DispatcherUnhandledException", e.Exception);
        MessageBox.Show(
            $"予期しないエラーが発生しました。\n\n{e.Exception.Message}\n\nログ: {LogPath}",
            "Osmo Action Viewer",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        WriteCrashLog("AppDomainUnhandledException", e.ExceptionObject as Exception);
    }

    private void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("TaskSchedulerUnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static void WriteCrashLog(string source, Exception? exception)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var builder = new StringBuilder();
            builder.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}");
            builder.AppendLine(exception?.ToString() ?? "No exception details.");
            builder.AppendLine();
            File.AppendAllText(LogPath, builder.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
