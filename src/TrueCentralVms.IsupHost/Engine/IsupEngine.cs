using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using TrueCentralVms.Core.Isup;
using TrueCentralVms.IsupHost.Native;

namespace TrueCentralVms.IsupHost.Engine;

/// <summary>
/// Servidor ISUP sobre el SDK de Hikvision (portado del proyecto de pruebas isuptest): escucha el registro de los equipos y valida su
/// clave contra el directorio que entrega el servidor del VMS, y por la conexión de cada equipo pide video, reproducciones y voz, pasa
/// peticiones ISAPI y recibe sus alarmas. Los callbacks del SDK llegan en hilos nativos: nunca se bloquean con trabajo largo.
/// </summary>
public sealed partial class IsupEngine : IDisposable
{
    /// <summary>Línea para el registro (la recoge el servidor desde la salida estándar).</summary>
    public event Action<string>? Log;
    /// <summary>Evento para el servidor: equipo en línea/fuera de línea, rechazo o alarma.</summary>
    public event Action<IsupHostEvent>? EventRaised;

    IsupHostOptions _options = new();
    string _publicIp = "";
    string _nativeDir = "", _dataDir = "";
    bool _started, _disposed;
    int _hCms = -1;
    readonly List<PortRelay> _relays = [];
    int _hAlarm = -1, _hAlarmLegacyTcp = -1, _hAlarmLegacyUdp = -1, _hStream = -1, _hPlayback = -1, _hVoice = -1, _hPictures = -1;
    readonly List<IsupListenerState> _listeners = [];

    // Directorio: ID ISUP → clave. Lo reemplaza el servidor completo cada vez que cambia algo (SetDirectory).
    volatile Dictionary<string, string> _keys = new(StringComparer.Ordinal);

    readonly ConcurrentDictionary<string, int> _userByDevice = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<int, string> _deviceByUser = new();
    readonly ConcurrentDictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, IsupRejection> _rejections = new(StringComparer.Ordinal);

    // Una petición (ISAPI o XML) a la vez POR EQUIPO: un equipo por 4G atiende mal órdenes simultáneas, y con un candado global uno
    // lento (hasta 15 s por orden) frenaba a todos los demás.
    readonly ConcurrentDictionary<string, object> _deviceGates = new(StringComparer.Ordinal);
    object GateOf(string deviceId) => _deviceGates.GetOrAdd(deviceId, _ => new object());

    // Los delegados deben seguir vivos mientras el SDK pueda llamarlos.
    readonly Isup.DeviceRegisterCb _registerCb;
    readonly Isup.PreviewNewLinkCb _newLinkCb, _pbNewLinkCb;
    readonly Isup.PreviewDataCb _dataCb, _pbDataCb;
    readonly Isup.AlarmMsgCb _alarmCb;
    readonly Isup.SsStorageCb _ssStorageCb;
    readonly Isup.SsMsgCb _ssMsgCb;
    readonly Isup.ExceptionCb _exceptionCb;
    readonly Isup.VoiceNewLinkCb _voiceNewLinkCb;
    readonly Isup.VoiceDataCb _voiceDataCb;

    /// <summary>Lo que se sabe del registro de un equipo en línea.</summary>
    sealed record Registration(string DeviceId, string Address, bool Legacy, string VersionText, string Firmware, string Serial,
        string FullSerial, string Name, DateTimeOffset Since)
    {
        public bool Asleep { get; set; }
        public string Protocol => Legacy ? "2.0/4.0" : "5.0";
    }

    public IsupEngine()
    {
        _registerCb = OnRegister;
        _newLinkCb = OnNewLink;
        _dataCb = OnPreviewData;
        _pbNewLinkCb = OnPlaybackNewLink;
        _pbDataCb = OnPlaybackData;
        _alarmCb = OnAlarm;
        _ssStorageCb = OnPictureStore;
        _ssMsgCb = OnPictureMessage;
        _exceptionCb = OnSdkException;
        _voiceNewLinkCb = OnVoiceNewLink;
        _voiceDataCb = OnVoiceData;
    }

    void Emit(string message) => Log?.Invoke(message);
    void Raise(IsupHostEvent e) { try { EventRaised?.Invoke(e); } catch (Exception ex) { Emit("Error al publicar un evento: " + ex.Message); } }

    public bool IsOnline(string deviceId) => _userByDevice.ContainsKey(deviceId);
    bool IsLegacy(string deviceId) => _registrations.TryGetValue(deviceId, out var r) && r.Legacy;
    public string PublicIp => _publicIp;
    public string DataDir => _dataDir;

    // ====================== arranque / cierre ======================

