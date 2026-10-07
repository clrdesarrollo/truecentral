using System.Runtime.InteropServices;

namespace TrueCentralVms.Drivers.Hikvision.Interop;

/// <summary>
/// Subconjunto de Sadp.dll (SDK de SADP de Hikvision, x64 4.5.0.3, el mismo de
/// iVMS-4200 y SADP Tool): búsqueda de equipos y cambio de parámetros de red
/// por multicast con la MAC, sin iniciar sesión. Es un SDK aparte de
/// HCNetSDK; viaja en native\hikvision-sadp con sus OpenSSL 3.
/// Las estructuras no tienen header en el paquete: los layouts salen del
/// manual del SADP SDK y quedaron validados contra la búsqueda real (ver
/// <see cref="DeviceInfo"/>).
/// </summary>
internal static class SadpSdk
{
    private const string Dll = "Sadp.dll";

    /// <summary>
    /// SADP_DEVICE_INFO: se lee como bloque de bytes (solo se usan algunos
    /// campos). Offsets validados con una búsqueda real de cámaras y un DVR
    /// Hikvision: MAC, IP, máscara, serie, puerto, puerta de enlace, DHCP y
    /// puerto HTTP coinciden con lo que anuncia el XML de SADP.
    /// </summary>
    public static class DeviceInfo
    {
        public const int Size = 476;
        public const int SerialNo = 12;        // char[48]
        public const int Mac = 60;             // char[20]
        public const int IPv4Address = 80;     // char[16]
        public const int IPv4SubnetMask = 96;  // char[16]
        public const int Port = 116;           // DWORD: puerto del SDK (8000)
        public const int Result = 272;         // int: 1 alta, 2 cambio, 3 baja (SADP_ADD/UPDATE/DEC)
        public const int DevDesc = 276;        // char[24]: modelo
        public const int IPv4Gateway = 324;    // char[16]
        public const int DhcpEnabled = 434;    // BYTE
        public const int HttpPort = 436;       // WORD
    }

    /// <summary>SADP_DEV_NET_PARAM (436 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct SADP_DEV_NET_PARAM
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string szIPv4Address;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string szIPv4SubNetMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string szIPv4Gateway;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szIPv6Address;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szIPv6Gateway;
        public ushort wPort;
        public byte byIPv6MaskLen;
        public byte byDhcpEnable;
        public ushort wHttpPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)] public byte[] byRes;
    }

    /// <summary>PDEVICE_FIND_CALLBACK: (const SADP_DEVICE_INFO*, void* pUserData).</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void DeviceFindCallback(IntPtr lpDeviceInfo, IntPtr pUserData);

    /// <summary>Arranca la sesión SADP. bInstallNPF = 0: solo multicast UDP, sin WinPcap.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern bool SADP_Start_V30(DeviceFindCallback pDeviceFindCallBack, int bInstallNPF, IntPtr pUserData);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern bool SADP_SendInquiry();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern bool SADP_Stop();

    /// <summary>Cambia la red del equipo con esa MAC (tiene que haberlo visto la sesión en curso).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern bool SADP_ModifyDeviceNetParam(string sMAC, string sPassword, ref SADP_DEV_NET_PARAM lpNetParam);

    /// <summary>
    /// Activa un equipo de fábrica: le fija la contraseña del usuario admin
    /// (como "Activate" de SADP Tool). Se identifica por su N° de serie
    /// completo, tal como lo anunció la sesión en curso.
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern bool SADP_ActivateDevice(string sDevSerialNO, string sCommand);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint SADP_GetLastError();

    /// <summary>Errores del SDK de SADP (manual del SADP SDK).</summary>
    public static string DescribeError(uint code) => code switch
    {
        2002 => "la sesión de SADP no está iniciada",
        2003 or 2004 or 2006 => "no se pudo usar la tarjeta de red del servidor",
        2005 => "parámetros inválidos",
        2009 => "el equipo rechazó el cambio",
        2011 => "el equipo no respondió (tiempo agotado)",
        2018 => "el equipo está bloqueado por demasiados intentos con contraseña incorrecta; espere unos minutos",
        2019 => "el equipo no está activado: actívelo antes de cambiarle la IP",
        2020 => "el equipo rechazó la contraseña por débil; use una más robusta",
        2021 => "el equipo ya está activado",
        2024 => "contraseña incorrecta",
        _ => $"error {code} del SDK de SADP",
    };
}
