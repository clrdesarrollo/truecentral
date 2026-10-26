using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrueCentralVms.Watchdog;

/// <summary>
/// Monitor del servidor CLR TrueCentral VMS: vigila el servicio de Windows, la
/// API (/api/health) y PostgreSQL embebido, y permite iniciar/detener el
/// servicio. Es la capa EXTERNA de vigilancia: el supervisor que corre DENTRO
/// del servidor (panel web, Sistema > Servicios) reinicia los subsistemas
/// caídos, pero no puede levantarse a sí mismo si el proceso entero se cae.
///
/// Mantiene el servicio en ejecución: si lo encuentra detenido sin que alguien
/// lo haya pedido con «Detener servicio» (<see cref="StopIntent"/>), lo vuelve
/// a iniciar. Vive en segundo plano con un ícono junto al reloj: cerrar la
/// ventana solo la oculta, y se inicia solo al iniciar sesión un administrador
/// (tarea programada que crea el instalador, con <c>--background</c>).
/// </summary>
public partial class MainWindow : Window
{
    private const string ServiceName = "CLRTrueCentralVMS";

    /// <summary>Estado resumido del servicio para pintar la UI.</summary>
    private enum SvcState { NotInstalled, Stopped, Running, Pending, Unknown }

    private readonly ServerEnvironment _env = ServerEnvironment.Discover();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    private bool _checking;             // hay un ciclo de verificación en curso
    private bool _busy;                 // hay una operación iniciar/detener en curso
    private bool _stopRequested;        // el último stop fue pedido desde acá (no es una caída)
    private SvcState _lastSvc = SvcState.Unknown;
    private bool? _lastApiOk;
    /// <summary>Lectura anterior, para confirmar un cambio antes de anunciarlo.</summary>
    private bool? _previousApiRead;

    /// <summary>
    /// Detenido menos que esto no se toca: deja actuar primero a las acciones de
    /// recuperación del SCM (reinicio a los 5 s) y a un reinicio en curso (el
    /// del panel web detiene y vuelve a iniciar el servicio).
    /// </summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(15);
    private DateTime? _stoppedSinceUtc;
    private DateTime _nextAutoStartUtc = DateTime.MinValue;
    private int _autoStartAttempts;
    private string? _keepAliveNote;     // último motivo para NO reiniciar (se anuncia una vez)

    private readonly TrayIcon _tray = new();
    private bool _exiting;
    private bool _hiddenNoticeShown;

    private Brush Ok => (Brush)FindResource("OkBrush");
    private Brush Danger => (Brush)FindResource("DangerBrush");
    private Brush Warn => (Brush)FindResource("WarnBrush");
    private Brush Muted => (Brush)FindResource("MutedBrush");