    /// <summary>
    /// Inicia el SDK y abre los puertos. Un puerto que no se pueda abrir queda anotado en el estado (y el resto sigue funcionando);
    /// solo devuelve un error si no se pudo iniciar el SDK o no se abrió ningún puerto de registro.
    /// </summary>
    public string? Start(IsupHostOptions options, string nativeDir, string dataDir)
    {
        if (_started) return null;
        _options = options;
        _nativeDir = nativeDir;
        _dataDir = dataDir;
        _configuredAddress = options.PublicAddress?.Trim() ?? "";
        // La dirección general: la configurada o, sin ella, la de salida a internet. Es la del servidor de imágenes (una sola para todos)
        // y la de un equipo al que todavía no se le anunció la suya (ver IsupEngine.Address).
        _publicIp = PerDeviceAddress ? LocalNetwork.InternetFacingIp() : _configuredAddress;
        var logDir = Path.Combine(dataDir, "sdklog");
        Directory.CreateDirectory(logDir);

        // El SDK carga componentes con rutas relativas (./HCAapSDKCom/...) y OpenSSL 1.1 por su cuenta: se trabaja desde su carpeta.
        Directory.SetCurrentDirectory(nativeDir);
        var crypto = Path.Combine(nativeDir, "libcrypto-1_1-x64.dll");
        var ssl = Path.Combine(nativeDir, "libssl-1_1-x64.dll");
        if (File.Exists(crypto) && File.Exists(ssl))
        {
            SetInitPaths(Isup.NET_ECMS_SetSDKInitCfg, crypto, ssl, Isup.INIT_CFG_LIBEAY_PATH, Isup.INIT_CFG_SSLEAY_PATH);
            SetInitPaths(Isup.NET_ESTREAM_SetSDKInitCfg, crypto, ssl, Isup.INIT_CFG_LIBEAY_PATH, Isup.INIT_CFG_SSLEAY_PATH);
            SetInitPaths(Isup.NET_EALARM_SetSDKInitCfg, crypto, ssl, Isup.INIT_CFG_LIBEAY_PATH, Isup.INIT_CFG_SSLEAY_PATH);
        }
        else Emit($"Aviso: falta OpenSSL 1.1 (libcrypto-1_1-x64.dll / libssl-1_1-x64.dll) en {nativeDir}; los equipos ISUP 5.0 no podrán registrarse.");

        if (!Isup.NET_ECMS_Init()) return $"No se pudo iniciar el módulo de registro del SDK ISUP (error {Isup.NET_ECMS_GetLastError()}).";
        if (!Isup.NET_ESTREAM_Init()) return $"No se pudo iniciar el módulo de video del SDK ISUP (error {Isup.NET_ESTREAM_GetLastError()}).";
        if (!Isup.NET_EALARM_Init()) return $"No se pudo iniciar el módulo de alarmas del SDK ISUP (error {Isup.NET_EALARM_GetLastError()}).";
        _started = true;

        // Espera y reintentos de cada orden al equipo: por omisión el SDK espera 5 s y envía una vez; los equipos por 4G tardan más.
        var send = new NET_EHOME_SEND_PARAM { dwSize = (uint)Marshal.SizeOf<NET_EHOME_SEND_PARAM>(), dwRecvTimeOut = (uint)options.CommandTimeoutMs, bySendTimes = 2 };
        if (!Isup.NET_ECMS_SetSDKLocalCfg(Isup.LOCAL_CFG_SEND_PARAM, ref send))
            Emit($"Aviso: no se pudo fijar la espera de las órdenes a los equipos (error {Isup.NET_ECMS_GetLastError()}).");
        Isup.NET_ECMS_SetLogToFile(3, logDir, true);
        Isup.NET_ESTREAM_SetLogToFile(3, logDir, true);
        Isup.NET_EALARM_SetLogToFile(3, logDir, true);

        // La guía exige registrar el callback de excepciones: por aquí avisa el SDK de que se cortó un flujo o no pudo recibir una alarma.
        var exceptionCb = Marshal.GetFunctionPointerForDelegate(_exceptionCb);
        if (!Isup.NET_ECMS_SetExceptionCallBack(0, IntPtr.Zero, exceptionCb, IntPtr.Zero))
            Emit($"Aviso: no se pudo registrar el callback de excepciones del registro (error {Isup.NET_ECMS_GetLastError()}).");
        if (!Isup.NET_ESTREAM_SetExceptionCallBack(0, IntPtr.Zero, exceptionCb, IntPtr.Zero))
            Emit($"Aviso: no se pudo registrar el callback de excepciones de video (error {Isup.NET_ESTREAM_GetLastError()}).");

        // Registro (TCP y UDP) en el primer puerto: el SDK admite uno solo (un segundo StartListen da el error 46). Los demás se reenvían
        // a él por TCP. Latido de 15 s; fuera de línea tras 6 perdidos (90 s), como anuncia la respuesta de registro.
        var ports = options.RegistrationPorts.Where(p => p > 0).Distinct().ToList();
        if (ports.Count == 0) return "No hay puerto de registro ISUP configurado.";
        var cms = new NET_EHOME_CMS_LISTEN_PARAM { fnCB = Marshal.GetFunctionPointerForDelegate(_registerCb), dwKeepAliveSec = 15, dwTimeOutCount = 6 };
        Isup.SetIp(ref cms.struAddress, "0.0.0.0", (ushort)ports[0]);
        _hCms = Isup.NET_ECMS_StartListen(ref cms);
        AddListener("Registro", ports[0], _hCms >= 0, _hCms >= 0 ? null : $"error {Isup.NET_ECMS_GetLastError()}");
        if (_hCms < 0) return $"No se pudo abrir el puerto de registro ISUP {ports[0]} (¿lo ocupa otro programa, como la receptora de Hikvision?).";
        foreach (var port in ports.Skip(1))
        {
            try { _relays.Add(new PortRelay(port, ports[0])); AddListener($"Registro (reenvío al {ports[0]})", port, true, null); }
            catch (System.Net.Sockets.SocketException ex) { AddListener($"Registro (reenvío al {ports[0]})", port, false, ex.Message); }
        }

        var stream = new NET_EHOME_LISTEN_PREVIEW_CFG { fnNewLinkCB = Marshal.GetFunctionPointerForDelegate(_newLinkCb), byLinkMode = 0 };
        Isup.SetIp(ref stream.struIPAdress, "0.0.0.0", (ushort)options.StreamPort);
        _hStream = Isup.NET_ESTREAM_StartListenPreview(ref stream);
        AddListener("Video en vivo", options.StreamPort, _hStream >= 0, _hStream >= 0 ? null : $"error {Isup.NET_ESTREAM_GetLastError()}");

        var pb = new NET_EHOME_LISTEN_PREVIEW_CFG { fnNewLinkCB = Marshal.GetFunctionPointerForDelegate(_pbNewLinkCb), byLinkMode = 0 };
        Isup.SetIp(ref pb.struIPAdress, "0.0.0.0", (ushort)options.PlaybackPort);
        _hPlayback = Isup.NET_ESTREAM_StartListenPlayBack(ref pb);
        AddListener("Reproducción", options.PlaybackPort, _hPlayback >= 0, _hPlayback >= 0 ? null : $"error {Isup.NET_ESTREAM_GetLastError()}");

        if (options.VoicePort > 0)
        {
            var voice = new NET_EHOME_LISTEN_VOICETALK_CFG { fnNewLinkCB = Marshal.GetFunctionPointerForDelegate(_voiceNewLinkCb), byLinkMode = 0 };
            Isup.SetIp(ref voice.struIPAdress, "0.0.0.0", (ushort)options.VoicePort);
            _hVoice = Isup.NET_ESTREAM_StartListenVoiceTalk(ref voice);
            AddListener("Voz", options.VoicePort, _hVoice >= 0, _hVoice >= 0 ? null : $"error {Isup.NET_ESTREAM_GetLastError()}");
        }

        // Alarmas ISUP 5.0: llegan por MQTT (con TCP el SDK solo abría un enlace y no entendía al equipo).
        var alarm = new NET_EHOME_ALARM_LISTEN_PARAM { fnMsgCb = Marshal.GetFunctionPointerForDelegate(_alarmCb), byProtocolType = 2 };
        Isup.SetIp(ref alarm.struAddress, "0.0.0.0", (ushort)options.AlarmPort);
        _hAlarm = Isup.NET_EALARM_StartListen(ref alarm);
        AddListener("Alarmas (ISUP 5.0)", options.AlarmPort, _hAlarm >= 0, _hAlarm >= 0 ? null : $"error {Isup.NET_EALARM_GetLastError()}");

        // ISUP 2.0/4.0: alarmas por TCP y, salvo que se les indique otra cosa, por UDP en el mismo puerto.
        if (options.AllowLegacy && options.LegacyAlarmPort > 0)
        {
            var tcp = new NET_EHOME_ALARM_LISTEN_PARAM { fnMsgCb = Marshal.GetFunctionPointerForDelegate(_alarmCb), byProtocolType = 0 };
            Isup.SetIp(ref tcp.struAddress, "0.0.0.0", (ushort)options.LegacyAlarmPort);
            _hAlarmLegacyTcp = Isup.NET_EALARM_StartListen(ref tcp);
            var udp = new NET_EHOME_ALARM_LISTEN_PARAM { fnMsgCb = Marshal.GetFunctionPointerForDelegate(_alarmCb), byProtocolType = 1 };
            Isup.SetIp(ref udp.struAddress, "0.0.0.0", (ushort)options.LegacyAlarmPort);
            _hAlarmLegacyUdp = Isup.NET_EALARM_StartListen(ref udp);
            bool ok = _hAlarmLegacyTcp >= 0 || _hAlarmLegacyUdp >= 0;
            AddListener("Alarmas (ISUP 2.0/4.0)", options.LegacyAlarmPort, ok, ok ? null : $"error {Isup.NET_EALARM_GetLastError()}");
        }

        // Servidor de imágenes: sin él, los equipos que adjuntan la foto aparte no envían por ISUP los eventos que la llevan.
        if (options.PictureServerPort > 0) StartPictureServer(options, logDir);

        Emit($"Servidor ISUP listo. Registro {string.Join("/", options.RegistrationPorts)}, video {options.StreamPort}, reproducción {options.PlaybackPort}, " +
             $"voz {options.VoicePort}, alarmas {options.AlarmPort} (5.0)" +
             (options.AllowLegacy ? $" y {options.LegacyAlarmPort} (2.0/4.0)" : "; equipos ISUP 2.0/4.0 NO admitidos (sin clave)") +
             $", imágenes {options.PictureServerPort}. " +
             $"Los equipos de las redes de este servidor ({string.Join(", ", LocalNetwork.Candidates(includeVirtual: false))}) reciben la IP de su misma red; " +
             (PerDeviceAddress
                 ? "los demás, la IP de este servidor por la que llegan. "
                 : $"los demás, la dirección configurada {_configuredAddress} (también la de las fotos). ") +
             $"SDK CMS 0x{Isup.NET_ECMS_GetBuildVersion():X8}, stream 0x{Isup.NET_ESTREAM_GetBuildVersion():X8}.");
        return null;
    }

