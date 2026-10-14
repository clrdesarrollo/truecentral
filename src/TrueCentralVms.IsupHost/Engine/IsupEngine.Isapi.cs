using System.Runtime.InteropServices;
using System.Text;
using TrueCentralVms.Core.Isup;
using TrueCentralVms.IsupHost.Native;

namespace TrueCentralVms.IsupHost.Engine;

/// <summary>El equipo rechazó una orden del SDK. <see cref="Code"/> es el error del SDK (23 = no admitido, 29 = falló en el equipo…).</summary>
public sealed class IsupCommandException(int code, string message) : InvalidOperationException(message)
{
    public int Code { get; } = code;
}

public sealed partial class IsupEngine
{
    const int NotSupportedError = 23;   // NET_DVR_NOSUPPORT

    // ====================== ISAPI por el túnel ======================

    /// <summary>
    /// Petición ISAPI por el túnel ISUP (NET_ECMS_ISAPIPassThrough): verbo y ruta en la primera línea, cuerpo opcional. Bloqueante; una
    /// petición a la vez por equipo. No lanza: los errores del SDK vuelven en la respuesta.
    /// </summary>
    public IsupIsapiResponse Isapi(string deviceId, string method, string pathAndQuery, byte[]? body, int timeoutMs, int maxResponse = 4 << 20)
    {
        if (!_userByDevice.TryGetValue(deviceId, out int userId))
            return new IsupIsapiResponse(false, [], -1, "El equipo no está conectado al servidor por ISUP.");
        // ISUP 2.0/4.0 no tiene ISAPI: cada petición esperaría el tiempo completo para fallar.
        if (IsLegacy(deviceId))
            return new IsupIsapiResponse(false, [], NotSupportedError, "El equipo usa ISUP 2.0/4.0, que no admite ISAPI.");
        lock (GateOf(deviceId))
        {
            var urlBytes = Encoding.ASCII.GetBytes($"{method.ToUpperInvariant()} {pathAndQuery}\r\n\0");
            body ??= [];
            IntPtr pUrl = Marshal.AllocHGlobal(urlBytes.Length), pOut = Marshal.AllocHGlobal(maxResponse);
            IntPtr pIn = body.Length > 0 ? Marshal.AllocHGlobal(body.Length) : IntPtr.Zero;
            try
            {
                Marshal.Copy(urlBytes, 0, pUrl, urlBytes.Length);
                if (pIn != IntPtr.Zero) Marshal.Copy(body, 0, pIn, body.Length);
                ZeroFill(pOut, Math.Min(maxResponse, 64 * 1024));   // el primer NUL marca el final si el SDK no informa el largo
                var prm = new NET_EHOME_PTXML_PARAM
                {
                    pRequestUrl = pUrl, dwRequestUrlLen = (uint)(urlBytes.Length - 1),
                    pInBuffer = pIn, dwInSize = (uint)body.Length,
                    pOutBuffer = pOut, dwOutSize = (uint)maxResponse, dwRecvTimeOut = (uint)Math.Max(1000, timeoutMs),
                };
                if (!Isup.NET_ECMS_ISAPIPassThrough(userId, ref prm))
                {
                    int err = Isup.NET_ECMS_GetLastError();
                    return new IsupIsapiResponse(false, [], err, $"El equipo no respondió a {method} {StripQuery(pathAndQuery)} (error {err}{ErrorHint(err)}).");
                }
                // El largo real viene en dwReturnedXMLLen; isuptest lo vio también en dwOutSize. Si ninguno sirve, hasta el primer NUL.
                int len = prm.dwReturnedXMLLen > 0 && prm.dwReturnedXMLLen <= (uint)maxResponse ? (int)prm.dwReturnedXMLLen
                        : prm.dwOutSize > 0 && prm.dwOutSize < (uint)maxResponse ? (int)prm.dwOutSize : 0;
                if (len == 0) while (len < maxResponse && Marshal.ReadByte(pOut, len) != 0) len++;
                var data = new byte[len];
                Marshal.Copy(pOut, data, 0, len);
                LogRejection(deviceId, method, pathAndQuery, data);
                return new IsupIsapiResponse(true, data, 0, null);
            }
            finally
            {
                Marshal.FreeHGlobal(pUrl); Marshal.FreeHGlobal(pOut);
                if (pIn != IntPtr.Zero) Marshal.FreeHGlobal(pIn);
            }
        }
    }

