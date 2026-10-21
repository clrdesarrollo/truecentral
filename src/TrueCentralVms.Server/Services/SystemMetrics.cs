using System.Runtime.InteropServices;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Uso de CPU/RAM/disco de la máquina del servidor, sin contadores de
/// rendimiento ni paquetes extra: GetSystemTimes para CPU (porcentaje por
/// delta entre lecturas), GlobalMemoryStatusEx para RAM y DriveInfo para el
/// disco donde vive el content root (ahí están pgdata y los datos).
/// </summary>
public sealed class SystemMetrics(IHostEnvironment env)
{
    private readonly object _lock = new();
    private ulong _lastIdle, _lastKernel, _lastUser;
    private double _lastCpu;
    private long _lastSampleTicks;

    public SystemMetricsDto Read()
    {
        double cpu = SampleCpu();

        var memory = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        double ramPercent = 0, ramUsedGb = 0, ramTotalGb = 0;
        if (GlobalMemoryStatusEx(ref memory))
        {
            ramPercent = memory.dwMemoryLoad;
            ramTotalGb = ToGb(memory.ullTotalPhys);
            ramUsedGb = ToGb(memory.ullTotalPhys - memory.ullAvailPhys);
        }

        double diskPercent = 0, diskUsedGb = 0, diskTotalGb = 0;
        string diskName = "";
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(env.ContentRootPath) ?? "C:\\");
            diskName = drive.Name.TrimEnd('\\');
            diskTotalGb = ToGb((ulong)drive.TotalSize);
            diskUsedGb = ToGb((ulong)(drive.TotalSize - drive.TotalFreeSpace));
            diskPercent = drive.TotalSize > 0
                ? Math.Round(100.0 * (drive.TotalSize - drive.TotalFreeSpace) / drive.TotalSize, 1)
                : 0;
        }
        catch { /* unidad no lista (¿red?): el indicador queda en 0 */ }

        return new SystemMetricsDto(cpu, ramPercent, ramUsedGb, ramTotalGb,
            diskPercent, diskUsedGb, diskTotalGb, diskName);
    }

    /// <summary>
    /// CPU total de la máquina como porcentaje del intervalo desde la lectura
    /// anterior. La primera lectura (sin delta) devuelve 0; llamadas a menos
    /// de 1 s de distancia reusan el último valor para no medir ruido.
    /// </summary>
    private double SampleCpu()
    {
        lock (_lock)
        {
            if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
                return _lastCpu;

            ulong idle = ToUInt64(idleFt), kernel = ToUInt64(kernelFt), user = ToUInt64(userFt);
            long now = Environment.TickCount64;

            if (_lastSampleTicks != 0 && now - _lastSampleTicks < 1000)
                return _lastCpu;

            if (_lastSampleTicks != 0)
            {
                // kernel incluye idle: ocupado = (kernel - idle) + user.
                ulong deltaIdle = idle - _lastIdle;
                ulong deltaTotal = (kernel - _lastKernel) + (user - _lastUser);
                if (deltaTotal > 0)
                    _lastCpu = Math.Round(100.0 * (deltaTotal - deltaIdle) / deltaTotal, 1);
            }

            (_lastIdle, _lastKernel, _lastUser, _lastSampleTicks) = (idle, kernel, user, now);
            return _lastCpu;
        }
    }

    // ------------------------------------------------------------------
    // Throughput de red
    // ------------------------------------------------------------------
    // Se muestrea en un temporizador propio (no al leer) para que el valor sea
    // el del último intervalo aunque nadie lo consulte seguido; arranca con la
    // primera lectura y guarda los últimos minutos para la curva y el pico.
    private const int NetSampleSeconds = 2;
    private const int NetHistory = 150; // 5 min
    private readonly object _netLock = new();
    private Timer? _netTimer;
    private long _netLastTicks, _netLastRx, _netLastTx;
    private double _rxMbps, _txMbps, _linkMbps;
    private string[] _adapters = [];
    private readonly Queue<double> _netHistory = new();

    public NetworkThroughputDto ReadNetwork()
    {
        lock (_netLock)
        {
            if (_netTimer is null)
            {
                SampleNetwork(null);
                _netTimer = new Timer(SampleNetwork, null, TimeSpan.FromSeconds(NetSampleSeconds), TimeSpan.FromSeconds(NetSampleSeconds));
            }
            var history = _netHistory.ToList();
            return new NetworkThroughputDto(_rxMbps, _txMbps, _linkMbps, history.Count > 0 ? history.Max() : 0,
                history, NetSampleSeconds, _adapters);
        }
    }

    /// <summary>
    /// Suma los bytes de las interfaces activas con dirección IPv4 propia. Así
    /// no se cuenta dos veces el tráfico en un host con conmutador virtual: la
    /// placa física unida al conmutador no tiene IP, su vEthernet sí.
    /// </summary>
    private void SampleNetwork(object? _)
    {
        try
        {
            var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                            && n.NetworkInterfaceType is not (System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                                or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                            && n.GetIPProperties().UnicastAddresses.Any(a =>
                                a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                                && !System.Net.IPAddress.IsLoopback(a.Address)
                                && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)))
                .ToList();
            // La capacidad es la de las salidas reales (con puerta de enlace): las placas
            // virtuales solo-anfitrión (VMware, Hyper-V interno) informan 100 Mbps ficticios.
            var uplinks = nics.Where(n => n.GetIPProperties().GatewayAddresses.Any(g =>
                !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any))).ToList();
            if (uplinks.Count == 0) uplinks = nics;
            long rx = 0, tx = 0, link = 0;
            foreach (var nic in nics)
            {
                var stats = nic.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
            foreach (var nic in uplinks)
                if (nic.Speed > 0) link += nic.Speed;
            long now = Environment.TickCount64;
            lock (_netLock)
            {
                double seconds = (now - _netLastTicks) / 1000.0;
                // Una interfaz que aparece o se va cambia la suma: ese intervalo se descarta.
                if (_netLastTicks != 0 && seconds > 0 && rx >= _netLastRx && tx >= _netLastTx)
                {
                    _rxMbps = Math.Round((rx - _netLastRx) * 8 / seconds / 1_000_000, 2);
                    _txMbps = Math.Round((tx - _netLastTx) * 8 / seconds / 1_000_000, 2);
                    _netHistory.Enqueue(Math.Round(_rxMbps + _txMbps, 2));
                    while (_netHistory.Count > NetHistory) _netHistory.Dequeue();
                }
                (_netLastTicks, _netLastRx, _netLastTx) = (now, rx, tx);
                _linkMbps = link / 1_000_000.0;
                _adapters = uplinks.Select(n => n.Name).ToArray();
            }
        }
        catch { /* sin acceso a las interfaces: el indicador queda en el último valor */ }
    }

    private static double ToGb(ulong bytes) => Math.Round(bytes / 1024.0 / 1024.0 / 1024.0, 1);

    private static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME ft) =>
        ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(
        out System.Runtime.InteropServices.ComTypes.FILETIME idleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
