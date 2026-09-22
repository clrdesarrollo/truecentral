using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Client.ViewModels;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Ventana de citofonía, una por frente. Tres modos según la llamada:
/// <list type="bullet">
/// <item><b>Sonando</b>: video del visitante, Contestar / Rechazar / silenciar
/// el timbre en este puesto, y ya se puede abrir la puerta.</item>
/// <item><b>En conversación</b> (la contestó este operador): voz en ambos
/// sentidos, micrófono silenciable, volumen, puerta y Colgar. Si la contestó
/// otro puesto, la ventana lo informa y se cierra sola.</item>
/// <item><b>Sin llamada</b> (abierta a mano desde el módulo): ver la cámara y
/// hablarle al visitante que está en la puerta.</item>
/// </list>
/// Cerrar la ventana en conversación cuelga; mientras suena, solo la cierra en
/// este puesto (la llamada sigue sonando en los demás).
/// </summary>
public partial class IntercomCallWindow : Window
{
    private static readonly Dictionary<int, IntercomCallWindow> Open = [];

    private readonly ApiClient _api;
    private readonly ClientSettings _settings;
    private readonly IntercomsViewModel _module;
    private readonly Func<int, ChannelNode?> _findChannel;
    private readonly IntercomVoiceClient _voice;
    private readonly VideoCellViewModel _cell;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private DispatcherTimer? _autoClose;
    private IntercomDto _intercom;
    private IntercomCallDto? _call;
    private bool _busy;
    private bool _closing;
    /// <summary>Este operador está colgando: el fin de la llamada que llegue ahora sí cierra la ventana.</summary>
    private bool _hangingUp;

    public sealed record DoorItem(int Number, string Label);

    private IntercomCallWindow(ApiClient api, ClientSettings settings, IntercomsViewModel module,
        Func<int, ChannelNode?> findChannel, IntercomDto intercom)
    {
        _api = api;
        _settings = settings;
        _module = module;
        _findChannel = findChannel;
        _intercom = intercom;
        _voice = new IntercomVoiceClient(api);
        _cell = new VideoCellViewModel(api, settings);
        InitializeComponent();
        VideoHost.DataContext = _cell;

        StateChanged += (_, _) => MaxGlyph.Text = WindowState == WindowState.Maximized ? "" : "";
        _clock.Tick += (_, _) => RenderTimer();
        _clock.Start();

        _voice.StatusChanged += text => Dispatcher.InvokeAsync(() => StatusText.Text = text);
        _voice.MicLevelChanged += level => Dispatcher.InvokeAsync(() => MicMeter.Value = level);
        _voice.RemoteLevelChanged += level => Dispatcher.InvokeAsync(() => RemoteMeter.Value = level);
        _voice.Ended += () => Dispatcher.InvokeAsync(() =>
        {
            // La voz se cerró con la llamada ya terminada: ahora sí la ventana puede irse.
            if (_call is { State: IntercomCallState.Completed or IntercomCallState.Missed or IntercomCallState.Rejected })
                ScheduleAutoClose(4);
            Render();
        });
        _voice.Detached += reason => Dispatcher.InvokeAsync(() =>
        {
            // El frente colgó (abrió la puerta o cumplió su tope) pero se sigue hablando.
            _autoClose?.Stop();
            _call = null;
            StatusText.Text = $"El citófono terminó la llamada ({reason}). La conversación sigue abierta: cuelgue cuando termine.";
            Render();
        });
        _module.CallChanged += OnCallChanged;

        Loaded += async (_, _) => await OpenVideoAsync();
        Closed += async (_, _) =>
        {
            Open.Remove(_intercom.Id);
            _module.CallChanged -= OnCallChanged;
            _clock.Stop();
            _autoClose?.Stop();
            _cell.Dispose();
            await _voice.StopAsync();
        };
    }

