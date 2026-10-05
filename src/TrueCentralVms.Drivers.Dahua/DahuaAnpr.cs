using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Drivers.Dahua.Interop;

namespace TrueCentralVms.Drivers.Dahua;

/// <summary>Gancho de diagnóstico de los drivers Dahua (el servidor lo conecta a su log).</summary>
public static class DahuaDiagnostics
{
    public static Action<string>? Log
    {
        get => DahuaAnpr.Diagnostic;
        set => DahuaAnpr.Diagnostic = value;
    }
}

/// <summary>
/// Patentes de cámaras Dahua ITC por el NetSDK: sesión propia y
/// CLIENT_RealLoadPictureEx con EVENT_IVS_ALL; de lo que llega se toman los
/// eventos de tránsito (TrafficJunction y TrafficGate), que traen la patente,
/// el vehículo y las fotos. Las estructuras se leen por desplazamiento
/// (<see cref="NetSdk.Traffic"/>, medidos compilando el header).
///
/// La sesión se vigila con una consulta liviana cada 30 s: si el equipo deja
/// de contestar, la suscripción se da por caída y el servicio de patentes la
/// rehace sola.
/// </summary>
internal sealed class DahuaAnpr : IPlateSubscription
{
    /// <summary>Diagnóstico opcional (lo engancha el servidor a su log).</summary>
    public static Action<string>? Diagnostic;

    /// <summary>Eventos que se describen enteros en el log (para validar los desplazamientos con hardware).</summary>
    private const int DiagnosticEvents = 5;
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
    /// <summary>Una misma lectura llega a veces en varias fotos seguidas: se entrega una vez.</summary>
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(3);

    private readonly DeviceConnectionInfo _info;
    private readonly Action<PlateRecognition> _onPlate;
    private readonly NetSdk.AnalyzerDataCallback _callback; // referencia viva mientras el SDK pueda llamarla
    private readonly Timer _keepAlive;
    private readonly ConcurrentDictionary<uint, byte> _otherTypesSeen = new();
    private long _loginId;
    private long _handle;
    private volatile bool _alive = true;
    private int _failedChecks;
    private int _described;
    private string _lastPlate = "";
    private DateTime _lastPlateAt;

    public bool IsAlive => _alive;

