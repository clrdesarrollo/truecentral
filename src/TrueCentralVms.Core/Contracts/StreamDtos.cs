using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Core.Contracts;

/// <summary>Solicitud de visualización de un canal en vivo.</summary>
public sealed record StreamRequestDto(int DeviceId, int RtspChannel, StreamProfile Profile);

/// <summary>
/// Concesión de streaming: URL RTSP hacia el media server del VMS con un
/// token de sesión embebido. El token expira pronto (solo autoriza el inicio
/// de la conexión): ante una reconexión SIEMPRE se pide una concesión nueva.
/// </summary>
/// <param name="ExactSeek">Solo reproducción: false cuando el equipo no
/// arranca exactamente en el instante pedido (ONVIF Perfil G).</param>
public sealed record StreamGrantDto(string RtspUrl, string Token, DateTime ExpiresAt, bool ExactSeek = true);

/// <summary>Vista en vivo del panel web: oferta SDP del navegador (WebRTC,
/// solo recepción) para un canal y perfil.</summary>
public sealed record WebRtcOfferDto(int DeviceId, int RtspChannel, StreamProfile Profile, string Offer);

/// <summary>Respuesta SDP del media server y el identificador con que el
/// navegador cierra la sesión (DELETE /api/streams/webrtc/{Session}).</summary>
public sealed record WebRtcAnswerDto(string Answer, string Session);

/// <summary>Sesión de streaming activa (para el dashboard de administración).</summary>
public sealed record ActiveSessionDto(
    int Id,
    string Username,
    string DeviceName,
    int RtspChannel,
    string Profile,
    string ClientIp,
    DateTime StartedAt);

// ---------------------------------------------------------------------------
// Reproducción remota (las grabaciones viven en el DVR/NVR/tarjeta del equipo)
// ---------------------------------------------------------------------------

/// <summary>Tramo grabado de un canal, en hora LOCAL del equipo.
/// Kind: Continuous | Motion | Alarm | Manual | Other.</summary>
public sealed record RecordingSegmentDto(DateTime Start, DateTime End, string Kind);

/// <summary>
/// Solicitud de reproducción de un rango grabado (hora local del equipo).
/// <paramref name="Speed"/> 1 = camino normal (MediaMTX pulsa el equipo);
/// distinto de 1 = el servidor toma la sesión RTSP y le pide al equipo esa
/// velocidad, que es la única forma de que el grabador entregue más rápido.
/// </summary>
/// <param name="AlertId">Aviso desde cuya repetición se reproduce (solo para la bitácora).</param>
public sealed record PlaybackRequestDto(int DeviceId, int RtspChannel, DateTime StartLocal, DateTime EndLocal,
    double Speed = 1, long? AlertId = null);

/// <summary>Reproducción en el panel web: el mismo rango que <see cref="PlaybackRequestDto"/> más la oferta SDP del navegador.</summary>
public sealed record PlaybackWebRtcOfferDto(int DeviceId, int RtspChannel, DateTime StartLocal, DateTime EndLocal,
    double Speed, string Offer);

/// <summary>
/// Respuesta WebRTC de una reproducción. <paramref name="Path"/> es la ruta
/// de MediaMTX (para preguntar por qué falló); <paramref name="ExactSeek"/>
/// false = el equipo reproduce desde el inicio de su grabación (ONVIF).
/// </summary>
public sealed record PlaybackWebRtcAnswerDto(string Answer, string Session, string Path, bool ExactSeek);