    void AddListener(string name, int port, bool open, string? error)
    {
        lock (_listeners) _listeners.Add(new IsupListenerState(name, port, open, error));
        if (!open) Emit($"Aviso: no se pudo abrir el puerto {port} ({name}): {error}. ¿Está ocupado?");
    }

    static void SetInitPaths(Func<int, IntPtr, bool> set, string cryptoPath, string sslPath, int libeayType, int ssleayType)
    {
        foreach (var (type, path) in new[] { (libeayType, cryptoPath), (ssleayType, sslPath) }) SetInitPath(set, type, path);
    }

    static void SetInitPath(Func<int, IntPtr, bool> set, int type, string path)
    {
        IntPtr buf = Marshal.AllocHGlobal(512);
        try
        {
            ZeroFill(buf, 512);
            var b = Encoding.ASCII.GetBytes(path);
            Marshal.Copy(b, 0, buf, Math.Min(b.Length, 255));
            set(type, buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var s in _liveSessions.Values.ToList()) StopLive(s);
        // Las 2.0/4.0 pueden no tener sesión (0): se cierran también por su enlace y las que esperan el enlace.
        foreach (var s in _pbSessions.Values.Concat(_pbByLink.Values).Concat(_pbAwaiting.Keys).Distinct().ToList()) StopPlayback(s);
        foreach (var v in _voiceSessions.Values.ToList()) CloseVoice(v);
        foreach (var r in _relays) r.Dispose();
        if (!_started) return;
        // Un StopListen del SDK puede quedarse esperando un callback: se limita el tiempo para no colgar el cierre del proceso.
        var teardown = Task.Run(() =>
        {
            if (_hVoice >= 0) Isup.NET_ESTREAM_StopListenVoiceTalk(_hVoice);
            if (_hPlayback >= 0) Isup.NET_ESTREAM_StopListenPlayBack(_hPlayback);
            if (_hStream >= 0) Isup.NET_ESTREAM_StopListenPreview(_hStream);
            if (_hAlarm >= 0) Isup.NET_EALARM_StopListen(_hAlarm);
            if (_hAlarmLegacyTcp >= 0) Isup.NET_EALARM_StopListen(_hAlarmLegacyTcp);
            if (_hAlarmLegacyUdp >= 0) Isup.NET_EALARM_StopListen(_hAlarmLegacyUdp);
            if (_hPictures >= 0) { Isup.NET_ESS_StopListen(_hPictures); Isup.NET_ESS_Fini(); }
            if (_hCms >= 0) Isup.NET_ECMS_StopListen(_hCms);
            Isup.NET_ESTREAM_Fini();
            Isup.NET_EALARM_Fini();
            Isup.NET_ECMS_Fini();
        });
        if (!teardown.Wait(TimeSpan.FromSeconds(6))) Emit("Aviso: el SDK ISUP no cerró a tiempo; se continúa de todos modos.");
        _started = false;
    }

    // ====================== directorio y estado ======================

    /// <summary>
    /// Reemplaza el directorio de equipos que pueden registrarse. Los que estén en línea y ya no figuren, o cuya clave cambió, se dan de
    /// baja: si siguen en el directorio vuelven a registrarse solos y se les pide la clave nueva.
    /// </summary>
    public void SetDirectory(IEnumerable<IsupDirectoryEntry> entries)
    {
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in entries)
            if (!string.IsNullOrWhiteSpace(e.DeviceId)) next[e.DeviceId.Trim()] = e.Key ?? "";
        var previous = _keys;
        _keys = next;
        foreach (var (id, userId) in _userByDevice.ToList())
        {
            bool removed = !next.TryGetValue(id, out var key);
            bool changed = !removed && previous.TryGetValue(id, out var old) && old != key;
            if (!removed && !changed) continue;
            Emit($"Equipo '{id}': {(removed ? "ya no está dado de alta en el VMS" : "cambió su clave ISUP")}; se le da de baja{(removed ? "" : " para que vuelva a registrarse con la clave nueva")}.");
            Isup.NET_ECMS_ForceLogout(userId);
            MarkOffline(userId);
        }
        foreach (var id in next.Keys) _rejections.TryRemove(id, out _);   // quien se dio de alta ya no es un rechazo pendiente
        Emit($"Directorio de equipos ISUP actualizado: {next.Count} equipo(s).");
    }

