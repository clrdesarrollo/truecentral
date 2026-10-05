using System.Runtime.InteropServices;
using TrueCentralVms.Drivers.Dahua.Interop;

namespace TrueCentralVms.Drivers.Dahua;

/// <summary>Resultado de inicializar un equipo Dahua de fábrica.</summary>
public sealed record DahuaInitResult(bool Success, string? Error, string? LocalIp);

/// <summary>
/// Inicialización de equipos Dahua que salen de fábrica sin usuario: se les
/// crea la cuenta "admin" con la contraseña elegida, igual que el botón
/// "Initialize" del ConfigTool y SmartPSS. Va por multicast con la MAC del
/// equipo, así que no hace falta que esté en la subred del servidor.
/// </summary>
public static class DahuaDeviceInitializer
{
    /// <summary>Usuario que crean los equipos Dahua al inicializarse (no se puede elegir otro).</summary>
    public const string AdminUser = "admin";

    /// <summary>Espera por intento: el equipo responde en menos de un segundo si el mensaje le llega.</summary>
    private const uint WaitMs = 4000;

    /// <summary>
    /// Inicializa el equipo. Prueba cada interfaz local en el orden dado,
    /// porque el multicast sale solo por una y el servidor puede tener varias
    /// (adaptadores virtuales, VPN). Bloqueante: llamar dentro de Task.Run.
    /// </summary>
    /// <param name="pwdResetWay">El que anunció el equipo en DHDiscover (el SDK exige que coincida).</param>
    /// <param name="email">Correo de recuperación; el llamador lo exige cuando el equipo lo pide (bit1 de pwdResetWay).</param>
    public static DahuaInitResult Initialize(string mac, string password, string? email, byte pwdResetWay,
        IReadOnlyList<string> localIps)
    {
        DahuaSdk.EnsureInitialized();

        var input = new NetSdk.NET_IN_INIT_DEVICE_ACCOUNT
        {
            szMac = mac,
            szUserName = AdminUser,
            szPwd = password,
            szCellPhone = "",
            szMail = email?.Trim() ?? "",
            byPwdResetWay = pwdResetWay,
            byReserved = new byte[2],
            szPasswdTip = "",
        };
        input.dwSize = (uint)Marshal.SizeOf<NetSdk.NET_IN_INIT_DEVICE_ACCOUNT>();

        // null = que el SDK elija la interfaz; se deja al final como último recurso.
        var candidates = localIps.Cast<string?>().Append(null).ToList();
        uint lastError = 0;
        bool sentBefore = false;
        foreach (var localIp in candidates)
        {
            var output = new NetSdk.NET_OUT_INIT_DEVICE_ACCOUNT
            {
                dwSize = (uint)Marshal.SizeOf<NetSdk.NET_OUT_INIT_DEVICE_ACCOUNT>(),
            };
            if (NetSdk.CLIENT_InitDevAccount(ref input, ref output, WaitMs, localIp))
                return new DahuaInitResult(true, null, localIp);

            lastError = NetSdk.CLIENT_GetLastError();
            // Un intento anterior pudo llegar al equipo aunque su respuesta se
            // perdiera: si ahora dice "ya inicializado", fue este servidor.
            if (lastError == NetSdk.ErrorDeviceAlreadyInit)
                return sentBefore
                    ? new DahuaInitResult(true, null, localIp)
                    : new DahuaInitResult(false, "El equipo ya estaba inicializado.", localIp);
            if (lastError == NetSdk.ErrorPasswordIllegal)
                return new DahuaInitResult(false, DescribeError(lastError), localIp);
            sentBefore = true;
        }
        return new DahuaInitResult(false, DescribeError(lastError), null);
    }

    private static string DescribeError(uint code) => code switch
    {
        NetSdk.ErrorPasswordIllegal =>
            "El equipo rechazó la contraseña por no cumplir su política de seguridad.",
        NetSdk.ErrorNetwork =>
            "El equipo no respondió. Verifique que siga conectado en la misma red física que el servidor.",
        _ => $"El equipo no se pudo inicializar (error 0x{code:X8} del NetSDK).",
    };
}
