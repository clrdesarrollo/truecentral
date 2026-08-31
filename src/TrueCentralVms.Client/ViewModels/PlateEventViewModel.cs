using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Un reconocimiento de patente en la pantalla: el DTO del servidor más las
/// imágenes (que se piden aparte y solo cuando se necesitan) y los textos ya
/// formateados para la ficha de detalle.
///
/// La miniatura se carga en cuanto el elemento entra a la lista; la escena
/// completa, recién al seleccionarlo (pesa cientos de kB y no se justifica
/// bajarla para las 300 lecturas de la lista).
/// </summary>
public sealed partial class PlateEventViewModel(ApiClient api, PlateEventDto dto) : ObservableObject
{
    /// <summary>Tope de descargas simultáneas de miniaturas (no ahogar la API con una ráfaga).</summary>
    private static readonly SemaphoreSlim ThumbnailThrottle = new(4, 4);

    public PlateEventDto Event { get; } = dto;

    public long Id => Event.Id;
    public string PlateNumber => Event.PlateNumber;

    /// <summary>Miniatura de la lista: el primer plano de la placa, o la escena si no vino.</summary>
    [ObservableProperty] private ImageSource? _thumbnail;

    /// <summary>Escena completa de la ficha de detalle.</summary>
    [ObservableProperty] private ImageSource? _sceneImage;

    /// <summary>Primer plano de la placa en tamaño grande (ficha de detalle).</summary>
    [ObservableProperty] private ImageSource? _plateImage;

    [ObservableProperty] private bool _isLoadingDetail;

    // ---------- Textos de la lista ----------

    /// <summary>Hora del equipo con los milisegundos: en un flujo vehicular dos
    /// lecturas del mismo segundo son habituales.</summary>
    public string TimeText => Event.CapturedAt.ToString("HH:mm:ss.fff");

    public string DateText => Event.CapturedAt.ToString("dd-MM-yyyy");

    public string SourceText => Event.ChannelName.Length > 0 && Event.ChannelName != Event.DeviceName
        ? $"{Event.DeviceName} · {Event.ChannelName}"
        : Event.DeviceName;

    public string ConfidenceText => Event.Confidence < 0 ? "—" : $"{Event.Confidence}%";

    /// <summary>Resumen del vehículo para la fila de la lista ("Automóvil gris").</summary>
    public string VehicleSummary
    {
        get
        {
            var parts = new List<string>();
            if (Event.VehicleType is { Length: > 0 }) parts.Add(Event.VehicleType);
            if (Event.VehicleColor is { Length: > 0 }) parts.Add(Event.VehicleColor.ToLowerInvariant());
            if (Event.SpeedKmh is { } speed) parts.Add($"{speed} km/h");
            return parts.Count == 0 ? "" : string.Join(" · ", parts);
        }
    }

    // ---------- Ficha de detalle: pares etiqueta/valor ----------

    /// <summary>
    /// Todo lo que informó la cámara, en el orden en que le sirve al operador.
    /// Solo se listan los campos que el equipo realmente entregó: una ficha
    /// llena de "—" esconde lo que sí hay.
    /// </summary>
    public IReadOnlyList<PlateDetailRow> Details
    {
        get
        {
            var rows = new List<PlateDetailRow>
            {
                new("Fecha y hora del equipo", Event.CapturedAt.ToString("dddd dd-MM-yyyy HH:mm:ss.fff")),
                new("Equipo", Event.DeviceName),
                new("Canal", $"{Event.ChannelName} (n.º {Event.ChannelNumber})"),
                new("Confianza de lectura", ConfidenceText),
            };
            Add(rows, "Confianza por carácter", CharConfidenceText);
            Add(rows, "Tipo de vehículo", Event.VehicleType);
            Add(rows, "Color del vehículo", Event.VehicleColor);
            Add(rows, "Marca reconocida", Event.VehicleBrand);
            Add(rows, "Observado en el vehículo", Event.VehicleAttributes);
            Add(rows, "Velocidad", Event.SpeedKmh is { } s ? $"{s} km/h" : null);
            Add(rows, "Largo del vehículo", Event.VehicleLengthCm is { } cm ? $"{cm / 100.0:0.##} m" : null);
            Add(rows, "Carril", Event.Lane?.ToString());
            Add(rows, "Sentido", Event.Direction);
            Add(rows, "Disparo de la captura", Event.DetectionMethod);
            Add(rows, "Infracción", Event.Violation);
            Add(rows, "Color de la placa", Event.PlateColor);
            Add(rows, "Tipo de placa", Event.PlateType);
            rows.Add(new PlateDetailRow("Recibido por el servidor", Event.ReceivedAt.ToLocalTime().ToString("HH:mm:ss")));
            return rows;

            static void Add(List<PlateDetailRow> target, string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    target.Add(new PlateDetailRow(label, value));
            }
        }
    }

    /// <summary>Confianza por carácter alineada con la patente ("B 92 · B 88 · …").</summary>
    private string? CharConfidenceText
    {
        get
        {
            if (Event.CharConfidences.Count == 0) return null;
            var parts = new List<string>(Event.CharConfidences.Count);
            for (int i = 0; i < Event.CharConfidences.Count; i++)
            {
                char letter = i < Event.PlateNumber.Length ? Event.PlateNumber[i] : '?';
                parts.Add($"{letter} {Event.CharConfidences[i]}%");
            }
            return string.Join("  ·  ", parts);
        }
    }

    // ---------- Recuadro de la placa sobre la escena ----------

    public bool HasPlateBox => Event.PlateWidth > 0 && Event.PlateHeight > 0;

    /// <summary>Recuadro normalizado 0..1 que la cámara marcó sobre la escena.</summary>
    public (double X, double Y, double Width, double Height) PlateBox =>
        (Event.PlateX, Event.PlateY, Event.PlateWidth, Event.PlateHeight);

    // ---------- Carga de imágenes ----------

    public async Task LoadThumbnailAsync(CancellationToken ct = default)
    {
        if (Thumbnail is not null) return;
        string kind = Event.HasPlateImage ? "plate" : "scene";
        if (!Event.HasPlateImage && !Event.HasSceneImage) return;

        await ThumbnailThrottle.WaitAsync(ct);
        try
        {
            var bytes = await api.GetPlateImageAsync(Event.Id, kind, ct);
            if (Decode(bytes) is { } image)
                Thumbnail = image;
        }
        finally
        {
            ThumbnailThrottle.Release();
        }
    }

    /// <summary>Imágenes grandes de la ficha (una sola vez por elemento).</summary>
    public async Task LoadDetailAsync(CancellationToken ct = default)
    {
        if (SceneImage is not null || PlateImage is not null) return;
        IsLoadingDetail = true;
        try
        {
            if (Event.HasSceneImage)
                SceneImage = Decode(await api.GetPlateImageAsync(Event.Id, "scene", ct));
            PlateImage = Event.HasPlateImage
                ? Decode(await api.GetPlateImageAsync(Event.Id, "plate", ct))
                : Thumbnail;
        }
        finally
        {
            IsLoadingDetail = false;
        }
    }

    /// <summary>
    /// JPEG → imagen congelada: <c>Freeze</c> la hace compartible entre hilos
    /// y evita que WPF mantenga vivo el stream de cada lectura.
    /// </summary>
    private static ImageSource? Decode(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null; // JPEG corrupto: la ficha se muestra sin foto
        }
    }
}

/// <summary>Fila etiqueta/valor de la ficha de detalle.</summary>
public sealed record PlateDetailRow(string Label, string Value);
