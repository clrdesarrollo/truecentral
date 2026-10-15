using System.Diagnostics;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Adam;

/// <summary>
/// Un módulo ADAM-6000 de E/S digital (6050, 6051, 6052, 6055, 6060, 6066) sobre
/// Modbus TCP: lee entradas y salidas, y maneja las salidas (fijar, alternar,
/// pulso y parpadeo). Se puede usar desde varios hilos.
/// </summary>
/// <remarks>
/// <para>Mapa Modbus (igual en toda la familia): entrada DI n en la bobina
/// 00001+n, salida n en la bobina 00017+n, número de modelo en el registro 40211.</para>
/// <para>El pulso y el parpadeo los temporiza este proceso y corren solos: la
/// orden vuelve en cuanto la salida quedó energizada. Cualquier orden nueva
/// sobre una salida reemplaza el pulso o parpadeo que tuviera en curso; al
/// cerrar el módulo, los que queden en curso dejan la salida suelta. Si el
/// proceso muere de golpe, la salida queda como esté: para cerraduras y
/// barreras hay que configurar el watchdog de comunicación del propio ADAM.</para>
/// </remarks>
public sealed class AdamModule : IAsyncDisposable
{
    /// <summary>Bobina 00001: primera entrada digital.</summary>
    public const ushort InputsAddress = 0;

    /// <summary>Bobina 00017: primera salida.</summary>
    public const ushort OutputsAddress = 16;

    /// <summary>Registro 40211: número de modelo en BCD (0x6066).</summary>
    public const ushort ModuleIdRegister = 210;

    /// <summary>Período mínimo de parpadeo: más rápido castiga los contactos del relé.</summary>
    public static readonly TimeSpan MinBlinkInterval = TimeSpan.FromMilliseconds(100);

    private readonly ModbusTcpClient _modbus;
    private readonly OutputEffect?[] _effects;

    /// <summary>
    /// Prepara el módulo sin conectar (la conexión se abre con la primera
    /// petición): el servidor da de alta equipos aunque estén apagados.
    /// </summary>
    public AdamModule(string host, int port, IoModuleModel model)
        : this(new ModbusTcpClient(host, port), model, reportedModel: null)
    {
    }

    private AdamModule(ModbusTcpClient modbus, IoModuleModel model, string? reportedModel)
    {
        _modbus = modbus;
        Model = model;
        ReportedModel = reportedModel;
        _effects = new OutputEffect?[model.Outputs];
    }

    public string Host => _modbus.Host;
    public int Port => _modbus.Port;

    /// <summary>Modelo con el que se trabaja (fija cuántas entradas y salidas se leen).</summary>
    public IoModuleModel Model { get; }

    /// <summary>Modelo que informó el propio equipo al conectar ("ADAM-6066"); null si no se consultó o no lo informa.</summary>
    public string? ReportedModel { get; }

    /// <summary>
    /// Un pulso o parpadeo no pudo terminar porque se cortó la comunicación:
    /// la salida puede haber quedado energizada (número de salida, motivo).
    /// </summary>
    public event Action<int, string>? EffectFailed;

    /// <summary>
    /// Conecta con el módulo y pregunta su modelo. Sin <paramref name="modelKey"/>
    /// se usa el que informa el equipo; con él, manda el indicado (y
    /// <see cref="ReportedModel"/> sirve para avisar si no coincide).
    /// </summary>
    public static async Task<AdamModule> ConnectAsync(string host, int port = ModbusTcpClient.DefaultPort,
        string? modelKey = null, CancellationToken ct = default)
    {
        IoModuleModel? forced = null;
        if (!string.IsNullOrWhiteSpace(modelKey))
            forced = AdamModels.Find(modelKey) ?? throw new DriverException($"Modelo de módulo desconocido: '{modelKey}'.");

        var modbus = new ModbusTcpClient(host, port);
        try
        {
            await modbus.ConnectAsync(ct);
            ushort? moduleId = await ReadModuleIdAsync(modbus, ct);
            string? reported = moduleId is { } id ? $"ADAM-{id:X4}" : null;
            var effective = forced
                ?? (moduleId is { } known ? AdamModels.FromModuleId(known) : null)
                ?? throw new DriverException(reported is null
                    ? "El módulo no informa su modelo. Elíjalo a mano."
                    : $"El equipo se identifica como {reported}, que no es un módulo de E/S digital conocido. Elija el modelo a mano.");
            return new AdamModule(modbus, effective, reported);
        }
        catch
        {
            await modbus.DisposeAsync();
            throw;
        }
    }