    public IsupHostStatus GetStatus(string version)
    {
        var devices = new List<IsupDeviceState>();
        var keys = _keys;
        foreach (var id in keys.Keys.Union(_registrations.Keys).Distinct().OrderBy(x => x, StringComparer.Ordinal))
        {
            _registrations.TryGetValue(id, out var r);
            bool online = _userByDevice.ContainsKey(id);
            devices.Add(new IsupDeviceState(id, online, r?.Address, r?.Protocol, online ? r?.Since : null, r?.Asleep == true, r?.Firmware,
                online && r != null && !r.Legacy && AlarmLinkUp(r.Address), AnnouncedTo(id)));
        }
        List<IsupListenerState> listeners;
        lock (_listeners) listeners = _listeners.ToList();
        return new IsupHostStatus(version, _publicIp, listeners, devices,
            _rejections.Values.OrderByDescending(r => r.LastAt).Take(100).ToList(),
            PerDeviceAddress, LocalNetwork.Candidates(includeVirtual: false));
    }

    public IsupDeviceState? GetDeviceState(string id)
    {
        if (!_registrations.TryGetValue(id, out var r)) return null;
        bool online = _userByDevice.ContainsKey(id);
        return new IsupDeviceState(id, online, r.Address, r.Protocol, online ? r.Since : null, r.Asleep, r.Firmware, online && !r.Legacy && AlarmLinkUp(r.Address),
            AnnouncedTo(id));
    }

