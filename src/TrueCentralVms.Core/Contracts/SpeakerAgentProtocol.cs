using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrueCentralVms.Core.Contracts;

/// <summary>
/// Protocolo entre el servidor y «TrueCentral Speaker», el agente que convierte una Raspberry Pi o
/// un PC con Windows en un parlante IP. El agente abre UN WebSocket hacia el servidor
/// (<see cref="ConnectPath"/>): así funciona detrás de NAT, 4G o MPLS sin abrir puertos en el equipo.
/// <list type="bullet">
/// <item>Mensajes de texto: un <see cref="AgentEnvelope"/> JSON (camelCase).</item>
/// <item>Mensajes binarios: audio. Byte 0 = <see cref="AudioFrame"/>, bytes 1-2 = id del flujo
/// (little-endian), el resto es la carga en el códec con que se abrió el flujo.</item>
/// </list>
/// Primer mensaje del agente: <see cref="Ops.Hello"/> (ya enrolado, con su token) o
/// <see cref="Ops.Enroll"/> (con el código de un solo uso que dio la web). El servidor contesta
/// <see cref="Ops.Welcome"/> o <see cref="Ops.Rejected"/>. Después el servidor manda
/// <see cref="Ops.Request"/> y el agente contesta <see cref="Ops.Response"/> con el mismo id;
/// el agente avisa cambios con <see cref="Ops.Event"/>.
/// </summary>
public static class SpeakerAgentProtocol
{
    public const string ConnectPath = "/api/speaker-agents/connect";

    /// <summary>Clave del driver de parlantes que atiende a los agentes.</summary>
    public const string DriverKey = "truecentral-agent";

    /// <summary>Versión del protocolo; el servidor rechaza agentes de una versión mayor que no entiende.</summary>
    public const int Version = 1;

    /// <summary>Primer byte de una trama binaria de audio.</summary>
    public const byte AudioFrame = 0x01;

    /// <summary>Largo de la cabecera de una trama de audio (tipo + id de flujo).</summary>
    public const int AudioHeaderBytes = 3;

    public static class Ops
    {
        public const string Hello = "hello";
        public const string Enroll = "enroll";
        public const string Welcome = "welcome";
        public const string Rejected = "rejected";
        public const string Request = "request";
        public const string Response = "response";
        public const string Event = "event";
    }

    /// <summary>Órdenes del servidor al agente.</summary>
    public static class Methods
    {
        /// <summary>Identificación y capacidades → <see cref="AgentProbeResult"/>.</summary>
        public const string Probe = "probe";
        public const string VolumeGet = "volume.get";
        /// <summary><see cref="AgentVolumeArgs"/>.</summary>
        public const string VolumeSet = "volume.set";
        /// <summary><see cref="AgentLiveOpenArgs"/> → <see cref="AgentStreamArgs"/>.</summary>
        public const string LiveOpen = "live.open";
        /// <summary><see cref="AgentStreamArgs"/>.</summary>
        public const string LiveClose = "live.close";
        /// <summary>Corta todo lo que esté sonando.</summary>
        public const string Stop = "stop";
        /// <summary>→ <see cref="AgentStateResult"/>.</summary>
        public const string State = "state";
        /// <summary>Tono de prueba del altavoz; <see cref="AgentToneArgs"/>.</summary>
        public const string TestTone = "test.tone";
    }

    /// <summary>Avisos del agente (sin respuesta).</summary>
    public static class Events
    {
        /// <summary>El agente cerró un flujo por su cuenta (sin audio, error del dispositivo); <see cref="AgentStreamArgs"/>.</summary>
        public const string StreamClosed = "stream-closed";
        /// <summary>El volumen cambió en el propio equipo (página local); <see cref="AgentVolumeArgs"/>.</summary>
        public const string VolumeChanged = "volume-changed";
    }

    /// <summary>Códecs del flujo en vivo (8 kHz mono): los mismos nombres que <c>ISpeakerAudioSession.Codec</c>.</summary>
    public static class Codecs
    {
        public const string ALaw = "alaw";
        public const string MuLaw = "ulaw";
        public const string Pcm16 = "pcm16";
    }

    /// <summary>Códigos de cierre del WebSocket (rango privado 4000-4999).</summary>
    public static class CloseCodes
    {
        /// <summary>Token o código inválido: el agente debe volver a enrolarse (no reintenta solo).</summary>
        public const int Unauthorized = 4401;
        /// <summary>Otra conexión del mismo agente tomó su lugar.</summary>
        public const int Replaced = 4409;
        /// <summary>El administrador revocó o eliminó el parlante.</summary>
        public const int Revoked = 4403;
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Normaliza un código de enrolamiento escrito a mano ("abcd efgh", "ABCD-EFGH" → "ABCDEFGH").</summary>
    public static string NormalizeCode(string? code) =>
        new((code ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Un id de agente válido: 32 hexadecimales en minúscula (lo genera el agente al instalarse).</summary>
    public static bool IsValidAgentId(string? agentId) =>
        agentId is { Length: 32 } && agentId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Arma una trama binaria de audio.</summary>
    public static byte[] AudioFrameOf(ushort stream, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[AudioHeaderBytes + payload.Length];
        frame[0] = AudioFrame;
        frame[1] = (byte)(stream & 0xFF);
        frame[2] = (byte)(stream >> 8);
        payload.CopyTo(frame.AsSpan(AudioHeaderBytes));
        return frame;
    }
}

/// <summary>Sobre único de todos los mensajes de texto; cada <see cref="Op"/> usa solo algunos campos.</summary>
public sealed record AgentEnvelope
{
    public required string Op { get; init; }
    /// <summary>Correlación de <c>request</c>/<c>response</c>.</summary>
    public int? Id { get; init; }
    public string? Method { get; init; }
    public JsonElement? Args { get; init; }
    public bool? Ok { get; init; }
    public string? Error { get; init; }
    public JsonElement? Data { get; init; }
    /// <summary>Nombre del aviso en <c>event</c>.</summary>
    public string? Name { get; init; }

    // hello / enroll
    public string? AgentId { get; init; }
    public string? Token { get; init; }
    public string? Code { get; init; }
    public AgentInfo? Info { get; init; }

    // welcome / rejected
    public int? SpeakerId { get; init; }
    public string? SpeakerName { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Lo que el agente cuenta de sí mismo al conectarse.</summary>
public sealed record AgentInfo(
    string Version,
    int ProtocolVersion,
    /// <summary>"linux-arm64", "win-x64"...</summary>
    string Platform,
    /// <summary>Hardware ("Raspberry Pi 4 Model B Rev 1.5", "Windows 11 Pro").</summary>
    string? Hardware,
    string? Hostname,
    string? SerialNumber);

public sealed record AgentProbeResult(
    string Model,
    string? SerialNumber,
    string FirmwareVersion,
    string DeviceType,
    int Volume,
    IReadOnlyList<string> LiveCodecs,
    /// <summary>Dispositivo de salida en uso (para mostrar).</summary>
    string? OutputDevice);

public sealed record AgentVolumeArgs(int Volume);

public sealed record AgentLiveOpenArgs(string Codec);

public sealed record AgentStreamArgs(int Stream);

public sealed record AgentToneArgs(int Seconds);

public sealed record AgentStateResult(bool IsPlaying, string? CurrentName);

/// <summary>Código de enrolamiento recién generado (se muestra una sola vez en la web).</summary>
public sealed record SpeakerEnrollmentDto(int SpeakerId, string Code, DateTime ExpiresAt);
