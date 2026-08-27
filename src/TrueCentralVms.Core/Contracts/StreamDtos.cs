using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Core.Contracts;

/// <summary>Solicitud de visualización de un canal en vivo.</summary>
public sealed record StreamRequestDto(int DeviceId, int RtspChannel, StreamProfile Profile);

/// <summary>
/// Concesión de streaming: URL RTSP hacia el media server del VMS con un
/// token de sesión embebido. El token expira pronto (solo autoriza el inicio
/// de la conexión): ante una reconexión SIEMPRE se pide una concesión nueva.
/// </summary>
public sealed record StreamGrantDto(string RtspUrl, string Token, DateTime ExpiresAt);

/// <summary>Sesión de streaming activa (para el dashboard de administración).</summary>
public sealed record ActiveSessionDto(
    int Id,
    string Username,
    string DeviceName,
    int RtspChannel,
    string Profile,
    string ClientIp,
    DateTime StartedAt);
