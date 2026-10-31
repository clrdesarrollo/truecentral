using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Axis;

/// <summary>
/// Patentes de AXIS License Plate Verifier (ACAP "ALPV") por el WebSocket de
/// eventos, tema <c>tnsaxis:CameraApplicationPlatform/ALPV.AllPlates</c>.
/// La aplicación manda varias veces cada vehículo (carState new → update →
/// lost): se entrega la primera lectura con texto de cada <c>carID</c>, que es
/// la que sirve para abrir una barrera a tiempo. El recorte de la placa viene
/// en el evento (<c>lpImage</c>, Base64, ALPV 2.9.19+); la escena se toma
/// del canal en ese momento. Campos según la API de VAPIX de la aplicación;
/// las mayúsculas varían entre versiones, por eso se leen sin distinguirlas.
/// </summary>
internal sealed class AxisPlateStream : IPlateSubscription
{
    internal const string Topic = "tnsaxis:CameraApplicationPlatform/ALPV.AllPlates";

    private readonly ClientWebSocket _socket;
    private readonly DeviceConnectionInfo _info;
    private readonly Action<PlateRecognition> _onPlate;
    private readonly TimeSpan _offset;
    private readonly CancellationTokenSource _cts = new();
    /// <summary>Vehículos ya entregados (carID → cuándo), para no repetirlos en sus actualizaciones.</summary>
    private readonly Dictionary<string, DateTime> _seen = new();
    private Task? _loop;
    private volatile bool _alive = true;

    private AxisPlateStream(ClientWebSocket socket, DeviceConnectionInfo info, Action<PlateRecognition> onPlate, TimeSpan offset)
    {
        _socket = socket;
        _info = info;
        _onPlate = onPlate;
        _offset = offset;
    }

    public bool IsAlive => _alive && _socket.State == WebSocketState.Open;

    public static async Task<AxisPlateStream> OpenAsync(DeviceConnectionInfo info, Action<PlateRecognition> onPlate, CancellationToken ct)
    {
        TimeSpan offset;
        using (var client = new AxisVapixClient(info))
            offset = await AxisRecordings.OffsetAsync(client, info, ct);
        var socket = await AxisEventStream.ConnectAsync(info, [Topic], ct);
        var stream = new AxisPlateStream(socket, info, onPlate, offset);
        stream._loop = Task.Run(async () =>
        {
            try { await AxisEventStream.ReceiveAsync(socket, stream.Handle, stream._cts.Token); }
            catch (Exception) { }
            finally { stream._alive = false; }
        });
        return stream;
    }

