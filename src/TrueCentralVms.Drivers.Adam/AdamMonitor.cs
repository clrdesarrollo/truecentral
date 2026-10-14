using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Adam;

/// <summary>
/// Sondea un módulo cada <see cref="PollInterval"/> y avisa cuando cambia una
/// entrada o una salida, o cuando se pierde o recupera la comunicación. Si el
/// módulo deja de responder, reintenta con espera creciente hasta 10 s.
/// </summary>
/// <remarks>
/// Los eventos llegan desde el hilo del monitor, nunca en paralelo entre sí:
/// el manejador debe encolar y volver. No cierra el módulo: quien lo creó lo
/// cierra después de cerrar el monitor.
/// </remarks>
public sealed class AdamMonitor(AdamModule module, TimeSpan pollInterval) : IAsyncDisposable
{
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public AdamModule Module { get; } = module;

    public TimeSpan PollInterval { get; } = pollInterval > TimeSpan.Zero
        ? pollInterval
        : throw new ArgumentOutOfRangeException(nameof(pollInterval), "El intervalo de sondeo debe ser positivo.");

    /// <summary>Última lectura buena; null hasta la primera.</summary>
    public IoState? LastState { get; private set; }

    /// <summary>null mientras no hubo un primer intento; después, si la última lectura fue buena.</summary>
    public bool? IsOnline { get; private set; }

    /// <summary>Cada lectura que trae algo distinto a la anterior (y la primera de todas).</summary>
    public event Action<IoState>? StateChanged;

    /// <summary>Cada canal que cambió, uno por evento. La primera lectura no genera cambios.</summary>
    public event Action<IoChannelChange>? ChannelChanged;

    /// <summary>Comunicación ganada (true, null) o perdida (false, motivo).</summary>
    public event Action<bool, string?>? ConnectionChanged;

    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("El monitor ya está corriendo.");
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Lee de inmediato sin esperar el próximo ciclo (por ejemplo, justo después de mover una salida).</summary>
    public void RefreshNow()
    {
        try
        {
            if (_wake.CurrentCount == 0) _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Ya había un aviso pendiente.
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var retryDelay = MinRetryDelay;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                var state = await Module.ReadStateAsync(ct);
                SetOnline(true, null);
                Publish(state);
                retryDelay = MinRetryDelay;
                wait = PollInterval;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (DriverException ex)
            {
                SetOnline(false, ex.Message);
                wait = retryDelay;
                retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
            }

            try
            {
                await _wake.WaitAsync(wait, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void SetOnline(bool online, string? error)
    {
        if (IsOnline == online) return;
        IsOnline = online;
        ConnectionChanged?.Invoke(online, error);
    }

    private void Publish(IoState state)
    {
        var previous = LastState;
        LastState = state;
        if (previous is null)
        {
            StateChanged?.Invoke(state);
            return;
        }

        var changes = new List<IoChannelChange>();
        Compare(IoChannelKind.Input, previous.Inputs, state.Inputs);
        Compare(IoChannelKind.Output, previous.Outputs, state.Outputs);
        if (changes.Count == 0) return;

        StateChanged?.Invoke(state);
        foreach (var change in changes)
            ChannelChanged?.Invoke(change);

        void Compare(IoChannelKind kind, IReadOnlyList<bool> before, IReadOnlyList<bool> after)
        {
            for (int channel = 0; channel < Math.Min(before.Count, after.Count); channel++)
                if (before[channel] != after[channel])
                    changes.Add(new IoChannelChange(kind, channel, after[channel], state.TimestampUtc));
        }
    }

    /// <summary>Detiene el sondeo y espera a que termine la lectura en curso.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_cts is null || _loop is null) return;
        _cts.Cancel();
        await _loop;
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }
}
