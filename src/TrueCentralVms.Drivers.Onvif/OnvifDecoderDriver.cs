using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Drivers.Onvif;

/// <summary>
/// Esqueleto del driver ONVIF para decodificadores/receptores compatibles
/// (perfil ONVIF para display: DisplayService/ReceiverService).
///
/// Existe para demostrar el patrón de extensibilidad de CLR TrueCentral VMS:
/// implementar IDecoderDriver + IDecoderDriverFactory y registrarlo en el
/// servidor es todo lo necesario para soportar una nueva marca/protocolo
/// (Dahua, ONVIF, etc.) sin tocar el resto del sistema.
///
/// Nota: muchas cámaras ONVIF/otras marcas ya se pueden mostrar en un wall
/// Hikvision con su propio driver de dispositivo (el decoder Hikvision decodifica
/// URLs RTSP directamente); este driver es para cuando el DECODER sea de
/// otra marca.
/// </summary>
public sealed class OnvifDecoderDriver : IDecoderDriver
{
    public const string Key = "onvif";

    public bool IsConnected => false;

    private static NotSupportedException NotImplementedYet() =>
        new("El driver ONVIF para decodificadores aún no está implementado. " +
            "Para fuentes ONVIF sobre un decoder Hikvision, registre el dispositivo con su driver: el decoder Hikvision decodifica su URL RTSP.");

    public Task ConnectAsync(DecoderConnectionInfo info, CancellationToken ct = default) =>
        throw NotImplementedYet();

    public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<DecoderCapabilities> GetCapabilitiesAsync(CancellationToken ct = default) =>
        throw NotImplementedYet();

    public Task StartDecodingAsync(int decodeChannel, StreamSource source, CancellationToken ct = default) =>
        throw NotImplementedYet();

    public Task StopDecodingAsync(int decodeChannel, CancellationToken ct = default) =>
        throw NotImplementedYet();

    public Task<IReadOnlyDictionary<int, string?>> ConfigureWallAsync(
        IReadOnlyList<ScreenWindowLayout> screens,
        IReadOnlyList<FloatingWindowLayout>? floating = null, CancellationToken ct = default) =>
        throw NotImplementedYet();

    public Task CloseWindowAsync(int decodeChannel, CancellationToken ct = default) => Task.CompletedTask;

    public Task ZoomWindowAsync(int decodeChannel, bool fullscreen, CancellationToken ct = default) =>
        throw NotImplementedYet();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class OnvifDecoderDriverFactory : IDecoderDriverFactory
{
    public string DriverKey => OnvifDecoderDriver.Key;
    public string DisplayName => "ONVIF (en desarrollo)";
    public IDecoderDriver Create() => new OnvifDecoderDriver();
}