    private DahuaAnpr(DeviceConnectionInfo info, Action<PlateRecognition> onPlate)
    {
        _info = info;
        _onPlate = onPlate;
        _callback = OnAnalyzerData;
        _keepAlive = new Timer(_ => CheckSession(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static Task<IPlateSubscription> SubscribeAsync(DeviceConnectionInfo info, Action<PlateRecognition> onPlate,
        CancellationToken ct) => Task.Run<IPlateSubscription>(() =>
    {
        var subscription = new DahuaAnpr(info, onPlate);
        try
        {
            subscription.Start();
            return subscription;
        }
        catch
        {
            subscription.Stop();
            throw;
        }
    }, ct);

    private void Start()
    {
        (_loginId, _) = DahuaDeviceDriver.Login(_info); // lanza DriverException con el motivo
        // Canal 0: las ITC tienen uno solo. bNeedPicFile = true para recibir las fotos.
        _handle = NetSdk.CLIENT_RealLoadPictureEx(_loginId, 0, NetSdk.EventIvsAll, true, _callback, IntPtr.Zero, IntPtr.Zero);
        if (_handle == 0)
            throw new DriverException(
                $"El equipo no aceptó la suscripción a sus eventos inteligentes (error 0x{NetSdk.CLIENT_GetLastError():X8} del NetSDK).");
        _keepAlive.Change(KeepAliveInterval, KeepAliveInterval);
        Diagnostic?.Invoke($"Dahua ANPR {_info.Host}: suscripción a eventos inteligentes abierta (NetSDK).");
    }

    /// <summary>Consulta liviana: dos fallos seguidos = sesión perdida.</summary>
    private void CheckSession()
    {
        if (!_alive) return;
        var input = new NetSdk.NET_IN_GET_DEVICETYPE_INFO { dwSize = (uint)Marshal.SizeOf<NetSdk.NET_IN_GET_DEVICETYPE_INFO>() };
        var output = new NetSdk.NET_OUT_GET_DEVICETYPE_INFO { dwSize = (uint)Marshal.SizeOf<NetSdk.NET_OUT_GET_DEVICETYPE_INFO>() };
        if (NetSdk.CLIENT_GetDeviceType(_loginId, ref input, ref output, 5000))
        {
            _failedChecks = 0;
            return;
        }
        if (++_failedChecks >= 2)
        {
            _alive = false;
            Diagnostic?.Invoke($"Dahua ANPR {_info.Host}: el equipo dejó de responder; se volverá a suscribir.");
        }
    }

    // ------------------------------------------------------------------
    // Callback del SDK (hilo del SDK: leer, entregar y volver)
    // ------------------------------------------------------------------

    private int OnAnalyzerData(long handle, uint alarmType, IntPtr info, IntPtr buffer, uint bufferSize,
        IntPtr user, int sequence, IntPtr reserved)
    {
        try
        {
            if (info == IntPtr.Zero) return 0;
            Read? plate = alarmType switch
            {
                NetSdk.EventIvsTrafficJunction => ReadJunction(info),
                NetSdk.EventIvsTrafficGate => ReadGate(info),
                _ => (Read?)null,
            };
            if (plate is null)
            {
                // Otro tipo de evento: se anota una vez por tipo (por si la cámara
                // informa las patentes con un evento que todavía no se lee).
                if (_otherTypesSeen.TryAdd(alarmType, 0))
                    Diagnostic?.Invoke($"Dahua ANPR {_info.Host}: evento 0x{alarmType:X} recibido (no es de patente; se ignora).");
                return 0;
            }
            if (string.IsNullOrWhiteSpace(plate.Value.Plate.PlateNumber)) return 0;

            // Fotos: el recorte de la patente está ubicado por el DH_PIC_INFO
            // del objeto; la escena es lo que queda antes de él (o todo).
            byte[]? scene = null, crop = null;
            if (buffer != IntPtr.Zero && bufferSize > 0)
            {
                (uint cropOffset, uint cropLength) = plate.Value.Crop;
                if (cropLength > 0 && cropOffset + cropLength <= bufferSize)
                {
                    crop = new byte[cropLength];
                    Marshal.Copy(buffer + (int)cropOffset, crop, 0, (int)cropLength);
                }
                uint sceneLength = cropLength > 0 && cropOffset > 0 ? cropOffset : bufferSize;
                scene = new byte[sceneLength];
                Marshal.Copy(buffer, scene, 0, (int)sceneLength);
            }

            var result = plate.Value.Plate with { SceneImage = scene, PlateImage = crop };
            if (Interlocked.Increment(ref _described) <= DiagnosticEvents)
                Diagnostic?.Invoke(
                    $"Dahua ANPR {_info.Host}: evento 0x{alarmType:X} ({plate.Value.Name}) secuencia {sequence}: " +
                    $"patente '{result.PlateNumber}', tipo '{result.PlateType}', color '{result.PlateColor}', " +
                    $"vehículo '{result.VehicleColor}' '{result.VehicleBrand}', velocidad {result.SpeedKmh}, carril {result.Lane}, " +
                    $"confianza {result.Confidence}, caja ({result.PlateX:0.###}, {result.PlateY:0.###}, {result.PlateWidth:0.###}, {result.PlateHeight:0.###}), " +
                    $"hora del equipo {result.CapturedAt:yyyy-MM-dd HH:mm:ss}, fotos {bufferSize} B (recorte {crop?.Length ?? 0} B).");

            // Una misma pasada puede llegar en varias fotos: se entrega una vez.
            lock (_callback)
            {
                var now = DateTime.UtcNow;
                if (result.PlateNumber == _lastPlate && now - _lastPlateAt < DuplicateWindow) return 0;
                _lastPlate = result.PlateNumber;
                _lastPlateAt = now;
            }
            _onPlate(result);
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"Dahua ANPR {_info.Host}: no se pudo leer un evento ({ex.Message}).");
        }
        return 0;
    }

    private readonly record struct Read(PlateRecognition Plate, string Name, (uint Offset, uint Length) Crop);

    private static Read ReadJunction(IntPtr p)
    {
        const int obj = NetSdk.Traffic.JunctionObject, car = NetSdk.Traffic.JunctionTrafficCar;
        // La patente viene en los datos del vehículo de tránsito; el texto del
        // objeto es el respaldo (algunos firmwares solo llenan uno).
        string plate = Str(p, car + NetSdk.Traffic.CarPlateNumber, 32);
        if (plate.Length == 0) plate = Str(p, obj + NetSdk.Traffic.ObjText, 128);
        int speed = Marshal.ReadInt32(p, car + NetSdk.Traffic.CarSpeed);
        if (speed <= 0) speed = Marshal.ReadInt32(p, NetSdk.Traffic.JunctionSpeed);
        int lane = Marshal.ReadInt32(p, car + NetSdk.Traffic.CarLane);
        if (lane <= 0) lane = Marshal.ReadInt32(p, NetSdk.Traffic.JunctionLane);
        float length = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p, car + NetSdk.Traffic.CarVehicleLength));
        string name = Str(p, NetSdk.Traffic.JunctionName, 128);

        return new Read(Build(p, obj,
            channel: Marshal.ReadInt32(p, NetSdk.Traffic.JunctionChannel),
            plate: plate,
            time: ReadTime(p, NetSdk.Traffic.JunctionUtc),
            plateType: Str(p, car + NetSdk.Traffic.CarPlateType, 32),
            plateColor: Str(p, car + NetSdk.Traffic.CarPlateColor, 32),
            vehicleColor: Str(p, car + NetSdk.Traffic.CarVehicleColor, 32),
            vehicleBrand: Str(p, car + NetSdk.Traffic.CarVehicleSign, 32),
            vehicleType: Str(p, NetSdk.Traffic.JunctionVehicle + NetSdk.Traffic.ObjSubType, 62),
            speed: speed, lane: lane,
            lengthCm: length > 0 && length < 100 ? (int)Math.Round(length * 100) : null,
            violation: Str(p, car + NetSdk.Traffic.CarViolationDesc, 64),
            method: name.Length > 0 ? name : "TrafficJunction"),
            name, Crop(p, obj));
    }

