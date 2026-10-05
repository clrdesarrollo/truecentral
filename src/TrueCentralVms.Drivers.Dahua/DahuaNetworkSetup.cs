using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using TrueCentralVms.Drivers.Dahua.Interop;

namespace TrueCentralVms.Drivers.Dahua;

/// <summary>Equipo visto por la búsqueda del NetSDK, con la estructura cruda que pide CLIENT_ModifyDevice.</summary>
public sealed class DahuaSdkSearchResult
{
    public required string Mac { get; init; }
    public required string Ip { get; init; }
    public required string Model { get; init; }
    public required string Serial { get; init; }
    public required string Version { get; init; }
    public required int HttpPort { get; init; }
    public required bool Dhcp { get; init; }
    public required byte InitStatus { get; init; }
    public required uint UnLoginFuncMask { get; init; }
    /// <summary>Interfaz local por la que respondió: por ahí hay que mandarle el cambio.</summary>
    public required string LocalIp { get; init; }
    internal byte[] Raw { get; init; } = [];
}

/// <summary>Resultado de cambiar la IP de un equipo Dahua.</summary>
public sealed record DahuaChangeIpResult(bool Success, string? Error);

/// <summary>
/// Configuración de red de equipos Dahua sin iniciar sesión (por multicast y
/// MAC), como "Change IP" de SmartPSS y el ConfigTool. Sirve para sacar de la
/// IP de fábrica (192.168.1.108) a un equipo recién inicializado que el
/// servidor ve en el segmento pero no alcanza por IP.
/// </summary>
public static class DahuaNetworkSetup
{
    // El SDK declara la búsqueda y la modificación "not support multi-thread".
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Busca equipos con el NetSDK por cada interfaz local durante la ventana
    /// indicada. Más lento que DHDiscover, pero entrega la estructura firmada
    /// que exige el cambio de IP.
    /// </summary>
    public static async Task<List<DahuaSdkSearchResult>> SearchAsync(IReadOnlyList<string> localIps, TimeSpan window,
        CancellationToken ct = default)
    {
        DahuaSdk.EnsureInitialized();
        await Gate.WaitAsync(ct);
        try { return await SearchCoreAsync(localIps, window, ct); }
        finally { Gate.Release(); }
    }

