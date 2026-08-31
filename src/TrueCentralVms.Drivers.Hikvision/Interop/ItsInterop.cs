using System.Runtime.InteropServices;

namespace TrueCentralVms.Drivers.Hikvision.Interop;

/// <summary>
/// Interop del subsistema ITS/ANPR de HCNetSDK, calcado de la cabecera
/// <c>incEn\HCNetSDK.h</c> de la versión 6.1.9.48 que distribuye este producto.
///
/// POR QUÉ NO SE USA <see cref="CHCNetSDK"/>: ese archivo viene heredado de un
/// SDK anterior y sus estructuras de patentes quedaron cortas —
/// <c>NET_DVR_PLATE_INFO</c> perdió diez campos y dos punteros antes de
/// <c>sLicense</c>, y <c>NET_ITS_PICTURE_INFO</c> otros cuatro—, así que
/// desempaquetarlas con esa versión corre todos los campos y devuelve basura
/// (patentes con caracteres fantasma, velocidades de cuatro dígitos y cero
/// imágenes). Se verificó contra una DS-2CD7A26G0/P-IZS real.
///
/// Reglas x64 respetadas aquí: los punteros ocupan 8 bytes y alinean a 8, y el
/// resto usa alineación natural (la misma con que se compiló el DLL). El
/// tamaño esperado de cada estructura va anotado en su documentación.
/// </summary>
internal static class ItsInterop
{
    public const int MaxLicenseLen = 16;
    public const int MaxCategoryLen = 8;
    public const int MaxIdLen = 48;
    public const int MaxAlarmReasonLen = 32;
    public const int MaxTimeLen = 32;

    // ------------------------------------------------------------------
    // Estructuras auxiliares
    // ------------------------------------------------------------------

    /// <summary>Rectángulo normalizado 0..1 sobre la imagen (16 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_VCA_RECT
    {
        public float fX;
        public float fY;
        public float fWidth;
        public float fHeight;
    }

    /// <summary>Marca de tiempo del equipo (12 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_TIME_V30
    {
        public ushort wYear;
        public byte byMonth;
        public byte byDay;
        public byte byHour;
        public byte byMinute;
        public byte bySecond;
        public byte byISO8601;
        public ushort wMilliSec;
        public sbyte cTimeDifferenceH;
        public sbyte cTimeDifferenceM;
    }

    /// <summary>Parámetros de armado del canal de alarma (20 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_SETUPALARM_PARAM
    {
        public uint dwSize;
        /// <summary>Prioridad: 0 alta, 1 media, 2 baja.</summary>
        public byte byLevel;
        /// <summary>0 = NET_DVR_PLATE_RESULT (antiguo); 1 = NET_ITS_PLATE_RESULT.</summary>
        public byte byAlarmInfoType;
        public byte byRetAlarmTypeV40;
        public byte byRetDevInfoVersion;
        public byte byRetVQDAlarmType;
        public byte byFaceAlarmDetection;
        public byte bySupport;
        public byte byBrokenNetHttp;
        public ushort wTaskNo;
        public byte byDeployType;
        public byte bySubScription;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public byte[] byRes1;
        public byte byAlarmTypeURL;
        public byte byCustomCtrl;
    }

    // ------------------------------------------------------------------
    // Patente y vehículo
    // ------------------------------------------------------------------

