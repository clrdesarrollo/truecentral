using System.Runtime.InteropServices;

namespace TrueCentralVms.Drivers.Hikvision.Interop;

/// <summary>
/// Interop adicional para la API de video wall de HCNetSDK (serie DS-6900UDI y
/// controladores de muro). El CHCNetSDK.cs del demo oficial no incluye estas
/// funciones/estructuras; están portadas de incEn/HCNetSDK.h.
/// </summary>
public static class WallSdk
{
    // Comandos
    public const uint NET_DVR_SET_VIDEOWALLDISPLAYMODE = 1730;     // rectángulo base / habilitación del muro
    public const uint NET_DVR_GET_VIDEOWALLDISPLAYMODE = 1731;
    public const uint NET_DVR_GET_VIDEOWALLDISPLAYNO = 1732;       // lista de salidas del muro (NET_DVR_DISPLAYCFG)
    public const uint NET_DVR_SET_VIDEOWALLDISPLAYPOSITION = 1733;
    public const uint NET_DVR_GET_VIDEOWALLDISPLAYPOSITION = 1734; // posición de una salida en el muro
    public const uint NET_DVR_GET_VIDEOWALLWINDOWPOSITION = 1735;
    public const uint NET_DVR_SET_VIDEOWALLWINDOWPOSITION = 1736;  // abrir/mover/cerrar ventanas
    public const uint NET_DVR_VIDEOWALLWINDOW_CLOSEALL = 1737;
    public const uint NET_DVR_MATRIX_GETWINSTATUS = 9009;          // estado de decodificación de una ventana
    public const uint WALL_ABILITY = 0x212;                        // capacidad de video wall (XML)
    public const uint NET_DVR_WALLWINPARAM_SET = 9005;             // parámetros de ventana (división, etc.)
    public const uint NET_DVR_WALLWINPARAM_GET = 9006;
    public const uint NET_DVR_SWITCH_WIN_TOP = 9017;               // traer ventana a la capa superior
    public const uint NET_DVR_SWITCH_WIN_BOTTOM = 9018;            // enviar ventana a la capa inferior

