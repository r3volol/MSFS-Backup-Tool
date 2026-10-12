using System.Windows;
using System.Windows.Threading;
using MsfsBackup.Core;

namespace MsfsBackupTool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Elevated helper: create symbolic links, no window.
        if (e.Args.Length == 3 && e.Args[0] == LinkCreator.HelperVerb)
        {
            int code = 1;
            try { code = LinkCreator.RunHelper(e.Args[1], e.Args[2]); }
            finally { Shutdown(code); }
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"Unexpected error:\n\n{e.Exception.Message}", "MSFS Backup Tool", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