    /// <summary>Lectura de la placa (96 bytes en x64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_PLATE_INFO
    {
        public byte byPlateType;
        public byte byColor;
        public byte byBright;
        /// <summary>Cantidad de caracteres leídos.</summary>
        public byte byLicenseLen;
        /// <summary>Confianza global de la lectura, 0..100.</summary>
        public byte byEntireBelieve;
        /// <summary>Región: 0 reservado, 1 Europa, 2 Rusia, 3 UE&amp;CEI, 4 Medio Oriente, 5 APAC, 6 África y América.</summary>
        public byte byRegion;
        /// <summary>Índice de país (COUNTRY_INDEX del SDK).</summary>
        public byte byCountry;
        public byte byArea;
        /// <summary>0 desconocido, 1 placa larga, 2 placa corta.</summary>
        public byte byPlateSize;
        public byte byAddInfoFlag;
        public ushort wCRIndex;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] byRes;
        public IntPtr pAddInfoBuffer;
        /// <summary>Categoría de la placa informada por el equipo (texto corto).</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxCategoryLen)]
        public byte[] sPlateCategory;
        public uint dwXmlLen;
        public IntPtr pXmlBuf;
        /// <summary>Posición de la placa sobre la imagen, normalizada 0..1.</summary>
        public NET_VCA_RECT struPlateRect;
        /// <summary>Patente leída (ASCII terminado en NUL).</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxLicenseLen)]
        public byte[] sLicense;
        /// <summary>Confianza de cada carácter, 0..100.</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxLicenseLen)]
        public byte[] byBelieve;
    }

    /// <summary>Datos del vehículo (48 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_VEHICLE_INFO
    {
        public uint dwIndex;
        public byte byVehicleType;
        /// <summary>Tono del color de la carrocería (claro/oscuro).</summary>
        public byte byColorDepth;
        /// <summary>Color de la carrocería (VCR_CLR_CLASS).</summary>
        public byte byColor;
        public byte byRadarState;
        public ushort wSpeed;
        /// <summary>Largo del vehículo en centímetros.</summary>
        public ushort wLength;
        public byte byIllegalType;
        /// <summary>Marca reconocida por el logo (VLR_VEHICLE_CLASS).</summary>
        public byte byVehicleLogoRecog;
        public byte byVehicleSubLogoRecog;
        public byte byVehicleModel;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] byCustomInfo;
        public ushort wVehicleLogoRecog;
        public byte byIsParking;
        public byte byRes;
        public uint dwParkingTime;
        public byte byBelieve;
        public byte byCurrentWorkerNumber;
        public byte byCurrentGoodsLoadingRate;
        public byte byDoorsStatus;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] byRes3;
    }

    /// <summary>
    /// Una foto del grupo que acompaña al reconocimiento (104 bytes en x64).
    /// <c>byType</c>: 0 placa, 1 escena, 2 imagen compuesta, 3 stream,
    /// 12/13 habitáculo, 14 rostro, 15 personalizada.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_ITS_PICTURE_INFO
    {
        public uint dwDataLen;
        public byte byType;
        /// <summary>0 = los datos vienen en pBuffer; 1 = quedaron en un servidor de almacenamiento.</summary>
        public byte byDataType;
        public byte byCloseUpType;
        public byte byPicRecogMode;
        public uint dwRedLightTime;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxTimeLen)]
        public byte[] byAbsTime;
        public NET_VCA_RECT struPlateRect;
        public NET_VCA_RECT struPlateRecgRect;
        public IntPtr pBuffer;
        public uint dwUTCTime;
        public byte byCompatibleAblity;
        public byte byTimeDiffFlag;
        public sbyte cTimeDifferenceH;
        public sbyte cTimeDifferenceM;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] byRes2;
    }

    /// <summary>
    /// COMM_ITS_PLATE_RESULT (0x3050): reconocimiento de las cámaras ITS
    /// modernas, con hasta 6 fotos (944 bytes en x64).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_ITS_PLATE_RESULT
    {
        public uint dwSize;
        /// <summary>Identifica la pasada del vehículo: agrupa los mensajes de sus fotos.</summary>
        public uint dwMatchNo;
        public byte byGroupNum;
        public byte byPicNo;
        public byte bySecondCam;
        public byte byFeaturePicNo;
        /// <summary>Carril que disparó la captura.</summary>
        public byte byDriveChan;
        public byte byVehicleType;
        public byte byDetSceneID;
        public byte byVehicleAttribute;
        public ushort wIllegalType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] byIllegalSubType;
        public byte byPostPicNo;
        public byte byChanIndex;
        public ushort wSpeedLimit;
        /// <summary>Canal real = byChanIndexEx * 256 + byChanIndex.</summary>
        public byte byChanIndexEx;
        public byte byVehiclePositionControl;
        public NET_DVR_PLATE_INFO struPlateInfo;
        public NET_DVR_VEHICLE_INFO struVehicleInfo;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxIdLen)]
        public byte[] byMonitoringSiteID;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxIdLen)]
        public byte[] byDeviceID;
        /// <summary>1 subida, 2 bajada, 3 bidireccional, 4 al oeste, 5 al norte, 6 al este, 7 al sur, 8 otra.</summary>
        public byte byDir;
        /// <summary>1 lazo magnético, 2 video, 3 multi-fotograma, 4 radar.</summary>
        public byte byDetectType;
        public byte byRelaLaneDirectionType;
        public byte byCarDirectionType;
        public uint dwCustomIllegalType;
        public IntPtr pIllegalInfoBuf;
        public byte byIllegalFromatType;
        /// <summary>Colgante en el parabrisas: 0 desconocido, 1 no, 2 sí.</summary>
        public byte byPendant;
        public byte byDataAnalysis;
        /// <summary>Vehículo con etiqueta amarilla: 0 desconocido, 1 no, 2 sí.</summary>
        public byte byYellowLabelCar;
        /// <summary>Transporte de carga peligrosa: 0 desconocido, 1 no, 2 sí.</summary>
        public byte byDangerousVehicles;
        /// <summary>Cinturón del conductor: 0 desconocido, 1 puesto, 2 sin cinturón.</summary>
        public byte byPilotSafebelt;
        public byte byCopilotSafebelt;
        public byte byPilotSunVisor;
        public byte byCopilotSunVisor;
        /// <summary>Conductor usando el teléfono: 0 desconocido, 1 no, 2 sí.</summary>
        public byte byPilotCall;
        public byte byBarrierGateCtrlType;
        /// <summary>0 = dato en tiempo real; 1 = histórico.</summary>
        public byte byAlarmDataType;
        /// <summary>Hora de la primera foto de la pasada (reloj del equipo).</summary>
        public NET_DVR_TIME_V30 struSnapFirstPicTime;
        public uint dwIllegalTime;
        public uint dwPicNum;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public NET_ITS_PICTURE_INFO[] struPicInfo;
    }

    /// <summary>
    /// COMM_ITS_GATE_VEHICLE (0x3052): cámaras de barrera / control de acceso
    /// vehicular, con hasta 4 fotos (1024 bytes en x64).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_ITS_GATE_VEHICLE
    {
        public uint dwSize;
        public uint dwMatchNo;
        public byte byGroupNum;
        public byte byPicNo;
        public byte bySecondCam;
        public byte byRes;
        /// <summary>Carril, 1..32.</summary>
        public ushort wLaneid;
        public byte byCamLaneId;
        public byte byRes1;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxAlarmReasonLen)]
        public byte[] byAlarmReason;
        /// <summary>0 = vehículo normal; distinto de 0 = coincidió con una lista de bloqueo.</summary>
        public ushort wBackList;
        public ushort wSpeedLimit;
        public uint dwChanIndex;
        public NET_DVR_PLATE_INFO struPlateInfo;
        public NET_DVR_VEHICLE_INFO struVehicleInfo;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxIdLen)]
        public byte[] byMonitoringSiteID;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxIdLen)]
        public byte[] byDeviceID;
        /// <summary>0 otra, 1 entrada, 2 salida.</summary>
        public byte byDir;
        public byte byDetectType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public byte[] byRes2;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxIdLen)]
        public byte[] byCardNo;
        public uint dwPicNum;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public NET_ITS_PICTURE_INFO[] struPicInfo;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxTimeLen)]
        public byte[] bySwipeTime;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 224)]
        public byte[] byRes3;
    }

    /// <summary>
    /// COMM_UPLOAD_PLATE_RESULT (0x2800): formato antiguo de los equipos
    /// previos a ITS. pBuffer1 = escena, pBuffer2 = recorte de la placa.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_PLATE_RESULT
    {
        public uint dwSize;
        public byte byResultType;
        public byte byChanIndex;
        public ushort wAlarmRecordID;
        public uint dwRelativeTime;
        /// <summary>"yyyymmddhhmmssxxx" en ASCII.</summary>
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxTimeLen)]
        public byte[] byAbsTime;
        public uint dwPicLen;
        public uint dwPicPlateLen;
        public uint dwVideoLen;
        public byte byTrafficLight;
        public byte byPicNum;
        public byte byDriveChan;
        public byte byVehicleType;
        public uint dwBinPicLen;
        public uint dwCarPicLen;
        public uint dwFarCarPicLen;
        public IntPtr pBuffer3;
        public IntPtr pBuffer4;
        public IntPtr pBuffer5;
        public byte byRelaLaneDirectionType;
        public byte byCarDirectionType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] byRes3;
        public NET_DVR_PLATE_INFO struPlateInfo;
        public NET_DVR_VEHICLE_INFO struVehicleInfo;
        public IntPtr pBuffer1;
        public IntPtr pBuffer2;
    }

    // ------------------------------------------------------------------
    // Funciones del SDK usadas por el módulo
    // ------------------------------------------------------------------
    // Se declaran aquí (y no se reutilizan las de CHCNetSDK) porque reciben
    // las estructuras corregidas de arriba.

    [DllImport("HCNetSDK.dll")]
    public static extern int NET_DVR_SetupAlarmChan_V41(int lUserID, ref NET_DVR_SETUPALARM_PARAM lpSetupParam);
}