    static string StripQuery(string path) { int q = path.IndexOf('?'); return q < 0 ? path : path[..q]; }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _rejectionLogged = new(StringComparer.Ordinal);

    /// <summary>
    /// Deja en el registro (una vez cada 10 min por equipo y ruta) las respuestas en que el equipo rechaza la petición: por el túnel
    /// no hay código HTTP, así que es la única pista de qué ruta no admite un equipo ISUP.
    /// </summary>
    void LogRejection(string deviceId, string method, string pathAndQuery, byte[] data)
    {
        if (data.Length == 0 || data.Length > 8192) return;
        var text = Encoding.UTF8.GetString(data);
        var m = System.Text.RegularExpressions.Regex.Match(text, @"<statusCode>\s*(\d+)\s*</statusCode>|""statusCode""\s*:\s*(\d+)");
        if (!m.Success) return;
        var code = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        if (code is "0" or "1" or "7") return;
        string key = $"{deviceId}|{method}|{StripQuery(pathAndQuery)}";
        if (_rejectionLogged.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(10)) return;
        _rejectionLogged[key] = DateTime.UtcNow;
        Emit($"ISAPI de '{deviceId}': {method} {pathAndQuery} → {Clip(text.Replace("\r", "").Replace("\n", " "), 300)}");
    }

    string? IsapiText(string deviceId, string method, string path, string? body = null, int timeoutMs = 10000)
    {
        var r = Isapi(deviceId, method, path, body == null ? null : Encoding.UTF8.GetBytes(body), timeoutMs);
        return r.Ok ? Encoding.UTF8.GetString(r.Body) : null;
    }

    static bool IsapiOk(string? text) => text != null && (text.Contains("<statusCode>1</statusCode>") || text.Contains("<statusString>OK</statusString>")
                                                          || text.Contains("\"statusCode\":1") || text.Contains("\"statusCode\": 1"));

    // ====================== órdenes por XML (capítulo 16 de la guía) ======================

    int _xmlSequence;

