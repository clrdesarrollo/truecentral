using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Isup;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services.Isup;

/// <summary>
/// Reglas comunes de los equipos que se conectan por ISUP, para que todos los módulos (video, acceso, alarmas, parlantes,
/// citofonía) las apliquen igual: validar el ID y la clave del formulario, impedir que un mismo ID quede con dos claves en módulos
/// distintos y explicar qué configurar en el equipo cuando todavía no se conectó.
/// </summary>
public static class IsupEquipmentRules
{
    /// <summary>Tipos de equipo (los mismos de <see cref="Core.Contracts.IsupEquipmentRefDto.Kind"/>).</summary>
    public const string Video = "video", Access = "access", Alarm = "alarm", Speaker = "speaker", Intercom = "intercom";

    public static string KindLabel(string kind) => kind switch
    {
        Video => "equipo de video",
        Access => "equipo de control de acceso",
        Alarm => "panel de alarma",
        Speaker => "parlante",
        Intercom => "frente de citofonía",
        _ => "equipo",
    };

    /// <summary>
    /// Valida ID y clave del formulario. La clave puede quedar vacía: en edición conserva la guardada, y los equipos ISUP 2.0/4.0
    /// (grabadores móviles, por ejemplo) se registran sin clave. Un equipo 5.0 sin clave en el VMS es rechazado al conectarse, y el
    /// motivo («la clave no coincide») queda a la vista en su formulario y en Sistema → Conexión ISUP.
    /// </summary>
    public static string? Validate(string? isupId, string? key, bool keyStored)
    {
        if (IsupGateway.ValidateDeviceId(isupId) is { } idError) return idError;
        return string.IsNullOrEmpty(key) ? null : IsupGateway.ValidateKey(key);
    }

    /// <summary>Clave con que queda el equipo: la escrita en el formulario, o la que ya tenía (vacía = sin clave).</summary>
    public static string EffectiveKey(string? written, byte[]? stored, CredentialProtector protector) =>
        !string.IsNullOrEmpty(written) ? written : stored is { Length: > 0 } ? protector.Unprotect(stored) : "";

    /// <summary>La clave cifrada para guardarla; null si el equipo no usa clave.</summary>
    public static byte[]? ProtectKey(string? key, CredentialProtector protector) =>
        string.IsNullOrEmpty(key) ? null : protector.Protect(key);

    /// <summary>
    /// ¿Otro equipo del VMS usa ese ID ISUP con OTRA clave? Un mismo equipo físico puede estar en dos módulos (un frente de citofonía
    /// que además es terminal de acceso) y entonces comparte el registro: la clave tiene que ser la misma en ambos.
    /// </summary>
    public static async Task<string?> KeyConflictAsync(VmsDbContext db, CredentialProtector protector, string isupId, string? key,
        string selfKind, int? selfId, CancellationToken ct)
    {
        foreach (var (kind, eq) in await UsersOfAsync(db, isupId, ct))
        {
            if (kind == selfKind && eq.Id == selfId) continue;
            string other;
            try { other = eq.IsupKeyCiphertext is { Length: > 0 } cipher ? protector.Unprotect(cipher) : ""; }
            catch (Exception) { continue; }
            if (other != (key ?? ""))
                return $"El ID ISUP {isupId} ya lo usa «{eq.Name}» ({KindLabel(kind)}) con otra clave. Un mismo equipo debe tener la misma " +
                       "clave ISUP en todos los módulos donde esté agregado.";
        }
        return null;
    }

