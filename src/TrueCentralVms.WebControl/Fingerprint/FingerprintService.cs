using System.Runtime.InteropServices;
using System.Text;

namespace TrueCentralVms.WebControl.Fingerprint;

/// <summary>Estado de la sesión de enrolamiento que consulta el panel web.</summary>
public enum EnrollState
{
    Idle,
    Opening,
    Capturing,
    Done,
    Error,
    Cancelled,
}

/// <summary>Foto del estado de la sesión (lo que devuelve <c>GET /api/enroll/session</c>).</summary>
public sealed record EnrollSnapshot(
    string State,
    string Message,
    int Step,
    int TotalSteps,
    int ImageSeq,
    string? Template,
    int? Quality,
    string? Reader,
    string? Error);

/// <summary>
/// Envuelve el SDK del lector de huellas en una sesión consultable por HTTP.
///
/// El SDK bloquea durante todo el enrolamiento (varias apoyadas del dedo) y
/// reporta el avance por un callback, así que la captura corre en un hilo
/// dedicado y el panel web va leyendo el estado. Solo puede haber una sesión a
/// la vez: el SDK maneja un único lector.
/// </summary>
public sealed class FingerprintService
{
    private readonly object _gate = new();
    private readonly Action<string> _log;

    // El SDK guarda el puntero al callback: el delegado debe seguir vivo
    // mientras corra el proceso (si lo recolecta el GC, el SDK llama a memoria
    // liberada al primer mensaje).
    private static FpModuleSdk.FpMessageHandler? _messageHandler;

    private Thread? _worker;
    private bool _busy;
    private bool _cancelRequested;

    private EnrollState _state = EnrollState.Idle;
    private string _message = "Sin capturas en curso.";
    private string? _error;
    private int _step;
    private int _totalSteps;
    private string? _template;
    private int? _quality;
    private string? _reader;

    private byte[]? _imageBmp;
    private int _imageSeq;

    public FingerprintService(Action<string> log)
    {
        _log = log;
        _messageHandler = OnSdkMessage;
        FpModuleSdk.FPModule_InstallMessageHandler(_messageHandler);
        GC.KeepAlive(_messageHandler);
    }

    public bool Busy { get { lock (_gate) return _busy; } }

