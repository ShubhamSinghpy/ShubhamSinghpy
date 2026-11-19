using System.Windows;
using System.Windows.Threading;
using MediaOrganizer.Core;

namespace MediaOrganizer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Log unexpected errors instead of silently closing; the journal makes a restart safe.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Unhandled exception.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
        AppLog.Info("Application started.");
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception.", e.Exception);
        MessageBox.Show(
            "An unexpected error occurred:\n\n" + e.Exception.Message +
            "\n\nYour original files are not affected. Details were written to the log file.\n" +
            "If a copy was running, start the app again and press Analyze, then Start / Resume to continue safely.",
            "Professional Media Organizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