    void Reject(string id, string reason)
    {
        if (id.Length == 0) return;
        var now = DateTimeOffset.Now;
        bool isNew = false;
        _rejections.AddOrUpdate(id, _ => { isNew = true; return new IsupRejection(id, reason, now, now, 1); },
            (_, r) => r with { Reason = reason, LastAt = now, Count = r.Count + 1 });
        // Un equipo mal configurado reintenta cada pocos segundos: solo se avisa la primera vez (y luego lo cuenta el estado).
        if (isNew)
        {
            Emit($"Equipo '{id}' rechazado: {reason}.");
            Raise(new IsupHostEvent(IsupEventKinds.Rejected, id, now, reason));
        }
    }

    // ====================== registro (callback del SDK, hilo nativo) ======================

    static string IdOf(IntPtr pOut, uint outLen) => pOut != IntPtr.Zero && outLen >= 270 ? Isup.ReadCString(pOut + Layout.RegDeviceId, Isup.MaxDeviceIdLen) : "";

    int OnRegister(int lUserID, uint type, IntPtr pOut, uint outLen, IntPtr pIn, uint inLen, IntPtr user)
    {
        try
        {
            switch (type)
            {
                case Isup.ENUM_DEV_AUTH:
                {
                    // ISUP 5.0: el SDK pide la clave esperada del equipo y la compara él mismo. Sin clave → rechaza.
                    var id = IdOf(pOut, outLen);
                    if (!_keys.TryGetValue(id, out var key))
                    {
                        Reject(id, "su ID ISUP no está dado de alta en el VMS");
                        return 0;
                    }
                    if (pIn != IntPtr.Zero && inLen > 1)
                    {
                        var kb = Encoding.ASCII.GetBytes(key);
                        int n = (int)Math.Min((uint)kb.Length, inLen - 1);
                        Marshal.Copy(kb, 0, pIn, n);
                        Marshal.WriteByte(pIn, n, 0);
                    }
                    return 1;
                }

                case Isup.ENUM_DEV_SESSIONKEY:
                {
                    // En 5.0 el SDK ya tiene la clave de sesión: se le pasa al módulo de alarmas para que descifre las del equipo.
                    var id = IdOf(pOut, outLen);
                    var sk = new NET_EHOME_DEV_SESSIONKEY();
                    var idb = Encoding.ASCII.GetBytes(id);
                    unsafe { for (int i = 0; i < idb.Length && i < Isup.MaxDeviceIdLen - 1; i++) sk.sDeviceID[i] = idb[i]; }
                    if (Isup.NET_ECMS_GetDeviceSessionKey(ref sk)) Isup.NET_EALARM_SetDeviceSessionKey(ref sk);
                    return 1;
                }

                case Isup.ENUM_DEV_DAS_REQ:
                {
                    // El equipo pregunta a qué servidor registrarse: a este mismo, en el primer puerto de registro, por la IP por la que llegó.
                    int port = _options.RegistrationPorts.FirstOrDefault(p => p > 0);
                    var deviceIp = pOut != IntPtr.Zero && outLen >= 420 ? Isup.ReadCString(pOut + Layout.RegAddress, 128) : "";
                    var address = Announce(IdOf(pOut, outLen), deviceIp);
                    var das = $"{{\"Type\":\"DAS\",\"DasInfo\":{{\"Address\":\"{address}\",\"Domain\":\"\",\"ServerType\":0,\"Port\":{port},\"UdpPort\":{port}}}}}";
                    var db = Encoding.ASCII.GetBytes(das);
                    if (pIn != IntPtr.Zero && db.Length < inLen)
                    {
                        Marshal.Copy(db, 0, pIn, db.Length);
                        Marshal.WriteByte(pIn, db.Length, 0);
                    }
                    return 1;
                }

                case Isup.ENUM_DEV_ON:
                {
                    var id = IdOf(pOut, outLen);
                    if (id.Length == 0) return 0;
                    // ISUP 2.0/4.0 no pasa por AUTH: el ID tiene que estar en el directorio igual.
                    if (!_keys.ContainsKey(id)) { Reject(id, "su ID ISUP no está dado de alta en el VMS"); return 0; }
                    var address = Isup.ReadCString(pOut + Layout.RegAddress, 128);
                    var version = Isup.ReadCString(pOut + Layout.RegProtocolVersion, 6);
                    var firmware = Isup.ReadCString(pOut + Layout.RegFirmware, 24);
                    var serial = Isup.ReadCString(pOut + Layout.RegSerial, 12);
                    string name = "", fullSerial = "";
                    if (outLen >= Layout.RegV12Size)
                    {
                        name = ReadText(pOut + Layout.RegDevName, 64);
                        fullSerial = Isup.ReadCString(pOut + Layout.RegFullSerial, 64);
                    }
                    // Sin el intercambio de 5.0 (AUTH/DAS) y con versión distinta de 5.x se trata como ISUP 2.0/4.0.
                    bool legacy = !version.StartsWith('5');
                    if (legacy)
                    {
                        // 2.0/4.0 no prueba la clave: con solo el ID, cualquiera se haría pasar por el equipo.
                        if (!_options.AllowLegacy)
                        {
                            Reject(id, $"se registra con ISUP {version} (sin clave), y este servidor solo acepta ISUP 5.0 " +
                                       "(configure el equipo en ISUP 5.0 o habilite Isup:AllowLegacy)");
                            return 0;
                        }
                        if (_registrations.TryGetValue(id, out var live) && !live.Legacy && _userByDevice.ContainsKey(id))
                        {
                            Reject(id, $"intento de registro ISUP {version} (sin clave) con el ID de un equipo en línea por ISUP 5.0" +
                                       $"{(address.Length > 0 ? $" desde {address}" : "")}: se rechaza para no reemplazarlo");
                            return 0;
                        }
                    }
                    var reg = new Registration(id, address, legacy, version, firmware, serial, fullSerial, name, DateTimeOffset.Now);
                    _registrations[id] = reg;
                    _userByDevice[id] = lUserID;
                    _deviceByUser[lUserID] = id;
                    _rejections.TryRemove(id, out _);
                    FillServerInfo(pIn, inLen, legacy, Announce(id, address));
                    Emit($"Equipo '{id}' en línea{(address.Length > 0 ? $" (IP {address})" : "")}, ISUP {reg.Protocol}{(firmware.Length > 0 ? $", firmware {firmware}" : "")}" +
                         $"{(fullSerial.Length > 0 ? $", serie {fullSerial}" : serial.Length > 0 ? $", serie {serial}" : "")}.");
                    Raise(new IsupHostEvent(IsupEventKinds.Online, id, DateTimeOffset.Now, address));
                    if (!legacy && address.Length > 0) _ = Task.Run(() => EnsureAlarmLink(id, address));   // fuera del callback: hace ISAPI
                    return 1;
                }

                case Isup.ENUM_DEV_OFF:
                    MarkOffline(lUserID);
                    return 1;

                case Isup.ENUM_DEV_SLEEP:
                {
                    // Equipos con ahorro de energía (4G, batería): no responden hasta despertarlos o hasta que despierten solos.
                    var id = IdOf(pOut, outLen);
                    if (id.Length == 0) _deviceByUser.TryGetValue(lUserID, out id!);
                    if (string.IsNullOrEmpty(id)) return 1;
                    if (_registrations.TryGetValue(id, out var r)) r.Asleep = true;
                    Emit($"Equipo '{id}' entró en reposo (ahorro de energía).");
                    Raise(new IsupHostEvent(IsupEventKinds.Sleep, id, DateTimeOffset.Now));
                    return 1;
                }

                case Isup.ENUM_DEV_ADDRESS_CHANGED:
                {
                    var id = IdOf(pOut, outLen);
                    var ip = pOut != IntPtr.Zero && outLen >= 420 ? Isup.ReadCString(pOut + Layout.RegAddress, 128) : "";
                    if (id.Length > 0 && ip.Length > 0 && _registrations.TryGetValue(id, out var reg)) _registrations[id] = reg with { Address = ip };
                    Emit($"Equipo '{id}': cambió su dirección{(ip.Length > 0 ? $" a {ip}" : "")}.");
                    // Puede haber cambiado de red: el próximo video o voz se le pide por la IP de este servidor que ahora le corresponde.
                    if (id.Length > 0 && ip.Length > 0) Announce(id, ip);
                    return 1;
                }

                case Isup.ENUM_DEV_DAS_EHOMEKEY_ERROR:
                    Reject(IdOf(pOut, outLen), "la clave ISUP que presenta no coincide con la del VMS");
                    return 1;

                case Isup.ENUM_DEV_SESSIONKEY_ERROR:
                    Emit($"Equipo '{IdOf(pOut, outLen)}': falló el intercambio de la clave de sesión (ISUP 5.0).");
                    return 1;

                default:
                    return 1;   // re-registro, latidos y tipos que no se usan
            }
        }
        catch (Exception ex) { Emit("Error en el registro: " + ex.Message); return 1; }
    }

