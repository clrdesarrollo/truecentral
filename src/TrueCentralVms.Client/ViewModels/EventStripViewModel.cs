using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>Una ficha de la cinta de eventos.</summary>
public sealed partial class EventStripItem : ObservableObject
{
    private readonly Func<int, ChannelNode?> _findChannel;

    public EventStripItem(LiveEventDto e, Func<int, ChannelNode?> findChannel)
    {
        _findChannel = findChannel;
        _event = e;
    }

    /// <summary>El evento (un repetido lo reemplaza con el contador subido).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(TimeText), nameof(WhereText), nameof(Level), nameof(RepeatsText),
        nameof(HasCamera), nameof(IsAlert), nameof(ToolTip))]
    private LiveEventDto _event;

    public string Id => Event.Id;
    public string Title => Event.Title;
    public string TimeText => Event.At.ToLocalTime().ToString("HH:mm:ss");
    public string WhereText => string.Join(" · ", new[] { Event.ResourceName, Event.LocationPath }
        .Where(x => !string.IsNullOrWhiteSpace(x)));
    public string RepeatsText => Event.Repeats > 0 ? $"×{Event.Repeats + 1}" : "";
    public bool IsAlert => Event.AlertId is not null;

    /// <summary>Gravedad, con los mismos niveles de color que los demás paneles.</summary>
    public string Level => Event.Severity switch
    {
        AlarmSeverity.Critical => "Alarm",
        AlarmSeverity.Warning => "Warning",
        _ => "Ok",
    };

    /// <summary>La primera cámara del evento que este puesto tiene en su lista (null = ninguna).</summary>
    public ChannelNode? Camera => Event.ChannelIds.Select(_findChannel).FirstOrDefault(c => c is not null);
    public bool HasCamera => Camera is not null;

    public string ToolTip => string.Join(Environment.NewLine, new[]
    {
        $"{Title} · {Event.At.ToLocalTime():dd-MM HH:mm:ss}",
        WhereText,
        Event.Detail ?? "",
        HasCamera ? "Clic: abre la cámara en el cuadro seleccionado · Arrastre: a un cuadro" : "Sin cámaras que usted pueda ver",
        IsAlert ? "Doble clic: la ventana de alarma de este aviso" : "",
    }.Where(x => x.Length > 0));
}

/// <summary>
/// Cinta de eventos del Centro de mando: una franja plegable bajo la grilla con
/// los últimos eventos en vivo (los mismos que reciben los Cuadros vigía), el
/// más reciente a la izquierda. Clic en una ficha abre su cámara en el cuadro
/// seleccionado, se puede arrastrar a un cuadro y, si es un aviso, el doble clic
/// abre su ventana de alarma.
///
/// Filtros: gravedad mínima y «Solo lo de esta grilla» (los eventos de las
/// cámaras y recursos que hay en los cuadros, o que vigilaría alguno de sus vigías).
/// Cada Centro de mando tiene la suya (la grilla principal y los que se abren con «+»).
/// </summary>
public sealed partial class EventStripViewModel : ObservableObject
{
    private const int MaxItems = 60;

    private readonly LiveEventHub _hub;
    private readonly ClientSettings _settings;
    private readonly Func<int, ChannelNode?> _findChannel;
    private readonly Func<IReadOnlyList<VideoCellViewModel>> _cells;
    private readonly Func<long, Task> _openAlert;
    private readonly Func<ChannelNode, Task> _openCamera;
    private readonly DispatcherTimer _refilter;
    /// <summary>«Vaciar»: lo anterior a este momento ya no se muestra.</summary>
    private DateTime _clearedAt = DateTime.MinValue;

    public EventStripViewModel(LiveEventHub hub, ClientSettings settings, Func<int, ChannelNode?> findChannel,
        Func<IReadOnlyList<VideoCellViewModel>> cells, Func<long, Task> openAlert, Func<ChannelNode, Task> openCamera)
    {
        _hub = hub;
        _settings = settings;
        _findChannel = findChannel;
        _cells = cells;
        _openAlert = openAlert;
        _openCamera = openCamera;
        _isOpen = settings.EventStripOpen;
        _onlyBoard = settings.EventStripOnlyBoard;
        _minSeverity = Enum.IsDefined((AlarmSeverity)settings.EventStripMinSeverity)
            ? (AlarmSeverity)settings.EventStripMinSeverity : AlarmSeverity.Warning;
        hub.Arrived += OnArrived;
        // «Solo lo de esta grilla» depende de lo que hay en los cuadros: se rearma cada tanto.
        _refilter = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refilter.Tick += (_, _) => { if (OnlyBoard) Rebuild(); };
        _refilter.Start();
        Rebuild();
    }

