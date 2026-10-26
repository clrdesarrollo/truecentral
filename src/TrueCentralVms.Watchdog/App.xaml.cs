using System.Windows;

namespace TrueCentralVms.Watchdog;

/// <summary>
/// Una sola instancia por sesión de Windows. Si ya hay un Watchdog corriendo (p. ej. en segundo plano desde el inicio de
/// sesión), abrirlo otra vez solo le pide al que ya está que muestre su ventana.
///
/// <c>--background</c> (lo usa la tarea programada del inicio de sesión) arranca con la ventana oculta: solo el ícono
/// junto al reloj. Cerrar la ventana tampoco termina la aplicación (ShutdownMode = OnExplicitShutdown): se sale desde
/// el menú del ícono.
/// </summary>
public partial class App : Application
{
    public const string BackgroundArgument = "--background";
    private const string InstanceName = @"Local\CLRTrueCentralVMS.Watchdog";
    private const string ShowEventName = @"Local\CLRTrueCentralVMS.Watchdog.Show";

    private Mutex? _instance;
    private EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(initiallyOwned: true, InstanceName, out bool createdNew);
        if (!createdNew)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { /* el otro está saliendo */ }
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        if (!e.Args.Contains(BackgroundArgument, StringComparer.OrdinalIgnoreCase))
            window.ShowFromTray();

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(window.ShowFromTray);
        }) { IsBackground = true, Name = "Watchdog: mostrar ventana" };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instance is not null)
        {
            try { _instance.ReleaseMutex(); } catch { /* no era el dueño */ }
            _instance.Dispose();
        }
        base.OnExit(e);
    }
}
