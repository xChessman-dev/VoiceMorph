using System.Windows;

namespace VoiceMorph.App;

public partial class App : Application
{
    internal static string? DiagnosticDirectory { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == "--ui-check") DiagnosticDirectory = e.Args[1];
        if (DiagnosticDirectory is not null) DispatcherUnhandledException += (_, args) =>
        {
            System.IO.Directory.CreateDirectory(DiagnosticDirectory);
            System.IO.File.WriteAllText(System.IO.Path.Combine(DiagnosticDirectory, "error.txt"), args.Exception.ToString());
            args.Handled = true; Shutdown(1);
        };
        base.OnStartup(e);
        var window = new MainWindow();
        if (DiagnosticDirectory is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -4000;
            window.Top = 0;
            window.ShowActivated = false;
        }
        window.Show();
    }
}
