using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.AlarmScreen.ViewModels;

/// <summary>Un aviso en pantalla. Los colores son los de siempre de la caseta.</summary>
public partial class AlarmCardViewModel(AlarmScreenAlertDto dto) : ObservableObject
{
    [ObservableProperty] private AlarmScreenAlertDto _dto = dto;

    public long Id => Dto.Id;
    public string Title => Dto.Title;

    /// <summary>Dónde pasó: la ubicación del recurso (si tiene ficha) o el mensaje del aviso.</summary>
    public string SourceName => !string.IsNullOrWhiteSpace(Dto.LocationPath) ? Dto.LocationPath
        : !string.IsNullOrWhiteSpace(Dto.Message) ? Dto.Message
        : "Origen desconocido";

    /// <summary>El mensaje del aviso, cuando no es lo mismo que ya dice <see cref="SourceName"/>.</summary>
    public string Message => Dto.Message;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Dto.Message) && Dto.Message != SourceName;

    /// <summary>Consignas de la ficha del recurso: lo que tiene que hacer el guardia.</summary>
    public string Instructions => Dto.Instructions ?? "";
    public bool HasInstructions => !string.IsNullOrWhiteSpace(Dto.Instructions);

    public string TimeText => Dto.OccurredAt.ToLocalTime().ToString("HH:mm:ss");
    public string DateText => Dto.OccurredAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
    public string OriginText => string.IsNullOrWhiteSpace(Dto.WorkflowName) ? "" : $"Automatización: {Dto.WorkflowName}";

    public string PriorityText => Dto.Priority switch
    {
        1 => "CRÍTICA",
        2 => "ADVERTENCIA",
        3 => "INFORMATIVA",
        _ => "AVISO"
    };

    public IBrush PriorityBrush => Dto.Priority switch
    {
        1 => new SolidColorBrush(Color.Parse("#D32F2F")),
        2 => new SolidColorBrush(Color.Parse("#F57C00")),
        3 => new SolidColorBrush(Color.Parse("#FBC02D")),
        _ => new SolidColorBrush(Color.Parse("#607D8B"))
    };

    public bool Acknowledged => Dto.Acknowledged;

    /// <summary>Lo que el guardia todavía tiene que atender.</summary>
    public bool IsPending => !Dto.Acknowledged;

    /// <summary>Quién lo atendió: otra pantalla («vista en…») o un operador en TrueCentral.</summary>
    public string AckText => !Dto.Acknowledged
        ? "Sin marcar"
        : Dto.AcknowledgedFrom == "screen"
            ? $"Vista en {Dto.AcknowledgedBy} a las {Dto.AcknowledgedAt?.ToLocalTime():HH:mm:ss}"
            : $"Confirmada en TrueCentral por {Dto.AcknowledgedBy} a las {Dto.AcknowledgedAt?.ToLocalTime():HH:mm:ss}";

    // --- Cómo se ve la tarjeta en la lista ---
    // Lo pendiente resalta y lo atendido se apaga. Con veinte avisos en pantalla, el guardia
    // tiene que ver de un vistazo cuáles le faltan, sin leer uno por uno.

    public IBrush CardBackground => Dto.Acknowledged
        ? new SolidColorBrush(Color.Parse("#151C24"))
        : new SolidColorBrush(Color.Parse("#22303C"));

    public double CardOpacity => Dto.Acknowledged ? 0.55 : 1.0;

    public IBrush TitleBrush => new SolidColorBrush(Color.Parse(Dto.Acknowledged ? "#90A4AE" : "#FFFFFF"));

    /// <summary>Barra de prioridad: más ancha en lo pendiente para que salte a la vista.</summary>
    public double PriorityBarWidth => Dto.Acknowledged ? 4 : 8;

    /// <summary>Ruta relativa de la foto en el servidor, si la tiene.</summary>
    public string? PhotoUrl => Dto.PhotoUrl;

    [ObservableProperty] private Bitmap? _photo;

    /// <summary>Evita volver a pedir una foto que ya se intentó y no estaba.</summary>
    public bool PhotoRequested { get; set; }

    /// <summary>Aplica una versión más nueva del aviso (confirmación, foto).</summary>
    public void Apply(AlarmScreenAlertDto updated)
    {
        var hadPhoto = Dto.PhotoUrl;
        Dto = updated;
        if (hadPhoto != updated.PhotoUrl) PhotoRequested = false;
        NotifyAll();
    }

    public void MarkAcknowledged(string? by, string? from, DateTimeOffset at)
    {
        if (Dto.Acknowledged) return;
        Dto = Dto with
        {
            Acknowledged = true,
            AcknowledgedAt = at,
            AcknowledgedBy = by,
            AcknowledgedFrom = from
        };
        NotifyAll();
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SourceName));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(HasMessage));
        OnPropertyChanged(nameof(Instructions));
        OnPropertyChanged(nameof(HasInstructions));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(DateText));
        OnPropertyChanged(nameof(PriorityText));
        OnPropertyChanged(nameof(PriorityBrush));
        OnPropertyChanged(nameof(Acknowledged));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(AckText));
        OnPropertyChanged(nameof(PhotoUrl));
        OnPropertyChanged(nameof(OriginText));
        OnPropertyChanged(nameof(CardBackground));
        OnPropertyChanged(nameof(CardOpacity));
        OnPropertyChanged(nameof(TitleBrush));
        OnPropertyChanged(nameof(PriorityBarWidth));
    }
}
