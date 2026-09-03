using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Centro de eventos: el historial de las alertas que generaron las
/// automatizaciones, con su acuse de recibo. Responde de un vistazo las dos
/// preguntas que importan después de un incidente: qué avisó el sistema y
/// quién se dio por enterado (o si nadie lo hizo).
///
/// Se mantiene solo: cada alerta nueva y cada confirmación llegan por el hub.
/// </summary>
public sealed partial class EventCenterViewModel : ObservableObject
{
    private readonly ApiClient _api;

    public EventCenterViewModel(ApiClient api, VmsHubClient hub)
    {
        _api = api;
        hub.WorkflowNotification += _ => Refresh();
        hub.WorkflowAlertAcknowledged += OnAcknowledged;
    }

    /// <summary>Alertas listadas (de la más reciente a la más antigua).</summary>
    public ObservableCollection<AlertItem> Alerts { get; } = [];

    /// <summary>Pide abrir la ventana de alarma de una alerta (lo atiende MainViewModel).</summary>
    public event Action<WorkflowAlertDto>? OpenRequested;

    /// <summary>Pide sacar el módulo a una ventana independiente (lo atiende MainViewModel).</summary>
    public event Action? PopOutRequested;

    /// <summary>true = el módulo está en su ventana aparte (el botón se esconde).</summary>
    [ObservableProperty] private bool _isPoppedOut;

    [RelayCommand]
    private void PopOut() => PopOutRequested?.Invoke();

    [ObservableProperty] private bool _onlyPending;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _error;

    partial void OnOnlyPendingChanged(bool value) => Refresh();

    private void Refresh() => Application.Current?.Dispatcher.InvokeAsync(() => _ = LoadAsync());

    /// <summary>Trae el historial desde el servidor (últimas 200 alertas).</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        Error = null;
        try
        {
            var list = await _api.GetAlertsAsync(pendingOnly: OnlyPending, take: 200);
            Alerts.Clear();
            foreach (var alert in list?.Items ?? [])
                Alerts.Add(new AlertItem(alert));
            Summary = list is null
                ? ""
                : $"{list.Total} alerta(s)" + (list.Pending > 0 ? $"  ·  {list.Pending} sin confirmar" : "  ·  todas confirmadas");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Alguien confirmó una alerta (aquí o en otro puesto): se actualiza la fila.</summary>
    private void OnAcknowledged(WorkflowAlertDto alert) =>
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var item = Alerts.FirstOrDefault(a => a.Dto.Id == alert.Id);
            if (item is null) return;
            if (OnlyPending) Alerts.Remove(item);
            else item.Dto = alert;
        });

    [RelayCommand]
    private void Open(AlertItem? item)
    {
        if (item is not null) OpenRequested?.Invoke(item.Dto);
    }

    /// <summary>Darse por enterado desde la lista, sin abrir la ventana.</summary>
    [RelayCommand]
    private async Task AcknowledgeAsync(AlertItem? item)
    {
        if (item is null || !item.Dto.Pending) return;
        try
        {
            if (await _api.AcknowledgeAlertAsync(item.Dto.Id) is { } updated)
            {
                if (OnlyPending) Alerts.Remove(item);
                else item.Dto = updated;
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}

/// <summary>Fila del centro de eventos: el DTO más lo que necesita la vista.</summary>
public sealed partial class AlertItem(WorkflowAlertDto dto) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Message), nameof(WorkflowName), nameof(TriggerSummary))]
    [NotifyPropertyChangedFor(nameof(RaisedText), nameof(StateText), nameof(StateLevel), nameof(AckText))]
    [NotifyPropertyChangedFor(nameof(Glyph), nameof(HasPhoto), nameof(CanAcknowledge))]
    private WorkflowAlertDto _dto = dto;

    public string Title => Dto.Title;
    public string Message => Dto.Message;
    public string WorkflowName => Dto.WorkflowName;
    public string TriggerSummary => Dto.TriggerSummary;
    public string RaisedText => Dto.RaisedAt.ToLocalTime().ToString("dd-MM-yyyy HH:mm:ss");
    public bool HasPhoto => Dto.ImagePaths.Count > 0 || Dto.ImagePath is { Length: > 0 };
    public bool CanAcknowledge => Dto.Pending;

    /// <summary>Glifo por severidad (mismo criterio que la ventana de alarma).</summary>
    public string Glyph => Dto.Severity == AlarmSeverity.Info ? "\uE783" : "\uE814";

    public string StateText => Dto.AcknowledgedAt is not null ? "Confirmada"
        : Dto.RequiresAck ? "PENDIENTE"
        : "Informativa";

    /// <summary>"on" | "off" | "muted": lo usa el estilo de la etiqueta.</summary>
    public string StateLevel => Dto.AcknowledgedAt is not null ? "on"
        : Dto.RequiresAck ? "off"
        : "muted";

    public string AckText => Dto.AcknowledgedAt is { } at
        ? $"{Dto.AcknowledgedBy} · {at.ToLocalTime():dd-MM-yyyy HH:mm:ss} · a los {Dto.ResponseSeconds} s · " +
          (Dto.AcknowledgedFrom == "client" ? "cliente" : "panel web")
        : Dto.RequiresAck ? "Nadie se ha dado por enterado" : "No requiere confirmación";
}