    public EnrollSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new EnrollSnapshot(
                _state.ToString().ToLowerInvariant(), _message, _step, _totalSteps, _imageSeq,
                _state == EnrollState.Done ? _template : null,
                _state == EnrollState.Done ? _quality : null,
                _reader, _error);
        }
    }

    /// <summary>Última imagen capturada, ya en BMP (la muestra el panel web).</summary>
    public byte[]? CurrentImage()
    {
        lock (_gate) return _imageBmp;
    }

    /// <summary>Versión del SDK (dato de diagnóstico del agente).</summary>
    public string SdkVersion()
    {
        var buffer = new byte[64];
        try
        {
            return FpModuleSdk.FPModule_GetSDKVersion(buffer) == FpModuleSdk.FP_SUCCESS
                ? Decode(buffer)
                : "desconocida";
        }
        catch (DllNotFoundException)
        {
            return "no se encontró FPModule_SDK.dll";
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    /// <summary>
    /// Prueba de conexión: abre el lector, lee su versión y lo cierra. Sirve
    /// para diagnosticar sin obligar a nadie a apoyar el dedo.
    /// </summary>
    public (bool Ok, string Message) TestReader(string readerId)
    {
        lock (_gate)
        {
            if (_busy) return (false, "Hay una captura en curso.");
            _busy = true;
        }
        try
        {
            var readers = FingerprintReaders.Scan();
            int ret;
            using (FingerprintReaders.ReserveOtherReaders(readerId, readers))
                ret = FpModuleSdk.FPModule_OpenDevice();

            if (ret != FpModuleSdk.FP_SUCCESS)
                return (false, FpModuleSdk.DescribeError(ret));

            var info = new byte[64];
            string device = FpModuleSdk.FPModule_GetDeviceInfo(info) == FpModuleSdk.FP_SUCCESS
                ? Decode(info)
                : "sin información";
            FpModuleSdk.FPModule_CloseDevice();
            return (true, $"Lector conectado. Equipo: {device}");
        }
        catch (DllNotFoundException)
        {
            return (false, "Falta FPModule_SDK.dll junto al agente.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            lock (_gate) _busy = false;
        }
    }

    /// <summary>Arranca una captura en segundo plano. Devuelve el error si no se pudo iniciar.</summary>
    public string? Start(string readerId, int collectTimes, int timeoutSeconds)
    {
        lock (_gate)
        {
            if (_busy) return "Ya hay una captura de huella en curso.";
            _busy = true;
            _cancelRequested = false;
            _state = EnrollState.Opening;
            _message = "Conectando con el lector…";
            _error = null;
            _step = 0;
            _totalSteps = collectTimes;
            _template = null;
            _quality = null;
            _reader = readerId;
            _imageBmp = null;
            _imageSeq = 0;
        }

        _worker = new Thread(() => Run(readerId, collectTimes, timeoutSeconds))
        {
            IsBackground = true,
            Name = "FingerprintEnroll",
        };
        _worker.Start();
        return null;
    }

    /// <summary>
    /// Cancela la captura. El SDK no tiene "cancelar": se cierra el lector para
    /// que la llamada bloqueada falle y el hilo termine.
    /// </summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (!_busy) return;
            _cancelRequested = true;
            _message = "Cancelando…";
        }
        try { FpModuleSdk.FPModule_CloseDevice(); } catch { }
    }

    private void Run(string readerId, int collectTimes, int timeoutSeconds)
    {
        try
        {
            var readers = FingerprintReaders.Scan();
            int ret;
            using (FingerprintReaders.ReserveOtherReaders(readerId, readers))
                ret = FpModuleSdk.FPModule_OpenDevice();

            if (ret != FpModuleSdk.FP_SUCCESS)
            {
                Fail(FpModuleSdk.DescribeError(ret));
                return;
            }

            FpModuleSdk.FPModule_SetTimeout(Math.Clamp(timeoutSeconds, 1, 60));
            FpModuleSdk.FPModule_SetCollectTimes(Math.Clamp(collectTimes, 0, 4));

            lock (_gate)
            {
                _state = EnrollState.Capturing;
                _message = "Apoye el dedo en el lector.";
            }

            var template = new byte[FpModuleSdk.TemplateSize];
            ret = FpModuleSdk.FPModule_FpEnroll(template);

            if (ret == FpModuleSdk.FP_SUCCESS)
            {
                int quality = FpModuleSdk.FPModule_GetQuality(template);
                lock (_gate)
                {
                    _state = EnrollState.Done;
                    _template = Convert.ToBase64String(template);
                    _quality = quality;
                    _step = _totalSteps;
                    _message = $"Huella capturada (calidad {quality}/100).";
                }
                _log($"Huella enrolada con calidad {quality}.");
            }
            else if (Cancelled())
            {
                lock (_gate)
                {
                    _state = EnrollState.Cancelled;
                    _message = "Captura cancelada.";
                }
            }
            else
            {
                Fail(FpModuleSdk.DescribeError(ret));
            }
        }
        catch (DllNotFoundException)
        {
            Fail("Falta FPModule_SDK.dll junto al agente: reinstale el agente de enrolamiento.");
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            try { FpModuleSdk.FPModule_CloseDevice(); } catch { }
            lock (_gate) _busy = false;
        }
    }

    private bool Cancelled()
    {
        lock (_gate) return _cancelRequested;
    }

    private void Fail(string error)
    {
        lock (_gate)
        {
            _state = _cancelRequested ? EnrollState.Cancelled : EnrollState.Error;
            _error = error;
            _message = _cancelRequested ? "Captura cancelada." : error;
        }
        if (!Cancelled()) _log($"Captura fallida: {error}");
    }

    /// <summary>Callback del SDK: llega desde su propio hilo durante el enrolamiento.</summary>
    private void OnSdkMessage(FpModuleSdk.FpMessageType type, IntPtr data)
    {
        try
        {
            switch (type)
            {
                case FpModuleSdk.FpMessageType.PressFinger:
                    SetMessage("Apoye el dedo en el lector.");
                    break;
                case FpModuleSdk.FpMessageType.RiseFinger:
                    SetMessage("Levante el dedo.");
                    break;
                case FpModuleSdk.FpMessageType.EnrollTime:
                    int step = Marshal.ReadInt32(data);
                    lock (_gate)
                    {
                        _step = step;
                        _message = $"Captura {step} de {Math.Max(_totalSteps, step)}: apoye el mismo dedo.";
                    }
                    break;
                case FpModuleSdk.FpMessageType.CapturedImage:
                    var image = Marshal.PtrToStructure<FpModuleSdk.FpImageData>(data);
                    if (image.Image != IntPtr.Zero && image.Width > 0 && image.Height > 0)
                    {
                        var raw = new byte[image.Width * image.Height];
                        Marshal.Copy(image.Image, raw, 0, raw.Length);
                        var bmp = GrayscaleBmp(raw, image.Width, image.Height);
                        lock (_gate)
                        {
                            _imageBmp = bmp;
                            _imageSeq++;
                        }
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _log($"Mensaje del SDK no procesado: {ex.Message}");
        }
    }

    private void SetMessage(string message)
    {
        lock (_gate)
        {
            if (_state == EnrollState.Capturing) _message = message;
        }
    }

    /// <summary>
    /// Arma un BMP de 8 bits en escala de grises con la imagen del sensor
    /// (paleta de 256 grises y filas de abajo hacia arriba, como manda el
    /// formato). Así el panel web la muestra sin convertir nada.
    /// </summary>
    public static byte[] GrayscaleBmp(byte[] pixels, int width, int height)
    {
        // Cada fila del BMP se alinea a 4 bytes.
        int stride = (width + 3) & ~3;
        const int headerSize = 14 + 40 + 256 * 4; // file header + info header + paleta
        var bmp = new byte[headerSize + stride * height];

        void WriteInt(int offset, int value) =>
            BitConverter.TryWriteBytes(bmp.AsSpan(offset, 4), value);

        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        WriteInt(2, bmp.Length);
        WriteInt(10, headerSize);       // offset a los datos
        WriteInt(14, 40);               // tamaño del BITMAPINFOHEADER
        WriteInt(18, width);
        WriteInt(22, height);
        bmp[26] = 1;                    // planos
        bmp[28] = 8;                    // bits por pixel
        WriteInt(34, stride * height);  // tamaño de la imagen
        WriteInt(46, 256);              // colores usados

        for (int i = 0; i < 256; i++)
        {
            int p = 54 + i * 4;
            bmp[p] = bmp[p + 1] = bmp[p + 2] = (byte)i;
        }

        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(pixels, y * width, bmp, headerSize + (height - 1 - y) * stride, width);

        return bmp;
    }

    private static string Decode(byte[] buffer)
    {
        int end = Array.IndexOf(buffer, (byte)0);
        return Encoding.Latin1.GetString(buffer, 0, end < 0 ? buffer.Length : end).Trim();
    }
}
