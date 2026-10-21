using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.ZkTeco;

public sealed class C3AccessDriverFactory : IAccessControlDriverFactory
{
    public string DriverKey => "zkteco-c3";
    public string DisplayName => "ZKTeco inBio / C3 / MA (protocolo C3 del PullSDK, puerto 4370)";
    public int DefaultPort => C3Protocol.DefaultPort;
    public bool DefaultHttps => false;
    public AccessAuthMode AuthMode => AccessAuthMode.CommKey;
    public string? Hint => "Las controladoras inBio/C3 y los terminales MA no piden usuario: la credencial es su clave de comunicación " +
                           "(la que se fija en ZKAccess con «Modificar clave de comunicación»). De fábrica no tienen: deje el campo vacío. " +
                           "Guardan UNA tarjeta por persona y el identificador de la persona debe ser numérico.";
    public bool SupportsPersonSync => true;
    public IAccessControlDriver Create() => new C3AccessDriver();
}

/// <summary>
/// Controladoras ZKTeco inBio (160/260/460/480…) y C3 (100/200/400) por su
/// protocolo propio (<see cref="C3Protocol"/>), el de ZKAccess y el PullSDK.
/// No sirve el driver de terminales (<see cref="ZkTecoAccessDriver"/>): la
/// controladora no contesta ese protocolo aunque escuche en el mismo puerto.
///
/// Qué hace: identifica la controladora y sus puertas, abre / cierra / deja
/// abierta cada puerta, escucha sus eventos en tiempo real (cada pasada,
/// rechazo, apertura, cierre y alarma) y lee el estado del sensor de cada
/// puerta, fija la hora y la reinicia. El padrón (usuarios, tarjetas y
/// horarios) se escribe en sus tablas de datos con los mismos comandos del
/// PullSDK (capturados de plcommpro.dll contra una inBio480).
///
/// Los eventos no se empujan: se piden. La escucha mantiene UNA sesión abierta
/// y le pide a la controladora lo nuevo cada segundo; la controladora responde
/// los eventos pendientes de esa sesión, o, si no hay, un registro con el
/// estado de las puertas, que es de donde sale el estado del sensor.
/// </summary>
public sealed class C3AccessDriver : IAccessControlDriver
{
    /// <summary>Tope de puertas (la controladora más grande administra 4).</summary>
    private const int MaxDoors = 4;
    /// <summary>Cada cuánto se le piden los eventos nuevos en la escucha.</summary>
    private static readonly TimeSpan RtLogInterval = TimeSpan.FromSeconds(1);
    /// <summary>Pulso de apertura cuando la puerta no tiene uno configurado.</summary>
    private const int DefaultOpenSeconds = 5;

    /// <summary>
    /// Último estado de puertas que mandó cada controladora en su escucha. El
    /// monitoreo lo lee de aquí en vez de abrir otra sesión, que se llevaría
    /// eventos que le tocan a la escucha.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (DateTime At, byte[] Sensors, byte[] Alarms)> LastStatus = new();
    /// <summary>Un estado más viejo que esto ya no se considera vivo.</summary>
    private static readonly TimeSpan StatusFreshness = TimeSpan.FromSeconds(15);

    /// <summary>
    /// La clave vacía y "0" son "sin clave": el formulario sugiere 0 como en
    /// los terminales ZKTeco, y una controladora de fábrica no tiene ninguna.
    /// </summary>
    private static string? PasswordOf(AccessConnectionInfo info)
    {
        string value = (info.Password ?? "").Trim();
        return value is "" or "0" ? null : value;
    }

    private static Task<C3Connection> OpenAsync(AccessConnectionInfo info, CancellationToken ct) =>
        C3Connection.OpenAsync(info.Host, info.Port, PasswordOf(info), ct);

