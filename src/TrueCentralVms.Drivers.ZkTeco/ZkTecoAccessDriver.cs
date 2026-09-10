using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.ZkTeco;

public sealed class ZkTecoAccessDriverFactory : IAccessControlDriverFactory
{
    public string DriverKey => "zkteco-tcp";
    public string DisplayName => "ZKTeco (control de acceso por SDK, puerto 4370)";
    public int DefaultPort => ZkProtocol.DefaultPort;
    public bool DefaultHttps => false;
    public AccessAuthMode AuthMode => AccessAuthMode.CommKey;
    public string? Hint => "ZKTeco no pide usuario: la credencial es la clave de comunicación del equipo " +
                           "(Comm Key, en Comunicación → Seguridad; de fábrica 0).";
    public IAccessControlDriver Create() => new ZkTecoAccessDriver();
}

/// <summary>
/// Control de acceso ZKTeco (terminales autónomos tipo MA/F/SF y controladoras
/// inBio) por el protocolo propio del fabricante en el puerto TCP 4370
/// (<see cref="ZkProtocol"/>), el mismo que usan ZKAccess y ZKBioSecurity.
///
/// Qué lee el administrador de dispositivos: nombre de modelo
/// (<c>~DeviceName</c>), serie (<c>~SerialNumber</c>), firmware, plataforma,
/// MAC, cuántas cerraduras administra (<c>LockCount</c>) y los cupos y la
/// ocupación del equipo (usuarios, huellas, tarjetas, registros).
///
/// Diferencias con las otras marcas, que explican por qué este driver es
/// distinto: el equipo <b>no tiene usuario ni contraseña</b> (la credencial es
/// la clave de comunicación), <b>no habla HTTP</b> y <b>acepta una sola
/// conexión del SDK a la vez</b>, así que cada operación abre y cierra su
/// sesión. Los parámetros que el equipo no conoce se responden vacíos: todo se
/// lee de forma tolerante y con respaldo.
/// </summary>
public sealed class ZkTecoAccessDriver : IAccessControlDriver
{
    /// <summary>Tope de puertas que se leen del equipo (la inBio más grande administra 4).</summary>
    private const int MaxDoors = 8;

    /// <summary>
    /// La credencial de ZKTeco es numérica. Vacío = sin clave (0), que es como
    /// vienen de fábrica; cualquier otra cosa es un error de configuración y
    /// conviene decirlo antes de intentar conectarse.
    /// </summary>
    private static int CommKeyOf(AccessConnectionInfo info)
    {
        string value = (info.Password ?? "").Trim();
        if (value.Length == 0) return 0;
        if (!int.TryParse(value, out int key) || key < 0)
            throw new DriverException("La clave de comunicación de ZKTeco es un número (0 si el equipo no tiene).");
        return key;
    }

    public async Task<AccessDeviceInfo> ProbeAsync(AccessConnectionInfo info, CancellationToken ct = default)
    {
        await using var connection = await ZkConnection.OpenAsync(info.Host, info.Port, CommKeyOf(info), ct);

        string? model = await connection.GetParameterAsync("~DeviceName", ct);
        string? serial = await connection.GetParameterAsync("~SerialNumber", ct);
        string? platform = await connection.GetParameterAsync("~Platform", ct);
        string? mac = await connection.GetParameterAsync("MAC", ct);
        string? firmware = await connection.GetFirmwareAsync(ct);

        // Un equipo que contesta el protocolo pero no dice ni cómo se llama ni
        // qué serie tiene no es un terminal de acceso utilizable.
        if (model is null && serial is null)
            throw new DriverException(
                "El equipo respondió el protocolo de ZKTeco pero no entregó su identificación " +
                "(¿es un terminal de control de acceso?).");

        int doorCount = 1;
        if (await connection.GetParameterAsync("LockCount", ct) is { } locks &&
            int.TryParse(locks, out int declared) && declared > 0)
            doorCount = Math.Min(declared, MaxDoors);

        var sizes = await connection.GetSizesAsync(ct);
        bool fingerprint = await connection.GetParameterAsync("~ZKFPVersion", ct) is not null ||
                           (sizes?.FingerCapacity ?? 0) > 0;
        bool face = await connection.GetParameterAsync("FaceFunOn", ct) is { } faceOn &&
                    faceOn.Trim() is not ("0" or "");

        var capabilities = new AccessCapabilities(
            DoorCount: doorCount,
            // La apertura remota es parte del protocolo (comando de desbloqueo);
            // no se prueba aquí porque abriría la puerta.
            SupportsRemoteControl: true,
            SupportsEvents: true,           // el equipo guarda el historial de accesos y lo entrega por el mismo SDK
            SupportsCards: true,
            SupportsFingerprint: fingerprint,
            SupportsFace: face,
            UserCapacity: sizes is { UserCapacity: > 0 } ? sizes.Value.UserCapacity : null,
            // El equipo informa cuántas tarjetas tiene registradas, no su cupo:
            // el cupo de tarjetas es el de personas.
            CardCapacity: sizes is { UserCapacity: > 0 } ? sizes.Value.UserCapacity : null);

        // El terminal autónomo no nombra sus puertas: quedan con el nombre
        // genérico y el operador las renombra en el VMS.
        var doors = Enumerable.Range(1, doorCount)
            .Select(number => new AccessDoorInfo(number, $"Puerta {number}"))
            .ToList();

        var kind = doorCount > 1 ? AccessDeviceKind.Controller : AccessDeviceKind.Terminal;
        return new AccessDeviceInfo(model, serial, firmware, platform, mac, kind, capabilities, doors);
    }