    void MarkOffline(int userId)
    {
        if (!_deviceByUser.TryRemove(userId, out var id)) return;
        // Si el equipo ya volvió a registrarse con otro usuario, no se lo da por caído.
        if (_userByDevice.TryGetValue(id, out var current) && current != userId) return;
        _userByDevice.TryRemove(id, out _);
        foreach (var s in _liveSessions.Values.Where(s => s.DeviceId == id).ToList()) { s.Close(); _liveSessions.TryRemove(s.Session, out _); }
        foreach (var s in _pbSessions.Values.Where(s => s.DeviceId == id).ToList()) { s.Close(); _pbSessions.TryRemove(s.Session, out _); }
        foreach (var (link, s) in _pbByLink.Where(kv => kv.Value.DeviceId == id).ToList()) { s.Close(); _pbByLink.TryRemove(link, out _); }
        foreach (var s in _pbAwaiting.Keys.Where(s => s.DeviceId == id).ToList()) { s.Close(); EndLinkWait(s); }
        foreach (var v in _voiceSessions.Values.Where(v => v.DeviceId == id).ToList()) v.MarkClosed();
        Emit($"Equipo '{id}' fuera de línea.");
        Raise(new IsupHostEvent(IsupEventKinds.Offline, id, DateTimeOffset.Now));
    }

