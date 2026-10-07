using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using TrueCentralVms.Drivers.Hikvision.Interop;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>Equipo visto por la sesión SADP del SDK.</summary>
public sealed record HikvisionSadpDevice(string Mac, string Ip, string Mask, string Gateway, string Serial,
    string Model, int Port, int HttpPort, bool Dhcp);

/// <summary>Resultado de cambiar la red de un equipo Hikvision por SADP.</summary>
public sealed record HikvisionSadpResult(bool Success, string? Error);

/// <summary>
/// Cambio de IP de equipos Hikvision sin iniciar sesión, por SADP (como SADP
/// Tool e iVMS-4200): el SDK busca por multicast y modifica por MAC con la
/// contraseña del equipo. Sirve con el equipo en otra subred del mismo
/// segmento físico (p. ej. la IP de fábrica 192.168.1.64).
/// </summary>
public static class HikvisionSadp
{
    // El SDK tiene UNA sesión global (Start/Stop): búsquedas y cambios van de a uno.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Busca equipos durante la ventana indicada.</summary>
    public static async Task<List<HikvisionSadpDevice>> SearchAsync(TimeSpan window, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var found = new ConcurrentDictionary<string, HikvisionSadpDevice>(StringComparer.OrdinalIgnoreCase);
            await RunSessionAsync(found, window, _ => Task.FromResult(0), ct);
            return found.Values.OrderBy(d => d.Ip, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Cambia IP, máscara, puerta de enlace y DHCP del equipo con esa MAC.
    /// Conserva sus puertos (el SDK los exige en la misma estructura).
    /// </summary>
    public static async Task<HikvisionSadpResult> ChangeNetworkAsync(string mac, string password, bool dhcp,
        string ip, string mask, string gateway, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var found = new ConcurrentDictionary<string, HikvisionSadpDevice>(StringComparer.OrdinalIgnoreCase);
            HikvisionSadpResult? result = null;
            await RunSessionAsync(found, TimeSpan.FromSeconds(3), _ =>
            {
                if (!found.TryGetValue(NormalizeMac(mac), out var device))
                {
                    result = new HikvisionSadpResult(false,
                        "El equipo no respondió a la búsqueda SADP. Vuelva a buscar e intente de nuevo.");
                    return Task.FromResult(0);
                }
                var param = new SadpSdk.SADP_DEV_NET_PARAM
                {
                    // Con DHCP el equipo ignora la IP fija, pero el campo no puede ir vacío.
                    szIPv4Address = dhcp ? device.Ip : ip,
                    szIPv4SubNetMask = dhcp ? device.Mask : mask,
                    szIPv4Gateway = dhcp ? device.Gateway : gateway,
                    szIPv6Address = "",
                    szIPv6Gateway = "",
                    wPort = (ushort)(device.Port > 0 ? device.Port : 8000),
                    byIPv6MaskLen = 64,
                    byDhcpEnable = (byte)(dhcp ? 1 : 0),
                    wHttpPort = (ushort)(device.HttpPort > 0 ? device.HttpPort : 80),
                    byRes = new byte[126],
                };
                // El MAC se pasa tal como lo anunció el SDK (formato aa-bb-cc-...).
                result = SadpSdk.SADP_ModifyDeviceNetParam(device.Mac, password, ref param)
                    ? new HikvisionSadpResult(true, null)
                    : new HikvisionSadpResult(false,
                        $"El equipo no aceptó el cambio: {SadpSdk.DescribeError(SadpSdk.SADP_GetLastError())}.");
                return Task.FromResult(0);
            }, ct);
            return result ?? new HikvisionSadpResult(false, "No se pudo iniciar la búsqueda SADP.");
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Activa el equipo de fábrica con esa MAC: le fija la contraseña del
    /// usuario admin (cámaras, grabadores, control de acceso, citofonía... todo
    /// equipo Hikvision se activa igual). Funciona aunque siga en su IP de
    /// fábrica, fuera de la subred del servidor.
    /// </summary>
    public static async Task<HikvisionSadpResult> ActivateAsync(string mac, string password, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var found = new ConcurrentDictionary<string, HikvisionSadpDevice>(StringComparer.OrdinalIgnoreCase);
            HikvisionSadpResult? result = null;
            await RunSessionAsync(found, TimeSpan.FromSeconds(3), _ =>
            {
                if (!found.TryGetValue(NormalizeMac(mac), out var device) || device.Serial.Length == 0)
                {
                    result = new HikvisionSadpResult(false,
                        "El equipo no respondió a la búsqueda SADP. Vuelva a buscar e intente de nuevo.");
                    return Task.FromResult(0);
                }
                // El SDK identifica al equipo por su N° de serie completo, no por la MAC.
                result = SadpSdk.SADP_ActivateDevice(device.Serial, password)
                    ? new HikvisionSadpResult(true, null)
                    : new HikvisionSadpResult(false,
                        $"El equipo no aceptó la activación: {SadpSdk.DescribeError(SadpSdk.SADP_GetLastError())}.");
                return Task.FromResult(0);
            }, ct);
            return result ?? new HikvisionSadpResult(false, "No se pudo iniciar la búsqueda SADP.");
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Abre la sesión SADP, pregunta, espera la ventana y ejecuta <paramref name="action"/>
    /// con la sesión todavía abierta (el cambio necesita que el SDK conozca al equipo).
    /// </summary>
    private static async Task RunSessionAsync(ConcurrentDictionary<string, HikvisionSadpDevice> found,
        TimeSpan window, Func<HikvisionSadpDevice?, Task> action, CancellationToken ct)
    {
        SadpSdk.DeviceFindCallback callback = (info, _) =>
        {
            try
            {
                if (info == IntPtr.Zero) return;
                var raw = new byte[SadpSdk.DeviceInfo.Size];
                Marshal.Copy(info, raw, 0, raw.Length);
                if (BitConverter.ToInt32(raw, SadpSdk.DeviceInfo.Result) == 3) return; // el equipo se fue
                var device = new HikvisionSadpDevice(
                    Mac: ReadString(raw, SadpSdk.DeviceInfo.Mac, 20),
                    Ip: ReadString(raw, SadpSdk.DeviceInfo.IPv4Address, 16),
                    Mask: ReadString(raw, SadpSdk.DeviceInfo.IPv4SubnetMask, 16),
                    Gateway: ReadString(raw, SadpSdk.DeviceInfo.IPv4Gateway, 16),
                    Serial: ReadString(raw, SadpSdk.DeviceInfo.SerialNo, 48),
                    Model: ReadString(raw, SadpSdk.DeviceInfo.DevDesc, 24),
                    Port: BitConverter.ToInt32(raw, SadpSdk.DeviceInfo.Port),
                    HttpPort: BitConverter.ToUInt16(raw, SadpSdk.DeviceInfo.HttpPort),
                    Dhcp: raw[SadpSdk.DeviceInfo.DhcpEnabled] != 0);
                if (device.Mac.Length > 0) found[NormalizeMac(device.Mac)] = device;
            }
            catch
            {
                // Nunca dejar escapar una excepción hacia código nativo.
            }
        };

        if (!await Task.Run(() => SadpSdk.SADP_Start_V30(callback, 0, IntPtr.Zero), ct))
            throw new InvalidOperationException(
                $"No se pudo iniciar SADP: {SadpSdk.DescribeError(SadpSdk.SADP_GetLastError())}.");
        try
        {
            SadpSdk.SADP_SendInquiry();
            await Task.Delay(window, ct);
            await Task.Run(() => action(null), ct);
        }
        finally
        {
            await Task.Run(() => SadpSdk.SADP_Stop());
            GC.KeepAlive(callback);
        }
    }

    /// <summary>MAC en forma comparable: minúsculas con guiones (SADP usa guiones; DHDiscover, dos puntos).</summary>
    public static string NormalizeMac(string mac) => mac.Trim().Replace(':', '-').ToLowerInvariant();

    private static string ReadString(byte[] raw, int offset, int length)
    {
        int end = Array.IndexOf(raw, (byte)0, offset, length);
        return Encoding.ASCII.GetString(raw, offset, (end < 0 ? offset + length : end) - offset).Trim();
    }
}
