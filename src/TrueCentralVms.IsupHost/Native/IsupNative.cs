using System.Runtime.InteropServices;

namespace TrueCentralVms.IsupHost.Native;

// =============================================================================
// Declaraciones P/Invoke del SDK ISUP/EHome de Hikvision (HCISUPCMS, HCISUPStream,
// HCISUPAlarm, HCISUPSS), portadas del proyecto de pruebas isuptest.
//
// Sin los .h oficiales: los nombres de funciones están verificados contra los exports
// de las DLL y los layouts que se usan en producción (registro, vídeo, reproducción,
// ISAPI por el túnel, alarmas, PTZ, salidas) contra un DVR iDS-7232HQHI-M2/S y una
// cámara ANPR reales. Si algo falla (cierre del proceso, error 17 = "parámetro no
// válido"), el primer sospechoso es un struct de aquí: por eso el motor corre en un
// proceso aparte del servidor.
// =============================================================================

public static unsafe class Isup
{
    public const int MaxDeviceIdLen = 256;

    // dwDataType del callback de registro (CMS)
    public const uint ENUM_DEV_ON = 0;
    public const uint ENUM_DEV_OFF = 1;
    public const uint ENUM_DEV_ADDRESS_CHANGED = 2;
    public const uint ENUM_DEV_AUTH = 3;
    public const uint ENUM_DEV_SESSIONKEY = 4;
    public const uint ENUM_DEV_DAS_REQ = 5;        // el DVR pide la direccion del servidor DAS (ISUP 5.0)
    // Resto de NET_EHOME_REGISTER_TYPE (HCISUPCMS.h): el 6 es la petición de SessionKey, no el latido.
    public const uint ENUM_DEV_SESSIONKEY_REQ = 6;
    public const uint ENUM_DEV_DAS_REREGISTER = 7;     // el equipo se vuelve a registrar
    public const uint ENUM_DEV_DAS_PINGREO = 8;        // latido de registro (solo si se activa por configuración local)
    public const uint ENUM_DEV_DAS_EHOMEKEY_ERROR = 9; // la clave ISUP del equipo no coincide
    public const uint ENUM_DEV_SESSIONKEY_ERROR = 10;  // fallo en el intercambio de SessionKey
    public const uint ENUM_DEV_SLEEP = 11;             // el equipo entra en reposo (4G/batería): no responde hasta NET_ECMS_WakeUp

    // dwAlarmType de NET_EHOME_ALARM_MSG (HCISUPAlarm.h)
    public const uint EHOME_ALARM = 1;                          // alarma básica: NET_EHOME_ALARM_INFO (tipo EN_ALARM_TYPE)
    public const uint EHOME_ALARM_HEATMAP_REPORT = 2;
    public const uint EHOME_ALARM_FACESNAP_REPORT = 3;
    public const uint EHOME_ALARM_GPS = 4;                      // posición GPS (equipos móviles; no se usa en el VMS)
    public const uint EHOME_ALARM_CID_REPORT = 5;
    public const uint EHOME_ALARM_NOTICE_PICURL = 6;            // foto disponible por URL: NET_EHOME_NOTICE_PICURL
    public const uint EHOME_ALARM_NOTIFY_FAIL = 7;              // fallo asíncrono (p. ej. no pudo capturar): NET_EHOME_NOTIFY_FAIL_INFO
    public const uint EHOME_ALARM_SELFDEFINE = 9;
    public const uint EHOME_ALARM_DEVICE_NETSWITCH_REPORT = 10; // el equipo cambió de red (sin estructura)
    public const uint EHOME_ALARM_ACS = 11;
    public const uint EHOME_ALARM_WIRELESS_INFO = 12;           // señal y tráfico de la red inalámbrica: NET_EHOME_ALARMWIRELESSINFO
    public const uint EHOME_ISAPI_ALARM = 13;                   // evento ISAPI (XML/JSON) con fotos: NET_EHOME_ALARM_ISAPI_INFO
    public const uint EHOME_ALARM_MPDCDATA = 15;                // conteo de pasajeros de equipo embarcado: NET_EHOME_ALARM_MPDCDATA
    public const uint EHOME_ALARM_QRCODE = 20;
    public const uint EHOME_ALARM_FACETEMP = 21;

    // byDataType del callback de datos de preview
    public const byte NET_DVR_SYSHEAD = 1;
    public const byte NET_DVR_STREAMDATA = 2;
    public const byte NET_DVR_AUDIOSTREAMDATA = 3;
    public const byte NET_DVR_PRIVATE_DATA = 112;

    // ---- Callbacks (BOOL = int de 4 bytes) ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int DeviceRegisterCb(int lUserID, uint dwDataType, IntPtr pOutBuffer, uint dwOutLen, IntPtr pInBuffer, uint dwInLen, IntPtr pUser);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int PreviewNewLinkCb(int lPreviewHandle, IntPtr pNewLinkCBMsg, IntPtr pUser);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void PreviewDataCb(int iPreviewHandle, IntPtr pPreviewCBMsg, IntPtr pUser);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int AlarmMsgCb(int iHandle, IntPtr pAlarmMsg, IntPtr pUser);

    /// <summary>Callback de excepciones del SDK (firma de HCISUPCMS.h / HCISUPStream.h): tipo, usuario, handle del enlace y puntero de usuario.</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void ExceptionCb(uint dwType, int iUserID, int iHandle, IntPtr pUser);

    // Tipos de excepción (HCISUPStream.h y HCISUPCMS.h)
    public const uint EHOME_PREVIEW_EXCEPTION = 0x102;    // fallo recibiendo el vídeo en vivo
    public const uint EHOME_PLAYBACK_EXCEPTION = 0x103;   // fallo recibiendo una reproducción
    public const uint EHOME_AUDIOTALK_EXCEPTION = 0x104;  // fallo en el audio bidireccional
    public const uint EHOME_CMSALARM_EXCEPTION = 0x105;   // el CMS no pudo recibir una alarma