    public async Task PingAsync(AccessConnectionInfo info, CancellationToken ct = default)
    {
        await using var connection = await ZkConnection.OpenAsync(info.Host, info.Port, CommKeyOf(info), ct);
        // Abrir la sesión ya prueba red y credencial; una lectura barata
        // confirma que el equipo responde comandos y no solo el saludo.
        _ = await connection.GetParameterAsync("~SerialNumber", ct);
    }

    // ==================================================================
    // Operación
    // ==================================================================

    /// <summary>Duración del pulso de apertura, en décimas de segundo (el protocolo cuenta así).</summary>
    private const int UnlockTenths = 30;

    /// <summary>
    /// Abre la cerradura con el comando de desbloqueo del SDK. El terminal
    /// autónomo solo sabe dar el pulso: no tiene "cerrar", "mantener abierta"
    /// ni "bloquear", que en esta familia son configuración del equipo.
    /// </summary>
    public async Task ControlDoorAsync(AccessConnectionInfo info, int doorNumber, AccessDoorCommand command,
        CancellationToken ct = default)
    {
        if (command != AccessDoorCommand.Open)
            throw new DriverException(
                "Los equipos ZKTeco solo aceptan la apertura remota: cerrar, mantener abierta y bloquear " +
                "se configuran en el propio equipo.");

        await using var connection = await ZkConnection.OpenAsync(info.Host, info.Port, CommKeyOf(info), ct);
        var payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload, UnlockTenths);
        var reply = await connection.CommandAsync(ZkProtocol.CmdUnlock, payload, ct);
        if (!reply.Ok) throw new DriverException("El equipo rechazó la orden de apertura.");
    }

    /// <summary>Largo de cada renglón del historial que entrega el equipo.</summary>
    private const int LogRecordSize = 40;

    /// <summary>
    /// Historial del equipo. ZKTeco lo entrega COMPLETO —no sabe filtrar por
    /// fecha— como un bloque de renglones de 40 bytes; el filtro por
    /// <paramref name="sinceUtc"/> lo hace el VMS al recibirlo.
    ///
    /// Cada renglón trae el identificador de la persona, cómo se identificó,
    /// la hora del equipo y el número de puerta. Este historial guarda las
    /// PASADAS, no los rechazos: los equipos de esta familia los llevan en un
    /// registro aparte en tiempo real, así que el VMS solo muestra accesos
    /// concedidos y no inventa denegaciones que no vio.
    /// </summary>
    public async Task<IReadOnlyList<AccessEventRecord>> FetchEventsAsync(AccessConnectionInfo info, DateTime sinceUtc,
        int max, CancellationToken ct = default)
    {
        await using var connection = await ZkConnection.OpenAsync(info.Host, info.Port, CommKeyOf(info), ct);
        byte[] data = await connection.ReadBulkAsync(ZkProtocol.CmdAttLogRead, null, ct);
        if (data.Length < LogRecordSize) return [];

        // Algunos firmware anteponen 4 bytes con el tamaño del bloque.
        int offset = data.Length % LogRecordSize == 4 ? 4 : 0;
        var events = new List<AccessEventRecord>();
        for (int at = offset; at + LogRecordSize <= data.Length; at += LogRecordSize)
        {
            var record = data.AsSpan(at, LogRecordSize);
            string employeeNo = System.Text.Encoding.ASCII.GetString(record[2..26]).Split('\0')[0].Trim();
            if (employeeNo.Length == 0) continue;

            uint stamp = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(record[27..31]);
            var local = ZkProtocol.DecodeTime(stamp);
            if (local == DateTime.MinValue) continue;
            // El equipo cuenta en SU hora local, que es la del servidor en una
            // instalación normal; el VMS guarda todo en UTC.
            var timestamp = DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
            if (timestamp <= sinceUtc) continue;

            events.Add(new AccessEventRecord(
                Timestamp: timestamp,
                DoorNumber: record[31] > 0 ? record[31] : 1,
                Kind: AccessEventKind.Granted,
                Credential: record[26] switch
                {
                    0 => AccessCredentialKind.Pin,
                    1 => AccessCredentialKind.Fingerprint,
                    2 => AccessCredentialKind.Card,
                    15 => AccessCredentialKind.Face,
                    _ => AccessCredentialKind.Unknown,
                },
                Description: "Acceso concedido",
                EmployeeNo: employeeNo,
                PersonName: null,
                CardNumber: null,
                MajorType: null,
                MinorType: record[26],
                RawJson: null));
        }
        return events.OrderBy(e => e.Timestamp).TakeLast(max).ToList();
    }
}
