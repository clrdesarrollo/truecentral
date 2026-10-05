using System.Runtime.InteropServices;

namespace TrueCentralVms.Drivers.Dahua.Interop;

/// <summary>
/// Subconjunto mínimo de dhnetsdk.dll para gestión (login, info del equipo,
/// nombres de canales). Firmas y layouts verificados contra
/// Resources\...\Include\Common\dhnetsdk.h del General NetSDK V3.061 Win64:
/// LLONG = __int64 (long), LDWORD = puntero (IntPtr), CALL_METHOD = __stdcall,
/// structs de login SIN pragma pack (alineación por defecto).
/// </summary>
internal static class NetSdk
{
    private const string Dll = "dhnetsdk.dll";

    /// <summary>DH_SERIALNO_LEN.</summary>
    public const int SerialNoLen = 48;

    // ------------------------------------------------------------------
    // Estructuras
    // ------------------------------------------------------------------

    /// <summary>NET_DEVICEINFO_Ex (dhnetsdk.h línea ~13287).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DEVICEINFO_Ex
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = SerialNoLen)]
        public byte[] sSerialNumber;
        public int nAlarmInPortNum;
        public int nAlarmOutPortNum;
        public int nDiskNum;
        public int nDVRType;               // NET_DEVICE_TYPE
        public int nChanNum;
        public byte byLimitLoginTime;
        public byte byLeftLogTimes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public byte[] bReserved;
        public int nLockLeftTime;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] Reserved;
        public int nNTlsPort;
        public int nKeyFrameEncrypt;
        public int emAlgorithm;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] Reserved2;
    }

    /// <summary>NET_IN_LOGIN_WITH_HIGHLEVEL_SECURITY (dhnetsdk.h línea ~80898).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct NET_IN_LOGIN_WITH_HIGHLEVEL_SECURITY
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szIP;
        public int nPort;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szUserName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szPassword;
        /// <summary>EM_LOGIN_SPAC_CAP_TYPE: 0 = TCP (por defecto).</summary>
        public int emSpecCap;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] byReserved;
        public IntPtr pCapParam;
        /// <summary>EM_LOGIN_TLS_TYPE: 0 = sin TLS.</summary>
        public int emTLSCap;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szLocalIP;
        /// <summary>0 desconocido, 3 Windows.</summary>
        public int nClientType;
    }

    /// <summary>NET_OUT_LOGIN_WITH_HIGHLEVEL_SECURITY (dhnetsdk.h línea ~80914).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_OUT_LOGIN_WITH_HIGHLEVEL_SECURITY
    {
        public uint dwSize;
        public NET_DEVICEINFO_Ex stuDeviceInfo;
        public int nError;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 132)] public byte[] byReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_IN_GET_DEVICETYPE_INFO { public uint dwSize; }

    /// <summary>NET_OUT_GET_DEVICETYPE_INFO: szTypeEx trae el modelo detallado.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct NET_OUT_GET_DEVICETYPE_INFO
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szTypeEx;
    }

    /// <summary>NET_TIME de Dahua: 6 enteros.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_TIME
    {
        public int dwYear, dwMonth, dwDay, dwHour, dwMinute, dwSecond;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct NET_PERIPHERAL_VERSIONS
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szVersion;
        public int emPeripheralType;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 24)] public string szBuildDate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 228)] public byte[] byReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_IN_GET_SOFTWAREVERSION_INFO { public uint dwSize; }

    /// <summary>NET_OUT_GET_SOFTWAREVERSION_INFO (dhnetsdk.h línea ~101050).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct NET_OUT_GET_SOFTWAREVERSION_INFO
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szVersion;
        public NET_TIME stuBuildDate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string szWebVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szSecurityVersion;
        public int nPeripheralNum;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public NET_PERIPHERAL_VERSIONS[] stuPeripheralVersions;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szAlgorithmTrainingVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szMobileWebVersion;
        public long nBuildTimeUTC;
    }

    // ------------------------------------------------------------------
    // Funciones (todas bloqueantes: llamarlas dentro de Task.Run)
    // ------------------------------------------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern bool CLIENT_Init(IntPtr cbDisConnect, IntPtr dwUser);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern void CLIENT_Cleanup();

    /// <summary>Timeout de conexión en ms y cantidad de intentos.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern void CLIENT_SetConnectTime(int nWaitTime, int nTryTimes);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern long CLIENT_LoginWithHighLevelSecurity(
        ref NET_IN_LOGIN_WITH_HIGHLEVEL_SECURITY pstInParam,
        ref NET_OUT_LOGIN_WITH_HIGHLEVEL_SECURITY pstOutParam);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern bool CLIENT_Logout(long lLoginID);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint CLIENT_GetLastError();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern bool CLIENT_GetDeviceType(long lLoginID,
        ref NET_IN_GET_DEVICETYPE_INFO pstInParam, ref NET_OUT_GET_DEVICETYPE_INFO pstOutParam, int nWaitTime);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern bool CLIENT_GetSoftwareVersion(long lLoginID,
        ref NET_IN_GET_SOFTWAREVERSION_INFO pstInParam, ref NET_OUT_GET_SOFTWAREVERSION_INFO pstOutParam, int nWaitTime);

    /// <summary>Nombres de canales: buffer con ranuras ANSI de 32 bytes por canal.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern bool CLIENT_QueryChannelName(long lLoginID,
        IntPtr pChannelName, int maxlen, ref int nChannelCount, int waittime);

    // ------------------------------------------------------------------
    // Reproducción remota: búsqueda de grabaciones en el disco del equipo
    // ------------------------------------------------------------------

    /// <summary>EM_QUERY_RECORD_TYPE.EM_RECORD_TYPE_ALL: todos los tipos de grabación.</summary>
    public const int RecordTypeAll = 0;

    /// <summary>NET_NO_RECORD_FOUND: la búsqueda terminó sin resultados (no es un fallo).</summary>
    public const uint ErrorNoRecordFound = 0x80000018;

    /// <summary>
    /// NET_RECORDFILE_INFO (dhnetsdk.h línea ~6177). 196 bytes exactos: la
    /// estructura no lleva padding porque todos sus campos son de 4 bytes o
    /// múltiplos, y los cuatro BYTE finales completan la última palabra.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct NET_RECORDFILE_INFO
    {
        public uint ch;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 124)] public string filename;
        public uint framenum;
        public uint size;                       // en KB
        public NET_TIME starttime;
        public NET_TIME endtime;
        public uint driveno;
        public uint startcluster;
        /// <summary>0 continua, 1 alarma, 2 detección de movimiento, 3 tarjeta, 4 imagen, 19 POS.</summary>
        public byte nRecordFileType;
        public byte bImportantRecID;
        public byte bHint;
        /// <summary>0 stream principal, 1..3 secundarios.</summary>
        public byte bRecType;
    }

    /// <summary>
    /// Abre una búsqueda de grabaciones (devuelve 0 si falla o si no hay
    /// resultados: distinguir con CLIENT_GetLastError). cardid puede ir nulo.
    /// </summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern long CLIENT_FindFile(long lLoginID, int nChannelId, int nRecordFileType,
        IntPtr cardid, ref NET_TIME tmStart, ref NET_TIME tmEnd,
        [MarshalAs(UnmanagedType.Bool)] bool bTime, int waittime);

    /// <summary>Siguiente archivo de la búsqueda: 1 = entregado, 0 = no hay más, &lt;0 = error.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int CLIENT_FindNextFile(long lFindHandle, ref NET_RECORDFILE_INFO lpFindData);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern bool CLIENT_FindClose(long lFindHandle);

    // ------------------------------------------------------------------
    // Inicialización de equipos de fábrica (primer usuario y contraseña)
    // ------------------------------------------------------------------

    /// <summary>NET_ERROR_PWD_ILLEGAL: la contraseña no cumple la política del equipo.</summary>
    public const uint ErrorPasswordIllegal = 0x800003F8;

    /// <summary>NET_ERROR_DEVICE_ALREADY_INIT.</summary>
    public const uint ErrorDeviceAlreadyInit = 0x800003F9;

    /// <summary>NET_NETWORK_ERROR: sin respuesta (tiempo agotado).</summary>
    public const uint ErrorNetwork = 0x80000002;

    /// <summary>NET_ERROR_NEED_ENCRYPTION_PASSWORD: el cambio de IP exige la contraseña correcta.</summary>
    public const uint ErrorNeedEncryptionPassword = 0x80000207;

    /// <summary>NET_ERROR_INVALID_PASSWORD.</summary>
    public const uint ErrorInvalidPassword = 0x8000046E;

    /// <summary>
    /// NET_IN_INIT_DEVICE_ACCOUNT (dhnetsdk.h línea ~101402). Fuera de los
    /// bloques pragma pack(4): alineación por defecto, y todos los campos
    /// quedan naturalmente alineados (el enum cae en el offset 400).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct NET_IN_INIT_DEVICE_ACCOUNT
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 40)] public string szMac;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szUserName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPwd;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szCellPhone;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szMail;
        /// <summary>Abandonado por el SDK.</summary>
        public byte byInitStatus;
        /// <summary>
        /// Debe ser el que anunció el equipo en la búsqueda. bit0 = recupera por
        /// celular (exige szCellPhone), bit1 = por correo (exige szMail), bit2 = por archivo XML.
        /// </summary>
        public byte byPwdResetWay;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public byte[] byReserved;
        /// <summary>EM_ACCOUNT_PROTOCOL: 0 = normal.</summary>
        public int emAccountProtocol;
        public int bAutoAddDevice;
        public int bIsAutoAddDevice;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szPasswdTip;
        public int bIs4th;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_OUT_INIT_DEVICE_ACCOUNT { public uint dwSize; }

    /// <summary>
    /// Inicializa por multicast, identificando al equipo por su MAC: sirve
    /// aunque el equipo esté en otra subred (la IP de fábrica 192.168.1.108),
    /// siempre que comparta el segmento L2. szLocalIp elige la interfaz de salida.
    /// </summary>
    // ------------------------------------------------------------------
    // Búsqueda por SDK y cambio de IP (lo que hace "Change IP" en SmartPSS)
    // ------------------------------------------------------------------

    /// <summary>
    /// DEVICE_NET_INFO_EX (dhnetsdk.h línea ~14257), sin pragma pack. Se maneja
    /// como bloque de bytes: el SDK exige devolverle en CLIENT_ModifyDevice la
    /// misma estructura que entregó en la búsqueda (verifyData lleva una firma
    /// ECC), así que se copia tal cual y se tocan solo los campos de abajo.
    /// Offsets calculados del header y validados contra la búsqueda real de una
    /// ITC413 y un XVR4232AN (MAC, serie, versión, puerto HTTP y máscara de
    /// funciones caen donde corresponde).
    /// </summary>
    public static class DevNetInfoEx
    {
        public const int Size = 1120;
        public const int IPVersion = 0;             // int: 4 o 6 (el mismo equipo llega una vez por cada una)
        public const int IP = 4;                    // char[64]
        public const int Submask = 72;              // char[64]
        public const int Gateway = 136;             // char[64]
        public const int Mac = 200;                 // char[40]
        public const int DeviceType = 240;          // char[32]: la clase ("ITC", "HCVR"), no el modelo
        public const int DetailType = 540;          // char[32]: el modelo
        public const int DhcpEnabled = 274;         // bool (1 byte)
        public const int SerialNo = 364;            // char[48]
        public const int SoftVersion = 412;         // char[128]
        public const int UserName = 764;            // char[16]
        public const int PassWord = 780;            // char[16]
        public const int HttpPort = 796;            // unsigned short
        public const int NewPassWordEnabled = 808;  // BOOL
        public const int NewPassWord = 812;         // char[64]
        public const int InitStatus = 876;          // BYTE
        public const int NewUserNameEnabled = 944;  // BOOL
        public const int NewUserName = 948;         // char[64]
        public const int UnLoginFuncMask = 1040;    // DWORD
    }

    /// <summary>DEVICE_NET_INFO_EX2: tras el DEVICE_NET_INFO_EX viene szLocalIP[64], la interfaz por la que respondió.</summary>
    public const int DevNetInfoEx2LocalIpOffset = DevNetInfoEx.Size;

    /// <summary>fSearchDevicesCBEx.</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void SearchDevicesCallbackEx(long lSearchHandle, IntPtr pDevNetInfo, IntPtr pUserData);

    /// <summary>NET_IN_STARTSERACH_DEVICE (dhnetsdk.h línea ~83915): 112 bytes en x64.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct NET_IN_STARTSERACH_DEVICE
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szLocalIp;
        public IntPtr cbSearchDevices;
        public IntPtr pUserData;
        /// <summary>EM_SEND_SEARCH_TYPE: 0 = multicast y broadcast.</summary>
        public int emSendType;
        public IntPtr cbSearchDevicesTTLV;
        public IntPtr cbSearchDevices4th;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_OUT_STARTSERACH_DEVICE { public uint dwSize; }

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern long CLIENT_StartSearchDevicesEx(ref NET_IN_STARTSERACH_DEVICE pInBuf,
        ref NET_OUT_STARTSERACH_DEVICE pOutBuf);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern bool CLIENT_StopSearchDevices(long lSearchHandle);

    /// <summary>Cambia IP, máscara y puerta de enlace. pDevNetInfo = DEVICE_NET_INFO_EX de la búsqueda, retocado.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern bool CLIENT_ModifyDevice(IntPtr pDevNetInfo, uint dwWaitTime, out int iError,
        string? szLocalIp, IntPtr reserved);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern bool CLIENT_InitDevAccount(ref NET_IN_INIT_DEVICE_ACCOUNT pInitAccountIn,
        ref NET_OUT_INIT_DEVICE_ACCOUNT pInitAccountOut, uint dwWaitTime, string? szLocalIp);
}