    // ---- CMS ----
    const string CMS = "HCISUPCMS";

    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_Init();
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_Fini();
    [DllImport(CMS)] public static extern int NET_ECMS_GetLastError();
    [DllImport(CMS)] public static extern uint NET_ECMS_GetBuildVersion();
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_SetLogToFile(int iLogLevel, [MarshalAs(UnmanagedType.LPStr)] string? strLogDir, [MarshalAs(UnmanagedType.Bool)] bool bAutoDel);
    [DllImport(CMS)] public static extern int NET_ECMS_StartListen(ref NET_EHOME_CMS_LISTEN_PARAM p);
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_StopListen(int lListenHandle);
    // Configuracion local del SDK (HCISUPPublic.h, NET_EHOME_LOCAL_CFG_TYPE): 2 = SEND_PARAM (espera de respuesta y reintentos de cada orden al equipo).
    public const int LOCAL_CFG_SEND_PARAM = 2;
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_SetSDKLocalCfg(int enumType, ref NET_EHOME_SEND_PARAM lpInBuff);
    // Proxy de la web del equipo (HCISUPCMS.h): con byProxyType = 1 el SDK abre un servidor HTTP que reenvia cada peticion al equipo por su
    // conexion ISUP. El equipo se elige por "?deviceId=" en la URL o por la cookie "deviceId" (cadenas de CHTTPProxySession en la DLL).
    public const byte ENUM_PROXY_TYPE_HTTP = 1;
    [DllImport(CMS)] public static extern int NET_ECMS_StartListenProxy(ref NET_EHOME_PT_PARAM lpStru);
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_StopListenProxy(int lListenHandle, uint dwProxyType);
    // BOOL según HCISUPCMS.h (isuptest lo declaraba int y comprobaba < 0, así que un rechazo del equipo pasaba inadvertido).
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_StartGetRealStream(int lUserID, ref NET_EHOME_PREVIEWINFO_IN pIn, ref NET_EHOME_PREVIEWINFO_OUT pOut);
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_StopGetRealStream(int lUserID, int lSessionID);
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_StartPushRealStream(int lUserID, ref NET_EHOME_PUSHSTREAM_IN pIn, ref NET_EHOME_PUSHSTREAM_OUT pOut);
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_SetDeviceSessionKey(ref NET_EHOME_DEV_SESSIONKEY p);
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_GetDeviceSessionKey(ref NET_EHOME_DEV_SESSIONKEY p);
    // ISAPI por el tunel ISUP. ISAPIPassThrough lleva el verbo en la URL ("GET /ISAPI/..."); GetPTXMLConfig recibe solo la ruta.
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_ISAPIPassThrough(int lUserID, ref NET_EHOME_PTXML_PARAM p);
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_GetPTXMLConfig(int lUserID, ref NET_EHOME_PTXML_PARAM p);

    // Firmas tomadas del header oficial HCISUPCMS.h (SDK 2.5).
    /// <summary>Registra el callback de excepciones del módulo CMS; la guía lo marca como obligatorio. dwMessage = 0 y hWnd = 0: solo callback, sin mensajes de ventana.</summary>
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_SetExceptionCallBack(uint dwMessage, IntPtr hWnd, IntPtr fExceptionCallBack, IntPtr pUser);
    /// <summary>Comando de configuración ISUP por XML: GETDEVICECONFIG / SETDEVICECONFIG con &lt;Params&gt;&lt;ConfigCmd&gt;…&lt;/ConfigCmd&gt;… (capítulo 16.2 de la guía).</summary>
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_XMLConfig(int lUserID, ref NET_EHOME_XML_CFG pXmlCfg, uint dwConfigSize);
    /// <summary>Ordenes de control por XML (sobre PPVSPMessage completo): ADJUSTTIME, REBOOT, GETDEVICEWORKSTATUS... (capitulo 16.1 de la guia).</summary>
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_XMLRemoteControl(int lUserID, ref NET_EHOME_XML_REMOTE_CTRL_PARAM lpCtrlParam, uint dwCtrlSize);

    // ---- Control remoto: PTZ, presets y salidas de alarma (NET_ECMS_RemoteControl; capítulos 7 y 8 de la guía) ----
    public const uint NET_EHOME_MANUAL_IOOUT = 19;     // salida de alarma manual: sin condición, entrada = NET_EHOME_MANUAL_IOOUT_CTRL
    public const uint NET_EHOME_PTZ_CTRL = 1000;       // PTZ: condición = canal (4 bytes), entrada = NET_EHOME_PTZ_PARAM
    public const uint NET_EHOME_PRESET_CTRL = 1001;    // presets: condición = canal, entrada = NET_EHOME_PRESET_PARAM
    public const uint NET_EHOME_PZIN = 1002;           // zoom 3D por rectángulo (NET_EHOME_PZIN_PARAM)
    public const uint NET_EHOME_PTRACK = 1003;         // centrar en un punto (NET_EHOME_POINT)
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_RemoteControl(int lUserID, uint dwCommand, ref NET_EHOME_REMOTE_CTRL_PARAM lpCtrlParam);

    // ---- Configuración por el SDK (NET_ECMS_GetDevConfig): sirve también a los equipos 2.0/4.0, que no tienen ISAPI ----
    public const uint NET_EHOME_GET_DEVICE_INFO = 1;   // salida = NET_EHOME_DEVICE_INFO (Layout.DevInfo*)
    public const uint NET_EHOME_GET_VERSION_INFO = 2;  // salida = NET_EHOME_VERSION_INFO (Layout.Version*)
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_GetDevConfig(int lUserID, uint dwCommand, ref NET_EHOME_CONFIG lpConfig, uint dwConfigSize);

