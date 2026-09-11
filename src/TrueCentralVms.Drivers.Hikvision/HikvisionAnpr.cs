using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Hikvision.Interop;

namespace TrueCentralVms.Drivers.Hikvision;

/// <summary>
/// Recepción de reconocimientos de patentes (ANPR/ITS) por el canal de alarma
/// de HCNetSDK: la cámara empuja cada lectura con sus imágenes; el VMS no
/// analiza video.
///
/// Las estructuras se desempaquetan con <see cref="ItsInterop"/> —calcado de la
/// cabecera 6.1.9.48— y NO con las de <see cref="CHCNetSDK"/>, que vienen de un
/// SDK anterior y dejarían todos los campos corridos.
///
/// Detalle clave del SDK: el callback de mensajes es ÚNICO POR PROCESO
/// (<c>NET_DVR_SetDVRMessageCallBack_V30</c>), así que se instala una sola vez
/// y el enrutamiento a cada suscripción se hace por el <c>lUserID</c> que el
/// propio SDK entrega en <c>NET_DVR_ALARMER</c>.
///
/// El equipo parte una misma pasada de vehículo en varios mensajes (uno por
/// foto del grupo, ver <c>byGroupNum</c>/<c>byPicNo</c>): se agrupan por
/// <c>dwMatchNo</c> + patente durante una ventana corta y se entrega UN solo
/// reconocimiento con todas sus imágenes.
/// </summary>
internal static class HikvisionAnpr
{
    /// <summary>Ventana de agrupación de los mensajes de una misma pasada.</summary>
    private static readonly TimeSpan GroupWindow = TimeSpan.FromMilliseconds(900);

    /// <summary>Suscripciones activas por sesión de login (lUserID del SDK).</summary>
    private static readonly ConcurrentDictionary<int, Action<PlateRecognition>> Handlers = new();

    // ------------------------------------------------------------------
    // Suscripción
    // ------------------------------------------------------------------

    public static IPlateSubscription Subscribe(DeviceConnectionInfo info, Action<PlateRecognition> onPlate)
    {
        HikvisionSdk.EnsureInitialized();
        // El callback del SDK es único por proceso: lo administra
        // HikvisionAlarmChannel y enruta por sesión (también lo usan los
        // eventos de analítica).
        HikvisionAlarmChannel.EnsureInstalled();

        // Sesión propia (no la caché de PTZ: esa se poda a los 3 minutos de
        // ocio y se llevaría el canal de alarma con ella).
        var deviceInfo = new CHCNetSDK.NET_DVR_DEVICEINFO_V30();
        int userId = CHCNetSDK.NET_DVR_Login_V30(info.Host, info.Port, info.Username, info.Password, ref deviceInfo);
        if (userId < 0)
        {
            uint code = CHCNetSDK.NET_DVR_GetLastError();
            throw new DriverException(
                $"No se pudo iniciar sesión en {info.Host}:{info.Port}: {HikvisionException.DescribeForUser(code)} (error {code}).");
        }

        Handlers[userId] = onPlate;
        HikvisionAlarmChannel.Register(userId, (command, data, length) => OnSdkMessage(userId, command, data, length));
        var param = new ItsInterop.NET_DVR_SETUPALARM_PARAM
        {
            dwSize = (uint)Marshal.SizeOf<ItsInterop.NET_DVR_SETUPALARM_PARAM>(),
            byLevel = 1,           // prioridad media
            byAlarmInfoType = 1,   // 1 = NET_ITS_PLATE_RESULT (formato nuevo, con grupo de fotos)
            byRes1 = new byte[2],
        };
        int handle = ItsInterop.NET_DVR_SetupAlarmChan_V41(userId, ref param);
        if (handle < 0)
        {
            uint code = CHCNetSDK.NET_DVR_GetLastError();
            Handlers.TryRemove(userId, out _);
            HikvisionAlarmChannel.Unregister(userId);
            CHCNetSDK.NET_DVR_Logout(userId);
            throw new DriverException(
                $"El equipo {info.Host} no aceptó el canal de eventos de patentes: " +
                $"{HikvisionException.DescribeForUser(code)} (error {code}).");
        }
        return new Subscription(userId, handle);
    }