    /// <summary>Parámetros de una ventana del muro; byWinMode = cantidad de sub-ventanas.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_WALLWINPARAM
    {
        public uint dwSize;
        public byte byTransparency;
        public byte byWinMode;       // división: 1,2,4,6,8,9,12,16,25,36 (según ability)
        public byte byEnableSpartan;
        public byte byDecResource;
        public byte byWndShowMode;
        public byte byEnabledFeature;
        public byte byFeatureMode;
        public byte byRes1;
        public uint dwAmplifyingSubWndNo;
        public byte byWndTopKeep;
        public byte byWndOpenKeep;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 22, ArraySubType = UnmanagedType.I1)]
        public byte[] byRes;

        public void Init() => byRes = new byte[22];
    }

    public const int MAX_DISPLAY_NUM = 512;
    public const int NAME_LEN = 32;

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_WALLWIN_INFO
    {
        public uint dwSize;
        public uint dwWinNum;    // n.º de ventana (formato muro<<24 | n)
        public uint dwSubWinNum; // n.º de sub-ventana (0 = ventana completa)
        public uint dwWallNo;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12, ArraySubType = UnmanagedType.I1)]
        public byte[] byRes;

        public void Init() => byRes = new byte[12];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_WALL_WIN_STATUS
    {
        public uint dwSize;
        public byte byDecodeStatus; // 0-detenida, 1-decodificando
        public byte byStreamType;
        public byte byPacketType;
        public byte byFpsDecV;      // fps de video decodificados
        public byte byFpsDecA;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7, ArraySubType = UnmanagedType.I1)]
        public byte[] byRes1;
        public uint dwDecodedV;     // frames de video decodificados
        public uint dwDecodedA;
        public ushort wImgW;
        public ushort wImgH;
        public byte byStreamMode;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 31, ArraySubType = UnmanagedType.I1)]
        public byte[] byRes2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_VIDEOWALLDISPLAYMODE
    {
        public uint dwSize;
        public byte byEnable; // 0-muro deshabilitado, 1-habilitado
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3, ArraySubType = UnmanagedType.I1)]
        public byte[] byRes1;
        public NET_DVR_RECTCFG_EX struRect; // rectángulo base del muro (coordenadas de referencia)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = NAME_LEN, ArraySubType = UnmanagedType.I1)]
        public byte[] sName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 100, ArraySubType = UnmanagedType.I1)]
        public byte[] byRes2;

        public void Init()
        {
            byRes1 = new byte[3];
            struRect.Init();
            sName = new byte[NAME_LEN];
            byRes2 = new byte[100];
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_RECTCFG_EX
    {
        public uint dwXCoordinate;
        public uint dwYCoordinate;
        public uint dwWidth;
        public uint dwHeight;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4, ArraySubType = UnmanagedType.I1)]
        public byte[] byRes;

        public void Init() => byRes = new byte[4];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_DISPLAYPARAM
    {
        public uint dwDisplayNo;   // número de salida de video
        public byte byDispChanType; // 1-BNC,2-VGA,3-HDMI,4-DVI,...,0xff-inválido
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 11, ArraySubType = UnmanagedType.I1)]
        public byte[] byRes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_DISPLAYCFG
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_DISPLAY_NUM, ArraySubType = UnmanagedType.Struct)]
        public NET_DVR_DISPLAYPARAM[] struDisplayParam;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128, ArraySubType = UnmanagedType.I1)]
        public byte[] byRes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_VIDEOWALLDISPLAYPOSITION
    {
        public uint dwSize;
        public byte byEnable;
        public byte byCoordinateType; // 0-coordenadas de referencia, 1-reales
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2, ArraySubType = UnmanagedType.I1)]
        public byte[] byRes1;
        public uint dwVideoWallNo;    // 1 byte n.º de muro + 3 bytes reservados
        public uint dwDisplayNo;
        public NET_DVR_RECTCFG_EX struRectCfg;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64, ArraySubType = UnmanagedType.I1)]
        public byte[] byRes2;

        public void Init()
        {
            byRes1 = new byte[2];
            struRectCfg.Init();
            byRes2 = new byte[64];
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_VIDEOWALLWINDOWPOSITION
    {
        public uint dwSize;
        public byte byEnable;          // 0-cerrar ventana, 1-abrir/mover
        public byte byWndOperateMode;  // 0-por coordenadas, 1-por resolución
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6, ArraySubType = UnmanagedType.I1)]
        public byte[] byRes1;
        public uint dwWindowNo;        // 1 byte muro + 1 byte reservado + 2 bytes n.º ventana
        public uint dwLayerIndex;      // solo lectura
        public NET_DVR_RECTCFG_EX struRect;       // posición en el muro
        public NET_DVR_RECTCFG_EX struResolution; // válido en modo resolución
        public uint dwXCoordinate;
        public uint dwYCoordinate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36, ArraySubType = UnmanagedType.I1)]
        public byte[] byRes2;

        public void Init()
        {
            byRes1 = new byte[6];
            struRect.Init();
            struResolution.Init();
            byRes2 = new byte[36];
        }
    }

    [DllImport("HCNetSDK.dll")]
    public static extern bool NET_DVR_GetDeviceConfig(
        int lUserID, uint dwCommand, uint dwCount,
        IntPtr lpInBuffer, uint dwInBufferSize,
        IntPtr lpStatusList, IntPtr lpOutBuffer, uint dwOutBufferSize);

    [DllImport("HCNetSDK.dll")]
    public static extern bool NET_DVR_SetDeviceConfig(
        int lUserID, uint dwCommand, uint dwCount,
        IntPtr lpInBuffer, uint dwInBufferSize,
        IntPtr lpStatusList, IntPtr lpInParamBuffer, uint dwInParamBufferSize);

    [DllImport("HCNetSDK.dll")]
    public static extern bool NET_DVR_RemoteControl(
        int lUserID, uint dwCommand, IntPtr lpInBuffer, uint dwInBufferSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_BUF_INFO
    {
        public IntPtr pBuf;
        public uint nLen;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_IN_PARAM
    {
        public NET_DVR_BUF_INFO struCondBuf;    // buffer de condición (ej. números de ventana)
        public NET_DVR_BUF_INFO struInParamBuf; // buffer de parámetros (ej. estructuras de posición)
        public uint dwRecvTimeout;              // 0 = timeout por defecto
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32, ArraySubType = UnmanagedType.I1)]
        public byte[] byRes;

        public void Init() => byRes = new byte[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_OUT_PARAM
    {
        public NET_DVR_BUF_INFO struOutBuf;
        public IntPtr lpStatusList; // DWORD por elemento
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32, ArraySubType = UnmanagedType.I1)]
        public byte[] byRes;

        public void Init() => byRes = new byte[32];
    }

    /// <summary>Variante extendida requerida para ABRIR ventanas del muro (la simple solo modifica existentes).</summary>
    [DllImport("HCNetSDK.dll")]
    public static extern bool NET_DVR_SetDeviceConfigEx(
        int iUserID, uint dwCommand, uint dwCount,
        ref NET_DVR_IN_PARAM lpInParam, ref NET_DVR_OUT_PARAM lpOutParam);

    [DllImport("HCNetSDK.dll")]
    public static extern bool NET_DVR_GetDeviceStatus(
        int lUserID, uint dwCommand, uint dwCount,
        IntPtr lpInBuffer, uint dwInBufferSize,
        IntPtr lpStatusList, IntPtr lpOutBuffer, uint dwOutBufferSize);
}