    public async Task<AccessDeviceInfo> ProbeAsync(AccessConnectionInfo info, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(info, ct);
        var p = await connection.GetParametersAsync(
            ["~SerialNumber", "FirmVer", "~DeviceName", "LockCount", "ReaderCount", "AuxInCount", "AuxOutCount", "~ZKFPVersion", "~MaxUserCount"], ct);

        string? serial = p.GetValueOrDefault("~SerialNumber");
        if (serial is null && !p.ContainsKey("LockCount"))
            throw new DriverException("El equipo respondió el protocolo C3 pero no entregó su identificación.");

        int doorCount = int.TryParse(p.GetValueOrDefault("LockCount"), out int locks) && locks > 0
            ? Math.Min(locks, MaxDoors) : 1;
        // ~DeviceName no existe en todos los firmware; el modelo se arma con las puertas.
        string model = p.GetValueOrDefault("~DeviceName") ?? $"inBio / C3 de {doorCount} puerta(s)";

        int? capacity = int.TryParse(p.GetValueOrDefault("~MaxUserCount"), out int hundreds) && hundreds > 0
            ? hundreds * 100 : null;
        var capabilities = new AccessCapabilities(
            DoorCount: doorCount,
            SupportsRemoteControl: true,
            SupportsEvents: true,
            SupportsCards: true,
            // Con lector de huella el equipo informa la versión de su algoritmo
            // (el MA500 dice 10); las inBio aceptan lectores de huella externos.
            SupportsFingerprint: p.ContainsKey("~ZKFPVersion") || model.Contains("inbio", StringComparison.OrdinalIgnoreCase),
            SupportsFace: false,
            // El equipo informa el cupo en centenas (300 = 30.000 personas); una tarjeta por persona.
            UserCapacity: capacity,
            CardCapacity: capacity);

        var doors = Enumerable.Range(1, doorCount).Select(n => new AccessDoorInfo(n, $"Puerta {n}")).ToList();
        // El mismo protocolo lo hablan las controladoras (inBio, C3) y los
        // terminales de acceso de la familia (MA300, MA500…).
        bool controller = IsController(model) || doorCount > 1;
        return new AccessDeviceInfo(model, serial, p.GetValueOrDefault("FirmVer"),
            controller ? "Controladora C3" : "Terminal C3", MacAddress: null,
            controller ? AccessDeviceKind.Controller : AccessDeviceKind.Terminal, capabilities, doors);
    }

    /// <summary>Modelo de controladora de puertas (inBio, C3-…) y no de terminal.</summary>
    public static bool IsController(string? model) =>
        model is not null && (model.Contains("inbio", StringComparison.OrdinalIgnoreCase) ||
                              model.StartsWith("C3", StringComparison.OrdinalIgnoreCase));

    public async Task PingAsync(AccessConnectionInfo info, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(info, ct);
        _ = await connection.GetParametersAsync(["~SerialNumber"], ct);
    }

    // ==================================================================
    // Puertas
    // ==================================================================

    public async Task ControlDoorAsync(AccessConnectionInfo info, int doorNumber, AccessDoorCommand command,
        CancellationToken ct = default)
    {
        if (doorNumber is < 1 or > MaxDoors) throw new DriverException($"La controladora no tiene la puerta {doorNumber}.");
        byte door = (byte)doorNumber;
        await using var connection = await OpenAsync(info, ct);
        switch (command)
        {
            case AccessDoorCommand.Open:
                // El mismo pulso que da una pasada válida: el tiempo de cerradura de la puerta.
                var p = await connection.GetParametersAsync([$"Door{door}Drivertime"], ct);
                int seconds = int.TryParse(p.GetValueOrDefault($"Door{door}Drivertime"), out int t) && t is > 0 and < 255
                    ? t : DefaultOpenSeconds;
                await connection.ControlAsync(C3Protocol.ControlOutput, door, C3Protocol.OutputDoor, (byte)seconds, ct);
                break;
            case AccessDoorCommand.RemainOpen:
                await connection.ControlAsync(C3Protocol.ControlOutput, door, C3Protocol.OutputDoor, C3Protocol.OutputHoldOpen, ct);
                break;
            case AccessDoorCommand.Close:
                // Primero se suspende el modo normal abierta (el que deja
                // "mantener abierta" o el horario): con él activo, cortar la
                // salida no cierra y la controladora informa el evento 37. Si
                // la puerta no estaba en ese modo la orden puede rechazarse, y
                // da igual. Probado así en una inBio480.
                try { await connection.ControlAsync(C3Protocol.ControlNormalOpen, door, 0, 0, ct); }
                catch (DriverException) { }
                await connection.ControlAsync(C3Protocol.ControlOutput, door, C3Protocol.OutputDoor, 0, ct);
                break;
            default:
                throw new DriverException(
                    "Las controladoras inBio/C3 no tienen bloqueo de puerta: solo abrir, mantener abierta y cerrar.");
        }
    }

    // ==================================================================
    // Captura de tarjeta
    // ==================================================================

    public bool SupportsCardCapture => true;

