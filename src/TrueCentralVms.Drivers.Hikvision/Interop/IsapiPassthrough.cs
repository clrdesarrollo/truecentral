using System.Runtime.InteropServices;
using System.Text;

namespace TrueCentralVms.Drivers.Hikvision.Interop;

/// <summary>
/// ISAPI a través de la sesión del SDK (<c>NET_DVR_STDXMLConfig</c>): el
/// equipo responde la misma XML que por HTTP, pero sin depender de su puerto
/// web ni de otra autenticación. En un DVR/NVR alcanza también la
/// configuración de las cámaras IP por el número de canal del grabador.
/// </summary>
internal static class IsapiPassthrough
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NET_DVR_XML_CONFIG_INPUT
    {
        public uint dwSize;
        public IntPtr lpRequestUrl;
        public uint dwRequestUrlLen;
        public IntPtr lpInBuffer;
        public uint dwInBufferSize;
        public uint dwRecvTimeOut;
        public byte byForceEncrpt;
        public byte byNumOfMultiPart;
        public byte byMIMEType;
        public byte byRes1;
        public uint dwSendTimeOut;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
        public byte[] byRes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NET_DVR_XML_CONFIG_OUTPUT
    {
        public uint dwSize;
        public IntPtr lpOutBuffer;
        public uint dwOutBufferSize;
        public uint dwReturnedXMLSize;
        public IntPtr lpStatusBuffer;
        public uint dwStatusSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] byRes;
    }

    [DllImport("HCNetSDK.dll")]
    private static extern bool NET_DVR_STDXMLConfig(int lUserID, ref NET_DVR_XML_CONFIG_INPUT lpInputParam, ref NET_DVR_XML_CONFIG_OUTPUT lpOutputParam);

    /// <summary>GET de una ruta ISAPI ("/ISAPI/Smart/LineDetection/1"). null si el equipo no la soporta o falla.</summary>
    public static string? Get(int userId, string path, int bufferSize = 64 * 1024)
    {
        byte[] url = Encoding.ASCII.GetBytes($"GET {path}\r\n");
        IntPtr urlPtr = Marshal.AllocHGlobal(url.Length);
        IntPtr outPtr = Marshal.AllocHGlobal(bufferSize);
        IntPtr statusPtr = Marshal.AllocHGlobal(4096);
        try
        {
            Marshal.Copy(url, 0, urlPtr, url.Length);
            var input = new NET_DVR_XML_CONFIG_INPUT
            {
                dwSize = (uint)Marshal.SizeOf<NET_DVR_XML_CONFIG_INPUT>(),
                lpRequestUrl = urlPtr,
                dwRequestUrlLen = (uint)url.Length,
                dwRecvTimeOut = 5000,
                byRes = new byte[24],
            };
            var output = new NET_DVR_XML_CONFIG_OUTPUT
            {
                dwSize = (uint)Marshal.SizeOf<NET_DVR_XML_CONFIG_OUTPUT>(),
                lpOutBuffer = outPtr,
                dwOutBufferSize = (uint)bufferSize,
                lpStatusBuffer = statusPtr,
                dwStatusSize = 4096,
                byRes = new byte[32],
            };
            if (!NET_DVR_STDXMLConfig(userId, ref input, ref output)) return null;
            int length = (int)Math.Min(output.dwReturnedXMLSize, (uint)bufferSize);
            if (length <= 0) return null;
            byte[] body = new byte[length];
            Marshal.Copy(outPtr, body, 0, length);
            return Encoding.UTF8.GetString(body).TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(urlPtr);
            Marshal.FreeHGlobal(outPtr);
            Marshal.FreeHGlobal(statusPtr);
        }
    }
}
