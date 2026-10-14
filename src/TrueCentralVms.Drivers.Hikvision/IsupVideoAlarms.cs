using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Core.Isup;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Alarmas que una cámara, DVR/NVR o cámara LPR envía por ISUP, traducidas a lo que esperan los módulos de video: lecturas de
/// patente y eventos de analítica/alarma. Por ISUP 5.0 llegan como la misma notificación ISAPI que daría su alertStream
/// (EventNotificationAlert en XML, o su JSON) con las fotos aparte; por 2.0/4.0, como la alarma básica binaria ya decodificada.
/// </summary>
internal static class IsupVideoAlarms
{
    /// <summary>Notificación ISAPI aplanada: primer valor de cada campo, sin importar el anidamiento ni si vino en XML o JSON.</summary>
    internal sealed class Fields
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public XElement? Xml { get; private init; }

        public static Fields? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var fields = new Fields();
            text = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
            try
            {
                if (text.StartsWith('<'))
                {
                    var root = XDocument.Parse(text).Root;
                    if (root is null) return null;
                    fields = new Fields { Xml = root };
                    foreach (var e in root.DescendantsAndSelf().Where(e => !e.HasElements))
                        fields._values.TryAdd(e.Name.LocalName, e.Value.Trim());
                }
                else
                {
                    using var doc = JsonDocument.Parse(text);
                    fields.Walk(doc.RootElement);
                }
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or JsonException)
            {
                return null;
            }
            return fields;
        }

        private void Walk(JsonElement e, string? name = null)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject()) Walk(p.Value, p.Name);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) Walk(item, name);
                    break;
                default:
                    if (name is not null) _values.TryAdd(name, e.ToString().Trim());
                    break;
            }
        }

        public string? this[string name] => _values.TryGetValue(name, out var v) && v.Length > 0 ? v : null;
        public bool Has(string name) => _values.ContainsKey(name);

        public int? Int(params string[] names)
        {
            foreach (var n in names)
                if (int.TryParse(this[n], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
            return null;
        }

        /// <summary>Hora del evento según el reloj del equipo (el desfase que traiga no se aplica: es su hora local).</summary>
        public DateTime When(DateTimeOffset received) =>
            DateTimeOffset.TryParse(this["dateTime"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                ? t.DateTime : received.LocalDateTime;
    }

    // ------------------------------------------------------------------
    // Patentes
    // ------------------------------------------------------------------

    public static PlateRecognition? ToPlate(IsupAlarm alarm)
    {
        if (Fields.Parse(alarm.Text) is not { } f) return null;
        bool anpr = string.Equals(f["eventType"], "ANPR", StringComparison.OrdinalIgnoreCase) || f.Has("licensePlate");
        if (!anpr) return null;

        string plate = f["licensePlate"] ?? f["originalLicensePlate"] ?? "";
        // Vehículo detectado sin placa legible: la cámara lo informa vacío o con su marca de "sin placa".
        if (plate.Length == 0 || plate.Equals("unknown", StringComparison.OrdinalIgnoreCase)) plate = "";
        var (scene, plateImage) = Pictures(alarm.Pictures);
        var rect = PlateRect(f);

        return new PlateRecognition(
            ChannelNumber: f.Int("channelID", "dynChannelID") ?? 1,
            PlateNumber: plate,
            CapturedAt: f.When(alarm.ReceivedAt),
            Confidence: f.Int("confidenceLevel") is int c and >= 0 and <= 100 ? c : -1,
            CharConfidences: [],
            PlateColor: Lookup(f["plateColor"], PlateColors),
            PlateType: f["plateType"] is { } type && !type.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? type : null,
            VehicleType: Lookup(f["vehicleType"], VehicleTypes),
            VehicleColor: Lookup(f["color"] ?? f["vehicleColor"], VehicleColors),
            VehicleBrand: Known(f["vehicleLogoRecog"]) is { } brand && !int.TryParse(brand, out _) ? brand : null,
            VehicleAttributes: null,
            SpeedKmh: f.Int("speed") is int speed and > 0 ? speed : null,
            VehicleLengthCm: null,
            Direction: Lookup(f["direction"], Directions),
            Lane: f.Int("laneNo") is int lane and > 0 ? lane : null,
            // picRecogMode y similares son códigos sin catálogo publicado: solo se muestra un texto.
            DetectionMethod: Known(f["detectionType"]) is { } method && !int.TryParse(method, out _) ? method : null,
            Violation: Known(f["illegalInfo"] ?? f["violationType"]),
            PlateX: rect.X, PlateY: rect.Y, PlateWidth: rect.W, PlateHeight: rect.H,
            SceneImage: scene,
            PlateImage: plateImage);
    }

    /// <summary>
    /// Escena y recorte de la placa. El host guarda cada foto con el nombre con que la mandó la cámara
    /// (licensePlatePicture, detectionPicture…); sin nombres, la más grande es la escena y la más chica la placa.
    /// </summary>
    private static (byte[]? Scene, byte[]? Plate) Pictures(IReadOnlyList<string>? paths)
    {
        if (paths is not { Count: > 0 }) return (null, null);
        var files = paths.Select(p => (Path: p, Name: Path.GetFileName(p).ToLowerInvariant(), Bytes: Read(p)))
            .Where(x => x.Bytes is { Length: > 0 }).ToList();
        if (files.Count == 0) return (null, null);
        var plate = files.FirstOrDefault(x => x.Name.Contains("plate"));
        var scene = files.FirstOrDefault(x => x != plate && (x.Name.Contains("background") || x.Name.Contains("detection") ||
                                                             x.Name.Contains("vehicle") || x.Name.Contains("scene")));
        if (scene.Bytes is null) scene = files.Where(x => x != plate).OrderByDescending(x => x.Bytes!.Length).FirstOrDefault();
        if (plate.Bytes is null && files.Count > 1) plate = files.Where(x => x != scene).OrderBy(x => x.Bytes!.Length).FirstOrDefault();
        return (scene.Bytes, plate.Bytes);
    }

    private static byte[]? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Recuadro de la placa en la escena, normalizado 0..1 (ISAPI lo da en 0..1 o en 0..1000 según el firmware).</summary>
    private static (double X, double Y, double W, double H) PlateRect(Fields f)
    {
        var rect = f.Xml?.Descendants().FirstOrDefault(e => e.Name.LocalName == "plateRect");
        double Get(string name)
        {
            string? v = rect?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }
        double x = Get("X"), y = Get("Y"), w = Get("width"), h = Get("height");
        if (Math.Max(Math.Max(x, y), Math.Max(w, h)) > 1) { x /= 1000; y /= 1000; w /= 1000; h /= 1000; }
        return (Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1), Math.Clamp(w, 0, 1), Math.Clamp(h, 0, 1));
    }

    private static readonly Dictionary<string, string> PlateColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["blue"] = "Azul", ["yellow"] = "Amarillo", ["white"] = "Blanco", ["black"] = "Negro", ["green"] = "Verde",
    };

    private static readonly Dictionary<string, string> VehicleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vehicle"] = "Vehículo", ["car"] = "Automóvil", ["largeBus"] = "Bus", ["bus"] = "Bus", ["truck"] = "Camión",
        ["heavyTruck"] = "Camión", ["lightTruck"] = "Camión liviano", ["van"] = "Furgón", ["SUVMPV"] = "SUV / MPV",
        ["pickupTruck"] = "Camioneta", ["minibus"] = "Minibús", ["motorcycle"] = "Motocicleta", ["twoWheelVehicle"] = "Dos ruedas",
        ["threeWheelVehicle"] = "Tres ruedas", ["tricycle"] = "Triciclo",
    };

    private static readonly Dictionary<string, string> VehicleColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = "Blanco", ["silver"] = "Plata", ["gray"] = "Gris", ["black"] = "Negro", ["red"] = "Rojo",
        ["deepBlue"] = "Azul oscuro", ["blue"] = "Azul", ["yellow"] = "Amarillo", ["green"] = "Verde", ["brown"] = "Café",
        ["pink"] = "Rosado", ["purple"] = "Morado", ["deepGray"] = "Gris oscuro", ["cyan"] = "Cian", ["orange"] = "Naranjo",
    };

    private static readonly Dictionary<string, string> Directions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["forward"] = "Entrada", ["reverse"] = "Salida", ["bothway"] = "Bidireccional",
    };

    /// <summary>Valor traducido; uno desconocido se muestra tal cual (es dato real de la cámara); "unknown" = sin dato.</summary>
    private static string? Lookup(string? value, Dictionary<string, string> labels) =>
        Known(value) is { } v ? labels.GetValueOrDefault(v, v) : null;

    private static string? Known(string? value) =>
        value is { Length: > 0 } v && !v.Equals("unknown", StringComparison.OrdinalIgnoreCase) && v != "0" ? v : null;

    // ------------------------------------------------------------------
    // Eventos de analítica y alarma
    // ------------------------------------------------------------------

    public static DeviceEvent? ToEvent(IsupAlarm alarm)
    {
        if (alarm.Basic is { } basic) return FromBasic(basic, alarm.ReceivedAt);
        if (Fields.Parse(alarm.Text) is not { } f) return null;

        string type = f["eventType"] ?? "";
        // Los que no son eventos de video: patentes (van por su propio módulo), control de acceso y latidos.
        if (type.Length == 0 || type.Equals("ANPR", StringComparison.OrdinalIgnoreCase) || f.Has("licensePlate") ||
            type.Equals("AccessControllerEvent", StringComparison.OrdinalIgnoreCase) || type.Equals("heartBeat", StringComparison.OrdinalIgnoreCase))
            return null;
        // ISAPI avisa también cuando el evento termina ("inactive"): solo interesa el comienzo.
        if (string.Equals(f["eventState"], "inactive", StringComparison.OrdinalIgnoreCase)) return null;

        var (kind, label) = Classify(type);
        int? ruleId = f.Int("regionID", "RegionID");
        int? input = kind == VideoEventKind.AlarmInput ? f.Int("inputIOPortID", "dynInputIOPortID") : null;
        string description = kind switch
        {
            VideoEventKind.AlarmInput when input is int n => $"{label} {n}",
            VideoEventKind.LineCrossing when ruleId is int r => $"{label} · línea {r}",
            _ when ruleId is int r && kind is VideoEventKind.Intrusion or VideoEventKind.RegionEntrance or VideoEventKind.RegionExit
                or VideoEventKind.Loitering or VideoEventKind.Parking or VideoEventKind.ObjectLeftOrTaken
                or VideoEventKind.FastMoving or VideoEventKind.Crowd => $"{label} · regla {r}",
            VideoEventKind.Other => f["eventDescription"] is { } d ? $"{d} ({type})" : $"Evento «{type}»",
            _ => label,
        };
        byte[]? image = alarm.Pictures is { Count: > 0 } pictures ? pictures.Select(Read).FirstOrDefault(b => b is { Length: > 0 }) : null;
        return new DeviceEvent(f.Int("channelID", "dynChannelID") ?? 0, kind, description, f.When(alarm.ReceivedAt),
            RuleName: f["ruleName"], AlarmInput: input, Image: image, RuleId: ruleId);
    }

    /// <summary>eventType de ISAPI → tipo de evento del VMS y su nombre.</summary>
    private static (VideoEventKind Kind, string Label) Classify(string eventType) => eventType.ToLowerInvariant() switch
    {
        "vmd" or "motion" or "motiondetection" => (VideoEventKind.Motion, "Detección de movimiento"),
        "linedetection" or "linecrossing" => (VideoEventKind.LineCrossing, "Cruce de línea"),
        "fielddetection" or "intrusion" => (VideoEventKind.Intrusion, "Intrusión"),
        "regionentrance" => (VideoEventKind.RegionEntrance, "Entrada a región"),
        "regionexiting" => (VideoEventKind.RegionExit, "Salida de región"),
        "loitering" => (VideoEventKind.Loitering, "Merodeo"),
        "unattendedbaggage" or "attendedbaggage" => (VideoEventKind.ObjectLeftOrTaken, "Objeto abandonado o retirado"),
        "parking" => (VideoEventKind.Parking, "Estacionamiento indebido"),
        "fastmoving" or "running" => (VideoEventKind.FastMoving, "Movimiento rápido"),
        "group" or "peoplegathering" => (VideoEventKind.Crowd, "Aglomeración de personas"),
        "facedetection" or "facesnap" => (VideoEventKind.FaceDetection, "Detección de rostro"),
        "peoplecounting" => (VideoEventKind.PeopleCounting, "Conteo de personas"),
        "videoloss" => (VideoEventKind.VideoLoss, "Pérdida de señal de video"),
        "tamperdetection" or "shelteralarm" => (VideoEventKind.Tamper, "Cámara tapada (sabotaje de video)"),
        "scenechangedetection" => (VideoEventKind.VideoException, "Cambio de escena"),
        "defocus" => (VideoEventKind.VideoException, "Cámara desenfocada"),
        "videoexception" or "badvideo" => (VideoEventKind.VideoException, "Señal de video anómala"),
        "audioexception" => (VideoEventKind.AudioException, "Anomalía de audio"),
        "io" or "alarminput" => (VideoEventKind.AlarmInput, "Entrada de alarma"),
        "diskfull" or "hdfull" => (VideoEventKind.DeviceFault, "Disco lleno"),
        "diskerror" or "hderror" => (VideoEventKind.DeviceFault, "Error del disco"),
        "nicbroken" => (VideoEventKind.DeviceFault, "Red desconectada"),
        "ipconflict" => (VideoEventKind.DeviceFault, "Conflicto de dirección IP"),
        "illaccess" or "illegalaccess" => (VideoEventKind.DeviceFault, "Acceso ilegal (credenciales rechazadas en el equipo)"),
        "recordexception" or "recordingfailure" => (VideoEventKind.DeviceFault, "Falla de grabación"),
        _ => (VideoEventKind.Other, eventType),
    };

    /// <summary>Alarma básica de ISUP 2.0/4.0 (EN_ALARM_TYPE de HCISUPAlarm.h). Action 1 = terminó: no se informa.</summary>
    private static DeviceEvent? FromBasic(IsupBasicAlarm basic, DateTimeOffset received)
    {
        if (basic.Action != 0) return null;
        (VideoEventKind Kind, string Label)? mapped = basic.AlarmType switch
        {
            0 => (VideoEventKind.DeviceFault, "Disco lleno"),
            1 => (VideoEventKind.DeviceFault, "Error de lectura/escritura del disco"),
            5 => (VideoEventKind.VideoLoss, "Pérdida de señal de video"),
            6 => (VideoEventKind.AlarmInput, "Entrada de alarma"),
            7 => (VideoEventKind.Tamper, "Cámara tapada (sabotaje de video)"),
            8 => (VideoEventKind.Motion, "Detección de movimiento"),
            9 => (VideoEventKind.DeviceFault, "Formato de video no coincide"),
            11 => (VideoEventKind.Motion, "Detección PIR"),
            17 => (VideoEventKind.DeviceFault, "Falla de grabación"),
            19 => (VideoEventKind.DeviceFault, "Acceso ilegal"),
            97 => (VideoEventKind.FaceDetection, "Detección de rostro"),
            98 => (VideoEventKind.VideoException, "Cámara desenfocada"),
            99 => (VideoEventKind.AudioException, "Anomalía de audio"),
            100 => (VideoEventKind.VideoException, "Cambio de escena"),
            101 => (VideoEventKind.LineCrossing, "Cruce de línea"),
            102 => (VideoEventKind.RegionEntrance, "Entrada a región"),
            103 => (VideoEventKind.RegionExit, "Salida de región"),
            104 => (VideoEventKind.Intrusion, "Intrusión"),
            105 => (VideoEventKind.Loitering, "Merodeo"),
            106 or 112 or 113 => (VideoEventKind.ObjectLeftOrTaken, "Objeto abandonado o retirado"),
            107 => (VideoEventKind.Parking, "Estacionamiento indebido"),
            108 => (VideoEventKind.FastMoving, "Movimiento rápido"),
            109 => (VideoEventKind.Crowd, "Aglomeración de personas"),
            706 => (VideoEventKind.DeviceFault, "Falla de grabación"),
            _ => null,
        };
        if (mapped is not { } m) return null;
        int? input = m.Kind == VideoEventKind.AlarmInput && basic.AlarmInput > 0 ? basic.AlarmInput : null;
        DateTime at = DateTime.TryParse(basic.DeviceTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : received.LocalDateTime;
        return new DeviceEvent(basic.Channel, m.Kind, input is int n ? $"{m.Label} {n}" : m.Label, at, AlarmInput: input);
    }
}