    public MainWindow()
    {
        InitializeComponent();

        string version = GetType().Assembly.GetName().Version?.ToString(3) ?? "?";
        SelfVersionText.Text = $"Watchdog v{version}";

        // El servicio, la URL del panel y el puerto de la base no ocupan lugar
        // en la ventana: van en el tooltip del indicador al que pertenecen.
        SvcCard.ToolTip = $"Servicio de Windows {ServiceName}";
        ApiCard.ToolTip = $"API y panel web: {_env.BaseUrl} (estado por {_env.HealthUrl})";
        DbCard.ToolTip = $"PostgreSQL embebido en 127.0.0.1:{_env.PgPort}";

        if (!_env.ServerFound)
            AddActivity("No se encontró la instalación del servidor junto al Watchdog; se usan los puertos por defecto.", Warn);

        _tray.OpenRequested += ShowFromTray;
        _tray.StartRequested += () => _ = StartServiceAsync(automatic: false);
        _tray.StopRequested += () => _ = StopServiceAsync();
        _tray.OpenPanelRequested += OpenPanel;
        _tray.ExitRequested += ExitWatchdog;

        // Con --background la ventana nunca se muestra (Loaded no llega): el
        // primer sondeo no puede depender de ella.
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
        Dispatcher.BeginInvoke(async () => await CheckAsync());
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Cerrar la ventana la oculta: el Watchdog sigue vigilando junto al reloj.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
            if (!_hiddenNoticeShown)
            {
                _hiddenNoticeShown = true;
                _tray.Notify("El Watchdog sigue funcionando",
                    "Sigue vigilando el servidor en segundo plano. Ábralo desde el ícono junto al reloj.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
        }
        base.OnClosing(e);
    }

    private void ExitWatchdog()
    {
        var answer = MessageBox.Show(
            "Si sale del Watchdog, nadie volverá a iniciar el servidor si se detiene.\n\n" +
            "Vuelve a abrirse solo la próxima vez que inicie sesión. ¿Salir igual?",
            "Salir del Watchdog", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;
        _exiting = true;
        Close();
        Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _http.Dispose();
        _tray.Dispose();
        base.OnClosed(e);
    }

    // ------------------------------------------------------------------
    // Ciclo de verificación
    // ------------------------------------------------------------------
    private async Task CheckAsync()
    {
        if (_checking)
            return;
        _checking = true;
        try
        {
            var svcTask = Task.Run(QueryService);
            var apiTask = QueryApiAsync();
            var dbTask = QueryDatabaseAsync();
            await Task.WhenAll(svcTask, apiTask, dbTask);

            var (svc, svcDetail) = svcTask.Result;
            var (apiOk, serverVersion, apiDetail) = apiTask.Result;
            bool dbOk = dbTask.Result;

            ReportTransitions(svc, apiOk);
            KeepAlive(svc);
            UpdateUi(svc, svcDetail, apiOk, serverVersion, apiDetail, dbOk);
            FooterRight.Text = $"Última verificación: {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            _checking = false;
        }
    }

    private (SvcState State, string Detail) QueryService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => (SvcState.Running, "En ejecución"),
                ServiceControllerStatus.Stopped => (SvcState.Stopped, "Detenido"),
                ServiceControllerStatus.StartPending => (SvcState.Pending, "Iniciando..."),
                ServiceControllerStatus.StopPending => (SvcState.Pending, "Deteniendo..."),
                ServiceControllerStatus.PausePending or ServiceControllerStatus.Paused
                    or ServiceControllerStatus.ContinuePending => (SvcState.Pending, sc.Status.ToString()),
                _ => (SvcState.Unknown, sc.Status.ToString()),
            };
        }
        catch (InvalidOperationException)
        {
            return (SvcState.NotInstalled, "No instalado");
        }
        catch (Exception ex)
        {
            return (SvcState.Unknown, ex.Message);
        }
    }

    private async Task<(bool Ok, string? Version, string Detail)> QueryApiAsync()
    {
        try
        {
            using var response = await _http.GetAsync(_env.HealthUrl);
            if (!response.IsSuccessStatusCode)
                return (false, null, $"HTTP {(int)response.StatusCode}");
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            string? version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            return (true, version, "Responde");
        }
        catch (Exception)
        {
            return (false, null, "Sin respuesta");
        }
    }

    private async Task<bool> QueryDatabaseAsync()
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", _env.PgPort).WaitAsync(TimeSpan.FromMilliseconds(1000));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ReportTransitions(SvcState svc, bool apiOk)
    {
        if (_lastSvc != svc)
        {
            if (svc == SvcState.Running && _lastSvc != SvcState.Unknown)
                AddActivity("El servicio está en ejecución.", Ok);
            else if (svc == SvcState.Stopped && _lastSvc is SvcState.Running or SvcState.Pending)
            {
                if (_stopRequested || _busy)
                    AddActivity("El servicio quedó detenido.", Muted);
                else
                {
                    AddActivity("El servicio se detuvo inesperadamente.", Danger);
                    DumpRecentEventLog();
                    if (StopIntent.Read() is null)
                        _tray.Notify("El servidor se detuvo", "Se detuvo sin que nadie lo pidiera. El Watchdog lo volverá a iniciar.",
                            System.Windows.Forms.ToolTipIcon.Warning);
                }
            }
            else if (svc == SvcState.NotInstalled && _lastSvc != SvcState.Unknown)
                AddActivity("El servicio no está instalado en este equipo.", Danger);
            _lastSvc = svc;
        }

        // Solo se anuncia un cambio confirmado por DOS lecturas seguidas: con el
        // sondeo cada 2 s, un pico de carga puntual no debe verse como caída.
        bool confirmed = _previousApiRead == apiOk;
        if (confirmed && _lastApiOk is { } last && last != apiOk && svc == SvcState.Running)
        {
            AddActivity(apiOk ? "La API volvió a responder." : "La API dejó de responder.", apiOk ? Ok : Warn);
            _lastApiOk = apiOk;
        }
        else if (_lastApiOk is null)
            _lastApiOk = apiOk;   // primera lectura: solo se toma nota
        _previousApiRead = apiOk;
    }

    private void UpdateUi(SvcState svc, string svcDetail, bool apiOk, string? serverVersion, string apiDetail, bool dbOk)
    {
        SvcDot.Fill = svc switch
        {
            SvcState.Running => Ok,
            SvcState.Pending => Warn,
            SvcState.Stopped or SvcState.NotInstalled => Danger,
            _ => Muted,
        };
        SvcText.Text = svcDetail;

        ApiDot.Fill = apiOk ? Ok : svc == SvcState.Running ? Warn : Danger;
        ApiText.Text = apiDetail;

        DbDot.Fill = dbOk ? Ok : svc == SvcState.Running ? Warn : Danger;
        DbText.Text = dbOk ? "Activa" : "Sin conexión";

        ServerVersionText.Text = serverVersion is null ? "" : $"Servidor v{serverVersion}";

        var (dot, text) = svc switch
        {
            // El servidor puede estar corriendo como proceso suelto (desarrollo,
            // o un arranque a mano): no hay servicio que controlar, pero el
            // sistema está en pie y darlo por caído sería mentir.
            SvcState.NotInstalled when apiOk && dbOk => (Ok, "Servidor en ejecución (sin servicio de Windows)"),
            SvcState.NotInstalled => (Danger, "El servicio no está instalado — reinstale el servidor"),
            SvcState.Pending => (Warn, svcDetail),
            SvcState.Stopped => (Danger, "Servidor detenido"),
            SvcState.Running when apiOk && dbOk => (Ok, "Sistema operativo"),
            SvcState.Running => (Warn, "Servicio en ejecución; esperando API y base de datos..."),
            _ => (Muted, "Estado desconocido"),
        };
        GlobalDot.Fill = dot;
        GlobalText.Text = text;

        StartButton.IsEnabled = !_busy && svc == SvcState.Stopped;
        StopButton.IsEnabled = !_busy && svc == SvcState.Running;

        var trayState = dot == Ok ? TrayState.Ok : dot == Warn ? TrayState.Warn : dot == Danger ? TrayState.Danger : TrayState.Unknown;
        _tray.Update(trayState, text, StartButton.IsEnabled, StopButton.IsEnabled);

        KeepAliveText.Text = StopIntent.Read() is { } stopped
            ? $"Detenido a propósito el {stopped.ToLocalTime():dd-MM HH:mm}: no se reinicia solo."
            : "Vigilancia activa: si el servicio se detiene sin que se pida, se vuelve a iniciar.";
    }

    // ------------------------------------------------------------------
    // Mantener vivo
    // ------------------------------------------------------------------
    /// <summary>
    /// Vuelve a iniciar el servicio si lleva detenido más de <see cref="StopGrace"/>
    /// y nadie lo pidió. No lo toca si está deshabilitado en Windows (también es
    /// una decisión explícita) ni si hay otro proceso del servidor corriendo
    /// (una restauración por consola o un servidor lanzado a mano).
    /// </summary>
    private void KeepAlive(SvcState svc)
    {
        if (svc == SvcState.Running && !_busy)
        {
            // Lo inició alguien (o volvió solo): ya no vale el pedido de detenerlo.
            // Con margen: otro Watchdog (otra sesión) puede verlo todavía en
            // ejecución justo entre que se marcó el pedido y empezó a detenerse.
            if (StopIntent.Read() is { } requested && DateTime.UtcNow - requested > TimeSpan.FromSeconds(30))
                StopIntent.Clear();
            if (_autoStartAttempts > 0)
                _tray.Notify("Servidor en ejecución", "El Watchdog volvió a iniciar el servidor.", System.Windows.Forms.ToolTipIcon.Info);
            _autoStartAttempts = 0;
            _keepAliveNote = null;
        }
        if (svc != SvcState.Stopped || _busy)
        {
            if (svc != SvcState.Stopped)
                _stoppedSinceUtc = null;
            return;
        }

        var now = DateTime.UtcNow;
        _stoppedSinceUtc ??= now;
        if (StopIntent.Read() is not null || now - _stoppedSinceUtc < StopGrace || now < _nextAutoStartUtc)
            return;
        if (WhyNotRestart() is { } reason)
        {
            if (reason != _keepAliveNote)
                AddActivity(reason, Muted);
            _keepAliveNote = reason;
            return;
        }

        _autoStartAttempts++;
        // Si no arranca, se insiste con calma: 30 s, 1 min, 2 min y luego cada 5 min.
        _nextAutoStartUtc = now + TimeSpan.FromSeconds(_autoStartAttempts switch { 1 => 30, 2 => 60, 3 => 120, _ => 300 });
        _ = StartServiceAsync(automatic: true);
    }

    private static string? WhyNotRestart()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.StartType == ServiceStartMode.Disabled)
                return "El servicio está deshabilitado en Windows: el Watchdog no lo inicia.";
        }
        catch
        {
            return null;
        }
        var others = Process.GetProcessesByName("TrueCentralVms.Server");
        foreach (var p in others)
            p.Dispose();
        return others.Length > 0
            ? "Hay otro proceso del servidor en ejecución (¿una restauración?): el Watchdog espera a que termine."
            : null;
    }

    // ------------------------------------------------------------------
    // Acciones
    // ------------------------------------------------------------------
    private async void OnStartClick(object sender, RoutedEventArgs e) => await StartServiceAsync(automatic: false);

    private async void OnStopClick(object sender, RoutedEventArgs e) => await StopServiceAsync();

    private async Task StartServiceAsync(bool automatic)
    {
        if (_busy)
            return;
        _busy = true;
        _stopRequested = false;
        StopIntent.Clear();
        StartButton.IsEnabled = StopButton.IsEnabled = false;
        AddActivity(automatic
            ? $"El servicio está detenido y nadie pidió detenerlo: iniciándolo (intento {_autoStartAttempts})..."
            : "Iniciando el servicio...", automatic ? Warn : Muted);
        try
        {
            await Task.Run(() =>
            {
                using var sc = new ServiceController(ServiceName);
                if (sc.Status == ServiceControllerStatus.Stopped)
                    sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(90));
            });
            AddActivity("Servicio iniciado correctamente.", Ok);
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            AddActivity("El servicio no llegó a En ejecución en 90 s; revise el registro de eventos.", Danger);
            DumpRecentEventLog();
        }
        catch (Exception ex)
        {
            AddActivity($"No se pudo iniciar el servicio: {Root(ex).Message}", Danger);
            DumpRecentEventLog();
            if (automatic)
                _tray.Notify("No se pudo iniciar el servidor", Root(ex).Message, System.Windows.Forms.ToolTipIcon.Error);
        }
        finally
        {
            _busy = false;
            await CheckAsync();
        }
    }

    private async Task StopServiceAsync()
    {
        if (_busy)
            return;
        var answer = MessageBox.Show(
            "El servidor quedará detenido y el Watchdog NO lo volverá a iniciar hasta que usted lo inicie.\n\n" +
            "Mientras tanto no habrá video, alarmas ni control de acceso. ¿Detener el servicio?",
            "Detener el servicio", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;
        _busy = true;
        _stopRequested = true;
        try
        {
            StopIntent.Set();
        }
        catch (Exception ex)
        {
            AddActivity($"No se pudo guardar el pedido de detención: {Root(ex).Message}", Danger);
            _busy = false;
            return;
        }
        StartButton.IsEnabled = StopButton.IsEnabled = false;
        AddActivity("Deteniendo el servicio (el apagado ordenado puede tardar unos segundos)...", Muted);
        try
        {
            await Task.Run(() =>
            {
                using var sc = new ServiceController(ServiceName);
                if (sc.Status != ServiceControllerStatus.Stopped)
                    sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(90));
            });
            AddActivity("Servicio detenido. El Watchdog no lo volverá a iniciar hasta que usted lo inicie.", Ok);
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            AddActivity("El servicio no terminó de detenerse en 90 s.", Danger);
        }
        catch (Exception ex)
        {
            AddActivity($"No se pudo detener el servicio: {Root(ex).Message}", Danger);
        }
        finally
        {
            _busy = false;
            await CheckAsync();
        }
    }

    private void OnOpenPanelClick(object sender, RoutedEventArgs e) => OpenPanel();

    private void OpenPanel()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_env.BaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddActivity($"No se pudo abrir el navegador: {ex.Message}", Danger);
        }
    }

    // ------------------------------------------------------------------
    // Actividad y diagnóstico
    // ------------------------------------------------------------------
    private void AddActivity(string message, Brush color)
    {
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1), FontSize = 12.5 };
        line.Inlines.Add(new Run($"{DateTime.Now:HH:mm:ss}  ") { Foreground = Muted });
        line.Inlines.Add(new Run(message) { Foreground = color });
        ActivityPanel.Children.Insert(0, line);
        while (ActivityPanel.Children.Count > 200)
            ActivityPanel.Children.RemoveAt(ActivityPanel.Children.Count - 1);
    }

    /// <summary>
    /// Vuelca al panel los errores recientes del registro de eventos de Windows
    /// relacionados con el servidor (el servicio escribe ahí cuando corre como
    /// servicio; el runtime de .NET registra los crashes).
    /// </summary>
    private void DumpRecentEventLog()
    {
        try
        {
            using var log = new EventLog("Application");
            var cutoff = DateTime.Now.AddMinutes(-10);
            int shown = 0;
            for (int i = log.Entries.Count - 1; i >= 0 && i > log.Entries.Count - 400 && shown < 5; i--)
            {
                EventLogEntry entry = log.Entries[i];
                if (entry.TimeGenerated < cutoff)
                    break;
                bool relevant =
                    entry.Source.Contains("TrueCentral", StringComparison.OrdinalIgnoreCase) ||
                    (entry.Source is ".NET Runtime" or "Application Error"
                        && entry.Message.Contains("TrueCentral", StringComparison.OrdinalIgnoreCase));
                if (!relevant || entry.EntryType is not (EventLogEntryType.Error or EventLogEntryType.Warning))
                    continue;
                string message = entry.Message.ReplaceLineEndings(" ").Trim();
                if (message.Length > 260)
                    message = message[..260] + "…";
                AddActivity($"[Registro de eventos {entry.TimeGenerated:HH:mm:ss}] {message}",
                    entry.EntryType == EventLogEntryType.Error ? Danger : Warn);
                shown++;
            }
        }
        catch
        {
            // Sin acceso al registro de eventos: no es crítico para el monitoreo.
        }
    }

    private static Exception Root(Exception ex) => ex.InnerException is null ? ex : Root(ex.InnerException);
}
