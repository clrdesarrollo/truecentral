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
}
