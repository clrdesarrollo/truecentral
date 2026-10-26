namespace TrueCentralVms.Core.Contracts;

/// <summary>
/// Progreso de una descarga en la base, del servicio (truecentral-patrol, que
/// corre sin escritorio) al aviso en pantalla de la sesión del guardia
/// (truecentral-patrol-aviso). Viaja como una línea JSON por el named pipe
/// <see cref="PipeName"/>.
/// </summary>
/// <param name="Stage">Etapa: "detected", "clock", "reading", "uploading", "erasing", "done", "queued", "error", "removed".</param>
/// <param name="Block">Bloque leído (etapa "reading").</param>
/// <param name="Blocks">Bloques que anunció el bastón.</param>
/// <param name="Message">Texto para mostrar (resultado del servidor o error).</param>
public sealed record PatrolDockProgress(
    string Port, long? Serial, string Stage, int Block, int Blocks, string? Message, DateTime At)
{
    public const string PipeName = "TrueCentralRondas.Progreso";
}