    public ObservableCollection<EventStripItem> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    public string EmptyText => OnlyBoard
        ? "Sin eventos de las cámaras y recursos de esta grilla."
        : "Sin eventos todavía. Aquí aparece lo que pasa, el más reciente a la izquierda.";

    [ObservableProperty] private bool _isOpen;

    /// <summary>Eventos que llegaron con la cinta plegada (el número del botón).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnseenText), nameof(HasUnseen))]
    private int _unseen;

    public bool HasUnseen => Unseen > 0 && !IsOpen;
    public string UnseenText => Unseen > 99 ? "99+" : Unseen.ToString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    private bool _onlyBoard;

    [ObservableProperty] private AlarmSeverity _minSeverity;

    /// <summary>Una opción de gravedad; se muestra por su texto (también la elegida).</summary>
    public sealed record SeverityChoice(string Label, AlarmSeverity Value)
    {
        public override string ToString() => Label;
    }

    public IReadOnlyList<SeverityChoice> SeverityOptions { get; } =
    [
        new("Todo", AlarmSeverity.Info),
        new("Advertencia y crítica", AlarmSeverity.Warning),
        new("Solo crítica", AlarmSeverity.Critical),
    ];

    partial void OnIsOpenChanged(bool value)
    {
        if (value) Unseen = 0;
        OnPropertyChanged(nameof(HasUnseen));
        _settings.EventStripOpen = value;
        _settings.Save();
    }

    partial void OnOnlyBoardChanged(bool value)
    {
        _settings.EventStripOnlyBoard = value;
        _settings.Save();
        Rebuild();
    }

    partial void OnMinSeverityChanged(AlarmSeverity value)
    {
        _settings.EventStripMinSeverity = (int)value;
        _settings.Save();
        Rebuild();
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    private void OnArrived(LiveEventDto e, bool repeat)
    {
        if (Items.FirstOrDefault(i => i.Id == e.Id) is { } existing)
        {
            existing.Event = e;
            return;
        }
        if (repeat || !Accepts(e)) return;
        Items.Insert(0, new EventStripItem(e, _findChannel));
        while (Items.Count > MaxItems) Items.RemoveAt(Items.Count - 1);
        if (!IsOpen) Unseen++;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Vuelve a armar la lista con los filtros actuales (el más reciente primero).</summary>
    private void Rebuild()
    {
        var wanted = _hub.Recent.Where(Accepts).OrderByDescending(e => e.At).Take(MaxItems).ToList();
        if (wanted.Select(e => e.Id).SequenceEqual(Items.Select(i => i.Id))) return;
        Items.Clear();
        foreach (var e in wanted) Items.Add(new EventStripItem(e, _findChannel));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool Accepts(LiveEventDto e)
    {
        if (e.Severity < MinSeverity || e.At <= _clearedAt) return false;
        if (!OnlyBoard) return true;
        var cells = _cells();
        if (e.ChannelIds.Any(id => cells.Any(c => c.AssignedChannel?.Channel.Id == id))) return true;
        foreach (var cell in cells)
        {
            if (cell.Panel is WatchCellPanel watch && watch.Options.Accepts(e)) return true;
            if (cell.Panel?.Content is { Kind: DashboardItemKinds.Resource, RefId: { } id, Ref: { } kind } &&
                e.ResourceKey == $"{kind}:{id}") return true;
        }
        return false;
    }

    /// <summary>Clic en una ficha: su cámara en el cuadro seleccionado (o en el primero libre).</summary>
    [RelayCommand]
    private async Task OpenAsync(EventStripItem? item)
    {
        if (item?.Camera is { } camera) await _openCamera(camera);
    }

    /// <summary>Doble clic: la ventana de alarma, si es un aviso; si no, igual que el clic.</summary>
    [RelayCommand]
    private async Task OpenAlertAsync(EventStripItem? item)
    {
        if (item?.Event.AlertId is { } id)
        {
            try { await _openAlert(id); }
            catch (ApiException) { /* el aviso ya no existe o no es visible */ }
        }
        else await OpenAsync(item);
    }

    /// <summary>Vacía la cinta: quedan solo los eventos que lleguen desde ahora.</summary>
    [RelayCommand]
    private void Clear()
    {
        _clearedAt = DateTime.UtcNow;
        Items.Clear();
        OnPropertyChanged(nameof(IsEmpty));
    }
}