    /// <summary>Equipos del VMS (de cualquier módulo) que usan un ID ISUP.</summary>
    public static async Task<List<(string Kind, IIsupEquipment Equipment)>> UsersOfAsync(VmsDbContext db, string isupId, CancellationToken ct)
    {
        var list = new List<(string, IIsupEquipment)>();
        list.AddRange((await db.AlarmPanels.AsNoTracking().Where(x => x.IsupDeviceId == isupId).ToListAsync(ct)).Select(x => (Alarm, (IIsupEquipment)x)));
        list.AddRange((await db.AccessDevices.AsNoTracking().Where(x => x.IsupDeviceId == isupId).ToListAsync(ct)).Select(x => (Access, (IIsupEquipment)x)));
        list.AddRange((await db.Devices.AsNoTracking().Where(x => x.IsupDeviceId == isupId).ToListAsync(ct)).Select(x => (Video, (IIsupEquipment)x)));
        list.AddRange((await db.Speakers.AsNoTracking().Where(x => x.IsupDeviceId == isupId).ToListAsync(ct)).Select(x => (Speaker, (IIsupEquipment)x)));
        list.AddRange((await db.Intercoms.AsNoTracking().Where(x => x.IsupDeviceId == isupId).ToListAsync(ct)).Select(x => (Intercom, (IIsupEquipment)x)));
        return list;
    }

    /// <summary>
    /// Por qué un equipo ISUP no está conectado y qué poner en él: si ya intentó registrarse, el motivo del rechazo; si no, los datos
    /// de la plataforma ISUP que hay que escribir en el equipo.
    /// </summary>
    public static async Task<string> NotConnectedMessageAsync(IsupService isup, IsupHostManager host, string isupId, string noun, CancellationToken ct)
    {
        if (!host.Enabled) return "El servicio ISUP está deshabilitado en este servidor (Isup:Enabled = false).";
        var status = await isup.GetHostStatusAsync(ct);
        if (status is null)
        {
            // Sin respuesta no siempre es «detenido»: tras un reinicio el host tarda unos segundos en abrir su API, y una consulta
            // puede no responder a tiempo. Solo se dice que no está en ejecución cuando el proceso de verdad no está.
            if (!host.IsRunning)
                return host.RestartPending
                    ? $"El servicio ISUP del servidor se está reiniciando; el {noun} se conectará solo en cuanto vuelva."
                    : "El servicio ISUP del servidor no está en ejecución; revise Sistema → Servicios.";
            if (!isup.Connected)
                return $"El servicio ISUP del servidor se está iniciando; el {noun} se conectará solo en cuanto el servicio esté listo.";
            status = isup.LastHostStatus;
            if (status is null)
                return "El servicio ISUP del servidor no respondió a tiempo al consultar su estado; se vuelve a intentar en un momento.";
        }
        int port = status.Listeners.FirstOrDefault(l => l.Name == "Registro")?.Port ?? host.Options.RegistrationPorts.FirstOrDefault();
        string setup = $"En el {noun}: plataforma ISUP 5.0 con servidor {ServerAddressHint(status, noun)}, puerto {port}, ID «{isupId}» y la misma clave.";
        // Quien llama aquí ya tiene el equipo en el VMS: un rechazo por «no dado de alta» es de antes de agregarlo (el directorio
        // se entrega en un instante y el equipo reintenta solo), así que no se muestra.
        var rejection = status.Rejections.FirstOrDefault(r => r.DeviceId == isupId && !r.Reason.Contains("no está dado de alta", StringComparison.Ordinal));
        if (rejection is not null)
            return $"El {noun} intentó conectarse y fue rechazado: {rejection.Reason}. {setup}";
        return $"El {noun} todavía no se conectó al servidor por ISUP. {setup} El {noun} tiene que llegar a esa dirección " +
               "(misma red, VPN o puerto publicado en internet).";
    }

    /// <summary>
    /// La dirección que hay que escribir en el equipo: la fija, o sin ella la IP de este servidor en la red del equipo (con varias redes
    /// no se sabe cuál es antes de que se conecte, así que se nombran todas).
    /// </summary>
    public static string ServerAddressHint(IsupHostStatus status, string noun)
    {
        if (!status.PerDeviceAddress) return status.PublicAddress;
        var locals = status.LocalAddresses ?? [];
        return locals.Count switch
        {
            0 => status.PublicAddress,
            1 => locals[0],
            _ => $"la IP de este servidor en la red del {noun} ({string.Join(" o ", locals)})",
        };
    }
}