    private static async Task<ushort?> ReadModuleIdAsync(ModbusTcpClient modbus, CancellationToken ct)
    {
        try
        {
            var registers = await modbus.ReadHoldingRegistersAsync(ModuleIdRegister, 1, ct);
            return registers[0] != 0 ? registers[0] : null;
        }
        catch (ModbusException)
        {
            // Firmware que no expone el registro: el modelo se elige a mano.
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Lectura
    // ------------------------------------------------------------------

    /// <summary>Lee entradas y salidas.</summary>
    public async Task<IoState> ReadStateAsync(CancellationToken ct = default)
    {
        var inputs = await ReadInputsAsync(ct);
        var outputs = await ReadOutputsAsync(ct);
        return new IoState(inputs, outputs, DateTime.UtcNow);
    }

    /// <summary>
    /// Estado de las entradas. En contacto seco el ADAM lee 1 con el contacto
    /// abierto y 0 con el contacto cerrado a GND.
    /// </summary>
    public Task<bool[]> ReadInputsAsync(CancellationToken ct = default) =>
        Model.Inputs == 0
            ? Task.FromResult(Array.Empty<bool>())
            : _modbus.ReadCoilsAsync(InputsAddress, (ushort)Model.Inputs, ct);

    /// <summary>Estado de las salidas (true = energizada).</summary>
    public Task<bool[]> ReadOutputsAsync(CancellationToken ct = default) =>
        Model.Outputs == 0
            ? Task.FromResult(Array.Empty<bool>())
            : _modbus.ReadCoilsAsync(OutputsAddress, (ushort)Model.Outputs, ct);

    // ------------------------------------------------------------------
    // Salidas
    // ------------------------------------------------------------------

    /// <summary>Energiza (<paramref name="on"/> = true) o suelta una salida. Corta su pulso o parpadeo en curso.</summary>
    public Task SetOutputAsync(int output, bool on, CancellationToken ct = default)
    {
        CheckOutput(output);
        StopEffect(output);
        return _modbus.WriteSingleCoilAsync(Coil(output), on, ct);
    }

    /// <summary>Fija todas las salidas de una vez. Corta los pulsos y parpadeos en curso.</summary>
    public Task SetAllOutputsAsync(bool on, CancellationToken ct = default)
    {
        if (Model.Outputs == 0) return Task.CompletedTask;
        for (int output = 0; output < Model.Outputs; output++)
            StopEffect(output);
        return _modbus.WriteMultipleCoilsAsync(OutputsAddress, Enumerable.Repeat(on, Model.Outputs).ToArray(), ct);
    }

    /// <summary>Invierte una salida y devuelve su nuevo estado.</summary>
    public async Task<bool> ToggleOutputAsync(int output, CancellationToken ct = default)
    {
        CheckOutput(output);
        StopEffect(output);
        var current = await _modbus.ReadCoilsAsync(Coil(output), 1, ct);
        bool next = !current[0];
        await _modbus.WriteSingleCoilAsync(Coil(output), next, ct);
        return next;
    }

    /// <summary>
    /// Energiza una salida durante <paramref name="duration"/> y la suelta
    /// (abrir una puerta, levantar una barrera). Vuelve en cuanto la salida
    /// quedó energizada; soltarla corre aparte. Pedir otro pulso sobre la misma
    /// salida antes de que termine lo reinicia.
    /// </summary>
    public Task PulseOutputAsync(int output, TimeSpan duration, CancellationToken ct = default)
    {
        CheckOutput(output);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        return StartEffectAsync(output, finalState: false, ct, async (token, started) =>
        {
            await _modbus.WriteSingleCoilAsync(Coil(output), true, token);
            started();
            await Task.Delay(duration, token);
            await _modbus.WriteSingleCoilAsync(Coil(output), false, token);
        });
    }

    /// <summary>
    /// Hace parpadear una salida (baliza, semáforo): la alterna cada
    /// <paramref name="interval"/> durante <paramref name="duration"/> y la deja
    /// suelta. Con <see cref="Timeout.InfiniteTimeSpan"/> parpadea hasta que otra
    /// orden sobre la salida lo corte. Vuelve en cuanto empezó.
    /// </summary>
    public Task BlinkOutputAsync(int output, TimeSpan interval, TimeSpan duration, CancellationToken ct = default)
    {
        CheckOutput(output);
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, MinBlinkInterval);
        if (duration != Timeout.InfiniteTimeSpan)
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        return StartEffectAsync(output, finalState: false, ct, async (token, started) =>
        {
            var clock = Stopwatch.StartNew();
            bool on = true;
            while (duration == Timeout.InfiniteTimeSpan || clock.Elapsed < duration)
            {
                await _modbus.WriteSingleCoilAsync(Coil(output), on, token);
                started();
                await Task.Delay(interval, token);
                on = !on;
            }
            await _modbus.WriteSingleCoilAsync(Coil(output), false, token);
        });
    }

    /// <summary>
    /// Arranca un pulso o parpadeo como dueño de la salida y vuelve cuando el
    /// efecto avisa que empezó (la primera escritura llegó al módulo). El resto
    /// corre aparte hasta terminar o hasta que otra orden lo reemplace. Las
    /// escrituras llevan el token del efecto: si otra orden lo reemplaza
    /// mientras una escritura espera turno, esa escritura se descarta y la
    /// orden nueva queda como última palabra sobre la salida.
    /// </summary>
    private async Task StartEffectAsync(int output, bool finalState, CancellationToken ct,
        Func<CancellationToken, Action, Task> body)
    {
        var effect = new OutputEffect(new CancellationTokenSource(), finalState);
        OutputEffect? previous;
        lock (_effects)
        {
            previous = _effects[output];
            _effects[output] = effect;
        }
        previous?.Stop.Cancel();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                await body(effect.Stop.Token, () => started.TrySetResult());
            }
            catch (Exception) when (effect.Stop.IsCancellationRequested)
            {
                // Lo reemplazó otra orden sobre la misma salida, o se cerró el
                // módulo (que deja la salida en su estado final).
                started.TrySetException(new DriverException("Otra orden sobre la salida reemplazó a esta antes de empezar."));
            }
            catch (Exception ex)
            {
                // Antes de energizar: el error es la respuesta a la orden. Después:
                // la salida pudo quedar energizada y nadie está esperando.
                if (!started.TrySetException(ex))
                    EffectFailed?.Invoke(output, ex.Message);
            }
            finally
            {
                lock (_effects)
                {
                    if (_effects[output] == effect) _effects[output] = null;
                }
            }
        }, CancellationToken.None);

        try
        {
            await started.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Lo canceló quien lo pidió: la salida no queda a medias.
            bool mine;
            lock (_effects)
            {
                mine = _effects[output] == effect;
                if (mine) _effects[output] = null;
            }
            effect.Stop.Cancel();
            if (mine)
            {
                try { await _modbus.WriteSingleCoilAsync(Coil(output), finalState, CancellationToken.None); }
                catch (DriverException) { /* sin comunicación: lo resuelve el watchdog del módulo */ }
            }
            throw;
        }
    }

    private void StopEffect(int output)
    {
        OutputEffect? effect;
        lock (_effects)
        {
            effect = _effects[output];
            _effects[output] = null;
        }
        effect?.Stop.Cancel();
    }

    private void CheckOutput(int output)
    {
        if (output < 0 || output >= Model.Outputs)
            throw new DriverException(Model.Outputs == 0
                ? $"El {Model.Name} no tiene salidas."
                : $"El {Model.Name} tiene salidas 0 a {Model.Outputs - 1}.");
    }

    private static ushort Coil(int output) => (ushort)(OutputsAddress + output);

    /// <summary>
    /// Cierra la conexión. Los pulsos y parpadeos en curso se cortan dejando
    /// cada salida en su estado final (un pulso termina con la salida suelta).
    /// Las salidas fijadas con <see cref="SetOutputAsync"/> quedan como están.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var pending = new List<(int Output, bool FinalState)>();
        lock (_effects)
        {
            for (int output = 0; output < _effects.Length; output++)
            {
                if (_effects[output] is not { } effect) continue;
                effect.Stop.Cancel();
                _effects[output] = null;
                pending.Add((output, effect.FinalState));
            }
        }

        foreach (var (output, finalState) in pending)
        {
            try
            {
                await _modbus.WriteSingleCoilAsync(Coil(output), finalState);
            }
            catch (DriverException)
            {
                // Sin comunicación no hay cómo soltarla; el watchdog del módulo
                // (si está configurado) aplica sus valores de seguridad.
            }
        }

        await _modbus.DisposeAsync();
    }

    /// <summary>Pulso o parpadeo en curso sobre una salida.</summary>
    private sealed record OutputEffect(CancellationTokenSource Stop, bool FinalState);
}
