using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>Frente de citofonía en la lista del módulo.</summary>
public sealed partial class IntercomItemViewModel(IntercomDto intercom) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Detail), nameof(IsOnline), nameof(StatusText), nameof(CallText),
        nameof(IsRinging), nameof(HasVideo))]
    private IntercomDto _intercom = intercom;

    public int Id => Intercom.Id;
    public string Name => Intercom.Name;
    public string Detail => $"{Intercom.Model ?? "—"} · {Intercom.Host}" + (Intercom.GroupName is { } g ? $" · {g}" : "");
    public bool IsOnline => Intercom.Status == IntercomStatus.Online;
    public bool HasVideo => Intercom.ChannelId is not null;
    public bool IsRinging => Intercom.ActiveCall?.State == IntercomCallState.Ringing;

    public string StatusText => Intercom.Status switch
    {
        IntercomStatus.Online => "En línea",
        IntercomStatus.Offline => "Sin conexión",
        IntercomStatus.AuthFailed => "Credenciales rechazadas",
        _ => "—",
    };

    public string CallText => Intercom.ActiveCall switch
    {
        { State: IntercomCallState.Ringing } => "Sonando",
        { State: IntercomCallState.InCall } c => $"En conversación · {c.AnsweredBy}",
        _ => Intercom.Enabled ? (Intercom.CallCenterEnabled ? "Libre" : "El botón no llama a la central") : "Pausado",
    };

    /// <summary>Aplica una llamada que empujó el hub sin esperar a releer el frente.</summary>
    public void ApplyCall(IntercomCallDto call)
    {
        bool active = call.State is IntercomCallState.Ringing or IntercomCallState.InCall;
        Intercom = Intercom with { ActiveCall = active ? call : null };
    }
}

/// <summary>Llamada del historial ya formateada para la lista.</summary>
public sealed record IntercomCallRow(IntercomCallDto Call)
{
    public string When => Call.StartedAt.ToLocalTime().ToString("dd-MM-yyyy HH:mm:ss");
    public string IntercomName => Call.IntercomName;

    public string Result => Call.State switch
    {
        IntercomCallState.Ringing => "Sonando",
        IntercomCallState.InCall => "En conversación",
        IntercomCallState.Completed => "Contestada",
        IntercomCallState.Missed => "No contestada",
        IntercomCallState.Rejected => "Rechazada",
        _ => Call.State.ToString(),
    };

    /// <summary>Para el color del distintivo (Missed en rojo, Completed en verde...).</summary>
    public string ResultKey => Call.State.ToString();

    public string AnsweredBy => Call.AnsweredBy ?? "—";

    public string Duration
    {
        get
        {
            if (Call.AnsweredAt is not { } at || Call.EndedAt is not { } end) return "—";
            var span = end - at;
            return span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min {span.Seconds} s" : $"{span.Seconds} s";
        }
    }

    public string Door => Call.DoorOpened ? $"Abierta ({Call.DoorOpenedBy})" : "—";
    public string Detail => string.Join(" · ", new[] { Call.Origin, Call.EndReason }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>
/// Módulo Citofonía del cliente. Vive desde el arranque (no al abrir la
/// viñeta): una llamada tiene que sonar esté donde esté el guardia. Mantiene
/// los frentes y su estado, el historial reciente, y el timbre, que suena
/// mientras haya alguna llamada sonando que este puesto no haya silenciado.
/// Las ventanas de llamada las abre <see cref="MainViewModel"/> a partir de
/// <see cref="CallRinging"/> y <see cref="WindowRequested"/>.
/// </summary>
public sealed partial class IntercomsViewModel : ObservableObject
{
    private const int HistorySize = 100;

    private readonly ApiClient _api;
    private readonly IntercomRinger _ringer = new();
    /// <summary>Llamadas sonando ahora mismo (id → llamada).</summary>
    private readonly Dictionary<long, IntercomCallDto> _ringing = [];
    /// <summary>Llamadas cuyo timbre silenció este puesto (siguen sonando en los demás).</summary>
    private readonly HashSet<long> _silenced = [];
    private bool _loaded;

    public ObservableCollection<IntercomItemViewModel> Intercoms { get; } = [];
    public ObservableCollection<IntercomCallRow> History { get; } = [];

    [ObservableProperty] private IntercomItemViewModel? _selected;
    [ObservableProperty] private bool _hasRingingCall;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isEmpty;

    /// <summary>Empezó a sonar una llamada: hay que mostrarle la ventana al guardia.</summary>
    public event Action<IntercomCallDto>? CallRinging;

    /// <summary>Una llamada cambió de estado (contestada, terminada...): las ventanas abiertas se actualizan.</summary>
    public event Action<IntercomCallDto>? CallChanged;

    /// <summary>El operador pidió ver/hablar con un frente sin que haya llamada.</summary>
    public event Action<IntercomDto>? WindowRequested;

    public IntercomsViewModel(ApiClient api, VmsHubClient hub)
    {
        _api = api;
        hub.IntercomCallChanged += call => Application.Current.Dispatcher.InvokeAsync(() => OnCallChanged(call));
        hub.IntercomStatusChanged += dto => Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var item = Intercoms.FirstOrDefault(i => i.Id == dto.Id);
            if (item is not null) item.Intercom = dto;
        });
        hub.ConfigChanged += entity =>
        {
            if (entity == "intercoms") Application.Current.Dispatcher.InvokeAsync(() => _ = LoadIntercomsAsync());
        };
        hub.ConnectionStateChanged += ok =>
        {
            // Al reconectar puede haber quedado una llamada sonando que no llegó por el hub.
            if (ok && _loaded) Application.Current.Dispatcher.InvokeAsync(() => _ = LoadActiveCallsAsync());
        };
    }