    private static Read ReadGate(IntPtr p)
    {
        const int obj = NetSdk.Traffic.GateObject;
        string name = Str(p, NetSdk.Traffic.GateName, 128);
        return new Read(Build(p, obj,
            channel: Marshal.ReadInt32(p, NetSdk.Traffic.GateChannel),
            plate: Str(p, obj + NetSdk.Traffic.ObjText, 128),
            time: ReadTime(p, NetSdk.Traffic.GateUtc),
            plateType: Str(p, obj + NetSdk.Traffic.ObjSubType, 62),
            plateColor: "", vehicleColor: "", vehicleBrand: "",
            vehicleType: Str(p, NetSdk.Traffic.GateVehicle + NetSdk.Traffic.ObjSubType, 62),
            speed: Marshal.ReadInt32(p, NetSdk.Traffic.GateSpeed),
            lane: Marshal.ReadInt32(p, NetSdk.Traffic.GateLane),
            lengthCm: null, violation: "",
            method: name.Length > 0 ? name : "TrafficGate"),
            name, Crop(p, obj));
    }

    private static PlateRecognition Build(IntPtr p, int obj, int channel, string plate, DateTime? time,
        string plateType, string plateColor, string vehicleColor, string vehicleBrand, string vehicleType,
        int speed, int lane, int? lengthCm, string violation, string method)
    {
        int confidence = Marshal.ReadInt32(p, obj + NetSdk.Traffic.ObjConfidence);
        if (confidence > 100) confidence = confidence * 100 / 255;

        // BoundingBox: left, top, right, bottom en coordenadas 0..8191.
        int left = Marshal.ReadInt32(p, obj + NetSdk.Traffic.ObjBoundingBox);
        int top = Marshal.ReadInt32(p, obj + NetSdk.Traffic.ObjBoundingBox + 4);
        int right = Marshal.ReadInt32(p, obj + NetSdk.Traffic.ObjBoundingBox + 8);
        int bottom = Marshal.ReadInt32(p, obj + NetSdk.Traffic.ObjBoundingBox + 12);
        double x = 0, y = 0, w = 0, h = 0;
        if (right > left && bottom > top && right <= 8192 && bottom <= 8192)
        {
            const double scale = 8192.0;
            (x, y, w, h) = (left / scale, top / scale, (right - left) / scale, (bottom - top) / scale);
        }

        static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
        return new PlateRecognition(
            ChannelNumber: channel + 1,
            PlateNumber: plate,
            // La hora del evento es la del reloj del equipo (en hora local, como
            // en Hikvision); si viene vacía, la de recepción.
            CapturedAt: time ?? DateTime.Now,
            Confidence: confidence is >= 0 and <= 100 ? confidence : -1,
            CharConfidences: [],
            PlateColor: NullIfEmpty(plateColor),
            PlateType: NullIfEmpty(plateType),
            VehicleType: NullIfEmpty(vehicleType),
            VehicleColor: NullIfEmpty(vehicleColor),
            VehicleBrand: NullIfEmpty(vehicleBrand),
            VehicleAttributes: null,
            SpeedKmh: speed > 0 && speed < 400 ? speed : null,
            VehicleLengthCm: lengthCm,
            Direction: null,
            Lane: lane > 0 && lane < 100 ? lane : null,
            DetectionMethod: method,
            Violation: NullIfEmpty(violation),
            PlateX: x, PlateY: y, PlateWidth: w, PlateHeight: h,
            SceneImage: null,
            PlateImage: null);
    }

