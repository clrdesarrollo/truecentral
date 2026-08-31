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
public sealed record PlaybackRequestDto(int DeviceId, int RtspChannel, DateTime StartLocal, DateTime EndLocal,
    double Speed = 1);
