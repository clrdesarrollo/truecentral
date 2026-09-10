using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using TrueCentralVms.WebControl.Fingerprint;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace TrueCentralVms.WebControl;

/// <summary>
/// El web control vive en la bandeja del sistema: arranca con la sesión, no
/// molesta con ventanas y atiende al panel web en 127.0.0.1. La ventana solo
/// sirve para diagnosticar (estado, lectores detectados y actividad); toda la
/// configuración de captura se hace desde el panel web.
/// </summary>
public partial class App : Application
{
    private readonly List<string> _activity = [];

    private WebControlSettings _settings = null!;
    private NotifyIcon? _tray;
    private MainWindow? _window;

    public FingerprintService Fingerprint { get; private set; } = null!;
    public WebControlHost Host { get; private set; } = null!;
    public bool Running { get; private set; }
    public string StatusText { get; private set; } = "Iniciando…";

    /// <summary>Avisa a la ventana de diagnóstico (si está abierta) de cada novedad.</summary>
    public event Action<string>? Logged;

    public IReadOnlyList<string> Activity => _activity;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Log($"Error no controlado: {args.Exception.Message}");
            args.Handled = true;
        };

        _settings = WebControlSettings.Load(Log);
        Fingerprint = new FingerprintService(Log);
        Host = new WebControlHost(_settings, Fingerprint, Log);

        CreateTrayIcon();

        try
        {
            await Host.StartAsync();
            Running = true;
            StatusText = $"Web control activo en {Host.BaseUrl}";
            SetTrayState($"CLR TrueCentral VMS — Complemento — {Host.BaseUrl}", ToolTipIcon.Info,
                "Listo para enrolar huellas desde el panel web.");
        }
        catch (Exception ex)
        {
            Running = false;
            StatusText = "El web control no pudo iniciar";
            Log(ex.Message);
            SetTrayState("CLR TrueCentral VMS — Complemento — con problemas", ToolTipIcon.Error, ex.Message);
        }
        _window?.RefreshStatus();
    }

    private void CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Estado del web control…", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => ExitControl());

        _tray = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "CLR TrueCentral VMS — Complemento",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private static Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } icon)
                return icon;
        }
        catch { }
        return SystemIcons.Application;
    }

    private void SetTrayState(string tip, ToolTipIcon icon, string message)
    {
        if (_tray is null) return;
        // El texto del icono de bandeja tiene un tope de 63 caracteres.
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        _tray.ShowBalloonTip(4000, "CLR TrueCentral VMS — Complemento", message, icon);
    }

    public void ShowWindow()
    {
        _window ??= new MainWindow(this);
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ForgetWindow() => _window = null;

    public void ExitControl()
    {
        if (Fingerprint.Busy &&
            MessageBox.Show("Hay una captura de huella en curso. ¿Cerrar el web control igual?",
                "CLR TrueCentral VMS — Complemento", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _tray?.Dispose();
        _tray = null;
        Host.StopAsync().GetAwaiter().GetResult();
        Shutdown();
    }

    public void Log(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (_activity)
        {
            _activity.Add(line);
            if (_activity.Count > 200) _activity.RemoveAt(0);
        }
        Dispatcher.BeginInvoke(() => Logged?.Invoke(line));
    }
}