    /// <summary>
    /// Orden de control por XML con sobre PPVSPMessage (NET_ECMS_XMLRemoteControl): ADJUSTTIME, REBOOT, GETDEVICEWORKSTATUS… Es la vía de
    /// los equipos ISUP 2.0/4.0, que no tienen ISAPI. Bloqueante; una a la vez por equipo.
    /// </summary>
    public IsupIsapiResponse XmlRemoteControl(string deviceId, string command, string paramsXml, int timeoutMs = 15000, string method = "CONTROL")
    {
        if (!_userByDevice.TryGetValue(deviceId, out int userId)) return new IsupIsapiResponse(false, [], -1, "El equipo no está conectado al servidor por ISUP.");
        var version = IsLegacy(deviceId) ? "2.0" : "4.0";
        int seq = Interlocked.Increment(ref _xmlSequence);
        var msg = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><PPVSPMessage><Version>{version}</Version><Sequence>{seq}</Sequence><CommandType>REQUEST</CommandType>" +
                  $"<Method>{method}</Method><Command>{command}</Command><Params>{paramsXml}</Params></PPVSPMessage>";
        lock (GateOf(deviceId))
        {
            var inb = Encoding.UTF8.GetBytes(msg + "\0");
            const int outSize = 16 * 1024, statusSize = 4096;
            IntPtr pIn = Marshal.AllocHGlobal(inb.Length), pOut = Marshal.AllocHGlobal(outSize), pStatus = Marshal.AllocHGlobal(statusSize);
            try
            {
                Marshal.Copy(inb, 0, pIn, inb.Length);
                ZeroFill(pOut, outSize);
                ZeroFill(pStatus, statusSize);
                var p = new NET_EHOME_XML_REMOTE_CTRL_PARAM
                {
                    dwSize = (uint)Marshal.SizeOf<NET_EHOME_XML_REMOTE_CTRL_PARAM>(),
                    lpInbuffer = pIn, dwInBufferSize = (uint)(inb.Length - 1),
                    dwSendTimeOut = (uint)timeoutMs, dwRecvTimeOut = (uint)timeoutMs,
                    lpOutBuffer = pOut, dwOutBufferSize = outSize,
                    lpStatusBuffer = pStatus, dwStatusBufferSize = statusSize,
                };
                if (!Isup.NET_ECMS_XMLRemoteControl(userId, ref p, (uint)Marshal.SizeOf<NET_EHOME_XML_REMOTE_CTRL_PARAM>()))
                {
                    int err = Isup.NET_ECMS_GetLastError();
                    return new IsupIsapiResponse(false, Encoding.UTF8.GetBytes(Isup.ReadCString(pStatus, statusSize)), err,
                        $"El equipo no aceptó {command} (error {err}{ErrorHint(err)}).");
                }
                var text = Isup.ReadCString(pOut, outSize);
                if (text.Length == 0) text = Isup.ReadCString(pStatus, statusSize);
                return new IsupIsapiResponse(true, Encoding.UTF8.GetBytes(text), 0, null);
            }
            finally { Marshal.FreeHGlobal(pIn); Marshal.FreeHGlobal(pOut); Marshal.FreeHGlobal(pStatus); }
        }
    }

    // ====================== control remoto: PTZ, presets y salidas ======================

