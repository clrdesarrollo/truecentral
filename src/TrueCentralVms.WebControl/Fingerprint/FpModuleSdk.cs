using System.Runtime.InteropServices;

namespace TrueCentralVms.WebControl.Fingerprint;

/// <summary>
/// Interop con el SDK del lector de huellas USB de Hikvision
/// (FPModule_SDK V2.2.0, familia DS-K1F820-F / DS-K1F800-F).
///
/// El SDK NO recibe puerto: <see cref="FPModule_OpenDevice"/> busca solo el
/// lector (por dentro habla con él por puerto serie virtual). Es de un solo
/// dispositivo a la vez y sus llamadas bloquean, así que
/// <see cref="FingerprintService"/> lo usa desde un único hilo dedicado.
/// </summary>
internal static class FpModuleSdk
{
    private const string Dll = "FPModule_SDK.dll";

    /// <summary>Tamaño de la plantilla que entrega el lector.</summary>
    public const int TemplateSize = 512;
    public const int MaxImageWidth = 256;
    public const int MaxImageHeight = 360;

    // Códigos de retorno del SDK.
    public const int FP_SUCCESS = 0;
    public const int FP_CONNECTION_ERR = 1;
    public const int FP_TIMEOUT = 2;
    public const int FP_ENROLL_FAIL = 3;
    public const int FP_PARAM_ERR = 4;
    public const int FP_EXTRACT_FAIL = 5;
    public const int FP_MATCH_FAIL = 6;

    public enum FpMessageType
    {
        /// <summary>Pide apoyar el dedo.</summary>
        PressFinger = 0,
        /// <summary>Pide levantar el dedo.</summary>
        RiseFinger = 1,
        /// <summary>Informa el número de captura en curso (int en pMsgData).</summary>
        EnrollTime = 2,
        /// <summary>Entrega la imagen capturada (FP_IMAGE_DATA en pMsgData).</summary>
        CapturedImage = 3,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FpImageData
    {
        public int Width;
        public int Height;
        public IntPtr Image;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void FpMessageHandler(FpMessageType type, IntPtr data);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_OpenDevice();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_CloseDevice();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_DetectFinger(ref int status);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_CaptureImage(byte[] image, ref int width, ref int height);

    /// <summary>Tiempo máximo de espera de la captura (1 a 60 segundos).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_SetTimeout(int seconds);

    /// <summary>Cuántas veces hay que apoyar el dedo (0 = modo por defecto, 2 a 4).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_SetCollectTimes(int times);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_InstallMessageHandler(FpMessageHandler handler);

    /// <summary>Enrola una huella; bloquea hasta completar las capturas o agotar el tiempo.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_FpEnroll(byte[] template);

    /// <summary>Calidad de la plantilla (0 a 100); mayor es mejor.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_GetQuality(byte[] template);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_MatchTemplate(byte[] template1, byte[] template2, int securityLevel);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_GetDeviceInfo(byte[] info);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPModule_GetSDKVersion(byte[] version);

    /// <summary>Mensaje en español para cada código de retorno del SDK.</summary>
    public static string DescribeError(int code) => code switch
    {
        FP_SUCCESS => "Operación correcta.",
        FP_CONNECTION_ERR => "No se pudo comunicar con el lector: revise que esté conectado por USB y que ningún otro programa (SADP, iVMS, otra pestaña) lo tenga tomado.",
        FP_TIMEOUT => "Se agotó el tiempo de espera: no se apoyó el dedo a tiempo.",
        FP_ENROLL_FAIL => "No se pudo generar la plantilla: limpie el sensor y repita apoyando bien el mismo dedo.",
        FP_PARAM_ERR => "Parámetro inválido en la llamada al SDK.",
        FP_EXTRACT_FAIL => "No se pudieron extraer las características de la huella.",
        FP_MATCH_FAIL => "Las huellas comparadas no coinciden.",
        _ => $"Error {code} del SDK del lector.",
    };
}