    /// <summary>
    /// La controladora no tiene "espere una tarjeta": se escuchan sus eventos
    /// en una sesión propia (cada sesión recibe todos los eventos, no se los
    /// roba a la escucha del servidor) y se toma la primera tarjeta que pase,
    /// registrada o no. Una sesión nueva empieza repitiendo eventos recientes:
    /// ese arrastre se descarta antes de esperar.
    /// </summary>
    public async Task<string?> CaptureCardAsync(AccessConnectionInfo info, int? cardReaderNo = null,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(info, ct);
        for (int i = 0; i < 10; i++)
        {
            var backlog = await connection.ReadRtLogAsync(ct);
            if (backlog is null || !HasEvents(backlog)) break;
        }

        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
            var data = await connection.ReadRtLogAsync(ct);
            if (data is null) return null;   // firmware sin registro binario
            for (int at = 0; at < data.Length; at += C3Protocol.RtLogRecordSize)
            {
                var r = data.AsSpan(at, C3Protocol.RtLogRecordSize);
                // Sin filtro por lector: el número de lector del VMS (el de
                // Hikvision) no es la puerta de la C3, y sirve cualquiera.
                if (r[10] == C3Protocol.EventDoorAlarmStatus) continue;
                uint card = BinaryPrimitives.ReadUInt32LittleEndian(r);
                if (card > 0) return card.ToString();
            }
        }
        return null;
    }

    private static bool HasEvents(byte[] data)
    {
        for (int at = 0; at < data.Length; at += C3Protocol.RtLogRecordSize)
            if (data[at + 10] != C3Protocol.EventDoorAlarmStatus) return true;
        return false;
    }

    public bool SupportsDoorStatus => true;

    /// <summary>
    /// Estado de las puertas según el último registro de estado que mandó la
    /// controladora en su escucha. Sin escucha viva no hay estado (no se abre
    /// otra sesión: se llevaría eventos que le tocan a la escucha).
    /// </summary>
    public Task<IReadOnlyList<AccessDoorStatus>> ReadDoorStatusAsync(AccessConnectionInfo info, int doorCount,
        CancellationToken ct = default)
    {
        if (!LastStatus.TryGetValue(info.Host, out var status) || DateTime.UtcNow - status.At > StatusFreshness)
            throw new DriverException("Todavía no hay estado de puertas de la controladora (esperando su escucha de eventos).");
        IReadOnlyList<AccessDoorStatus> doors = Enumerable.Range(1, Math.Min(doorCount, MaxDoors))
            .Select(n => new AccessDoorStatus(n, AccessDoorMode.Normal, (status.Sensors[n - 1] & 0x0F) switch
            {
                1 => false,
                2 => true,
                _ => (bool?)null,   // la puerta no tiene sensor
            }))
            .ToList();
        return Task.FromResult(doors);
    }

    // ==================================================================
    // Eventos
    // ==================================================================

    public bool SupportsEventStream => true;

    /// <summary>
    /// Historial: la tabla <c>transaction</c> leída con la opción "solo nuevas",
    /// como lo hace ZKAccess. La controladora lleva UN marcador por tabla: lo
    /// que entrega aquí ya no lo entrega otra vez, ni al VMS ni a un ZKAccess
    /// que siga conectado a ella (conviven mal: el que lee primero se lo lleva).
    /// Lo que también llegó por la escucha en vivo lo descarta el servidor al
    /// guardar (misma hora, persona, tipo y puerta).
    /// </summary>
    public async Task<IReadOnlyList<AccessEventRecord>> FetchEventsAsync(AccessConnectionInfo info, DateTime sinceUtc, int max,
        CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(info, ct);
        var table = await connection.TableAsync("transaction", ct);
        string[] fields = ["Cardno", "Pin", "Verified", "DoorID", "EventType", "InOutState", "Time_second"];
        var rows = await connection.ReadAsync(table, fields, ct, onlyNew: true);

        var events = new List<AccessEventRecord>();
        foreach (var row in rows)
        {
            var local = C3Protocol.DecodeTime((uint)row.Int(table.Field("Time_second")));
            if (local == DateTime.MinValue) continue;
            var record = BuildEvent(local, (uint)row.Int(table.Field("Cardno")), (uint)row.Int(table.Field("Pin")),
                (byte)row.Int(table.Field("Verified")), (byte)row.Int(table.Field("DoorID")),
                (byte)row.Int(table.Field("EventType")), (byte)row.Int(table.Field("InOutState")), raw: "transaction");
            if (record.Timestamp > sinceUtc) events.Add(record);
        }
        return events.OrderBy(e => e.Timestamp).TakeLast(max).ToList();
    }

    public async IAsyncEnumerable<AccessEventRecord> StreamEventsAsync(AccessConnectionInfo info,
        Action<AccessStreamSignal>? signal = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(info, ct);
        signal?.Invoke(AccessStreamSignal.Connected);
        bool textMode = false;

        while (!ct.IsCancellationRequested)
        {
            var records = new List<AccessEventRecord>();
            if (!textMode)
            {
                byte[]? data = await connection.ReadRtLogAsync(ct);
                if (data is null) textMode = true;   // firmware sin registro binario: se sigue en texto
                else
                    for (int at = 0; at < data.Length; at += C3Protocol.RtLogRecordSize)
                        if (ParseBinary(info.Host, data.AsSpan(at, C3Protocol.RtLogRecordSize)) is { } record)
                            records.Add(record);
            }
            if (textMode && ParseText(info.Host, await connection.ReadRtLogTextAsync(ct)) is { } textRecord)
                records.Add(textRecord);

            signal?.Invoke(AccessStreamSignal.Alive);
            foreach (var record in records) yield return record;

            await Task.Delay(RtLogInterval, ct);
        }
    }

    /// <summary>
    /// Un registro binario: o el estado de las puertas (se guarda para el
    /// monitoreo y no es un evento) o un evento.
    /// </summary>
    private static AccessEventRecord? ParseBinary(string host, ReadOnlySpan<byte> r)
    {
        var local = C3Protocol.DecodeTime(BinaryPrimitives.ReadUInt32LittleEndian(r[12..]));
        if (r[10] == C3Protocol.EventDoorAlarmStatus)
        {
            // [0..3] alarma de cada puerta, [4..7] sensor de cada puerta.
            LastStatus[host] = (DateTime.UtcNow, r[4..8].ToArray(), r[0..4].ToArray());
            return null;
        }
        if (local == DateTime.MinValue) return null;
        uint card = BinaryPrimitives.ReadUInt32LittleEndian(r);
        uint pin = BinaryPrimitives.ReadUInt32LittleEndian(r[4..]);
        return BuildEvent(local, card, pin, verify: r[8], door: r[9], eventType: r[10], direction: r[11],
            raw: Convert.ToHexString(r));
    }

    /// <summary>Un registro de texto (firmware nuevos): <c>time, pin, cardno, eventaddr, event, inoutstatus, verifytype</c>.</summary>
    private static AccessEventRecord? ParseText(string host, Dictionary<string, string> kv)
    {
        if (kv.Count == 0 || !kv.TryGetValue("time", out var time) ||
            !DateTime.TryParse(time, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var local))
            return null;
        if (!kv.ContainsKey("event"))
        {
            // Estado: "sensor" con 2 bits por puerta, "alarm" en hexadecimal.
            int sensor = Convert.ToInt32(kv.GetValueOrDefault("sensor") ?? "0", 16);
            var sensors = Enumerable.Range(0, 4).Select(i => (byte)((sensor >> (i * 2)) & 0x03)).ToArray();
            var alarms = Convert.FromHexString((kv.GetValueOrDefault("alarm") ?? "00000000").PadLeft(8, '0'));
            LastStatus[host] = (DateTime.UtcNow, sensors, alarms);
            return null;
        }
        int Int(string key) => int.TryParse(kv.GetValueOrDefault(key), out int v) ? v : 0;
        return BuildEvent(local, (uint)Int("cardno"), (uint)Int("pin"), (byte)Int("verifytype"), (byte)Int("eventaddr"),
            (byte)Int("event"), (byte)Int("inoutstatus"), raw: string.Join(",", kv.Select(p => $"{p.Key}={p.Value}")));
    }

    private static AccessEventRecord BuildEvent(DateTime local, uint card, uint pin, byte verify, byte door,
        byte eventType, byte direction, string raw)
    {
        var (kind, description) = Describe(eventType);
        var credential = eventType switch
        {
            8 or 9 or 205 => AccessCredentialKind.Remote,
            202 => AccessCredentialKind.ExitButton,
            // Lectura Wiegand que la controladora no entiende (formato que no es
            // el suyo, p. ej. ioProx de 39 bits): es una tarjeta, aunque no traiga número.
            41 or 42 => AccessCredentialKind.Card,
            _ => verify switch
            {
                1 => AccessCredentialKind.Fingerprint,
                3 => AccessCredentialKind.Pin,
                4 or 10 or 11 => AccessCredentialKind.Card,
                6 => card > 0 ? AccessCredentialKind.Card : AccessCredentialKind.Fingerprint,
                _ when card > 0 => AccessCredentialKind.Card,
                // Sin modo de verificación (el MA500 manda 0) lo dice el propio evento.
                _ => eventType switch
                {
                    14 or 15 or 16 or 18 or 19 or 31 or 33 or 34 or 35 or 103 => AccessCredentialKind.Fingerprint,
                    4 or 30 or 101 => AccessCredentialKind.Pin,
                    27 or 29 => AccessCredentialKind.Card,
                    _ => AccessCredentialKind.Unknown,
                },
            },
        };
        if (direction == 3) description += " (salida)";
        // En los eventos de auxiliares el número es el de la entrada o salida
        // auxiliar, no el de una puerta.
        bool auxiliary = eventType is 12 or 13 or 220 or 221;
        if (auxiliary) description += $" {door}";
        return new AccessEventRecord(
            // La controladora cuenta en SU hora local, que en una instalación
            // normal es la del servidor; el VMS guarda todo en UTC.
            Timestamp: DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime(),
            DoorNumber: !auxiliary && door is > 0 and <= MaxDoors ? door : null,
            Kind: kind,
            Credential: credential,
            Description: description,
            EmployeeNo: pin > 0 ? pin.ToString() : null,
            PersonName: null,
            CardNumber: card > 0 ? card.ToString() : null,
            MajorType: null,
            MinorType: eventType,
            RawJson: raw);
    }

    /// <summary>Tipos de evento del protocolo C3 (los del manual del PullSDK).</summary>
    private static (AccessEventKind Kind, string Description) Describe(byte eventType) => eventType switch
    {
        0 => (AccessEventKind.Granted, "Acceso concedido"),
        1 => (AccessEventKind.Granted, "Acceso en horario de puerta abierta"),
        2 => (AccessEventKind.Granted, "Primera tarjeta: puerta abierta"),
        3 => (AccessEventKind.Granted, "Acceso multitarjeta"),
        4 => (AccessEventKind.Granted, "Apertura con clave de emergencia"),
        5 => (AccessEventKind.DoorOpen, "Puerta abierta por horario"),
        6 => (AccessEventKind.Other, "Vinculación activada"),
        7 => (AccessEventKind.Other, "Alarma cancelada"),
        8 => (AccessEventKind.DoorOpen, "Apertura remota"),
        9 => (AccessEventKind.DoorClose, "Cierre remoto"),
        10 => (AccessEventKind.Other, "Horario de puerta abierta suspendido por hoy"),
        11 => (AccessEventKind.Other, "Horario de puerta abierta reactivado"),
        12 => (AccessEventKind.Other, "Salida auxiliar activada"),
        13 => (AccessEventKind.Other, "Salida auxiliar desactivada"),
        14 => (AccessEventKind.Granted, "Acceso con huella"),
        15 => (AccessEventKind.Granted, "Acceso multitarjeta (huella)"),
        16 => (AccessEventKind.Granted, "Huella en horario de puerta abierta"),
        17 => (AccessEventKind.Granted, "Acceso con tarjeta y huella"),
        18 or 19 => (AccessEventKind.Granted, "Primera tarjeta: puerta abierta"),
        20 => (AccessEventKind.Denied, "Rechazado: intervalo entre pasadas muy corto"),
        21 => (AccessEventKind.Denied, "Rechazado: puerta fuera de horario"),
        22 => (AccessEventKind.Denied, "Rechazado: fuera de horario"),
        23 => (AccessEventKind.Denied, "Acceso denegado"),
        24 => (AccessEventKind.Denied, "Rechazado: antipassback"),
        25 => (AccessEventKind.Denied, "Rechazado: interbloqueo"),
        26 or 32 => (AccessEventKind.Denied, "Esperando más credenciales (multitarjeta)"),
        27 => (AccessEventKind.Denied, "Tarjeta no registrada"),
        28 => (AccessEventKind.Alarm, "Puerta abierta demasiado tiempo"),
        29 => (AccessEventKind.Denied, "Rechazado: tarjeta vencida"),
        30 => (AccessEventKind.Denied, "Rechazado: clave incorrecta"),
        31 => (AccessEventKind.Denied, "Rechazado: intervalo entre huellas muy corto"),
        33 => (AccessEventKind.Denied, "Rechazado: huella vencida"),
        34 => (AccessEventKind.Denied, "Huella no registrada"),
        35 or 36 => (AccessEventKind.Denied, "Rechazado: puerta fuera de horario"),
        37 => (AccessEventKind.Other, "No se pudo cerrar en horario de puerta abierta"),
        41 => (AccessEventKind.Denied, "Rechazado: modo de verificación incorrecto"),
        42 => (AccessEventKind.Denied, "Rechazado: formato Wiegand incorrecto"),
        101 => (AccessEventKind.Alarm, "Apertura bajo coacción (clave)"),
        102 => (AccessEventKind.Alarm, "Puerta forzada"),
        103 => (AccessEventKind.Alarm, "Apertura bajo coacción (huella)"),
        200 => (AccessEventKind.DoorOpen, "Puerta abierta"),
        201 => (AccessEventKind.DoorClose, "Puerta cerrada"),
        202 => (AccessEventKind.DoorOpen, "Botón de salida"),
        203 => (AccessEventKind.Granted, "Acceso multitarjeta (tarjeta y huella)"),
        204 => (AccessEventKind.Other, "Terminó el horario de puerta abierta"),
        205 => (AccessEventKind.DoorOpen, "Puerta abierta remotamente hasta nueva orden"),
        206 => (AccessEventKind.Other, "Controladora encendida"),
        208 => (AccessEventKind.Granted, "Apertura por superusuario"),
        220 => (AccessEventKind.Other, "Entrada auxiliar abierta"),
        221 => (AccessEventKind.Other, "Entrada auxiliar en corto"),
        _ => (AccessEventKind.Other, $"Evento {eventType}"),
    };

    // ==================================================================
    // Padrón
    // ==================================================================

    /// <summary>
    /// Los horarios del VMS van de este número en adelante: los primeros los usa
    /// ZKAccess (el 1 es su «24 horas»), y una controladora que se migra sigue
    /// con su padrón viejo apuntando a ellos.
    /// </summary>
    private const int TimezoneBase = 100;
    /// <summary>Tramos por día de un horario C3.</summary>
    private const int SegmentsPerDay = 3;
    /// <summary>Largo del nombre en la tabla de usuarios (lo que corta el firmware).</summary>
    private const int NameBytes = 24;

    private static readonly string[] DayPrefixes = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    public bool SupportsPersonSync => true;

    // ==================================================================
    // Huellas (ZKFinger v10)
    // ==================================================================

    public string FingerprintFormat => AccessFingerprintFormats.ZkV10;
    public bool SupportsFingerprintSync => true;
    public bool SupportsFingerprintImport => true;

    private static readonly string[] TemplateFields = ["Size", "UID", "Pin", "FingerID", "Valid", "Template", "Resverd", "EndTag"];

    /// <summary>
    /// Dedo del VMS (1-5 pulgar a meñique derechos, 6-10 pulgar a meñique
    /// izquierdos) → FingerID de ZKTeco (0 meñique izquierdo … 4 pulgar
    /// izquierdo, 5 pulgar derecho … 9 meñique derecho).
    /// </summary>
    private static int ZkFingerOf(int number) => number <= 5 ? number + 4 : 10 - number;
    private static int VmsFingerOf(int fingerId) => fingerId >= 5 ? fingerId - 4 : 10 - fingerId;

    /// <summary>
    /// Huellas que la controladora tiene de la persona: las enroladas en el
    /// menú de un terminal MA, o las que le escribió el VMS. Validado
    /// copiando las de un MA500 a una inBio480 con FR1200.
    /// </summary>
    public async Task<IReadOnlyList<AccessFingerprintData>> ReadFingerprintsAsync(AccessConnectionInfo info, string employeeNo,
        CancellationToken ct = default)
    {
        byte[] pin = PinOf(employeeNo);
        await using var connection = await OpenAsync(info, ct);
        var table = await connection.TableAsync("templatev10", ct);
        // Sin pedir TODOS los campos el MA500 devuelve la plantilla vacía.
        var rows = await connection.ReadAsync(table, TemplateFields, ct, ("Pin", pin));
        return rows
            .Where(r => r.Int(table.Field("Valid")) != 0 && r.Bytes(table.Field("Template")).Length > 0)
            .Select(r => new AccessFingerprintData(VmsFingerOf((int)r.Int(table.Field("FingerID"))),
                Convert.ToBase64String(r.Bytes(table.Field("Template"))), AccessFingerprintFormats.ZkV10))
            .Where(f => AccessFingers.IsValid(f.Number))
            .GroupBy(f => f.Number).Select(g => g.First())
            .OrderBy(f => f.Number).ToList();
    }

    /// <summary>
    /// Reemplaza las huellas de la persona en el equipo por las del VMS. Si el
    /// VMS no tiene ninguna, no se tocan: pueden ser las recién enroladas en el
    /// menú del equipo, que todavía no se trajeron.
    /// </summary>
    private static async Task WriteFingerprintsAsync(C3Connection connection, byte[] pin, AccessPersonPlan plan,
        CancellationToken ct)
    {
        var fingerprints = plan.Fingerprints.Where(f => f.Format == AccessFingerprintFormats.ZkV10).ToList();
        if (fingerprints.Count == 0) return;

        var users = await connection.TableAsync("user", ct);
        var user = await connection.ReadAsync(users, ["UID", "Pin"], ct, ("Pin", pin));
        if (user.Count == 0) throw new DriverException("La controladora no devolvió a la persona recién escrita.");
        long uid = user[0].Int(users.Field("UID"));

        var table = await connection.TableAsync("templatev10", ct);
        await connection.DeleteAsync(table, "Pin", pin, ct);
        await connection.WriteAsync(table, TemplateFields, fingerprints.Select(f =>
        {
            byte[] template = Convert.FromBase64String(f.Template);
            return new[]
            {
                C3Protocol.EncodeInt(template.Length), C3Protocol.EncodeInt(uid), pin,
                C3Protocol.EncodeInt(ZkFingerOf(f.Number)), C3Protocol.EncodeInt(1), template, [], [],
            };
        }), ct);
    }

    /// <summary>El identificador de la persona es el PIN de la controladora, que es numérico.</summary>
    private static byte[] PinOf(string employeeNo) =>
        uint.TryParse(employeeNo.Trim(), out uint pin) && pin > 0
            ? C3Protocol.EncodeInt(pin)
            : throw new DriverException(
                $"Las controladoras inBio/C3 solo aceptan identificadores numéricos de persona ('{employeeNo}' no lo es).");

    public async Task ApplyWeekPlanAsync(AccessConnectionInfo info, AccessWeekPlan plan, CancellationToken ct = default)
    {
        var fields = new List<string> { "TimezoneId" };
        var values = new List<byte[]> { C3Protocol.EncodeInt(TimezoneBase + plan.Number) };
        // Los siete días y los tres tipos de feriado. El VMS usa un solo tipo
        // (todo su calendario es tipo 1) pero escribe los tres iguales: así un
        // feriado cargado a mano en ZKAccess con otro tipo tampoco abre de más.
        for (int day = 0; day < 7 + 3; day++)
        {
            var segments = (day < 7
                    ? plan.Segments.Where(s => s.Day == day && s.EndMinutes > s.StartMinutes).OrderBy(s => s.StartMinutes).ToList()
                    : plan.HolidaySegments)
                .ToList();
            if (segments.Count > SegmentsPerDay)
                throw new DriverException(
                    $"El horario «{plan.Name}» tiene {segments.Count} tramos {(day < 7 ? "un mismo día" : "en feriados")}; " +
                    $"las controladoras inBio/C3 admiten {SegmentsPerDay}.");
            string prefix = day < 7 ? DayPrefixes[day] : $"Hol{day - 6}";
            for (int n = 0; n < SegmentsPerDay; n++)
            {
                fields.Add($"{prefix}Time{n + 1}");
                values.Add(n < segments.Count
                    ? C3Protocol.EncodeInt(C3Protocol.EncodeSegment(segments[n].StartMinutes, segments[n].EndMinutes))
                    : []);
            }
        }
        await using var connection = await OpenAsync(info, ct);
        var table = await connection.TableAsync("timezone", ct);
        await connection.WriteAsync(table, fields, [values.ToArray()], ct);
    }

    /// <summary>
    /// Escribe la persona (alta o actualización por PIN) y reemplaza sus
    /// derechos: una fila de <c>userauthorize</c> por horario, con las puertas
    /// como máscara de bits (puerta 1 = 1, 2 = 2, 3 = 4, 4 = 8).
    /// </summary>
    /// <summary>
    /// Un calendario por controladora, para todos sus horarios (ver
    /// <see cref="ApplyHolidayCalendarAsync"/>); el servidor decide si
    /// corresponde escribirlo.
    /// </summary>
    public bool UsesDeviceHolidayCalendar => true;

    /// <summary>
    /// Deja la tabla <c>holiday</c> exactamente con estos días: una fila por
    /// día (la controladora no conoce rangos), tipo 1 y sin repetición
    /// (Loop = 2): el servidor escribe la próxima ocurrencia de cada feriado y
    /// la renueva cuando pasa. Lo que hubiera (incluso de ZKAccess) se borra:
    /// el calendario lo lleva el VMS.
    /// </summary>
    public async Task ApplyHolidayCalendarAsync(AccessConnectionInfo info, IReadOnlyList<AccessHolidayDate> holidays,
        CancellationToken ct = default)
    {
        var wanted = holidays
            .SelectMany(h => Enumerable.Range(0, h.End.DayNumber - h.Start.DayNumber + 1).Select(i => h.Start.AddDays(i)))
            .Select(d => (long)(d.Year * 10000 + d.Month * 100 + d.Day))
            .Distinct().OrderBy(d => d).ToList();

        await using var connection = await OpenAsync(info, ct);
        var table = await connection.TableAsync("holiday", ct);
        var existing = (await connection.ReadAsync(table, ["Holiday"], ct))
            .Select(r => r.Int(table.Field("Holiday"))).ToHashSet();

        foreach (long day in existing.Where(d => !wanted.Contains(d)))
            await connection.DeleteAsync(table, "Holiday", C3Protocol.EncodeInt(day), ct);
        var missing = wanted.Where(d => !existing.Contains(d)).ToList();
        if (missing.Count > 0)
            await connection.WriteAsync(table, ["Holiday", "HolidayType", "Loop"],
                missing.Select(d => new[] { C3Protocol.EncodeInt(d), C3Protocol.EncodeInt(1), C3Protocol.EncodeInt(2) }), ct);
    }

    public async Task ApplyPersonAsync(AccessConnectionInfo info, AccessPersonPlan plan, CancellationToken ct = default)
    {
        byte[] pin = PinOf(plan.EmployeeNo);
        // Una sola tarjeta por persona en esta familia: va la primera (el
        // servidor las ordena), y si no es numérica no se puede guardar.
        byte[] card = [];
        if (plan.Cards.FirstOrDefault() is { } first)
            card = ulong.TryParse(first.Trim(), out ulong number) && number <= uint.MaxValue
                ? C3Protocol.EncodeInt((long)number)
                : throw new DriverException($"La tarjeta '{first}' no es un número que la controladora pueda guardar.");

        // La vigencia se guarda por día, en la hora local del equipo.
        var from = plan.ValidFrom.ToLocalTime().Date;
        var to = plan.ValidTo.ToLocalTime().Date;

        await using var connection = await OpenAsync(info, ct);
        var users = await connection.TableAsync("user", ct);
        await connection.WriteAsync(users,
            ["Pin", "CardNo", "Password", "Group", "StartTime", "EndTime", "Name", "SuperAuthorize"],
            [[
                pin, card,
                System.Text.Encoding.ASCII.GetBytes(plan.PinCode?.Trim() ?? ""),
                [],
                C3Protocol.EncodeInt(C3Protocol.EncodeDate(from)),
                C3Protocol.EncodeInt(C3Protocol.EncodeDate(to)),
                C3Protocol.EncodeText(plan.Name, NameBytes),
                [],
            ]], ct);

        var rights = await connection.TableAsync("userauthorize", ct);
        await connection.DeleteAsync(rights, "Pin", pin, ct);
        var rows = plan.Doors
            .Where(d => d.DoorNumber is >= 1 and <= MaxDoors)
            .GroupBy(d => d.Plan.Number)
            .Select(g => new[]
            {
                pin,
                C3Protocol.EncodeInt(TimezoneBase + g.Key),
                C3Protocol.EncodeInt(g.Aggregate(0, (mask, d) => mask | (1 << (d.DoorNumber - 1)))),
            })
            .ToList();
        if (rows.Count > 0)
            await connection.WriteAsync(rights, ["Pin", "AuthorizeTimezoneId", "AuthorizeDoorId"], rows, ct);

        await WriteFingerprintsAsync(connection, pin, plan, ct);
    }

    public async Task RemovePersonAsync(AccessConnectionInfo info, string employeeNo, CancellationToken ct = default)
    {
        byte[] pin = PinOf(employeeNo);
        await using var connection = await OpenAsync(info, ct);
        await connection.DeleteAsync(await connection.TableAsync("userauthorize", ct), "Pin", pin, ct);
        await connection.DeleteAsync(await connection.TableAsync("templatev10", ct), "Pin", pin, ct);
        await connection.DeleteAsync(await connection.TableAsync("user", ct), "Pin", pin, ct);
    }

    // ==================================================================
    // Hora y mantenimiento
    // ==================================================================

    public bool SupportsClock => true;
    public bool SupportsReboot => true;

    public async Task<DeviceClock> GetClockAsync(AccessConnectionInfo info, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(info, ct);
        var before = DateTime.UtcNow;
        var p = await connection.GetParametersAsync(["DateTime"], ct);
        var readAt = before + (DateTime.UtcNow - before) / 2;
        var local = uint.TryParse(p.GetValueOrDefault("DateTime"), out uint value)
            ? C3Protocol.DecodeTime(value) : DateTime.MinValue;
        if (local == DateTime.MinValue) throw new DriverException("La controladora no entregó su hora.");
        return new DeviceClock(local, UtcOffset: null, TimeZone: null, ZoneOffset: null, DaylightSaving: null,
            ZoneDescription: null, DeviceTimeMode.Manual, NtpServer: null, NtpIntervalMinutes: null, readAt);
    }

    public async Task<string?> SetClockAsync(AccessConnectionInfo info, DeviceClockSetting setting, CancellationToken ct = default)
    {
        if (setting.Mode == DeviceTimeMode.Ntp)
            throw new DriverException("Las controladoras inBio/C3 no usan NTP: se les fija la hora directamente.");
        await using var connection = await OpenAsync(info, ct);
        await connection.SetTimeAsync(setting.TargetLocalTime(), ct);
        return "la controladora no guarda zona horaria: quedó con la hora local de la zona elegida.";
    }

    public async Task RebootAsync(AccessConnectionInfo info, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(info, ct);
        await connection.ControlAsync(C3Protocol.ControlRestart, 0, 0, 0, ct);
    }
}
