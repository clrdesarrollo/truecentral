using System.Collections.Concurrent;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Movimiento PTZ por velocidades (el joystick del panel web y del cliente):
/// pan, tilt y zoom a la vez. Prueba primero la orden nativa del driver
/// (ContinuousMove de ONVIF, ISAPI continuous de Hikvision, MOVE_CONTINUOUSLY
/// de Dahua); si el equipo no la soporta la traduce a las órdenes de
/// PtzControlAsync —dirección y zoom por separado, velocidad 1..7— y recuerda
/// qué dejó andando en cada canal para detener lo que cambie.
///
/// Hombre muerto: el cliente renueva el movimiento al menos cada segundo; si
/// pasan 3 s sin noticias (navegador cerrado, red caída) la cámara se detiene.
/// Las órdenes a los equipos no se cancelan con el pedido HTTP: un "detener" a
/// medio camino dejaría la cámara girando.
/// </summary>
public sealed class PtzMover(ILogger<PtzMover> logger)
{
    private static readonly TimeSpan DeadMan = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NativeRetry = TimeSpan.FromMinutes(10);

    private static readonly PtzCommand[] Directions =
    [
        PtzCommand.PanRight, PtzCommand.UpRight, PtzCommand.TiltUp, PtzCommand.UpLeft,
        PtzCommand.PanLeft, PtzCommand.DownLeft, PtzCommand.TiltDown, PtzCommand.DownRight,
    ];

    private sealed class ChannelState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public DateTime NoNativeUntil;               // el equipo no aceptó la orden nativa (se reintenta a los 10 min)
        public bool Moving;
        public DateTime LastAt;
        public (PtzCommand Command, int Speed)? Direction; // órdenes discretas en curso (traducción)
        public (PtzCommand Command, int Speed)? Zoom;
        public Func<Task>? Stop;                     // detiene con el driver y la conexión de la última orden
        public Timer? Watch;
    }

    private readonly ConcurrentDictionary<string, ChannelState> _channels = new();

    /// <summary>Aplica las velocidades (−1..1; las tres en 0 detienen). false si el equipo las rechazó.</summary>
    public async Task<bool> MoveAsync(int deviceId, int channelNumber, IDeviceDriver driver, DeviceConnectionInfo conn,
        double pan, double tilt, double zoom, CancellationToken ct)
    {
        string key = $"{deviceId}/{channelNumber}";
        var state = _channels.GetOrAdd(key, _ => new ChannelState());
        await state.Gate.WaitAsync(ct);
        try
        {
            bool ok = await ApplyAsync(key, state, driver, conn, channelNumber, pan, tilt, zoom);
            state.Moving = ok && (pan != 0 || tilt != 0 || zoom != 0);
            state.LastAt = DateTime.UtcNow;
            state.Stop = () => ApplyAsync(key, state, driver, conn, channelNumber, 0, 0, 0);
            if (state.Moving)
                state.Watch ??= new Timer(_ => _ = WatchAsync(key, state), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            else
                StopWatch(state);
            return ok;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task<bool> ApplyAsync(string key, ChannelState state, IDeviceDriver driver, DeviceConnectionInfo conn,
        int channelNumber, double pan, double tilt, double zoom)
    {
        if (DateTime.UtcNow >= state.NoNativeUntil)
        {
            if (await driver.PtzMoveAsync(conn, channelNumber, pan, tilt, zoom, CancellationToken.None))
            {
                state.Direction = state.Zoom = null;
                return true;
            }
            state.NoNativeUntil = DateTime.UtcNow + NativeRetry;
            logger.LogInformation("PTZ: el canal {Key} no acepta movimiento por velocidades; se traduce a órdenes de dirección y zoom.", key);
        }

        var direction = DirectionOf(pan, tilt);
        (PtzCommand, int)? zoomCommand = zoom == 0 ? null : (zoom > 0 ? PtzCommand.ZoomIn : PtzCommand.ZoomOut, SpeedOf(Math.Abs(zoom)));
        bool ok = await ChangeAsync(state.Direction, direction);
        state.Direction = direction;
        ok &= await ChangeAsync(state.Zoom, zoomCommand);
        state.Zoom = zoomCommand;
        return ok;

        // Detiene lo que estaba andando si cambia de orden; una misma orden con otra velocidad se vuelve a enviar.
        async Task<bool> ChangeAsync((PtzCommand Command, int Speed)? from, (PtzCommand Command, int Speed)? to)
        {
            if (from == to) return true;
            bool ok = true;
            if (from is { } f && (to is null || to.Value.Command != f.Command))
                ok &= await driver.PtzControlAsync(conn, channelNumber, f.Command, f.Speed, stop: true, CancellationToken.None);
            if (to is { } t)
                ok &= await driver.PtzControlAsync(conn, channelNumber, t.Command, t.Speed, stop: false, CancellationToken.None);
            return ok;
        }
    }

    /// <summary>Dirección de 8 sectores (tilt positivo = arriba) y velocidad 1..7 por la inclinación.</summary>
    private static (PtzCommand Command, int Speed)? DirectionOf(double pan, double tilt)
    {
        double magnitude = Math.Min(1, Math.Sqrt(pan * pan + tilt * tilt));
        if (magnitude < 0.01) return null;
        int sector = (int)Math.Round(Math.Atan2(tilt, pan) / (Math.PI / 4));
        return (Directions[(sector + 8) % 8], SpeedOf(magnitude));
    }

    private static int SpeedOf(double amount) => Math.Clamp((int)Math.Ceiling(amount * 7), 1, 7);

    private async Task WatchAsync(string key, ChannelState state)
    {
        if (!state.Moving || DateTime.UtcNow - state.LastAt < DeadMan) return;
        if (!await state.Gate.WaitAsync(0)) return; // hay una orden en curso: se revisa en el próximo tic
        try
        {
            if (!state.Moving || DateTime.UtcNow - state.LastAt < DeadMan || state.Stop is not { } stop) return;
            logger.LogWarning("PTZ: el canal {Key} dejó de recibir el movimiento del cliente por {Seconds} s: se detiene.",
                key, DeadMan.TotalSeconds);
            state.Moving = false;
            StopWatch(state);
            await stop();
        }
        catch (Exception ex)
        {
            logger.LogWarning("PTZ: no se pudo detener el canal {Key}: {Message}", key, ex.Message);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static void StopWatch(ChannelState state)
    {
        state.Watch?.Dispose();
        state.Watch = null;
    }
}
