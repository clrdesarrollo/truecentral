namespace TrueCentralVms.Core.Contracts;

// ---------------------------------------------------------------------------
// Protocolo entre la base de descarga (agente en el PC de la guardia) y el
// servidor. El agente siempre inicia (HTTPS saliente, funciona detrás de NAT):
// se enrola una vez con un código y desde ahí se autentica con su token en la
// cabecera "Authorization: Agent <token>". Cada pocos segundos manda un latido
// con los bastones que tiene puestos y recibe la hora del servidor y las
// órdenes pendientes del panel.

public static class PatrolAgentProtocol
{
    public const string AuthScheme = "Agent";
    /// <summary>Cada cuánto late el agente (y por lo tanto cuánto tarda en llegarle una orden del panel).</summary>
    public const int HeartbeatSeconds = 3;
    /// <summary>Sin latido en este tiempo la base se muestra desconectada.</summary>
    public const int OfflineAfterSeconds = 30;
}

public sealed record PatrolAgentEnrollRequest(string Code, string MachineName, string Version);

public sealed record PatrolAgentEnrollResponse(int AgentId, string AgentName, string Token);

/// <summary>Un bastón que el agente ve en uno de sus puertos.</summary>
public sealed record PatrolAgentReaderState(long Serial, string Port, bool Docked, string? Error);

public sealed record PatrolAgentHeartbeat(string Version, IReadOnlyList<PatrolAgentReaderState> Readers, string? Error);

/// <summary>Orden pendiente para el agente.</summary>
/// <param name="Kind">"read" o "read-erase".</param>
public sealed record PatrolAgentCommand(long Id, long Serial, string Kind);

/// <param name="Reminders">Rondas de las rutas de esta base que se acercan o ya debieron salir (fase 2).</param>
public sealed record PatrolAgentHeartbeatResponse(
    DateTime ServerLocal, DateTime ServerUtc, IReadOnlyList<PatrolAgentCommand> Commands,
    IReadOnlyList<PatrolAgentReminder>? Reminders = null);

/// <summary>Lo que leyó la base de un bastón (bloques crudos en hex).</summary>
public sealed record PatrolAgentUpload(
    long Serial, IReadOnlyList<string> BlocksHex, int DeclaredBlocks, DateTime? ClockSetAt, long? CommandId);

/// <summary>Resultado del borrado (o de su omisión) tras subir una descarga.</summary>
public sealed record PatrolAgentEraseReport(long DownloadId, bool Erased, string? Error);

/// <summary>El bastón entró o salió de la base, o falló algo con él.</summary>
/// <param name="Event">"docked", "undocked" o "error".</param>
public sealed record PatrolAgentReaderEvent(long Serial, string Event, string? Message);

public sealed record PatrolAgentCommandResult(long CommandId, bool Success, string? Message);