    /// <summary>Orden de control remoto (NET_ECMS_RemoteControl): condición opcional (el canal, 4 bytes) y la estructura de control.</summary>
    void RemoteControl<T>(string deviceId, uint command, int? channel, T input, string what) where T : struct
    {
        if (!_userByDevice.TryGetValue(deviceId, out int userId)) throw new IsupCommandException(-1, "El equipo no está conectado al servidor por ISUP.");
        int size = Marshal.SizeOf<T>();
        IntPtr pIn = Marshal.AllocHGlobal(size), pCond = channel is int ? Marshal.AllocHGlobal(4) : IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(input, pIn, false);
            if (pCond != IntPtr.Zero) Marshal.WriteInt32(pCond, channel!.Value);
            var prm = new NET_EHOME_REMOTE_CTRL_PARAM
            {
                dwSize = (uint)Marshal.SizeOf<NET_EHOME_REMOTE_CTRL_PARAM>(),
                lpCondBuffer = pCond, dwCondBufferSize = pCond == IntPtr.Zero ? 0u : 4u,
                lpInbuffer = pIn, dwInBufferSize = (uint)size,
            };
            // Sin la cola de ISAPI: el PTZ tiene que responder al instante.
            if (!Isup.NET_ECMS_RemoteControl(userId, command, ref prm))
            {
                int err = Isup.NET_ECMS_GetLastError();
                throw new IsupCommandException(err, $"{what}: el equipo no aceptó la orden (error {err}{ErrorHint(err)}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pIn);
            if (pCond != IntPtr.Zero) Marshal.FreeHGlobal(pCond);
        }
    }

    /// <summary>Movimiento PTZ (start inicia, !start detiene; velocidad 0..7).</summary>
    public void Ptz(string deviceId, int channel, IsupPtzCommand command, bool start, int speed)
    {
        var p = new NET_EHOME_PTZ_PARAM
        {
            dwSize = (uint)Marshal.SizeOf<NET_EHOME_PTZ_PARAM>(), byPTZCmd = (byte)command,
            byAction = (byte)(start ? 0 : 1), bySpeed = (byte)Math.Clamp(speed, 0, 7),
        };
        RemoteControl(deviceId, Isup.NET_EHOME_PTZ_CTRL, channel, p, $"PTZ {command} canal {channel}");
    }

    /// <summary>Ir a un preset, guardarlo o borrarlo (desde 1).</summary>
    public void Preset(string deviceId, int channel, IsupPresetAction action, int index)
    {
        var p = new NET_EHOME_PRESET_PARAM { dwSize = (uint)Marshal.SizeOf<NET_EHOME_PRESET_PARAM>(), byPresetCmd = (byte)action, dwPresetIndex = (uint)index };
        RemoteControl(deviceId, Isup.NET_EHOME_PRESET_CTRL, channel, p, $"Preset {index} canal {channel}");
    }

    /// <summary>
    /// Activa o desactiva una salida de alarma (relé, desde 1) por ISUP; seconds = 0 la deja activa hasta desactivarla. Si el equipo no
    /// admite la orden ISUP (los DVR rechazan con 29 las salidas de sus cámaras IP), se repite por ISAPI y, como ese disparo no tiene
    /// duración, se apaga solo al cumplirse los segundos.
    /// </summary>
    public void AlarmOutput(string deviceId, int output, bool on, int seconds)
    {
        var p = new NET_EHOME_MANUAL_IOOUT_CTRL
        {
            dwSize = (uint)Marshal.SizeOf<NET_EHOME_MANUAL_IOOUT_CTRL>(), dwChan = (uint)output,
            dwDelayTime = (uint)Math.Max(0, seconds), byAction = (byte)(on ? 1 : 0),
        };
        if (IsLegacy(deviceId))
            throw new IsupCommandException(NotSupportedError, $"Salida {output}: los equipos ISUP 2.0 probados no admiten el mando de relés.");
        try { RemoteControl(deviceId, Isup.NET_EHOME_MANUAL_IOOUT, null, p, $"Salida de alarma {output}"); }
        catch (IsupCommandException ex) when (ex.Code is 23 or 29)
        {
            var r = TriggerOutputIsapi(deviceId, output, on);
            if (!IsapiOk(r)) throw new IsupCommandException(ex.Code, $"{ex.Message} Por ISAPI tampoco.");
            Emit($"Salida de alarma {output} de '{deviceId}': {(on ? "activada" : "desactivada")} por ISAPI (el equipo no admite la orden ISUP para esta salida).");
            if (on && seconds > 0)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds));
                    var off = TriggerOutputIsapi(deviceId, output, false);
                    if (!IsapiOk(off)) Emit($"Aviso: la salida de alarma {output} de '{deviceId}' NO se pudo desactivar al cumplirse los {seconds} s.");
                });
        }
    }

    string? TriggerOutputIsapi(string deviceId, int output, bool on) =>
        IsapiText(deviceId, "PUT", $"/ISAPI/System/IO/outputs/{output}/trigger",
            $"<IOPortData version=\"2.0\" xmlns=\"http://www.hikvision.com/ver20/XMLSchema\"><outputState>{(on ? "high" : "low")}</outputState></IOPortData>");

    /// <summary>Despierta un equipo en reposo; tarda alrededor de un minuto y el equipo vuelve a registrarse.</summary>
    public void WakeUp(string deviceId)
    {
        if (!_userByDevice.TryGetValue(deviceId, out int userId)) throw new IsupCommandException(-1, "El equipo no está conectado al servidor por ISUP.");
        if (!Isup.NET_ECMS_WakeUp(userId))
        {
            int err = Isup.NET_ECMS_GetLastError();
            throw new IsupCommandException(err, $"El equipo no aceptó la orden de despertar (error {err}{ErrorHint(err)}).");
        }
    }

    /// <summary>Da de baja al equipo (vuelve a registrarse por sí mismo si sigue en el directorio).</summary>
    public bool ForceLogout(string deviceId)
    {
        if (!_userByDevice.TryGetValue(deviceId, out int userId)) return false;
        Isup.NET_ECMS_ForceLogout(userId);
        MarkOffline(userId);
        return true;
    }
}