    /// <summary>Abre (o trae al frente) la ventana del frente, opcionalmente con la llamada que está sonando.</summary>
    public static void Show(Window? owner, ApiClient api, ClientSettings settings, IntercomsViewModel module,
        Func<int, ChannelNode?> findChannel, IntercomDto intercom, IntercomCallDto? call)
    {
        if (!Open.TryGetValue(intercom.Id, out var window))
        {
            window = new IntercomCallWindow(api, settings, module, findChannel, intercom);
            if (owner is { IsLoaded: true }) window.Owner = owner;
            Open[intercom.Id] = window;
            window.Show();
        }
        if (call is not null) window.ApplyCall(call);
        window.Render();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private bool IsMine => _call is { State: IntercomCallState.InCall } c
                           && string.Equals(c.AnsweredBy, _api.Username, StringComparison.OrdinalIgnoreCase);

    private void OnCallChanged(IntercomCallDto call)
    {
        if (call.IntercomId != _intercom.Id) return;
        Dispatcher.InvokeAsync(() =>
        {
            ApplyCall(call);
            Render();
        });
    }

    private void ApplyCall(IntercomCallDto call)
    {
        // Solo la llamada en curso o una más nueva: un aviso atrasado no pisa el estado.
        if (_call is not null && call.Id < _call.Id) return;
        _call = call;
        _autoClose?.Stop();
        bool ended = call.State is IntercomCallState.Completed or IntercomCallState.Missed or IntercomCallState.Rejected;
        bool takenByOther = call.State == IntercomCallState.InCall && !IsMine;
        // Con la voz abierta no se decide aquí: si el frente colgó solo, el
        // servidor avisa "detached" y la conversación sigue; si no, cierra la
        // voz y Ended programa el cierre de la ventana.
        if (ended && _voice.IsActive && !_hangingUp) return;
        if (ended || takenByOther) ScheduleAutoClose(ended ? 4 : 3);
    }

    /// <summary>Llamada terminada o tomada por otro puesto: se informa y la ventana se cierra sola.</summary>
    private void ScheduleAutoClose(int seconds)
    {
        _autoClose?.Stop();
        _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _autoClose.Tick += (_, _) =>
        {
            _autoClose.Stop();
            if (!_voice.IsActive) Close();
        };
        _autoClose.Start();
    }

    // ------------------------------------------------------------------
    // Presentación
    // ------------------------------------------------------------------

    private void Render()
    {
        if (_module.Find(_intercom.Id) is { } fresh) _intercom = fresh;
        IntercomName.Text = _intercom.Name;
        Title = $"Citofonía — {_intercom.Name}";
        DoorButtons.ItemsSource = Enumerable.Range(1, Math.Max(1, _intercom.DoorCount))
            .Select(n => new DoorItem(n, _intercom.DoorCount > 1 ? $"Abrir puerta {n}" : "Abrir puerta")).ToList();

        var call = _call;
        string detail = call?.Origin is { Length: > 0 } origin ? origin : $"{_intercom.Model ?? "Frente"} · {_intercom.Host}";
        string state;
        Color color;
        RingingPanel.Visibility = Visibility.Collapsed;
        TalkPanel.Visibility = Visibility.Collapsed;
        MonitorPanel.Visibility = Visibility.Collapsed;

        switch (call?.State)
        {
            case IntercomCallState.Ringing:
                state = "LLAMADA ENTRANTE";
                color = Color.FromRgb(0xFB, 0xBF, 0x24);
                RingingPanel.Visibility = Visibility.Visible;
                break;
            case IntercomCallState.InCall when IsMine:
                state = "EN CONVERSACIÓN";
                color = Color.FromRgb(0x22, 0xC5, 0x5E);
                TalkPanel.Visibility = Visibility.Visible;
                HangUpText.Text = "Colgar";
                break;
            case IntercomCallState.InCall:
                state = "ATENDIDA EN OTRO PUESTO";
                color = Color.FromRgb(0x8B, 0x98, 0xA5);
                detail = $"La contestó {call.AnsweredBy}.";
                break;
            case IntercomCallState.Completed:
                state = "LLAMADA TERMINADA";
                color = Color.FromRgb(0x8B, 0x98, 0xA5);
                detail = call.EndReason ?? detail;
                break;
            case IntercomCallState.Missed:
                state = "NO CONTESTADA";
                color = Color.FromRgb(0xEF, 0x44, 0x44);
                detail = call.EndReason ?? detail;
                break;
            case IntercomCallState.Rejected:
                state = "RECHAZADA";
                color = Color.FromRgb(0x8B, 0x98, 0xA5);
                detail = call.EndReason ?? detail;
                break;
            default:
                // Sin llamada: ver y (si se quiere) hablar.
                if (_voice.IsActive)
                {
                    state = "HABLANDO";
                    color = Color.FromRgb(0x22, 0xC5, 0x5E);
                    TalkPanel.Visibility = Visibility.Visible;
                    HangUpText.Text = "Dejar de hablar";
                }
                else
                {
                    state = _intercom.Status == IntercomStatus.Online ? "EN VIVO" : "SIN CONEXIÓN";
                    color = _intercom.Status == IntercomStatus.Online ? Color.FromRgb(0x3B, 0x82, 0xF6) : Color.FromRgb(0xEF, 0x44, 0x44);
                    MonitorPanel.Visibility = Visibility.Visible;
                }
                break;
        }
        StateText.Text = state;
        StateText.Foreground = new SolidColorBrush(color);
        StateBadge.Background = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B));
        Frame.BorderBrush = call?.State == IntercomCallState.Ringing
            ? new SolidColorBrush(color)
            : (Brush)FindResource("BorderBrush");
        DetailText.Text = detail;
        RenderTimer();
    }

    private void RenderTimer()
    {
        var call = _call;
        DateTime? from = call?.State switch
        {
            IntercomCallState.Ringing => call.StartedAt,
            IntercomCallState.InCall => call.AnsweredAt ?? call.StartedAt,
            _ => null,
        };
        TimerText.Visibility = from is null ? Visibility.Collapsed : Visibility.Visible;
        if (from is { } start)
        {
            var span = DateTime.UtcNow - start.ToUniversalTime();
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            TimerText.Text = $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
        }
    }

    private async Task OpenVideoAsync()
    {
        if (_intercom.ChannelId is not int channelId || _findChannel(channelId) is not { } node)
        {
            NoVideoText.Visibility = Visibility.Visible;
            if (_intercom.ChannelId is not null)
                NoVideoText.Text = "La cámara del frente no está disponible en este cliente (canal deshabilitado o sin permiso).";
            return;
        }
        try
        {
            // Perfil principal: es la cara del visitante, se necesita detalle.
            await _cell.OpenAsync(node, StreamProfile.Main);
        }
        catch (Exception)
        {
            // El cuadro muestra su propio estado de error.
        }
    }

    // ------------------------------------------------------------------
    // Acciones
    // ------------------------------------------------------------------

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        try { await action(); }
        catch (ApiException ex) { StatusText.Text = ex.Message; }
        catch (InvalidOperationException ex) { StatusText.Text = ex.Message; }
        finally { _busy = false; }
    }

    private async void OnAnswerClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_call is not { State: IntercomCallState.Ringing } call) return;
        StatusText.Text = "Contestando…";
        _module.SilenceRinger(call.Id);
        var result = await _api.AnswerIntercomCallAsync(call.Id);
        if (result.Call is { } updated) ApplyCall(updated);
        Render();
        await StartVoiceAsync();
    });

    private async void OnRejectClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_call is not { State: IntercomCallState.Ringing } call) return;
        _module.SilenceRinger(call.Id);
        var result = await _api.RejectIntercomCallAsync(call.Id);
        StatusText.Text = result.Message;
        if (result.Call is { } updated) ApplyCall(updated);
        Render();
    });

    private void OnSilenceClick(object sender, RoutedEventArgs e)
    {
        if (_call is { } call) _module.SilenceRinger(call.Id);
        StatusText.Text = "Timbre silenciado en este puesto; la llamada sigue sonando en los demás.";
    }

    private async void OnHangUpClick(object sender, RoutedEventArgs e) => await RunAsync(HangUpAsync);

    private async Task HangUpAsync()
    {
        _hangingUp = true;
        try
        {
            if (_call is { State: IntercomCallState.InCall } call && IsMine)
            {
                try
                {
                    var result = await _api.HangUpIntercomCallAsync(call.Id);
                    if (result.Call is { } updated) ApplyCall(updated);
                }
                catch (ApiException ex) { StatusText.Text = ex.Message; }
            }
            await _voice.StopAsync();
            Render();
        }
        finally { _hangingUp = false; }
    }

    private async void OnTalkClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        _call = null;
        await StartVoiceAsync();
        Render();
    });

    private async Task StartVoiceAsync()
    {
        MuteToggle.IsChecked = false;
        _voice.Muted = false;
        _voice.Volume = (float)(VolumeSlider.Value / 100.0);
        await _voice.StartAsync(_intercom.Id, _settings.MicrophoneDevice);
    }

    private void OnMuteChanged(object sender, RoutedEventArgs e)
    {
        bool muted = MuteToggle.IsChecked == true;
        _voice.Muted = muted;
        MuteGlyph.Text = muted ? "" : "";
        MuteText.Text = muted ? "Micrófono silenciado" : "Silenciar mi micrófono";
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_voice is not null) _voice.Volume = (float)(e.NewValue / 100.0);
    }

    private async void OnOpenDoorClick(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        int door = sender is FrameworkElement { Tag: int n } ? n : 1;
        StatusText.Text = "Abriendo…";
        var result = await _api.OpenIntercomDoorAsync(_intercom.Id, door);
        StatusText.Text = result.Message;
    });

    // ------------------------------------------------------------------
    // Ventana
    // ------------------------------------------------------------------

    private async void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        if (_call is { State: IntercomCallState.Ringing } ringing) _module.SilenceRinger(ringing.Id);
        if (_voice.IsActive) await HangUpAsync();
        Close();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
