using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Cámara propia de los terminales de acceso (faciales): el VMS la agrega como
/// un equipo de video más (<see cref="Device.AccessDeviceId"/>), así sirve en
/// vivo, en los tableros, en la grabación y como cámara de sus puertas. Viene
/// incluida con el terminal: no ocupa canal de video de la licencia.
///
/// El terminal manda: dirección, credenciales, nombre y ubicación de la cámara
/// son los suyos y se copian cada vez que se guarda o se revalida (nunca hay
/// una clave vieja en la cámara que bloquee al terminal por intentos fallidos).
/// Se borra con él.
/// </summary>
public static class AccessTerminalCameras
{
    /// <summary>Driver de video con el que se ve la cámara de cada driver de acceso.</summary>
    private static readonly Dictionary<string, string> VideoDriverOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hikvision-isapi"] = "hikvision-netsdk",
    };

    /// <summary>Puerto de gestión del SDK de Hikvision (el de fábrica de los terminales).</summary>
    private const int SdkPort = 8000;

    /// <summary>¿Se puede ver la cámara de un equipo con este driver?</summary>
    public static bool Supports(string driverKey) => VideoDriverOf.ContainsKey(driverKey);

    /// <summary>
    /// Deja la cámara del terminal como debe estar: la crea, la pone al día o
    /// la quita. Devuelve el cambio para la bitácora ("agregó su cámara…"), o
    /// null si no hubo alta ni baja (la puesta al día no se anuncia: va con el
    /// cambio del terminal). No guarda: el llamador hace SaveChanges y, si
    /// <see cref="DbContext.ChangeTracker"/> tiene cambios, refresca MediaMTX.
    /// El terminal tiene que tener Id (ya guardado).
    /// </summary>
    public static async Task<string?> SyncAsync(VmsDbContext db, AccessDevice terminal, CancellationToken ct)
    {
        var camera = await db.Devices.Include(d => d.Channels).FirstOrDefaultAsync(d => d.AccessDeviceId == terminal.Id, ct);
        bool hasCamera = terminal.CameraRtspPort is not null;

        if (!hasCamera || !terminal.UseCamera || terminal.IsupDeviceId is not null || !Supports(terminal.DriverKey))
        {
            terminal.CameraNote = hasCamera && terminal.UseCamera
                ? terminal.IsupDeviceId is not null
                    ? "Conectado por ISUP: su cámara todavía no se puede ver por esa vía."
                    : "Su marca todavía no permite ver la cámara."
                : null;
            if (camera is null) return null;
            db.Devices.Remove(camera);
            return $"quitó su cámara \"{camera.Name}\"";
        }

        if (camera is null)
        {
            // Si alguien ya la agregó a mano en Fuentes de video, se usa esa (no se duplica el video).
            // Por puerto, no solo por dirección: tras un NAT varios equipos comparten la IP pública.
            int rtspPort = terminal.CameraRtspPort!.Value;
            var manual = await db.Devices.AsNoTracking()
                .FirstOrDefaultAsync(d => d.AccessDeviceId == null && d.IsupDeviceId == null && d.Host == terminal.Host &&
                                          (d.SdkPort == SdkPort || d.RtspPort == rtspPort), ct);
            if (manual is not null)
            {
                terminal.CameraNote = $"Ya está en Fuentes de video como \"{manual.Name}\": se usa esa " +
                                      "(asóciela a la puerta en su ficha de Recursos).";
                return null;
            }
            camera = new Device { AccessDeviceId = terminal.Id, DeviceType = DeviceType.Camera, SdkPort = SdkPort };
            Apply(camera, terminal);
            camera.Channels.Add(new Channel
            {
                ChannelNumber = 1,
                RtspChannel = 1,
                Name = terminal.Name,
                Enabled = true,
                IsOnline = true,
                LocationId = terminal.LocationId,
            });
            db.Devices.Add(camera);
            terminal.CameraNote = null;
            return $"agregó su cámara como \"{terminal.Name}\" (incluida: no ocupa canal de video de la licencia)";
        }

        // Al día con el terminal. El canal conserva el nombre que le hayan
        // puesto; si seguía con el del terminal, lo sigue.
        if (camera.Name != terminal.Name)
            foreach (var channel in camera.Channels.Where(c => c.Name == camera.Name))
                channel.Name = terminal.Name;
        if (camera.LocationId != terminal.LocationId)
            EquipmentLocation.Follow(camera.Channels, camera.LocationId, terminal.LocationId);
        Apply(camera, terminal);
        terminal.CameraNote = null;
        return null;
    }

    private static void Apply(Device camera, AccessDevice terminal)
    {
        void Set<T>(T current, T value, Action<T> assign)
        {
            if (!EqualityComparer<T>.Default.Equals(current, value)) assign(value);
        }
        Set(camera.Name, terminal.Name, v => camera.Name = v);
        Set(camera.DriverKey, VideoDriverOf[terminal.DriverKey], v => camera.DriverKey = v);
        Set(camera.Host, terminal.Host, v => camera.Host = v);
        Set(camera.RtspPort, terminal.CameraRtspPort ?? 554, v => camera.RtspPort = v);
        Set(camera.Username, terminal.Username, v => camera.Username = v);
        if (!camera.PasswordCiphertext.AsSpan().SequenceEqual(terminal.PasswordCiphertext))
            camera.PasswordCiphertext = terminal.PasswordCiphertext.ToArray();
        Set(camera.LocationId, terminal.LocationId, v => camera.LocationId = v);
        Set(camera.Model, terminal.Model, v => camera.Model = v);
        Set(camera.SerialNumber, terminal.SerialNumber, v => camera.SerialNumber = v);
        Set(camera.FirmwareVersion, terminal.FirmwareVersion, v => camera.FirmwareVersion = v);
    }
}
