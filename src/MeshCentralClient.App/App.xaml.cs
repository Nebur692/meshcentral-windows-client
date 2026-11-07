using System.Windows;
using System.Windows.Threading;

namespace MeshCentralClient.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // A failure nobody expected is shown with its code instead of closing the app.
        // Un fallo que nadie esperaba se enseña con su código en vez de cerrar la app.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            ErrorWindow.Show(Current?.MainWindow, UserError.Unexpected(args.Exception));
        };
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
        base.OnStartup(e);
    }
}
