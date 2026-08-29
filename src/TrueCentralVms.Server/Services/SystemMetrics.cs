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