    private static async Task<List<DahuaSdkSearchResult>> SearchCoreAsync(IReadOnlyList<string> localIps,
        TimeSpan window, CancellationToken ct)
    {
        var found = new ConcurrentDictionary<string, DahuaSdkSearchResult>(StringComparer.OrdinalIgnoreCase);
        // El delegado debe seguir vivo mientras el SDK pueda llamarlo.
        NetSdk.SearchDevicesCallbackEx callback = (_, info, _) =>
        {
            try
            {
                if (info == IntPtr.Zero) return;
                var raw = new byte[NetSdk.DevNetInfoEx.Size];
                Marshal.Copy(info, raw, 0, raw.Length);
                // Cada equipo responde también por IPv6: esa entrada no sirve para cambiar la IPv4.
                if (BitConverter.ToInt32(raw, NetSdk.DevNetInfoEx.IPVersion) != 4) return;
                string localIp = Marshal.PtrToStringAnsi(info + NetSdk.DevNetInfoEx2LocalIpOffset) ?? "";
                var result = new DahuaSdkSearchResult
                {
                    Mac = ReadString(raw, NetSdk.DevNetInfoEx.Mac, 40),
                    Ip = ReadString(raw, NetSdk.DevNetInfoEx.IP, 64),
                    Model = ReadString(raw, NetSdk.DevNetInfoEx.DetailType, 32) is { Length: > 0 } detail
                        ? detail : ReadString(raw, NetSdk.DevNetInfoEx.DeviceType, 32),
                    Serial = ReadString(raw, NetSdk.DevNetInfoEx.SerialNo, 48),
                    Version = ReadString(raw, NetSdk.DevNetInfoEx.SoftVersion, 128),
                    HttpPort = BitConverter.ToUInt16(raw, NetSdk.DevNetInfoEx.HttpPort),
                    Dhcp = raw[NetSdk.DevNetInfoEx.DhcpEnabled] != 0,
                    InitStatus = raw[NetSdk.DevNetInfoEx.InitStatus],
                    UnLoginFuncMask = BitConverter.ToUInt32(raw, NetSdk.DevNetInfoEx.UnLoginFuncMask),
                    LocalIp = localIp,
                    Raw = raw,
                };
                if (result.Mac.Length > 0) found[result.Mac] = result;
            }
            catch
            {
                // Nunca dejar escapar una excepción hacia código nativo.
            }
        };
        IntPtr callbackPtr = Marshal.GetFunctionPointerForDelegate(callback);

        var handles = new List<long>();
        try
        {
            foreach (var localIp in localIps)
            {
                var input = new NetSdk.NET_IN_STARTSERACH_DEVICE
                {
                    dwSize = (uint)Marshal.SizeOf<NetSdk.NET_IN_STARTSERACH_DEVICE>(),
                    szLocalIp = localIp,
                    cbSearchDevices = callbackPtr,
                };
                var output = new NetSdk.NET_OUT_STARTSERACH_DEVICE
                {
                    dwSize = (uint)Marshal.SizeOf<NetSdk.NET_OUT_STARTSERACH_DEVICE>(),
                };
                long handle = NetSdk.CLIENT_StartSearchDevicesEx(ref input, ref output);
                if (handle != 0) handles.Add(handle);
            }
            await Task.Delay(window, ct);
        }
        finally
        {
            foreach (var handle in handles) NetSdk.CLIENT_StopSearchDevices(handle);
            GC.KeepAlive(callback);
        }
        return found.Values.OrderBy(d => d.Ip, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Cambia la red del equipo: IP fija o DHCP. Pide las credenciales del
    /// equipo (el cambio se autentica). Con DHCP se conservan la IP, máscara y
    /// puerta de enlace actuales en la estructura (el equipo las ignora).
    /// Los DNS no viajan acá: esta estructura no los tiene.
    /// </summary>
    public static async Task<DahuaChangeIpResult> ChangeIpAsync(DahuaSdkSearchResult device, bool dhcp, string ip,
        string mask, string gateway, string username, string password, CancellationToken ct = default)
    {
        DahuaSdk.EnsureInitialized();
        var raw = (byte[])device.Raw.Clone();
        if (!dhcp)
        {
            WriteString(raw, NetSdk.DevNetInfoEx.IP, 64, ip);
            WriteString(raw, NetSdk.DevNetInfoEx.Submask, 64, mask);
            WriteString(raw, NetSdk.DevNetInfoEx.Gateway, 64, gateway);
        }
        raw[NetSdk.DevNetInfoEx.DhcpEnabled] = (byte)(dhcp ? 1 : 0);
        // Los campos viejos son de 16 bytes: las contraseñas largas van en los
        // "New", y se llenan siempre (el equipo moderno solo mira esos).
        WriteString(raw, NetSdk.DevNetInfoEx.UserName, 16, username.Length < 16 ? username : "");
        WriteString(raw, NetSdk.DevNetInfoEx.PassWord, 16, password.Length < 16 ? password : "");
        BitConverter.TryWriteBytes(raw.AsSpan(NetSdk.DevNetInfoEx.NewUserNameEnabled, 4), 1);
        WriteString(raw, NetSdk.DevNetInfoEx.NewUserName, 64, username);
        BitConverter.TryWriteBytes(raw.AsSpan(NetSdk.DevNetInfoEx.NewPassWordEnabled, 4), 1);
        WriteString(raw, NetSdk.DevNetInfoEx.NewPassWord, 64, password);

        await Gate.WaitAsync(ct);
        IntPtr buffer = Marshal.AllocHGlobal(raw.Length);
        try
        {
            Marshal.Copy(raw, 0, buffer, raw.Length);
            return await Task.Run(() =>
            {
                string? localIp = device.LocalIp.Length > 0 ? device.LocalIp : null;
                if (NetSdk.CLIENT_ModifyDevice(buffer, 5000, out int deviceError, localIp, IntPtr.Zero))
                    return new DahuaChangeIpResult(true, null);
                return new DahuaChangeIpResult(false, DescribeError(NetSdk.CLIENT_GetLastError(), deviceError));
            }, ct);
        }
        finally
        {
            // La copia lleva la contraseña: se borra antes de liberar.
            Marshal.Copy(new byte[raw.Length], 0, buffer, raw.Length);
            Array.Clear(raw);
            Marshal.FreeHGlobal(buffer);
            Gate.Release();
        }
    }

    private static string DescribeError(uint sdkError, int deviceError) => sdkError switch
    {
        NetSdk.ErrorNetwork => "El equipo no respondió al cambio de IP. Verifique que siga conectado en la misma red física.",
        NetSdk.ErrorNeedEncryptionPassword or NetSdk.ErrorInvalidPassword =>
            "El equipo rechazó el usuario o la contraseña.",
        _ => $"El equipo no aceptó el cambio de IP (error 0x{sdkError:X8} del NetSDK" +
             (deviceError != 0 ? $", código {deviceError} del equipo)." : ")."),
    };

    private static string ReadString(byte[] raw, int offset, int length)
    {
        int end = Array.IndexOf(raw, (byte)0, offset, length);
        return Encoding.ASCII.GetString(raw, offset, (end < 0 ? offset + length : end) - offset).Trim();
    }

    private static void WriteString(byte[] raw, int offset, int length, string value)
    {
        Array.Clear(raw, offset, length);
        var bytes = Encoding.ASCII.GetBytes(value);
        Array.Copy(bytes, 0, raw, offset, Math.Min(bytes.Length, length - 1));
    }
}
