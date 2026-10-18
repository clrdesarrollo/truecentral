using System.Windows.Threading;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Apariencia Consola: refresca una vez por segundo la franja de telemetría
/// de cada cuadro de la grilla principal (códec, FPS, stream y tiempo en
/// vivo). Solo lee el estado de los players que ya están abiertos: no
/// consulta al servidor. Con la apariencia clásica no se crea.
/// </summary>
internal sealed class CellTelemetryTicker : IDisposable
{
    private readonly MainViewModel _shell;
    private readonly DispatcherTimer _timer;

    public CellTelemetryTicker(MainViewModel shell)
    {
        _shell = shell;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
    }

    public void Start()
    {
        Refresh();
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();

    private void Refresh()
    {
        foreach (var cell in _shell.Cells) cell.RefreshTelemetry();
    }
}
