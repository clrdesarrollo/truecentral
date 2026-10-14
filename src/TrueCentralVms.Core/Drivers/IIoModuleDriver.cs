namespace TrueCentralVms.Core.Drivers;

/// <summary>Datos de conexión a un módulo de entradas y salidas digitales.</summary>
public sealed record IoConnectionInfo(string Host, int Port);

/// <summary>
/// Modelo de módulo que conoce un driver: cuántas entradas y salidas tiene.
/// <paramref name="Key"/> es lo que se guarda en la base ("ADAM-6066").
/// </summary>
public sealed record IoModuleModel(string Key, string Name, int Inputs, int Outputs, string Description);

/// <summary>
/// Identificación del módulo al probar la conexión. <paramref name="ReportedModel"/>
/// es lo que dice el propio equipo ("ADAM-6066"); <paramref name="Model"/> es el
/// modelo con que se trabajará (el elegido a mano o, si no, el informado).
/// </summary>
public sealed record IoModuleInfo(string? ReportedModel, IoModuleModel Model);

/// <summary>Foto de las entradas y salidas de un módulo (niveles crudos: true = 1).</summary>
public sealed record IoState(IReadOnlyList<bool> Inputs, IReadOnlyList<bool> Outputs, DateTime TimestampUtc);

public enum IoChannelKind { Input, Output }

/// <summary>Una entrada o salida que cambió entre dos lecturas.</summary>
public sealed record IoChannelChange(IoChannelKind Kind, int Number, bool Value, DateTime TimestampUtc);

/// <summary>
/// Conexión viva con un módulo: lo sondea, avisa los cambios y recibe las
/// órdenes a sus salidas por la MISMA conexión (estos módulos admiten pocas
/// conexiones simultáneas: el servidor debe ser el único que les hable).
/// Los eventos llegan desde el hilo del sondeo, de a uno: el manejador debe
/// encolar y volver.
/// </summary>
public interface IIoModuleSession : IAsyncDisposable
{
    IoModuleModel Model { get; }

    /// <summary>Última lectura buena; null hasta la primera.</summary>
    IoState? LastState { get; }

    /// <summary>null antes del primer intento; después, si la última lectura fue buena.</summary>
    bool? IsOnline { get; }

    /// <summary>Cada lectura distinta a la anterior (y la primera).</summary>
    event Action<IoState>? StateChanged;

    /// <summary>Cada entrada o salida que cambió, una por evento (la primera lectura no genera cambios).</summary>
    event Action<IoChannelChange>? ChannelChanged;

    /// <summary>Comunicación ganada (true, null) o perdida (false, motivo).</summary>
    event Action<bool, string?>? ConnectionChanged;

    /// <summary>
    /// Un pulso o parpadeo en curso no pudo terminar (se cortó la comunicación
    /// antes de soltar la salida): la salida puede haber quedado energizada.
    /// </summary>
    event Action<int, string>? OutputEffectFailed;

    /// <summary>Empieza a sondear.</summary>
    void Start();

    /// <summary>Lee de inmediato sin esperar el próximo ciclo (después de una orden).</summary>
    void RefreshNow();

    /// <summary>Energiza o suelta una salida. Corta su pulso o parpadeo en curso.</summary>
    Task SetOutputAsync(int output, bool on, CancellationToken ct = default);

    /// <summary>Invierte una salida y devuelve su nuevo estado.</summary>
    Task<bool> ToggleOutputAsync(int output, CancellationToken ct = default);

    /// <summary>
    /// Energiza una salida y la suelta sola después de <paramref name="duration"/>
    /// (abrir una puerta, levantar una barrera). Vuelve en cuanto la salida
    /// quedó energizada; un pulso nuevo sobre la misma salida lo reinicia.
    /// </summary>
    Task PulseOutputAsync(int output, TimeSpan duration, CancellationToken ct = default);

    /// <summary>
    /// Hace parpadear una salida cada <paramref name="interval"/> durante
    /// <paramref name="duration"/> y la deja suelta. Con
    /// <see cref="Timeout.InfiniteTimeSpan"/> parpadea hasta que llegue otra
    /// orden sobre la salida. Vuelve en cuanto empezó.
    /// </summary>
    Task BlinkOutputAsync(int output, TimeSpan interval, TimeSpan duration, CancellationToken ct = default);

    /// <summary>Fija todas las salidas de una vez. Corta los pulsos y parpadeos en curso.</summary>
    Task SetAllOutputsAsync(bool on, CancellationToken ct = default);
}

/// <summary>
/// Driver de una familia de módulos de E/S digital. Los métodos lanzan
/// <see cref="DriverException"/> con mensaje en español cuando el módulo es
/// inalcanzable o rechaza la petición.
/// </summary>
public interface IIoModuleDriver
{
    /// <summary>Modelos que conoce el driver, para elegirlo a mano.</summary>
    IReadOnlyList<IoModuleModel> Models { get; }

    /// <summary>Período mínimo de parpadeo que admite (más rápido castiga los contactos).</summary>
    TimeSpan MinBlinkInterval { get; }

    /// <summary>
    /// Conecta, pregunta el modelo y desconecta. Con <paramref name="modelKey"/>
    /// se trabaja con ese modelo aunque el equipo informe otro (el informado
    /// vuelve en <see cref="IoModuleInfo.ReportedModel"/> para avisar la diferencia).
    /// </summary>
    Task<IoModuleInfo> ProbeAsync(IoConnectionInfo info, string? modelKey, CancellationToken ct = default);

    /// <summary>Prepara la conexión viva (no conecta hasta <see cref="IIoModuleSession.Start"/>).</summary>
    IIoModuleSession Open(IoConnectionInfo info, IoModuleModel model, TimeSpan pollInterval);
}

public interface IIoModuleDriverFactory
{
    /// <summary>Clave estable ("adam-6000").</summary>
    string DriverKey { get; }

    string DisplayName { get; }

    int DefaultPort { get; }

    IIoModuleDriver Create();
}

/// <summary>Registro de drivers de módulos de E/S, poblado por inyección de dependencias.</summary>
public sealed class IoModuleDriverRegistry
{
    private readonly Dictionary<string, IIoModuleDriverFactory> _factories;

    public IoModuleDriverRegistry(IEnumerable<IIoModuleDriverFactory> factories)
    {
        _factories = factories.ToDictionary(f => f.DriverKey, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IIoModuleDriverFactory> All => _factories.Values;

    public IIoModuleDriverFactory? Find(string driverKey) =>
        _factories.TryGetValue(driverKey, out var factory) ? factory : null;
}