    private sealed class Subscription(int userId, int alarmHandle) : IPlateSubscription
    {
        private int _disposed;

        public bool IsAlive => Volatile.Read(ref _disposed) == 0;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return ValueTask.CompletedTask;
            Handlers.TryRemove(userId, out _);
            HikvisionAlarmChannel.Unregister(userId);
            DiscardPendingOf(userId);
            CHCNetSDK.NET_DVR_CloseAlarmChan_V30(alarmHandle);
            CHCNetSDK.NET_DVR_Logout(userId);
            return ValueTask.CompletedTask;
        }
    }

    // ------------------------------------------------------------------
    // Mensajes del SDK (hilo del SDK: copiar y salir rápido)
    // ------------------------------------------------------------------

    private static void OnSdkMessage(int userId, int command, IntPtr info, uint length)
    {
        if (!Handlers.ContainsKey(userId)) return;

        var (recognition, groupNo) = command switch
        {
            CHCNetSDK.COMM_ITS_PLATE_RESULT => ParseItsPlate(info, length),
            CHCNetSDK.COMM_ITS_GATE_VEHICLE => ParseGateVehicle(info, length),
            CHCNetSDK.COMM_UPLOAD_PLATE_RESULT => ParseLegacyPlate(info, length),
            _ => (null, 0u),
        };
        if (recognition is null)
            return;

        Accumulate(userId, groupNo, recognition);
    }

    // ------------------------------------------------------------------
    // Agrupación de los mensajes de una misma pasada
    // ------------------------------------------------------------------

    private sealed class PendingGroup
    {
        public required PlateRecognition Value;
        public required Timer Timer;
        public required int UserId;
    }

    private static readonly ConcurrentDictionary<string, PendingGroup> Pending = new();

    private static void Accumulate(int userId, uint groupNo, PlateRecognition recognition)
    {
        string key = $"{userId}|{groupNo}|{recognition.PlateNumber}";
        var group = Pending.AddOrUpdate(key,
            _ => new PendingGroup
            {
                Value = recognition,
                UserId = userId,
                Timer = new Timer(FlushGroup, key, Timeout.Infinite, Timeout.Infinite),
            },
            (_, existing) =>
            {
                lock (existing) existing.Value = Merge(existing.Value, recognition);
                return existing;
            });

        // Cada mensaje nuevo del grupo corre la ventana: el reconocimiento se
        // entrega cuando el equipo deja de mandar fotos de esta pasada.
        try { group.Timer.Change(GroupWindow, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* la suscripción se cerró en el intertanto */ }
    }

    /// <summary>Completa el reconocimiento acumulado con lo que traiga el mensaje
    /// siguiente: las fotos que falten y los datos de la lectura más confiable.</summary>
    private static PlateRecognition Merge(PlateRecognition acc, PlateRecognition next) => acc with
    {
        SceneImage = acc.SceneImage ?? next.SceneImage,
        PlateImage = acc.PlateImage ?? next.PlateImage,
        Confidence = Math.Max(acc.Confidence, next.Confidence),
        CharConfidences = acc.CharConfidences.Count >= next.CharConfidences.Count ? acc.CharConfidences : next.CharConfidences,
        SpeedKmh = acc.SpeedKmh ?? next.SpeedKmh,
        VehicleLengthCm = acc.VehicleLengthCm ?? next.VehicleLengthCm,
        Lane = acc.Lane ?? next.Lane,
        Direction = acc.Direction ?? next.Direction,
        DetectionMethod = acc.DetectionMethod ?? next.DetectionMethod,
        Violation = acc.Violation ?? next.Violation,
        VehicleType = acc.VehicleType ?? next.VehicleType,
        VehicleColor = acc.VehicleColor ?? next.VehicleColor,
        VehicleBrand = acc.VehicleBrand ?? next.VehicleBrand,
        VehicleAttributes = acc.VehicleAttributes ?? next.VehicleAttributes,
        PlateColor = acc.PlateColor ?? next.PlateColor,
        PlateType = acc.PlateType ?? next.PlateType,
        // El recuadro de la placa solo sirve junto con la foto que lo trajo.
        PlateX = acc.PlateWidth > 0 ? acc.PlateX : next.PlateX,
        PlateY = acc.PlateWidth > 0 ? acc.PlateY : next.PlateY,
        PlateWidth = acc.PlateWidth > 0 ? acc.PlateWidth : next.PlateWidth,
        PlateHeight = acc.PlateWidth > 0 ? acc.PlateHeight : next.PlateHeight,
    };

    private static void FlushGroup(object? state)
    {
        if (state is not string key || !Pending.TryRemove(key, out var group))
            return;
        group.Timer.Dispose();
        PlateRecognition value;
        lock (group) value = group.Value;
        if (Handlers.TryGetValue(group.UserId, out var handler))
        {
            try { handler(value); }
            catch { /* el consumidor registra sus propios errores */ }
        }
    }

    /// <summary>Descarta los grupos a medio armar de una suscripción que se cierra.</summary>
    private static void DiscardPendingOf(int userId)
    {
        foreach (var pair in Pending.ToArray())
        {
            if (pair.Value.UserId != userId) continue;
            if (Pending.TryRemove(pair.Key, out var stale))
                stale.Timer.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // Parseo de los mensajes del SDK
    // ------------------------------------------------------------------

    /// <summary>COMM_ITS_PLATE_RESULT (0x3050): cámaras ITS/ANPR modernas.</summary>
    private static (PlateRecognition?, uint) ParseItsPlate(IntPtr info, uint length)
    {
        if (length < Marshal.SizeOf<ItsInterop.NET_ITS_PLATE_RESULT>())
            return (null, 0);
        var r = Marshal.PtrToStructure<ItsInterop.NET_ITS_PLATE_RESULT>(info);

        string plate = CleanPlate(DecodeText(r.struPlateInfo.sLicense));
        if (plate.Length == 0)
            return (null, 0); // pasada sin patente legible: no es un reconocimiento

        var (scene, crop, rect, takenAt) = PickPictures(r.struPicInfo, (int)r.dwPicNum);
        var plateRect = rect ?? r.struPlateInfo.struPlateRect;
        int channel = r.byChanIndexEx * 256 + r.byChanIndex;

        return (new PlateRecognition(
            ChannelNumber: channel > 0 ? channel : 1,
            PlateNumber: plate,
            // La hora absoluta que la cámara grabó EN la foto es la buena: es
            // la misma que rotula la imagen. struSnapFirstPicTime viene en cero
            // en varios firmwares, y ahí sí toca la hora de la pasada.
            CapturedAt: takenAt ?? ToDateTime(r.struSnapFirstPicTime) ?? DateTime.Now,
            Confidence: r.struPlateInfo.byEntireBelieve <= 100 ? r.struPlateInfo.byEntireBelieve : -1,
            CharConfidences: CharConfidencesOf(r.struPlateInfo, plate.Length),
            PlateColor: ItsText.PlateColor(r.struPlateInfo.byColor),
            PlateType: ItsText.PlateType(r.struPlateInfo.byPlateType),
            VehicleType: ItsText.VehicleType(r.byVehicleType != 0 ? r.byVehicleType : r.struVehicleInfo.byVehicleType),
            VehicleColor: ItsText.VehicleColor(r.struVehicleInfo.byColor),
            VehicleBrand: ItsText.VehicleBrand(r.struVehicleInfo.byVehicleLogoRecog),
            VehicleAttributes: ItsText.Attributes(r.byPilotSafebelt, r.byCopilotSafebelt, r.byPilotCall,
                r.byDangerousVehicles, r.byYellowLabelCar, r.byPendant),
            SpeedKmh: r.struVehicleInfo.wSpeed > 0 ? r.struVehicleInfo.wSpeed : null,
            VehicleLengthCm: r.struVehicleInfo.wLength > 0 ? r.struVehicleInfo.wLength : null,
            Direction: ItsText.Direction(r.byDir),
            Lane: r.byDriveChan > 0 ? r.byDriveChan : null,
            DetectionMethod: ItsText.DetectType(r.byDetectType),
            Violation: ItsText.Violation(r.wIllegalType),
            PlateX: plateRect.fX, PlateY: plateRect.fY,
            PlateWidth: plateRect.fWidth, PlateHeight: plateRect.fHeight,
            SceneImage: scene,
            PlateImage: crop), r.dwMatchNo);
    }

    /// <summary>COMM_ITS_GATE_VEHICLE (0x3052): cámaras de barrera / control de acceso vehicular.</summary>
    private static (PlateRecognition?, uint) ParseGateVehicle(IntPtr info, uint length)
    {
        if (length < Marshal.SizeOf<ItsInterop.NET_ITS_GATE_VEHICLE>())
            return (null, 0);
        var r = Marshal.PtrToStructure<ItsInterop.NET_ITS_GATE_VEHICLE>(info);

        string plate = CleanPlate(DecodeText(r.struPlateInfo.sLicense));
        if (plate.Length == 0)
            return (null, 0);

        var (scene, crop, rect, takenAt) = PickPictures(r.struPicInfo, (int)r.dwPicNum);
        var plateRect = rect ?? r.struPlateInfo.struPlateRect;

        return (new PlateRecognition(
            ChannelNumber: r.dwChanIndex > 0 ? (int)r.dwChanIndex : 1,
            PlateNumber: plate,
            // Esta estructura no trae hora propia: sirve la de la foto y, si no
            // vino ninguna, la del servidor.
            CapturedAt: takenAt ?? DateTime.Now,
            Confidence: r.struPlateInfo.byEntireBelieve <= 100 ? r.struPlateInfo.byEntireBelieve : -1,
            CharConfidences: CharConfidencesOf(r.struPlateInfo, plate.Length),
            PlateColor: ItsText.PlateColor(r.struPlateInfo.byColor),
            PlateType: ItsText.PlateType(r.struPlateInfo.byPlateType),
            VehicleType: ItsText.VehicleType(r.struVehicleInfo.byVehicleType),
            VehicleColor: ItsText.VehicleColor(r.struVehicleInfo.byColor),
            VehicleBrand: ItsText.VehicleBrand(r.struVehicleInfo.byVehicleLogoRecog),
            VehicleAttributes: null,
            SpeedKmh: r.struVehicleInfo.wSpeed > 0 ? r.struVehicleInfo.wSpeed : null,
            VehicleLengthCm: r.struVehicleInfo.wLength > 0 ? r.struVehicleInfo.wLength : null,
            // En la cámara de barrera el sentido es entrada/salida.
            Direction: r.byDir switch { 1 => "Entrada", 2 => "Salida", _ => null },
            Lane: r.wLaneid > 0 ? r.wLaneid : null,
            DetectionMethod: ItsText.DetectType(r.byDetectType),
            // wBackList distinto de 0 = la patente coincidió con una lista de bloqueo del equipo.
            Violation: r.wBackList > 0 ? DecodeText(r.byAlarmReason) is { Length: > 0 } reason
                    ? reason
                    : "Coincide con lista de bloqueo"
                : null,
            PlateX: plateRect.fX, PlateY: plateRect.fY,
            PlateWidth: plateRect.fWidth, PlateHeight: plateRect.fHeight,
            SceneImage: scene,
            PlateImage: crop), r.dwMatchNo);
    }

    /// <summary>COMM_UPLOAD_PLATE_RESULT (0x2800): formato antiguo (equipos previos a ITS).</summary>
    private static (PlateRecognition?, uint) ParseLegacyPlate(IntPtr info, uint length)
    {
        if (length < Marshal.SizeOf<ItsInterop.NET_DVR_PLATE_RESULT>())
            return (null, 0);
        var r = Marshal.PtrToStructure<ItsInterop.NET_DVR_PLATE_RESULT>(info);

        string plate = CleanPlate(DecodeText(r.struPlateInfo.sLicense));
        if (plate.Length == 0)
            return (null, 0);

        return (new PlateRecognition(
            ChannelNumber: r.byChanIndex > 0 ? r.byChanIndex : 1,
            PlateNumber: plate,
            CapturedAt: ParseAbsTime(r.byAbsTime) ?? DateTime.Now,
            Confidence: r.struPlateInfo.byEntireBelieve <= 100 ? r.struPlateInfo.byEntireBelieve : -1,
            CharConfidences: CharConfidencesOf(r.struPlateInfo, plate.Length),
            PlateColor: ItsText.PlateColor(r.struPlateInfo.byColor),
            PlateType: ItsText.PlateType(r.struPlateInfo.byPlateType),
            VehicleType: ItsText.VehicleType(r.byVehicleType != 0 ? r.byVehicleType : r.struVehicleInfo.byVehicleType),
            VehicleColor: ItsText.VehicleColor(r.struVehicleInfo.byColor),
            VehicleBrand: ItsText.VehicleBrand(r.struVehicleInfo.byVehicleLogoRecog),
            VehicleAttributes: null,
            SpeedKmh: r.struVehicleInfo.wSpeed > 0 ? r.struVehicleInfo.wSpeed : null,
            VehicleLengthCm: r.struVehicleInfo.wLength > 0 ? r.struVehicleInfo.wLength : null,
            Direction: null,
            Lane: r.byDriveChan > 0 ? r.byDriveChan : null,
            DetectionMethod: null,
            Violation: null,
            PlateX: r.struPlateInfo.struPlateRect.fX, PlateY: r.struPlateInfo.struPlateRect.fY,
            PlateWidth: r.struPlateInfo.struPlateRect.fWidth, PlateHeight: r.struPlateInfo.struPlateRect.fHeight,
            // pBuffer1 = escena completa, pBuffer2 = recorte de la placa.
            SceneImage: CopyBuffer(r.pBuffer1, r.dwPicLen),
            PlateImage: CopyBuffer(r.pBuffer2, r.dwPicPlateLen)), 0);
    }

    // ------------------------------------------------------------------
    // Utilidades de marshaling
    // ------------------------------------------------------------------

    /// <summary>
    /// Elige la foto de escena y el primer plano de la placa del grupo que
    /// manda el equipo. Manda el tipo declarado (0 = placa, 1 = escena,
    /// 2 = imagen compuesta); si el equipo no lo informa, decide el tamaño: la
    /// escena siempre pesa más que el recorte. Devuelve además el recuadro de
    /// la placa asociado a la foto de escena elegida.
    /// </summary>
    private static (byte[]? Scene, byte[]? Crop, ItsInterop.NET_VCA_RECT? Rect, DateTime? TakenAt) PickPictures(
        ItsInterop.NET_ITS_PICTURE_INFO[]? pictures, int count)
    {
        if (pictures is null || count <= 0)
            return (null, null, null, null);

        // byDataType 1 = la foto quedó en un servidor de almacenamiento y
        // pBuffer no trae nada: esas se descartan.
        var valid = pictures.Take(Math.Min(count, pictures.Length))
            .Where(p => p.pBuffer != IntPtr.Zero && p.dwDataLen > 0 && p.byDataType == 0)
            .ToList();
        if (valid.Count == 0)
            return (null, null, null, null);

        int cropIndex = valid.FindIndex(p => p.byType == 0);
        int sceneIndex = valid.FindIndex(p => p.byType is 1 or 2);

        // Sin tipos utilizables: la más pesada es la escena y la más liviana el recorte.
        if (sceneIndex < 0)
        {
            sceneIndex = IndexOfExtreme(valid, cropIndex, largest: true);
            if (cropIndex < 0 && valid.Count > 1)
                cropIndex = IndexOfExtreme(valid, sceneIndex, largest: false);
        }
        else if (cropIndex < 0 && valid.Count > 1)
        {
            cropIndex = IndexOfExtreme(valid, sceneIndex, largest: false);
        }
        if (sceneIndex < 0)
            return (null, null, null, null);

        var scene = valid[sceneIndex];
        var rect = scene.struPlateRect.fWidth > 0 ? scene.struPlateRect : (ItsInterop.NET_VCA_RECT?)null;

        return (CopyBuffer(scene.pBuffer, scene.dwDataLen),
                cropIndex >= 0 ? CopyBuffer(valid[cropIndex].pBuffer, valid[cropIndex].dwDataLen) : null,
                rect,
                ParseAbsTime(scene.byAbsTime));
    }

    /// <summary>Índice de la foto más pesada (o más liviana) saltándose <paramref name="skip"/>.</summary>
    private static int IndexOfExtreme(List<ItsInterop.NET_ITS_PICTURE_INFO> pictures, int skip, bool largest)
    {
        int best = -1;
        for (int i = 0; i < pictures.Count; i++)
        {
            if (i == skip) continue;
            if (best < 0 ||
                (largest ? pictures[i].dwDataLen > pictures[best].dwDataLen
                         : pictures[i].dwDataLen < pictures[best].dwDataLen))
                best = i;
        }
        return best;
    }

    /// <summary>Copia el JPEG desde la memoria del SDK (válida solo durante el callback).</summary>
    private static byte[]? CopyBuffer(IntPtr buffer, uint length)
    {
        const uint MaxImageBytes = 16 * 1024 * 1024;
        if (buffer == IntPtr.Zero || length == 0 || length > MaxImageBytes)
            return null;
        byte[] data = new byte[length];
        Marshal.Copy(buffer, data, 0, (int)length);
        return data;
    }

    /// <summary>Texto ASCII del SDK, cortado en el primer NUL.</summary>
    private static string DecodeText(byte[]? raw)
    {
        if (raw is null) return "";
        int len = Array.IndexOf(raw, (byte)0);
        if (len < 0) len = raw.Length;
        return len == 0 ? "" : System.Text.Encoding.ASCII.GetString(raw, 0, len).Trim();
    }

    /// <summary>
    /// Normaliza la patente: algunos firmwares agregan espacios o guiones.
    /// Quedan solo letras y números en mayúscula (el formato del registro
    /// chileno; los caracteres no ASCII de otras regiones no se conservan).
    /// </summary>
    private static string CleanPlate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string plate = new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        // Lo que informan los firmwares cuando pasó un vehículo sin placa legible.
        return plate is "UNKNOWN" or "NOPLATE" or "NONE" ? "" : plate;
    }

    private static IReadOnlyList<int> CharConfidencesOf(ItsInterop.NET_DVR_PLATE_INFO plateInfo, int plateLength)
    {
        if (plateInfo.byBelieve is not { Length: > 0 } believe || plateLength <= 0)
            return [];
        int take = Math.Min(plateLength, believe.Length);
        var values = new List<int>(take);
        for (int i = 0; i < take; i++)
            values.Add(believe[i]);
        // Todo en cero = el equipo no informa confianza por carácter.
        return values.Any(v => v > 0) ? values : [];
    }

    /// <summary>Hora del equipo, o null si el firmware dejó la estructura en cero.</summary>
    private static DateTime? ToDateTime(ItsInterop.NET_DVR_TIME_V30 t)
    {
        try
        {
            if (t.wYear is < 2000 or > 2100) return null;
            return new DateTime(t.wYear, t.byMonth, t.byDay, t.byHour, t.byMinute, t.bySecond,
                Math.Min((int)t.wMilliSec, 999));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Hora absoluta rotulada por el equipo: "yyyymmddhhmmssxxx" en ASCII
    /// (los tres últimos dígitos son milisegundos). null si no vino.</summary>
    private static DateTime? ParseAbsTime(byte[]? raw)
    {
        string text = DecodeText(raw);
        foreach (string format in (string[])["yyyyMMddHHmmssfff", "yyyyMMddHHmmss"])
            if (DateTime.TryParseExact(text, format, null, System.Globalization.DateTimeStyles.None, out var parsed))
                return parsed;
        return null;
    }
}