    internal void Handle(string json)
    {
        if (Read(json) is not { } read) return;
        var (plate, carId, box) = read;

        lock (_seen)
        {
            var now = DateTime.UtcNow;
            foreach (string old in _seen.Where(kv => now - kv.Value > TimeSpan.FromMinutes(10)).Select(kv => kv.Key).ToList())
                _seen.Remove(old);
            if (carId.Length > 0 && !_seen.TryAdd(carId, now)) return;
        }

        // La escena se toma aparte, sin frenar la lectura de los siguientes eventos.
        _ = Task.Run(async () =>
        {
            byte[]? scene = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                scene = await new AxisDeviceDriver().CaptureSnapshotAsync(_info, plate.ChannelNumber, timeout.Token);
            }
            catch (Exception) { }
            _onPlate(Normalize(plate with { SceneImage = scene }, box));
        });
    }

    /// <summary>Rectángulo de la placa en píxeles de la escena (left/top/width/height).</summary>
    internal readonly record struct PixelBox(double Left, double Top, double Width, double Height);

    /// <summary>Lo que trae una notificación de ALPV, ya traducido (null = no es una patente nueva con texto).</summary>
    internal (PlateRecognition Plate, string CarId, PixelBox? Box)? Read(string json) => Parse(json, _offset);

    internal static (PlateRecognition Plate, string CarId, PixelBox? Box)? Parse(string json, TimeSpan offset)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("method", out var method) || method.GetString() != "events:notify") return null;
        if (!root.TryGetProperty("params", out var p) || !p.TryGetProperty("notification", out var n)) return null;
        string topic = n.TryGetProperty("topic", out var t) ? t.GetString() ?? "" : "";
        if (!topic.Contains("ALPV", StringComparison.OrdinalIgnoreCase)) return null;

        // Los campos pueden venir en data, source o key según la versión: se juntan todos.
        var f = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (n.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object)
            foreach (string part in new[] { "source", "key", "data" })
                if (m.TryGetProperty(part, out var obj) && obj.ValueKind == JsonValueKind.Object)
                    foreach (var prop in obj.EnumerateObject())
                        f[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString();

        string text = (f.GetValueOrDefault("text") ?? f.GetValueOrDefault("plateText") ?? "").Trim();
        string state = f.GetValueOrDefault("carState") ?? "";
        if (text.Length == 0 || state.Equals("lost", StringComparison.OrdinalIgnoreCase)) return null;

        DateTime captured = long.TryParse(f.GetValueOrDefault("capture_timestamp"), out long ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime + offset
            : n.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out long nms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(nms).UtcDateTime + offset
                : DateTime.UtcNow + offset;

        string? Opt(params string[] names) =>
            names.Select(f.GetValueOrDefault).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && !v.Equals("unknown", StringComparison.OrdinalIgnoreCase));

        string? direction = Opt("carMoveDirection") switch
        {
            null => null,
            var d when d.Equals("in", StringComparison.OrdinalIgnoreCase) => "Entrada",
            var d when d.Equals("out", StringComparison.OrdinalIgnoreCase) => "Salida",
            var d => d,
        };
        string? country = Opt("country", "plateCountry");
        string? region = Opt("region", "plateRegion", "regionCode");
        string? list = Opt("listName");
        string? attributes = string.Join(" · ", new[]
        {
            country is null ? null : $"País {country}" + (region is null ? "" : $" ({region})"),
            list is null ? null : $"Lista «{list}»" + (Opt("listMode") is { } mode ? $" ({mode})" : ""),
        }.Where(s => s is not null));

        byte[]? plateImage = null;
        if (Opt("lpImage") is { } b64)
            try { plateImage = Convert.FromBase64String(b64.Contains(',') ? b64[(b64.IndexOf(',') + 1)..] : b64); }
            catch (FormatException) { }

        PixelBox? box = double.TryParse(f.GetValueOrDefault("left"), NumberStyles.Float, CultureInfo.InvariantCulture, out double left) &&
                        double.TryParse(f.GetValueOrDefault("top"), NumberStyles.Float, CultureInfo.InvariantCulture, out double top) &&
                        double.TryParse(f.GetValueOrDefault("width"), NumberStyles.Float, CultureInfo.InvariantCulture, out double width) &&
                        double.TryParse(f.GetValueOrDefault("height"), NumberStyles.Float, CultureInfo.InvariantCulture, out double height)
            ? new PixelBox(left, top, width, height) : null;

        int confidence = double.TryParse(f.GetValueOrDefault("plateConfidence"), NumberStyles.Float, CultureInfo.InvariantCulture, out double c)
            ? (int)Math.Round(c <= 1 ? c * 100 : c) : -1;
        int lane = int.TryParse(f.GetValueOrDefault("roiID"), out int roi) && roi > 0 ? roi : 0;

        var plate = new PlateRecognition(
            ChannelNumber: AxisEventStream.ChannelOf(topic, f),
            PlateNumber: text.ToUpperInvariant(),
            CapturedAt: captured,
            Confidence: confidence,
            CharConfidences: [],
            PlateColor: null,
            PlateType: Opt("plateType"),
            VehicleType: Opt("vehicleType", "type"),
            VehicleColor: Opt("vehicleColor", "color"),
            VehicleBrand: Opt("vehicleMake", "make"),
            VehicleAttributes: attributes.Length > 0 ? attributes : null,
            SpeedKmh: null,
            VehicleLengthCm: null,
            Direction: direction,
            Lane: lane > 0 ? lane : null,
            DetectionMethod: "AXIS License Plate Verifier",
            Violation: null,
            PlateX: 0, PlateY: 0, PlateWidth: 0, PlateHeight: 0,
            SceneImage: null,
            PlateImage: plateImage);
        return (plate, f.GetValueOrDefault("carID") ?? "", box);
    }

    /// <summary>El rectángulo en píxeles se lleva a 0..1 con el tamaño real de la foto de escena.</summary>
    internal static PlateRecognition Normalize(PlateRecognition plate, PixelBox? box)
    {
        if (box is not { } b || plate.SceneImage is not { } jpeg || JpegSize(jpeg) is not { } size) return plate;
        return plate with
        {
            PlateX = Math.Clamp(b.Left / size.Width, 0, 1),
            PlateY = Math.Clamp(b.Top / size.Height, 0, 1),
            PlateWidth = Math.Clamp(b.Width / size.Width, 0, 1),
            PlateHeight = Math.Clamp(b.Height / size.Height, 0, 1),
        };
    }

    /// <summary>Ancho y alto de un JPEG, leídos de su marcador SOF (sin decodificar la imagen).</summary>
    internal static (int Width, int Height)? JpegSize(byte[] jpeg)
    {
        int i = 2;
        while (i + 9 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF) { i++; continue; }
            byte marker = jpeg[i + 1];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return ((jpeg[i + 7] << 8) | jpeg[i + 8], (jpeg[i + 5] << 8) | jpeg[i + 6]);
            int length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (length < 2) return null;
            i += 2 + length;
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _alive = false;
        _cts.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
            }
        }
        catch (Exception) { }
        if (_loop is not null) try { await _loop; } catch (Exception) { }
        _socket.Dispose();
        _cts.Dispose();
    }
}
