using System.Runtime.InteropServices;

namespace TrueCentralVms.Drivers.Hikvision.Interop;

/// <summary>
/// Interop de videoportero de HCNetSDK 6.1.9.48 (<c>incEn\HCNetSDK.h</c>): el
/// enlace largo de señalización de llamadas (<c>NET_DVR_VIDEO_CALL_SIGNAL_PROCESS</c>,
/// el mismo que usa iVMS-4200 como "centro de gestión") y la voz en modo
/// reenvío (<c>NET_DVR_StartVoiceCom_MR_V30</c>), que entrega y recibe el
/// audio ya codificado en vez de tomar la tarjeta de sonido de la máquina.
/// <see cref="CHCNetSDK"/> no trae el enlace largo, y su delegado de voz
/// declara el búfer como <c>string</c> (inservible para audio binario).
/// </summary>
internal static class IntercomInterop
{
    private const string Dll = "HCNetSDK.dll";

    /// <summary>Comando del enlace largo de señalización de llamadas.</summary>
    public const uint NET_DVR_VIDEO_CALL_SIGNAL_PROCESS = 16032;

    // dwType del callback de configuración remota.
    public const uint NET_SDK_CALLBACK_TYPE_STATUS = 0;
    public const uint NET_SDK_CALLBACK_TYPE_DATA = 2;

    // Estados (primer DWORD del búfer cuando dwType = STATUS).
    public const int NET_SDK_CALLBACK_STATUS_FAILED = 1002;
    public const int NET_SDK_CALLBACK_STATUS_EXCEPTION = 1003;

    /// <summary>
    /// dwCmdType de <see cref="NET_DVR_VIDEO_CALL_PARAM"/>: 0 solicitud de
    /// llamada, 1 cancelar, 2 contestar, 3 rechazar, 4 tiempo de timbre
    /// cumplido, 5 fin de la llamada, 6 el equipo está en llamada, 7 el
    /// cliente está en llamada, 8 monitor interior desconectado.
    /// </summary>
    public const uint CallRequest = 0, CallCancel = 1, CallAnswer = 2, CallReject = 3,
        CallBellTimeout = 4, CallHangUp = 5, CallDeviceBusy = 6, CallClientBusy = 7, CallIndoorOffline = 8;

    /// <summary>132 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_VIDEO_CALL_COND
    {
        public uint dwSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public byte[] byRes;
    }

    /// <summary>136 bytes (verificado con Marshal.SizeOf).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_VIDEO_CALL_PARAM
    {
        public uint dwSize;
        public uint dwCmdType;
        public ushort wPeriod;
        public ushort wBuildingNumber;
        public ushort wUnitNumber;
        public short wFloorNumber;
        public ushort wRoomNumber;
        public ushort wDevIndex;
        public byte byUnitType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 115)]
        public byte[] byRes;

        public static NET_DVR_VIDEO_CALL_PARAM Command(uint cmd) => new()
        {
            dwSize = (uint)Marshal.SizeOf<NET_DVR_VIDEO_CALL_PARAM>(),
            dwCmdType = cmd,
            byRes = new byte[115],
        };
    }

    /// <summary>byAudioEncType: 1 G.711µ, 2 G.711A (los demás no se usan aquí).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NET_DVR_COMPRESSION_AUDIO
    {
        public byte byAudioEncType;
        public byte byAudioSamplingRate;
        public byte byAudioBitRate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] byres;
        public byte bySupport;
    }

    public delegate void RemoteConfigCallback(uint dwType, IntPtr lpBuffer, uint dwBufLen, IntPtr pUserData);

    public delegate void VoiceDataCallback(int lVoiceComHandle, IntPtr pRecvDataBuffer, uint dwBufSize, byte byAudioFlag, IntPtr pUser);

    [DllImport(Dll)]
    public static extern int NET_DVR_StartRemoteConfig(int lUserID, uint dwCommand, ref NET_DVR_VIDEO_CALL_COND lpInBuffer,
        uint dwInBufferLen, RemoteConfigCallback cbStateCallback, IntPtr pUserData);

    [DllImport(Dll)]
    public static extern bool NET_DVR_SendRemoteConfig(int lHandle, uint dwDataType, ref NET_DVR_VIDEO_CALL_PARAM pSendBuf, uint dwBufSize);

    [DllImport(Dll)]
    public static extern bool NET_DVR_StopRemoteConfig(int lHandle);

    [DllImport(Dll)]
    public static extern int NET_DVR_StartVoiceCom_MR_V30(int lUserID, uint dwVoiceChan, VoiceDataCallback fVoiceDataCallBack, IntPtr pUser);

    [DllImport(Dll)]
    public static extern bool NET_DVR_VoiceComSendData(int lVoiceComHandle, byte[] pSendBuf, uint dwBufSize);

    [DllImport(Dll)]
    public static extern bool NET_DVR_StopVoiceCom(int lVoiceComHandle);

    [DllImport(Dll)]
    public static extern bool NET_DVR_GetCurrentAudioCompress(int lUserID, ref NET_DVR_COMPRESSION_AUDIO lpCompressAudio);
}
