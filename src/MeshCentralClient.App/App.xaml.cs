using System.Windows;
using System.Windows.Threading;

namespace MeshCentralClient.App;

public partial class App : Application
{
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>
    /// Every window says its icon itself, and the taskbar knows the app by name. Left to Windows, it worked out the icon from the exe and its
    /// cache of that path, and a person saw the generic one.
    /// Cada ventana dice su icono ella misma, y la barra de tareas conoce la app por su nombre. Dejado a Windows, lo deducía del exe y de su
    /// caché de esa ruta, y una persona veía el genérico.
    /// </summary>
    private static void UseOwnIcon()
    {
        SetCurrentProcessExplicitAppUserModelID("Nebur692.MeshCentralClient");
        var icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window { Icon: null } w) w.Icon = icon;
        }));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        UseOwnIcon();
        // A failure nobody expected is shown with its code instead of closing the app.
        // Un fallo que nadie esperaba se enseña con su código en vez de cerrar la app.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            ErrorWindow.Show(Current?.MainWindow, UserError.Unexpected(args.Exception));
        };
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
        base.OnStartup(e);
        ThemeService.LoadAndApply();
    }
}