    /// <summary>
    /// Respuesta de registro (NET_EHOME_SERVER_INFO_V50, 1488 bytes que llegan en cero): latido, servidor de alarmas (MQTT en 5.0, TCP/UDP en
    /// 2.0/4.0) y servidor de imágenes. La hora (NTP) no se toca: la pone en hora el módulo de mantenimiento del VMS.
    /// <paramref name="address"/> es la que se le anuncia al equipo.
    /// </summary>
    void FillServerInfo(IntPtr pIn, uint inLen, bool legacy, string address)
    {
        if (pIn == IntPtr.Zero || inLen < 300) return;
        int alarmPort = legacy ? _options.LegacyAlarmPort : _options.AlarmPort;
        Marshal.WriteInt32(pIn, 0, (int)inLen);
        Marshal.WriteInt32(pIn, Layout.SrvKeepAlive, 15);
        Marshal.WriteInt32(pIn, Layout.SrvTimeOutCount, 6);
        if (alarmPort > 0)
        {
            WriteAddress(pIn + Layout.SrvTcpAlarm, address, alarmPort);
            WriteAddress(pIn + Layout.SrvUdpAlarm, address, alarmPort);
            // 0 = solo UDP, 1 = UDP y TCP, 2 = MQTT (ISUP 5.0). Con 0 la cámara ANPR de pruebas nunca mantenía abierto su enlace MQTT.
            Marshal.WriteInt32(pIn, Layout.SrvAlarmType, legacy ? 1 : 2);
        }
        if (_hPictures >= 0 && inLen >= Layout.SrvMinSize)
        {
            // El módulo de imágenes escucha y se anuncia en UNA dirección (la configurada o la de salida a internet), fijada al arrancar:
            // se le indica esa, no la del equipo, o subiría las fotos a una IP en la que nadie escucha.
            WriteAddress(pIn + Layout.SrvPicture, _publicIp, _options.PictureServerPort);
            Marshal.WriteInt32(pIn, Layout.SrvPicType, PictureServerType);
            WriteAscii(pIn + Layout.SrvCloudAk, PictureAccessKey, 64);
            WriteAscii(pIn + Layout.SrvCloudSk, PictureSecretKey, 64);
            Marshal.WriteByte(pIn, Layout.SrvCloudHttps, 0);
            Marshal.WriteInt32(pIn, Layout.SrvCloudPoolId, 1);
        }
    }