    public async Task InitializeAsync()
    {
        await LoadIntercomsAsync();
        await LoadActiveCallsAsync();
        await LoadHistoryAsync();
        _loaded = true;
    }

    public IntercomDto? Find(int intercomId) => Intercoms.FirstOrDefault(i => i.Id == intercomId)?.Intercom;

    private async Task LoadIntercomsAsync()
    {
        try
        {
            var list = await _api.GetIntercomsAsync();
            int? selected = Selected?.Id;
            Intercoms.Clear();
            foreach (var dto in list) Intercoms.Add(new IntercomItemViewModel(dto));
            Selected = Intercoms.FirstOrDefault(i => i.Id == selected) ?? Intercoms.FirstOrDefault();
            IsEmpty = Intercoms.Count == 0;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            // Sin módulo licenciado o sin servidor: la lista queda vacía, nada suena.
            StatusMessage = ex.Message;
        }
    }

    private async Task LoadActiveCallsAsync()
    {
        try
        {
            foreach (var call in await _api.GetActiveIntercomCallsAsync()) OnCallChanged(call);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException) { /* se reintenta al reconectar */ }
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        try
        {
            var page = await _api.GetIntercomCallsAsync(null, 0, HistorySize);
            History.Clear();
            foreach (var call in page.Items) History.Add(new IntercomCallRow(call));
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            StatusMessage = ex.Message;
        }
    }

    private void OnCallChanged(IntercomCallDto call)
    {
        Intercoms.FirstOrDefault(i => i.Id == call.IntercomId)?.ApplyCall(call);

        bool isNew = !_ringing.ContainsKey(call.Id);
        if (call.State == IntercomCallState.Ringing)
        {
            _ringing[call.Id] = call;
            if (isNew)
            {
                StatusMessage = $"Llamada de {call.IntercomName}";
                CallRinging?.Invoke(call);
            }
        }
        else
        {
            _ringing.Remove(call.Id);
            _silenced.Remove(call.Id);
        }
        UpdateRinger();
        CallChanged?.Invoke(call);

        // Historial: se reemplaza la fila de esa llamada o se agrega arriba.
        var row = History.FirstOrDefault(r => r.Call.Id == call.Id);
        if (row is not null) History[History.IndexOf(row)] = new IntercomCallRow(call);
        else
        {
            History.Insert(0, new IntercomCallRow(call));
            while (History.Count > HistorySize) History.RemoveAt(History.Count - 1);
        }
    }

    /// <summary>Calla el timbre de una llamada en este puesto (sigue sonando en los demás).</summary>
    public void SilenceRinger(long callId)
    {
        _silenced.Add(callId);
        UpdateRinger();
    }

    private void UpdateRinger()
    {
        HasRingingCall = _ringing.Count > 0;
        if (_ringing.Keys.Any(id => !_silenced.Contains(id))) _ringer.Start();
        else _ringer.Stop();
    }

    [RelayCommand]
    private void OpenWindow(IntercomItemViewModel? item)
    {
        item ??= Selected;
        if (item is not null) WindowRequested?.Invoke(item.Intercom);
    }

    [RelayCommand]
    private async Task OpenDoorAsync(IntercomItemViewModel? item)
    {
        item ??= Selected;
        if (item is null) return;
        var answer = MessageBox.Show(Application.Current.MainWindow,
            $"¿Abrir la puerta de «{item.Name}»?", "Citofonía", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            var result = await _api.OpenIntercomDoorAsync(item.Id, 1);
            StatusMessage = result.Message;
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.Message;
            MessageBox.Show(Application.Current.MainWindow, ex.Message, "Citofonía", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void StopRinger() => _ringer.Stop();
}
