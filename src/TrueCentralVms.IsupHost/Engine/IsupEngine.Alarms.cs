using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TrueCentralVms.Core.Isup;
using TrueCentralVms.IsupHost.Native;

namespace TrueCentralVms.IsupHost.Engine;

public sealed partial class IsupEngine
{
    int _unsupportedRepliesSeen, _unknownLogged;

    string PicturesDir(DateTime day) => Path.Combine(_dataDir, "pictures", day.ToString("yyyyMMdd"));

    // ====================== alarmas (callback del SDK, hilo nativo) ======================

    int OnAlarm(int hAlarm, IntPtr pMsg, IntPtr user)
    {
        try
        {
            uint type = (uint)Marshal.ReadInt32(pMsg, Layout.MsgType);
            IntPtr info = Marshal.ReadIntPtr(pMsg, Layout.MsgInfo); int infoLen = Marshal.ReadInt32(pMsg, Layout.MsgInfoLen);
            IntPtr xml = Marshal.ReadIntPtr(pMsg, Layout.MsgXml); int xmlLen = Marshal.ReadInt32(pMsg, Layout.MsgXmlLen);
            var serial = Isup.ReadCString(pMsg + Layout.MsgSerial, 12);
            var now = DateTimeOffset.Now;

            // Estructuras binarias: alarma básica (sobre todo 2.0/4.0) y reporte Contact-ID de los paneles de alarma.
            if (type == Isup.EHOME_ALARM && info != IntPtr.Zero && infoLen >= Layout.BasicMinSize) { DecodeBasicAlarm(info, serial, now); return 1; }
            if (type == Isup.EHOME_ALARM_CID_REPORT && info != IntPtr.Zero && infoLen >= Layout.CidMinSize) { DecodeCid(info, serial, now); return 1; }

            string text = "";
            if (xml != IntPtr.Zero && xmlLen > 0 && xmlLen < 4_000_000) text = Marshal.PtrToStringUTF8(xml, xmlLen) ?? "";
            var pictures = new List<string>();
            // Tipo 13 (alarma ISAPI): NET_EHOME_ALARM_ISAPI_INFO con el XML/JSON del evento y sus fotos.
            if (type == Isup.EHOME_ISAPI_ALARM && info != IntPtr.Zero && infoLen >= 24)
            {
                var p = Marshal.ReadIntPtr(info, Layout.IsapiData);
                int plen = Marshal.ReadInt32(info, Layout.IsapiDataLen);
                if (text.Length == 0 && p != IntPtr.Zero && plen > 0 && plen < 4_000_000) text = Marshal.PtrToStringUTF8(p, plen)?.TrimEnd('\0') ?? "";
                int n = Marshal.ReadByte(info, Layout.IsapiPicCount);
                var pack = Marshal.ReadIntPtr(info, Layout.IsapiPics);
                if (n > 0 && n <= 16 && pack != IntPtr.Zero) pictures = SavePictures(pack, n, now);
            }
            // Otros tipos (11 = control de acceso 2.0/4.0, 9 = propio del fabricante…) que traen texto en la estructura.
            if (text.Length == 0 && info != IntPtr.Zero && infoLen > 0 && infoLen < 4_000_000)
            {
                var raw = new byte[infoLen];
                Marshal.Copy(info, raw, 0, infoLen);
                if (raw.Take(64).All(c => c >= 9 && c < 127 || c == 0)) text = Encoding.UTF8.GetString(raw).TrimEnd('\0');
            }
            // Respuesta «NOT SUPPORT» de un equipo 2.0 a la confirmación que el SDK le manda tras su alarma de prueba: no es una alarma.
            if (type == 0 && text.Contains("<CommandType>RESPONSE</CommandType>", StringComparison.Ordinal) && text.Contains("<WhichCommand>UNKNOWN</WhichCommand>", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _unsupportedRepliesSeen) == 1)
                    Emit("Un equipo ISUP 2.0 contesta «NOT SUPPORT» a la confirmación de alarma del SDK (no afecta a nada).");
                return 1;
            }
            if (text.Length == 0)
            {
                if (Interlocked.Increment(ref _unknownLogged) <= 20)
                {
                    var head = new byte[Math.Min(Math.Max(infoLen, 0), 96)];
                    if (info != IntPtr.Zero && head.Length > 0) Marshal.Copy(info, head, 0, head.Length);
                    Emit($"Alarma ISUP de tipo {type} sin contenido reconocible (serie '{serial}', {infoLen} bytes): {BitConverter.ToString(head)}");
                }
                return 1;
            }
            // Fotos subidas antes al servidor de imágenes: el evento las nombra por su URL.
            foreach (var stored in StoredPicturesIn(text))
                if (!pictures.Contains(stored)) pictures.Add(stored);
            var device = ResolveDevice(text, serial);
            if (device == null)
            {
                Emit($"Alarma ISUP de tipo {type} de un equipo que no se pudo identificar (serie '{serial}'): {Clip(text, 300)}");
                return 1;
            }
            Raise(new IsupHostEvent(IsupEventKinds.Alarm, device, now, Alarm: new IsupAlarm(device, (int)type, now, text, pictures.Count > 0 ? pictures : null)));
        }
        catch (Exception ex) { Emit("Error al procesar una alarma ISUP: " + ex.Message); }
        return 1;
    }

    /// <summary>
    /// Equipo dueño de una alarma en texto, por orden de fiabilidad: el número de serie del mensaje del SDK contra el del registro; el
    /// &lt;deviceID&gt; de la notificación si es un equipo registrado; un ID registrado contenido en el &lt;serialNumber&gt;; la IP; y, si
    /// hay un solo equipo en línea, ese.
    /// </summary>
    string? ResolveDevice(string text, string serial)
    {
        if (serial.Length > 0)
        {
            var bySerial = _registrations.Values.FirstOrDefault(r => _userByDevice.ContainsKey(r.DeviceId) &&
                (r.Serial == serial || (r.FullSerial.Length > 0 && r.FullSerial.EndsWith(serial, StringComparison.Ordinal))));
            if (bySerial != null) return bySerial.DeviceId;
        }
        string xmlDev = Pick(text, "deviceID"); if (xmlDev.Length == 0) xmlDev = Pick(text, "DeviceID");
        if (xmlDev.Length > 0 && _userByDevice.ContainsKey(xmlDev)) return xmlDev;
        string xmlSerial = Pick(text, "serialNumber");
        if (xmlSerial.Length > 0)
        {
            var bySerial = _registrations.Values.FirstOrDefault(r => _userByDevice.ContainsKey(r.DeviceId) &&
                ((r.FullSerial.Length > 0 && xmlSerial.Contains(r.FullSerial, StringComparison.OrdinalIgnoreCase)) || (r.Serial.Length > 0 && xmlSerial.EndsWith(r.Serial, StringComparison.Ordinal))));
            if (bySerial != null) return bySerial.DeviceId;
            var byId = _userByDevice.Keys.Where(k => xmlSerial.Contains(k, StringComparison.OrdinalIgnoreCase)).OrderByDescending(k => k.Length).FirstOrDefault();
            if (byId != null) return byId;
        }
        string xmlIp = Pick(text, "ipAddress");
        if (xmlIp.Length > 0)
        {
            var byIp = _registrations.Values.Where(r => r.Address == xmlIp && _userByDevice.ContainsKey(r.DeviceId)).Take(2).ToList();
            if (byIp.Count == 1) return byIp[0].DeviceId;
        }
        return _userByDevice.Count == 1 ? _userByDevice.Keys.First() : null;
    }

    static string Pick(string t, string tag)
    {
        var m = Regex.Match(t, $"<{tag}(?:\\s[^>]*)?>([^<]*)</{tag}>");
        if (m.Success) return m.Groups[1].Value.Trim();
        m = Regex.Match(t, $"\"{tag}\"\\s*:\\s*\"?([^\",}}]*)");
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    string? DeviceFromStruct(string structId, string serial)
    {
        if (structId.Length > 0 && (_userByDevice.ContainsKey(structId) || _keys.ContainsKey(structId))) return structId;
        if (serial.Length > 0)
        {
            var r = _registrations.Values.FirstOrDefault(r => r.Serial == serial);
            if (r != null) return r.DeviceId;
        }
        return structId.Length > 0 ? structId : _userByDevice.Count == 1 ? _userByDevice.Keys.First() : null;
    }

    void DecodeBasicAlarm(IntPtr p, string serial, DateTimeOffset now)
    {
        var id = DeviceFromStruct(Isup.ReadCString(p + Layout.BasicDeviceId, 256), serial);
        if (id == null) return;
        int atype = Marshal.ReadInt32(p, Layout.BasicType), action = Marshal.ReadInt32(p, Layout.BasicAction);
        if (atype is 703 or 704 or 705) return;   // informes periódicos de estado (CPU, bitrate, discos): no son alarmas
        var remark = Isup.ReadCString(p + Layout.BasicRemark, 64);
        if (remark.Equals("test/debug", StringComparison.OrdinalIgnoreCase)) remark = "";   // observación de fábrica de algunos firmwares
        var basic = new IsupBasicAlarm(atype, action, Marshal.ReadInt32(p, Layout.BasicChannel), Marshal.ReadInt32(p, Layout.BasicAlarmIn),
            Marshal.ReadInt32(p, Layout.BasicDisk), remark.Length > 0 ? remark : null, NullIfEmpty(Isup.ReadCString(p + Layout.BasicTime, 32)));
        Raise(new IsupHostEvent(IsupEventKinds.Alarm, id, now, Alarm: new IsupAlarm(id, (int)Isup.EHOME_ALARM, now, Basic: basic)));
    }

    void DecodeCid(IntPtr p, string serial, DateTimeOffset now)
    {
        var id = DeviceFromStruct(Isup.ReadCString(p + Layout.CidDeviceId, 256), serial);
        if (id == null) return;
        var cid = new IsupCidAlarm(
            Marshal.ReadInt32(p, Layout.CidCode), Marshal.ReadInt32(p, Layout.CidType), Marshal.ReadInt32(p, Layout.CidSubSys),
            NullIfEmpty(ReadText(p + Layout.CidDescribe, 32)), NullIfEmpty(Isup.ReadCString(p + Layout.CidTriggerTime, 32)),
            Marshal.ReadInt32(p, Layout.CidUserType), Marshal.ReadInt32(p, Layout.CidUserNo), Marshal.ReadInt32(p, Layout.CidZoneNo),
            NullIfEmpty(ReadText(p + Layout.CidUserName, 32)));
        Emit($"CID de '{id}': código {cid.Code}, partición {cid.SubSystem}, zona {cid.ZoneNo}, usuario {cid.UserNo}{(cid.UserName != null ? $" ({cid.UserName})" : "")} · {cid.Description}");
        Raise(new IsupHostEvent(IsupEventKinds.Alarm, id, now, Alarm: new IsupAlarm(id, (int)Isup.EHOME_ALARM_CID_REPORT, now, Cid: cid)));
    }

    static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Guarda en disco las fotos que acompañan a una alarma ISAPI (NET_EHOME_ALARM_ISAPI_PICDATA) y devuelve sus rutas.</summary>
    List<string> SavePictures(IntPtr pack, int count, DateTimeOffset now)
    {
        var saved = new List<string>();
        try
        {
            var dir = PicturesDir(now.LocalDateTime);
            Directory.CreateDirectory(dir);
            for (int i = 0; i < count; i++)
            {
                var e = pack + i * Layout.IsapiPicEntry;
                int len = Marshal.ReadInt32(e, 0);
                var data = Marshal.ReadIntPtr(e, Layout.IsapiPicData);
                if (len <= 0 || len > 20_000_000 || data == IntPtr.Zero) continue;
                var buf = new byte[len];
                Marshal.Copy(data, buf, 0, len);
                // El nombre que le dio el equipo (licensePlatePicture, detectionPicture…) dice qué es cada foto.
                var name = Regex.Replace(Path.GetFileNameWithoutExtension(Isup.ReadCString(e + Layout.IsapiPicFile, 256)), "[^A-Za-z0-9_-]", "");
                if (name.Length > 60) name = name[..60];
                var file = Path.Combine(dir, $"{now:HHmmssfff}_{Guid.NewGuid().ToString("N")[..8]}_{i + 1}{(name.Length > 0 ? "_" + name : "")}.jpg");
                File.WriteAllBytes(file, buf);
                saved.Add(file);
            }
        }
        catch (Exception ex) { Emit("No se pudieron guardar las fotos de una alarma: " + ex.Message); }
        return saved;
    }

    // ====================== servidor de imágenes (HCISUPSS) ======================
    // Tipo 4 = protocolo de almacenamiento ISUP 5.0 (AK/SK). Las claves solo validan que la subida venga de un equipo al que se le anunció.

    const int PictureServerType = 4;
    readonly string PictureAccessKey = "tcvms" + Guid.NewGuid().ToString("N")[..12];
    readonly string PictureSecretKey = Guid.NewGuid().ToString("N");
    readonly ConcurrentDictionary<string, string> _pictureFiles = new();

    void StartPictureServer(IsupHostOptions options, string logDir)
    {
        var sqlite = Path.Combine(_nativeDir, "sqlite3.dll");
        if (!File.Exists(sqlite)) { AddListener("Imágenes", options.PictureServerPort, false, "falta sqlite3.dll"); return; }
        SetInitPath(Isup.NET_ESS_SetSDKInitCfg, Isup.SS_INIT_CFG_SQLITE3_PATH, sqlite);
        var crypto = Path.Combine(_nativeDir, "libcrypto-1_1-x64.dll");
        var ssl = Path.Combine(_nativeDir, "libssl-1_1-x64.dll");
        if (File.Exists(crypto)) SetInitPaths(Isup.NET_ESS_SetSDKInitCfg, crypto, ssl, Isup.SS_INIT_CFG_LIBEAY_PATH, Isup.SS_INIT_CFG_SSLEAY_PATH);
        // Dirección que el módulo anuncia a los equipos: sin ella el DVR de pruebas preguntaba por el "mejor nodo" cada segundo sin subir nada.
        var pub = new NET_EHOME_IPADDRESS();
        Isup.SetIp(ref pub, _publicIp, (ushort)options.PictureServerPort);
        IntPtr buf = Marshal.AllocHGlobal(Marshal.SizeOf<NET_EHOME_IPADDRESS>());
        try { Marshal.StructureToPtr(pub, buf, false); Isup.NET_ESS_SetSDKInitCfg(Isup.SS_INIT_CFG_PUBLIC_IP_PORT, buf); }
        finally { Marshal.FreeHGlobal(buf); }
        if (!Isup.NET_ESS_Init()) { AddListener("Imágenes", options.PictureServerPort, false, $"error {Isup.NET_ESS_GetLastError()} al iniciar"); return; }
        Isup.NET_ESS_SetLogToFile(3, logDir, true);
        var ss = new NET_EHOME_SS_LISTEN_PARAM
        {
            fnSStorageCb = Marshal.GetFunctionPointerForDelegate(_ssStorageCb),
            fnSSMsgCb = Marshal.GetFunctionPointerForDelegate(_ssMsgCb),
            bySecurityMode = 1,   // como el demo oficial (0 exige cliente con firma/HTTPS)
        };
        // Tras un NAT no se puede escuchar en la IP pública (bind 10049): se escucha en la local por la que llega el tráfico.
        var listenIp = LocalNetwork.IsLocalAddress(_publicIp) ? _publicIp : LocalNetwork.InternetFacingIp();
        Isup.SetIp(ref ss.struAddress, listenIp, (ushort)options.PictureServerPort);
        var ak = Encoding.ASCII.GetBytes(PictureAccessKey); var sk = Encoding.ASCII.GetBytes(PictureSecretKey);
        unsafe
        {
            for (int i = 0; i < ak.Length && i < 63; i++) ss.szAccessKey[i] = ak[i];
            for (int i = 0; i < sk.Length && i < 63; i++) ss.szSecretKey[i] = sk[i];
        }
        _hPictures = Isup.NET_ESS_StartListen(ref ss);
        AddListener("Imágenes", options.PictureServerPort, _hPictures >= 0, _hPictures >= 0 ? null : $"error {Isup.NET_ESS_GetLastError()}");
        if (_hPictures < 0) Isup.NET_ESS_Fini();
    }

    /// <summary>Un equipo subió una foto (de un evento): se guarda y se devuelve su ruta al SDK.</summary>
    int OnPictureStore(int handle, IntPtr pFileName, IntPtr pFileBuf, uint len, IntPtr pFilePath, IntPtr user)
    {
        try
        {
            var name = pFileName == IntPtr.Zero ? "" : (Marshal.PtrToStringAnsi(pFileName) ?? "");
            if (pFileBuf == IntPtr.Zero || len == 0 || len > 50_000_000) return 0;
            var data = new byte[len];
            Marshal.Copy(pFileBuf, data, 0, (int)len);
            var dir = PicturesDir(DateTime.Now);
            Directory.CreateDirectory(dir);
            var safe = Regex.Replace(Path.GetFileName(name.Replace('\\', '/')), "[^A-Za-z0-9_.-]", "_");
            if (safe.Length == 0) safe = Guid.NewGuid().ToString("N");
            if (!Path.HasExtension(safe)) safe += ".jpg";
            var path = Path.Combine(dir, $"{DateTime.Now:HHmmssfff}_{safe}");
            File.WriteAllBytes(path, data);
            _pictureFiles[name] = path;
            if (pFilePath != IntPtr.Zero) WriteAscii(pFilePath, path, 260);
            return 1;
        }
        catch (Exception ex) { Emit("Servidor de imágenes: no se pudo guardar una foto: " + ex.Message); return 0; }
    }

    /// <summary>Mensajes del servidor de imágenes: validación KMS y clave secreta para la AK que presenta el equipo.</summary>
    int OnPictureMessage(int handle, int type, IntPtr pOut, uint outLen, IntPtr pIn, uint inLen, IntPtr user)
    {
        try
        {
            switch (type)
            {
                case Isup.SS_MSG_KMS_USER_PWD:
                    if (pIn != IntPtr.Zero && inLen >= 4) Marshal.WriteInt32(pIn, 1);
                    return 1;
                case Isup.SS_MSG_CLOUD_AK:
                case 4:   // el SDK 2.5 usa también este valor para presentar la AK y esperar la SK
                {
                    var ak = Isup.ReadCString(pOut, (int)Math.Min(outLen, 64));
                    if (pIn != IntPtr.Zero && inLen > 1) WriteAscii(pIn, ak == PictureAccessKey ? PictureSecretKey : "", (int)Math.Min(inLen, 64));
                    return 1;
                }
                default:
                    return 1;
            }
        }
        catch (Exception ex) { Emit("Servidor de imágenes: " + ex.Message); return 0; }
    }

    /// <summary>Ruta local de una foto subida al servidor de imágenes, por el nombre o la URL con que la referencia el evento.</summary>
    public string? FindStoredPicture(string reference)
    {
        if (reference.Length == 0) return null;
        if (_pictureFiles.TryGetValue(reference, out var p)) return p;
        foreach (var kv in _pictureFiles)
            if (kv.Key.Length > 3 && (reference.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) || kv.Key.Contains(reference, StringComparison.OrdinalIgnoreCase))) return kv.Value;
        return null;
    }

    /// <summary>Rutas locales de las fotos del servidor de imágenes que referencia un evento (por URL o nombre de archivo).</summary>
    List<string> StoredPicturesIn(string text)
    {
        var found = new List<string>();
        if (_pictureFiles.IsEmpty || text.Length == 0) return found;
        foreach (Match m in Regex.Matches(text, @"https?://[^\s<>""']+"))
            if (FindStoredPicture(WebUtility.HtmlDecode(m.Value)) is { } path && File.Exists(path) && !found.Contains(path))
                found.Add(path);
        return found;
    }

    /// <summary>Borra las fotos de más de <paramref name="days"/> días (el servidor copia las que necesita al recibir el evento).</summary>
    public void PrunePictures(int days)
    {
        try
        {
            var root = Path.Combine(_dataDir, "pictures");
            if (!Directory.Exists(root)) return;
            var limit = DateTime.Now.Date.AddDays(-days).ToString("yyyyMMdd");
            foreach (var dir in Directory.GetDirectories(root))
                if (string.CompareOrdinal(Path.GetFileName(dir), limit) < 0) Directory.Delete(dir, recursive: true);
            foreach (var kv in _pictureFiles.Where(kv => !File.Exists(kv.Value)).ToList()) _pictureFiles.TryRemove(kv.Key, out _);
        }
        catch (Exception ex) { Emit("No se pudieron limpiar las fotos antiguas: " + ex.Message); }
    }

    // ====================== enlace de alarmas de los equipos 5.0 ======================
    // El equipo abre su conexión de alarmas (MQTT) cuando se le CAMBIA el host de alarmas; el SDK lo empuja en el registro. Si ya era el
    // nuestro no empuja nada y el equipo no reconecta (ni con un PUT idéntico): se mueve un instante a otro puerto y se devuelve.

    /// <summary>
    /// ¿Hay una conexión TCP del equipo a nuestro puerto de alarmas? Detrás de NAT la IP que informa el equipo es privada y no coincide con
    /// la que ve el servidor: entonces vale una conexión de alarmas desde la única dirección pública de la que vienen los registros.
    /// </summary>
    bool AlarmLinkUp(string deviceIp)
    {
        if (deviceIp.Length == 0) return false;
        try
        {
            var conns = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Where(c => c.State == TcpState.Established).ToList();
            var alarm = conns.Where(c => c.LocalEndPoint.Port == _options.AlarmPort).Select(c => c.RemoteEndPoint.Address.ToString()).ToList();
            if (alarm.Contains(deviceIp)) return true;
            if (!System.Net.IPAddress.TryParse(deviceIp, out var ip) || !LocalNetwork.IsPrivate(ip)) return false;
            var registered = conns.Where(c => _options.RegistrationPorts.Contains(c.LocalEndPoint.Port)).Select(c => c.RemoteEndPoint.Address.ToString()).ToHashSet();
            return registered.Count == 1 && alarm.Any(registered.Contains);
        }
        catch (NetworkInformationException) { return false; }
    }

    void EnsureAlarmLink(string id, string deviceIp)
    {
        try
        {
            Thread.Sleep(6000);   // tiempo para que el SDK empuje el host y el equipo conecte por sí solo
            if (!_userByDevice.ContainsKey(id) || AlarmLinkUp(deviceIp)) return;
            var announced = AddressFor(id);
            var root = ParseXml(IsapiText(id, "GET", "/ISAPI/Event/notification/httpHosts"));
            var host = root?.DescendantsAndSelf().Where(x => x.Name.LocalName == "HttpHostNotification")
                .FirstOrDefault(h => Val(h, "ipAddress") == announced && Val(h, "portNo") == _options.AlarmPort.ToString());
            if (host == null)
            {
                Emit($"Alarmas de '{id}': su host de alarmas no apunta a este servidor ({announced}:{_options.AlarmPort}); no se reinicia el enlace.");
                return;
            }
            var path = $"/ISAPI/Event/notification/httpHosts/{Val(host, "id")}";
            var original = host.ToString();
            var shifted = new XElement(host);
            shifted.Elements().First(x => x.Name.LocalName == "portNo").Value = (_options.AlarmPort + 1).ToString();
            Emit($"Alarmas de '{id}': el equipo no abrió el enlace de alarmas; se reinicia moviendo un instante su host de alarmas.");
            IsapiText(id, "PUT", path, shifted.ToString());
            Thread.Sleep(2500);
            var back = IsapiText(id, "PUT", path, original);   // siempre se devuelve al valor configurado
            if (!IsapiOk(back)) { Emit($"AVISO: no se pudo devolver el host de alarmas de '{id}' a {announced}:{_options.AlarmPort}; revíselo en el equipo."); return; }
            Thread.Sleep(4000);
            Emit(AlarmLinkUp(deviceIp) ? $"Alarmas de '{id}': enlace de alarmas abierto." : $"AVISO: el equipo '{id}' sigue sin abrir el enlace de alarmas.");
        }
        catch (Exception ex) { Emit($"Alarmas de '{id}': error al comprobar el enlace: {ex.Message}"); }
    }

    static XElement? ParseXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try { return XDocument.Parse(xml).Root; } catch (System.Xml.XmlException) { return null; }
    }

    static string Val(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? "";
}