    /// <summary>Despierta un equipo en reposo (ENUM_DEV_SLEEP). Tarda alrededor de un minuto y el equipo vuelve a registrarse.</summary>
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_WakeUp(int lUserID);

    /// <summary>Fuerza la baja de un equipo registrado (al quitarlo del directorio o cambiarle la clave): el equipo vuelve a registrarse y se le pide la clave nueva.</summary>
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_ForceLogout(int lUserID);

    // enumType de SetSDKInitCfg (supuesto): 0 = ruta de libeay, 1 = ruta de ssleay
    public const int INIT_CFG_LIBEAY_PATH = 0;
    public const int INIT_CFG_SSLEAY_PATH = 1;
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_SetSDKInitCfg(int enumType, IntPtr lpInBuff);

    // ---- Stream ----
    const string STREAM = "HCISUPStream";
    [DllImport(STREAM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESTREAM_SetSDKInitCfg(int enumType, IntPtr lpInBuff);

    [DllImport(STREAM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESTREAM_Init();
    [DllImport(STREAM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESTREAM_Fini();
    [DllImport(STREAM)] public static extern int NET_ESTREAM_GetLastError();
    [DllImport(STREAM)] public static extern uint NET_ESTREAM_GetBuildVersion();
    /// <summary>Registra el callback de excepciones de vídeo en vivo, reproducción y voz (HCISUPStream.h); la guía lo marca como obligatorio.</summary>
    [DllImport(STREAM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESTREAM_SetExceptionCallBack(uint dwMessage, IntPtr hWnd, IntPtr fExceptionCallBack, IntPtr pUser);
    [DllImport(STREAM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESTREAM_SetLogToFile(int iLogLevel, [MarshalAs(UnmanagedType.LPStr)] string? strLogDir, [MarshalAs(UnmanagedType.Bool)] bool bAutoDel);
    [DllImport(STREAM)] public static extern int NET_ESTREAM_StartListenPreview(ref NET_EHOME_LISTEN_PREVIEW_CFG p);
    [DllImport(STREAM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESTREAM_StopListenPreview(int iListenHandle);
    [DllImport(STREAM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESTREAM_SetPreviewDataCB(int iPreviewHandle, ref NET_EHOME_PREVIEW_DATA_CB_PARAM p);
    [DllImport(STREAM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESTREAM_StopPreview(int iPreviewHandle);

    // ---- Busqueda de archivos grabados (HCISUPCMS.h; en ISUP 2.0/4.0 la reproduccion es por nombre de archivo) ----
    public const int ENUM_SEARCH_RECORD_FILE = 0;
    public const int ENUM_GET_NEXT_STATUS_SUCCESS = 1000, ENUM_GET_NEXT_STATUS_NO_FILE = 1001, ENUM_GET_NEXT_STATUS_NEED_WAIT = 1002,
        ENUM_GET_NEXT_STATUS_FINISH = 1003, ENUM_GET_NEXT_STATUS_FAILED = 1004, ENUM_GET_NEXT_STATUS_NOT_SUPPORT = 1005;
    [DllImport(CMS)] public static extern int NET_ECMS_StartFindFile_V11(int lUserID, int lSearchType, ref NET_EHOME_REC_FILE_COND pFindCond, uint dwCondSize);
    [DllImport(CMS)] public static extern int NET_ECMS_FindNextFile_V11(int lHandle, ref NET_EHOME_REC_FILE pFindData, uint dwDataSize);
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_StopFindFile(int lHandle);

    // ---- Reproduccion de grabaciones (en ISUP 5.0 solo por hora) ----
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_StartPlayBack(int lUserID, ref NET_EHOME_PLAYBACK_INFO_IN pIn, ref NET_EHOME_PLAYBACK_INFO_OUT pOut);
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_StartPushPlayBack(int lUserID, ref NET_EHOME_PUSHSTREAM_IN pIn, ref NET_EHOME_PUSHSTREAM_OUT pOut);
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_StopPlayBack(int lUserID, int lSessionID);
    [DllImport(STREAM)] public static extern int NET_ESTREAM_StartListenPlayBack(ref NET_EHOME_LISTEN_PREVIEW_CFG p);
    [DllImport(STREAM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESTREAM_StopListenPlayBack(int iListenHandle);
    [DllImport(STREAM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESTREAM_SetPlayBackDataCB(int iPlayBackHandle, ref NET_EHOME_PREVIEW_DATA_CB_PARAM p);
    [DllImport(STREAM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESTREAM_StopPlayBack(int iPlayBackHandle);

    // ---- Voz bidireccional por el servidor de medios (capítulo «voice talk with SMS» de la guía; firmas de HCISUPCMS.h / HCISUPStream.h) ----
    // CMS: StartVoiceWithStmServer (le indica al equipo a qué puerto de voz conectarse) + StartPushVoiceStream (empieza a mandar).
    // Stream: StartListenVoiceTalk abre el puerto; el equipo se conecta y llega VOICETALK_NEWLINK_CB; con el handle de ese enlace se recibe
    // (SetVoiceTalkDataCB) y se envía (SendVoiceTalkData: 160 bytes por trama en G.711, 80 en G.722).
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int VoiceNewLinkCb(int lHandle, IntPtr pNewLinkCBInfo, IntPtr pUserData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void VoiceDataCb(int lHandle, IntPtr pDataCBInfo, IntPtr pUserData);

    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_StartVoiceWithStmServer(int lUserID, ref NET_EHOME_VOICE_TALK_IN lpVoiceTalkIn, ref NET_EHOME_VOICE_TALK_OUT lpVoiceTalkOut);
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_StartPushVoiceStream(int lUserID, ref NET_EHOME_PUSHVOICE_IN lpPushParamIn, ref NET_EHOME_PUSHVOICE_OUT lpPushParamOut);
    [DllImport(CMS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ECMS_StopVoiceTalkWithStmServer(int lUserID, int lSessionID);
    [DllImport(STREAM)] public static extern int NET_ESTREAM_StartListenVoiceTalk(ref NET_EHOME_LISTEN_VOICETALK_CFG pListenParam);
    [DllImport(STREAM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESTREAM_StopListenVoiceTalk(int lListenHandle);
    [DllImport(STREAM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESTREAM_SetVoiceTalkDataCB(int lHandle, ref NET_EHOME_VOICETALK_DATA_CB_PARAM pStruCBParam);
    [DllImport(STREAM)] public static extern int NET_ESTREAM_SendVoiceTalkData(int lHandle, ref NET_EHOME_VOICETALK_DATA pVoicTalkData);
    [DllImport(STREAM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESTREAM_StopVoiceTalk(int lHandle);

    // ---- Alarm ----
    const string ALARM = "HCISUPAlarm";
    [DllImport(ALARM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_EALARM_SetSDKInitCfg(int enumType, IntPtr lpInBuff);

    [DllImport(ALARM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_EALARM_Init();
    [DllImport(ALARM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_EALARM_Fini();
    [DllImport(ALARM)] public static extern int NET_EALARM_GetLastError();
    [DllImport(ALARM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_EALARM_SetLogToFile(int iLogLevel, [MarshalAs(UnmanagedType.LPStr)] string? strLogDir, [MarshalAs(UnmanagedType.Bool)] bool bAutoDel);
    [DllImport(ALARM)] public static extern int NET_EALARM_StartListen(ref NET_EHOME_ALARM_LISTEN_PARAM p);
    [DllImport(ALARM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_EALARM_StopListen(int iListenHandle);
    [DllImport(ALARM)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_EALARM_SetDeviceSessionKey(ref NET_EHOME_DEV_SESSIONKEY p);

    // ---- Storage Server: servidor de imágenes al que los equipos suben las fotos de sus eventos (HCISUPSS.h) ----
    // Se anuncia al equipo en la respuesta de registro (struPictureSever + dwPicServerType de NET_EHOME_SERVER_INFO_V50).
    const string SS = "HCISUPSS";
    public const int SS_INIT_CFG_LIBEAY_PATH = 4;   // enum NET_EHOME_SS_INIT_CFG_TYPE (distinto del de CMS/Alarm, que usa 0 y 1)
    public const int SS_INIT_CFG_SSLEAY_PATH = 5;
    public const int SS_INIT_CFG_PUBLIC_IP_PORT = 3;  // direccion publica que el servidor anuncia a los equipos (NET_EHOME_IPADDRESS)
    public const int SS_INIT_CFG_SQLITE3_PATH = 6;  // ruta de sqlite3.dll (el modulo guarda un indice de archivos en SQLite)
    public const int SS_MSG_TOMCAT = 1;             // NET_EHOME_SS_MSG_TYPE
    public const int SS_MSG_KMS_USER_PWD = 2;
    public const int SS_MSG_CLOUD_AK = 3;

    /// <summary>EHomeSSStorageCallBack: el SDK entrega el archivo subido; se guarda y se devuelve su ruta en pFilePath (MAX_PATH).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int SsStorageCb(int iHandle, IntPtr pFileName, IntPtr pFileBuf, uint dwFileLen, IntPtr pFilePath, IntPtr pUser);
    /// <summary>EHomeSSMsgCallBack: mensajes del servidor de imágenes (URLs Tomcat, validación KMS, clave secreta para la AK de la nube).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int SsMsgCb(int iHandle, int enumType, IntPtr pOutBuffer, uint dwOutLen, IntPtr pInBuffer, uint dwInLen, IntPtr pUser);

    [DllImport(SS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESS_Init();
    [DllImport(SS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESS_Init_V11(ref NET_EHOME_SS_INIT_PARAM p);   // con clave de la base de datos SQLite
    [DllImport(SS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESS_Fini();
    [DllImport(SS)] public static extern uint NET_ESS_GetLastError();
    [DllImport(SS)] public static extern uint NET_ESS_GetBuildVersion();
    [DllImport(SS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESS_SetLogToFile(int iLogLevel, [MarshalAs(UnmanagedType.LPStr)] string? strLogDir, [MarshalAs(UnmanagedType.Bool)] bool bAutoDel);
    [DllImport(SS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESS_SetSDKInitCfg(int enumType, IntPtr lpInBuff);
    [DllImport(SS)] public static extern int NET_ESS_StartListen(ref NET_EHOME_SS_LISTEN_PARAM p);
    [DllImport(SS)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool NET_ESS_StopListen(int lListenHandle);

    // ---- Helpers ----
    public static void SetIp(ref NET_EHOME_IPADDRESS a, string ip, ushort port)
    {
        var b = System.Text.Encoding.ASCII.GetBytes(ip);
        fixed (byte* p = a.szIP)
        {
            for (int i = 0; i < 128; i++) p[i] = i < b.Length && i < 127 ? b[i] : (byte)0;
        }
        a.wPort = port;
    }

    /// <summary>Lee una cadena ASCII terminada en NUL (maximo maxLen bytes).</summary>
    public static string ReadCString(IntPtr p, int maxLen)
    {
        if (p == IntPtr.Zero) return "";
        var bytes = new List<byte>();
        for (int i = 0; i < maxLen; i++)
        {
            byte c = Marshal.ReadByte(p, i);
            if (c == 0) break;
            bytes.Add(c);
        }
        return System.Text.Encoding.ASCII.GetString(bytes.ToArray());
    }
}

// ---- Structs (layout x64, secuencial como en C) ----

/// <summary>Parametros de NET_ECMS_XMLRemoteControl (HCISUPCMS.h). Tiempos en ms (5000 por defecto).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_XML_REMOTE_CTRL_PARAM
{
    public uint dwSize;
    public IntPtr lpInbuffer;
    public uint dwInBufferSize;
    public uint dwSendTimeOut;
    public uint dwRecvTimeOut;
    public IntPtr lpOutBuffer;
    public uint dwOutBufferSize;
    public IntPtr lpStatusBuffer;
    public uint dwStatusBufferSize;
    public fixed byte byRes[16];
}

/// <summary>Envio de ordenes al equipo (136 bytes, HCISUPPublic.h): espera de respuesta en ms y numero de envios (1-3, para redes con perdidas).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_SEND_PARAM
{
    public uint dwSize;
    public uint dwRecvTimeOut;
    public byte bySendTimes;
    public fixed byte byRes2[127];
}

/// <summary>Parametros del proxy (136 bytes, HCISUPCMS.h): direccion de escucha, protocolo (0 = TCP) y tipo (0 = NetSDK, 1 = HTTP).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_PT_PARAM
{
    public NET_EHOME_IPADDRESS struIP;
    public byte byProtocolType;
    public byte byProxyType;
    public fixed byte byRes[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_IPADDRESS
{
    public fixed byte szIP[128];
    public ushort wPort;
    public fixed byte byRes[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_CMS_LISTEN_PARAM
{
    public NET_EHOME_IPADDRESS struAddress;
    public IntPtr fnCB;            // DEVICE_REGISTER_CB
    public IntPtr pUserData;
    public uint dwKeepAliveSec;    // latido del registro (0 = 30 s)
    public uint dwTimeOutCount;    // latidos perdidos antes de dar al equipo por fuera de línea (0 = 3)
    public fixed byte byRes[24];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_ALARM_LISTEN_PARAM
{
    public NET_EHOME_IPADDRESS struAddress;
    public IntPtr fnMsgCb;         // EHomeMsgCallBack
    public IntPtr pUserData;
    public byte byProtocolType;    // 0 = TCP, 1 = UDP, 2 = MQTT (ISUP 5.0)
    public byte byUseCmsPort;
    public byte byUseThreadPool;
    public byte byRes1;
    public uint dwKeepAliveSec;    // latido del enlace de alarmas (0 = 30 s)
    public uint dwTimeOutCount;    // latidos perdidos antes de cortar (0 = 3)
    public fixed byte byRes[20];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_LISTEN_PREVIEW_CFG
{
    public NET_EHOME_IPADDRESS struIPAdress;
    public IntPtr fnNewLinkCB;     // PREVIEW_NEWLINK_CB
    public IntPtr pUser;
    public byte byLinkMode;        // 0 = TCP, 1 = UDP
    public fixed byte byRes[127];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_PREVIEW_DATA_CB_PARAM
{
    public IntPtr fnPreviewDataCB; // PREVIEW_DATA_CB
    public IntPtr pUserData;
    public byte byStreamFormat;    // 0 = PS
    public fixed byte byRes[127];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_PREVIEWINFO_IN
{
    public int iChannel;
    public uint dwStreamType;      // 0 = principal, 1 = secundario
    public uint dwLinkMode;        // 0 = TCP, 1 = UDP
    public NET_EHOME_IPADDRESS struStreamSever;
    public fixed byte byRes[128];  // margen por si el layout real es mayor
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_PREVIEWINFO_OUT
{
    public int lSessionID;
    public IntPtr hHandle;
    public fixed byte byRes[512];  // sobredimensionado a proposito
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_PUSHSTREAM_IN
{
    public uint dwSize;
    public int lSessionID;
    public fixed byte byRes[128];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_PUSHSTREAM_OUT
{
    public uint dwSize;
    public fixed byte byRes[512];
}

/// <summary>Hora del equipo (hora local salvo que byLocalOrUTC indique UTC). 12 bytes. Reconstruida de memoria.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_TIME
{
    public ushort wYear;
    public byte byMonth, byDay, byHour, byMinute, bySecond, byRes1;
    public ushort wMSecond;
    public fixed byte byRes2[2];
}

/// <summary>
/// Peticion de reproduccion (656 bytes). Layout reconstruido de memoria y de los mensajes de log del SDK:
/// los campos modo de enlace/cifrado/local-o-UTC/segmento duplicado aparecen en sus textos de error.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 656)]
public unsafe struct NET_EHOME_PLAYBACK_INFO_IN
{
    [FieldOffset(0)] public uint dwSize;
    [FieldOffset(4)] public uint dwChannel;
    [FieldOffset(8)] public byte byPlayBackMode;      // 0 = por nombre (NO soportado en ISUP 5.0), 1 = por hora
    [FieldOffset(9)] public byte byStreamPackage;     // 0 = PS
    [FieldOffset(10)] public byte byLinkMode;         // 0 = TCP
    [FieldOffset(11)] public byte byLinkEncrypt;      // 0 = sin cifrar
    // union (512 bytes desde el offset 12): por hora = inicio, fin, local/UTC, segmento duplicado
    [FieldOffset(12)] public NET_EHOME_TIME struStartTime;
    [FieldOffset(24)] public NET_EHOME_TIME struStopTime;
    [FieldOffset(36)] public byte byLocalOrUTC;       // 0 = hora local del equipo
    [FieldOffset(37)] public byte byDuplicateSegment;
    // union, por nombre (struPlayBackbyName, verificado con HCISUPCMS.h): nombre[100], tipo de desplazamiento, desplazamiento, tamano, flujo
    [FieldOffset(12)] public fixed byte szFileName[100];
    [FieldOffset(112)] public uint dwSeekType;        // 0 = desplazamiento en bytes, 1 = en segundos
    [FieldOffset(116)] public uint dwFileOffset;
    [FieldOffset(120)] public uint dwFileSpan;        // 0 = hasta el final del archivo
    [FieldOffset(124)] public byte byNameStreamType;  // 0 = el que tenga el equipo, 1 = principal, 2 = secundario
    [FieldOffset(524)] public NET_EHOME_IPADDRESS struStreamSever;   // servidor de video al que el equipo enviara la reproduccion
}

/// <summary>Condicion de busqueda de grabaciones (104 bytes, HCISUPCMS.h).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_REC_FILE_COND
{
    public uint dwChannel;              // desde 1
    public uint dwRecType;              // 0xff = todos los tipos
    public NET_EHOME_TIME struStartTime;
    public NET_EHOME_TIME struStopTime;
    public uint dwStartIndex;
    public uint dwMaxFileCountPer;      // la guia recomienda como maximo 8
    public byte byLocalOrUTC;           // 0 = hora local del equipo
    public fixed byte byRes[63];
}

/// <summary>Un archivo grabado (272 bytes, HCISUPCMS.h).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_REC_FILE
{
    public uint dwSize;
    public fixed byte sFileName[100];
    public NET_EHOME_TIME struStartTime;
    public NET_EHOME_TIME struStopTime;
    public uint dwFileSize;
    public uint dwFileMainType;
    public uint dwFileSubType;
    public uint dwFileIndex;
    public byte byTimeDiffH, byTimeDiffM;
    public fixed byte byRes[126];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_PLAYBACK_INFO_OUT
{
    public int lSessionID;
    public int lHandle;
    public fixed byte byRes[512];   // sobredimensionado a proposito
}

/// <summary>Parámetro del túnel ISAPI (NET_EHOME_PTXML_PARAM, HCISUPCMS.h): petición, condición, entrada, salida, longitud recibida y espera en ms.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_PTXML_PARAM
{
    public IntPtr pRequestUrl;
    public uint dwRequestUrlLen;
    public IntPtr pCondBuffer;
    public uint dwCondSize;
    public IntPtr pInBuffer;
    public uint dwInSize;
    public IntPtr pOutBuffer;
    public uint dwOutSize;
    public uint dwReturnedXMLLen;   // largo real de la respuesta del equipo
    public uint dwRecvTimeOut;      // ms (0 = 5000)
    public uint dwHandle;
    public fixed byte byRes[24];
}

/// <summary>Parámetro de NET_ESS_Init_V11 (NET_EHOME_SS_INIT_PARAM): clave de la base de datos del servidor de imágenes.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_SS_INIT_PARAM
{
    public fixed byte szKey[32];
    public fixed byte byRes[224];
}

/// <summary>Parámetro de escucha del servidor de imágenes (NET_EHOME_SS_LISTEN_PARAM, HCISUPSS.h; layout verificado con el header).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_SS_LISTEN_PARAM
{
    public NET_EHOME_IPADDRESS struAddress;
    public fixed byte szKMS_UserName[512];
    public fixed byte szKMS_Password[512];
    public IntPtr fnSStorageCb;      // EHomeSSStorageCallBack
    public IntPtr fnSSMsgCb;         // EHomeSSMsgCallBack
    public fixed byte szAccessKey[64];   // AK del protocolo de almacenamiento ISUP 5.0 (tipo nube)
    public fixed byte szSecretKey[64];   // SK
    public IntPtr pUserData;
    public byte byHttps;
    public fixed byte byRes1[3];
    public IntPtr fnSSRWCb;          // EHomeSSRWCallBack (no se usa)
    public IntPtr fnSSRWCbEx;        // EHomeSSRWCallBackEx (no se usa)
    public byte bySecurityMode;      // 0 = modo seguro activado (por defecto), 1 = desactivado
    public fixed byte byRes[51];
}

/// <summary>
/// Parámetro de NET_ECMS_XMLConfig (NET_EHOME_XML_CFG, HCISUPCMS.h; 96 bytes en x64): comando en texto (GETDEVICECONFIG / SETDEVICECONFIG),
/// entrada &lt;Params&gt;…, salida, tiempos de espera en ms (5 s por defecto) y un buffer opcional para el estado XML que devuelve el equipo.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_XML_CFG
{
    public IntPtr pCmdBuf;
    public uint dwCmdLen;
    public IntPtr pInBuf;
    public uint dwInSize;
    public IntPtr pOutBuf;
    public uint dwOutSize;
    public uint dwSendTimeOut;
    public uint dwRecvTimeOut;
    public IntPtr pStatusBuf;
    public uint dwStatusSize;
    public fixed byte byRes[24];
}

/// <summary>Parámetro de NET_ECMS_GetDevConfig (NET_EHOME_CONFIG, HCISUPCMS.h; 88 bytes en x64): condición, entrada y salida.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_CONFIG
{
    public IntPtr pCondBuf;
    public uint dwCondSize;
    public IntPtr pInBuf;
    public uint dwInSize;
    public IntPtr pOutBuf;
    public uint dwOutSize;
    public fixed byte byRes[40];
}

/// <summary>Parámetro de NET_ECMS_RemoteControl (HCISUPCMS.h; 72 bytes en x64): buffer de condición (p. ej. el canal) y buffer de control.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_REMOTE_CTRL_PARAM
{
    public uint dwSize;
    public IntPtr lpCondBuffer;
    public uint dwCondBufferSize;
    public IntPtr lpInbuffer;
    public uint dwInBufferSize;
    public fixed byte byRes[32];
}

/// <summary>Orden PTZ (NET_EHOME_PTZ_PARAM, 36 bytes): comando EN_PTZ_CMD, acción 0 = empezar / 1 = parar, velocidad 0 a 7.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_PTZ_PARAM
{
    public uint dwSize;
    public byte byPTZCmd;
    public byte byAction;
    public byte bySpeed;
    public fixed byte byRes[29];
}

/// <summary>Preset (NET_EHOME_PRESET_PARAM, 44 bytes): 1 = guardar, 2 = borrar, 3 = ir al preset.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_PRESET_PARAM
{
    public uint dwSize;
    public byte byPresetCmd;
    public fixed byte byRes1[3];
    public uint dwPresetIndex;
    public fixed byte byRes2[32];
}

/// <summary>Salida de alarma manual (NET_EHOME_MANUAL_IOOUT_CTRL, 32 bytes): salida desde 1, duración en s (0 = hasta desactivar), acción 0 = apagar / 1 = encender.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_MANUAL_IOOUT_CTRL
{
    public uint dwSize;
    public uint dwChan;
    public uint dwDelayTime;
    public byte byAction;
    public fixed byte byRes[19];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_DEV_SESSIONKEY
{
    public fixed byte sDeviceID[Isup.MaxDeviceIdLen];
    public fixed byte sSessionKey[16];
}

// ---- Voz por el servidor de medios (verificado con HCISUPCMS.h / HCISUPStream.h del paquete ISUP 5.0; layout x64) ----

/// <summary>Escucha de voz del servidor de medios (NET_EHOME_LISTEN_VOICETALK_CFG).</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_LISTEN_VOICETALK_CFG
{
    public NET_EHOME_IPADDRESS struIPAdress;
    public IntPtr fnNewLinkCB;     // VOICETALK_NEWLINK_CB
    public IntPtr pUser;
    public byte byLinkMode;        // 0 = TCP (UDP reservado)
    public byte byLinkEncrypt;     // 0 = sin TLS
    public fixed byte byRes[126];
}

/// <summary>
/// Petición de voz (NET_EHOME_VOICE_TALK_IN). byEncodingType es un arreglo de 9 enum NET_EHOME_TALK_ENCODING_TYPE (4 bytes cada uno en MSVC):
/// codificaciones aceptadas, en orden de preferencia (todo en 0 = la del equipo). byBroadcast = 1: solo bajada (parlante), el equipo no manda audio.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_VOICE_TALK_IN
{
    public uint dwVoiceChan;
    public NET_EHOME_IPADDRESS struStreamSever;
    public fixed int byEncodingType[9];
    public byte byLinkEncrypt;
    public byte byBroadcast;
    public byte byBroadLevel;       // prioridad 0-15 (con byBroadcast = 1)
    public byte byBroadVolume;      // volumen 0-15 (con byBroadcast = 1)
    public byte byAudioSamplingRate;
    public fixed byte byRes[114];
}

/// <summary>Respuesta de la petición de voz (NET_EHOME_VOICE_TALK_OUT): sesión que asigna el equipo.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_VOICE_TALK_OUT
{
    public int lSessionID;
    public int lHandle;
    public fixed byte byRes[124];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_PUSHVOICE_IN
{
    public uint dwSize;
    public int lSessionID;
    public fixed byte byToken[64];
    public fixed byte byRes[64];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_PUSHVOICE_OUT
{
    public uint dwSize;
    public int lHandle;
    public fixed byte byRes[124];
}

/// <summary>Trama de audio hacia el equipo (NET_EHOME_VOICETALK_DATA): 160 bytes por envío en G.711, 80 en G.722.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_VOICETALK_DATA
{
    public IntPtr pSendBuf;
    public uint dwDataLen;
    public uint dwTimeout;          // ms (0 = 5000)
    public fixed byte byRes[124];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NET_EHOME_VOICETALK_DATA_CB_PARAM
{
    public IntPtr fnVoiceTalkDataCB;   // VOICETALK_DATA_CB
    public IntPtr pUserData;
    public fixed byte byRes[128];
}

/// <summary>Offsets de las estructuras que el SDK entrega por puntero en sus callbacks (x64, de los .h oficiales).</summary>
public static class Layout
{
    // NET_EHOME_DEVICE_INFO (512 bytes, solo DWORD y arreglos de bytes): dwSize 0 · dwChannelNumber (analógicos) 4 · dwChannelAmount
    // (analógicos + IP) 8 · dwDevType 12 (1 DVR, 3 DVS, 30 IPC, 40 domo IP…) · dwDiskNumber 16 · sSerialNumber[128] 20 · alarmas 148-163 ·
    // dwStartChannel 164 · dwAudioChanNum 168 · dwMaxDigitChannelNum 172 · dwAudioEncType 176 · SIM 180-339 · cero canal 340-351 ·
    // wDevClass 352 · byStartDTalkChan 354 · byRes[157].
    public const int DevInfoSize = 512, DevInfoChannels = 4, DevInfoChannelAmount = 8, DevInfoType = 12, DevInfoDisks = 16, DevInfoSerial = 20,
        DevInfoStartChannel = 164, DevInfoMaxDigital = 172, DevInfoAudioEnc = 176;
    // NET_EHOME_VERSION_INFO (256 bytes): dwSize · sSoftwareVersion[32] 4 · sDSPSoftwareVersion[32] · sPanelVersion[32] · sHardwareVersion[32] · byRes[124].
    public const int VersionInfoSize = 256, VersionSoftware = 4;

    // NET_EHOME_VOICETALK_NEWLINK_CB_INFO: szDeviceID[256] · dwEncodeType (0 G.722, 1 G.711µ, 2 G.711a, 3 G.726, 4 AAC, 5 MP2L2, 6 PCM, 7 MP3)
    // · sDeviceSerial[12] · dwAudioChan · lSessionID · byToken[64] · fnVoiceTalkDataCB · pUserData. NET_EHOME_VOICETALK_DATA_CB_INFO: pData 0 · dwDataLen 8.
    public const int VoiceDeviceId = 0, VoiceEncodeType = 256, VoiceSerial = 260, VoiceAudioChannel = 272, VoiceSessionId = 276;

    // NET_EHOME_NEWLINK_CB_MSG (vídeo y reproducción): szDeviceID[256] 0 · iSessionID 256 · dwChannelNo 260 · byStreamType 264.
    public const int LinkDeviceId = 0, LinkSessionId = 256, LinkChannel = 260;

    // NET_EHOME_PREVIEW_CB_MSG: byDataType 0 · pRecvdata 8 · dwDataLen 16.
    public const int DataType = 0, DataPointer = 8, DataLength = 16;

    // NET_EHOME_DEV_REG_INFO (+ _V12): byDeviceID[256] 8 · byFirmwareVersion[24] 264 · struDevAdd 288 (IP[128] + puerto) · dwDevType 420 ·
    // byPassWord[32] 428 · sDeviceSerial[12] 460 · byDevProtocolVersion[6] 475 · bySessionKey[16] 481 · (V12) struRegAddr 524 · sDevName[64] 656 ·
    // byDeviceFullSerial[64] 720.
    public const int RegDeviceId = 8, RegFirmware = 264, RegAddress = 288, RegDevType = 420, RegPassword = 428, RegSerial = 460,
        RegProtocolVersion = 475, RegSessionKey = 481, RegDevName = 656, RegFullSerial = 720, RegV12Size = 784;

    // NET_EHOME_ALARM_MSG: dwAlarmType 0 · pAlarmInfo 8 · dwAlarmInfoLen 16 · pXmlBuf 24 · dwXmlBufLen 32 · sSerialNumber[12] 36.
    public const int MsgType = 0, MsgInfo = 8, MsgInfoLen = 16, MsgXml = 24, MsgXmlLen = 32, MsgSerial = 36;

    // NET_EHOME_ALARM_ISAPI_INFO: pAlarmData 0 · dwAlarmDataLen 8 · byDataType 12 (1 XML, 2 JSON) · byPicturesNumber 13 · pPicPackData 16.
    // NET_EHOME_ALARM_ISAPI_PICDATA (272 bytes): dwPicLen 0 · szFilename[256] 8 · pPicData 264.
    public const int IsapiData = 0, IsapiDataLen = 8, IsapiPicCount = 13, IsapiPics = 16, IsapiPicEntry = 272, IsapiPicFile = 8, IsapiPicData = 264;

    // NET_EHOME_CID_INFO (HCISUPAlarm.h, 536 bytes): byDeviceID[256] 4 · dwCIDCode 260 · dwCIDType 264 · dwSubSysNo 268 · byCIDDescribe[32] 272 ·
    // byTriggerTime[32] 304 · byUploadTime[32] 336 · struCIDParam 368 { dwUserType 368 · lUserNo 372 · lZoneNo 376 · lKeyboardNo 380 ·
    // lVideoChanNo 384 · lDiskNo 388 · lModuleAddr 392 · byUserName[32] 396 }.
    public const int CidDeviceId = 4, CidCode = 260, CidType = 264, CidSubSys = 268, CidDescribe = 272, CidTriggerTime = 304,
        CidUserType = 368, CidUserNo = 372, CidZoneNo = 376, CidUserName = 396, CidMinSize = 460;

    // NET_EHOME_ALARM_INFO (440 bytes): szAlarmTime[32] 4 · szDeviceID[256] 36 · dwAlarmType 292 · dwAlarmAction 296 · dwVideoChannel 300 ·
    // dwAlarmInChannel 304 · dwDiskNumber 308 · byRemark[64] 312 · byRetransFlag 376.
    public const int BasicTime = 4, BasicDeviceId = 36, BasicType = 292, BasicAction = 296, BasicChannel = 300, BasicAlarmIn = 304,
        BasicDisk = 308, BasicRemark = 312, BasicRetrans = 376, BasicMinSize = 412;

    // NET_EHOME_SERVER_INFO_V50 (respuesta de registro): dwSize 0 · dwKeepAliveSec 4 · dwTimeOutCount 8 · struTCPAlarmSever 12 · struUDPAlarmSever 144 ·
    // dwAlarmServerType 276 (0 UDP, 1 UDP+TCP, 2 MQTT) · struNTPSever 280 · dwNTPInterval 412 · struPictureSever 416 · dwPicServerType 548 ·
    // struBlackListServer 552 (292) · struRedirectSever 844 · byClouldAccessKey[64] 976 · byClouldSecretKey[64] 1040 · byClouldHttps 1104 ·
    // dwAlarmKeepAliveSec 1108 · dwAlarmTimeOutCount 1112 · dwClouldPoolId 1116 (1488 bytes en total).
    public const int SrvKeepAlive = 4, SrvTimeOutCount = 8, SrvTcpAlarm = 12, SrvUdpAlarm = 144, SrvAlarmType = 276, SrvNtp = 280, SrvNtpInterval = 412,
        SrvPicture = 416, SrvPicType = 548, SrvCloudAk = 976, SrvCloudSk = 1040, SrvCloudHttps = 1104, SrvAlarmKeepAlive = 1108, SrvAlarmTimeOut = 1112,
        SrvCloudPoolId = 1116, SrvMinSize = 1120;
}