    // ====================== utilidades ======================

    static void WriteAscii(IntPtr p, string text, int size)
    {
        ZeroFill(p, size);
        var b = Encoding.ASCII.GetBytes(text);
        Marshal.Copy(b, 0, p, Math.Min(b.Length, size - 1));
    }

    static void WriteAddress(IntPtr p, string ip, int port)
    {
        ZeroFill(p, 128);
        var b = Encoding.ASCII.GetBytes(ip);
        Marshal.Copy(b, 0, p, Math.Min(b.Length, 127));
        Marshal.WriteInt16(p, 128, (short)port);   // wPort tras szIP[128]
    }

    static void ZeroFill(IntPtr p, int n)
    {
        int i = 0;
        for (; i + 8 <= n; i += 8) Marshal.WriteInt64(p, i, 0);
        for (; i < n; i++) Marshal.WriteByte(p, i, 0);
    }

    /// <summary>Texto de un campo de largo fijo: UTF-8 si es válido, si no GB18030/Latin-1 (los nombres que pone el usuario en el equipo).</summary>
    static string ReadText(IntPtr p, int maxLen)
    {
        if (p == IntPtr.Zero) return "";
        int n = 0;
        while (n < maxLen && Marshal.ReadByte(p, n) != 0) n++;
        var b = new byte[n];
        Marshal.Copy(p, b, 0, n);
        try { return new UTF8Encoding(false, true).GetString(b).Trim(); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(b).Trim(); }
    }

    static string Clip(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    static string ErrorHint(int err) => err switch
    {
        4 => " (canal incorrecto)", 10 => " (el equipo no respondió a tiempo)", 12 => " (orden fuera de secuencia)", 17 => " (parámetro no válido)",
        23 => " (el equipo no admite esta función)", 29 => " (la operación falló en el equipo)", 8301 => " (el equipo está en reposo)", _ => "",
    };

    /// <summary>Avisos del SDK (callback obligatorio según la guía): corte de un flujo de video, de reproducción o de voz, o fallo recibiendo una alarma.</summary>
    void OnSdkException(uint type, int userId, int handle, IntPtr user)
    {
        try
        {
            string what = type switch
            {
                Isup.EHOME_PREVIEW_EXCEPTION => "se perdió la recepción del video en vivo",
                Isup.EHOME_PLAYBACK_EXCEPTION => "se perdió la recepción de una reproducción",
                Isup.EHOME_AUDIOTALK_EXCEPTION => "falló el enlace de voz",
                Isup.EHOME_CMSALARM_EXCEPTION => "no se pudo recibir una alarma",
                _ => $"tipo 0x{type:X}",
            };
            string dev = _deviceByUser.TryGetValue(userId, out var d) ? $" · equipo '{d}'" : "";
            Emit($"Excepción del SDK: {what}{dev} (handle {handle}).");
            if (type == Isup.EHOME_PREVIEW_EXCEPTION && _liveByLink.TryGetValue(handle, out var s)) s.Close();
            if (type == Isup.EHOME_PLAYBACK_EXCEPTION && _pbByLink.TryGetValue(handle, out var p)) p.Close();
            if (type == Isup.EHOME_AUDIOTALK_EXCEPTION && _voiceByLink.TryGetValue(handle, out var v)) v.MarkClosed();
        }
        catch (Exception ex) { Emit("Error en el callback de excepciones: " + ex.Message); }
    }
}
