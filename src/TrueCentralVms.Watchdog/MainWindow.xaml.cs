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
/// </summary>
public partial class MainWindow : Window
{
    private const string ServiceName = "CLRTrueCentralVMS";

    /// <summary>Estado resumido del servicio para pintar la UI.</summary>
    private enum SvcState { NotInstalled, Stopped, Running, Pending, Unknown }

    private readonly ServerEnvironment _env = ServerEnvironment.Discover();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMilliseconds(1800) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    private bool _checking;             // hay un ciclo de verificación en curso
    private bool _busy;                 // hay una operación iniciar/detener en curso
    private bool _stopRequested;        // el último stop fue pedido desde acá (no es una caída)
    private SvcState _lastSvc = SvcState.Unknown;
    private bool? _lastApiOk;

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
        ApiCard.ToolTip = $"API y panel web: {_env.BaseUrl}/api/health";
        DbCard.ToolTip = $"PostgreSQL embebido en 127.0.0.1:{_env.PgPort}";

        if (!_env.ServerFound)
            AddActivity("No se encontró la instalación del servidor junto al Watchdog; se usan los puertos por defecto.", Warn);

        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
        Loaded += async (_, _) => await CheckAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _http.Dispose();
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
                }
            }
            else if (svc == SvcState.NotInstalled && _lastSvc != SvcState.Unknown)
                AddActivity("El servicio no está instalado en este equipo.", Danger);
            _lastSvc = svc;
        }

        if (_lastApiOk is { } last && last != apiOk && svc == SvcState.Running)
            AddActivity(apiOk ? "La API volvió a responder." : "La API dejó de responder.", apiOk ? Ok : Warn);
        _lastApiOk = apiOk;
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
    }

    // ------------------------------------------------------------------
    // Acciones
    // ------------------------------------------------------------------
    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        _busy = true;
        _stopRequested = false;
        StartButton.IsEnabled = StopButton.IsEnabled = false;
        AddActivity("Iniciando el servicio...", Muted);
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
        }
        finally
        {
            _busy = false;
            await CheckAsync();
        }
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        _busy = true;
        _stopRequested = true;
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
            AddActivity("Servicio detenido.", Ok);
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

    private void OnOpenPanelClick(object sender, RoutedEventArgs e)
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