    private static (uint, uint) Crop(IntPtr p, int obj) =>
        Marshal.ReadByte(p, obj + NetSdk.Traffic.ObjPicEnabled) != 0
            ? ((uint)Marshal.ReadInt32(p, obj + NetSdk.Traffic.ObjPicOffset),
               (uint)Marshal.ReadInt32(p, obj + NetSdk.Traffic.ObjPicLength))
            : (0u, 0u);

    private static DateTime? ReadTime(IntPtr p, int offset)
    {
        int year = Marshal.ReadInt32(p, offset), month = Marshal.ReadInt32(p, offset + 4),
            day = Marshal.ReadInt32(p, offset + 8), hour = Marshal.ReadInt32(p, offset + 12),
            minute = Marshal.ReadInt32(p, offset + 16), second = Marshal.ReadInt32(p, offset + 20);
        try
        {
            return year is > 2000 and < 2100
                ? new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local)
                : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Texto C de largo fijo. Las patentes y colores vienen en ASCII/UTF-8.</summary>
    private static string Str(IntPtr p, int offset, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(p + offset, bytes, 0, length);
        int end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end < 0 ? length : end).Trim();
    }

    // ------------------------------------------------------------------

    private void Stop()
    {
        _alive = false;
        _keepAlive.Change(Timeout.Infinite, Timeout.Infinite);
        long handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0) NetSdk.CLIENT_StopLoadPic(handle);
        long login = Interlocked.Exchange(ref _loginId, 0);
        if (login != 0) NetSdk.CLIENT_Logout(login);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Run(Stop);
        await _keepAlive.DisposeAsync();
        GC.KeepAlive(_callback);
    }
}
