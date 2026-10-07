using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.Services;

/// <summary>
/// Qué puede OPERAR esta sesión (armar, abrir una puerta, mover un PTZ,
/// hablar…), no solo ver. Lo entrega el servidor (/api/auth/operable) con las
/// mismas reglas con que valida cada orden; los módulos lo consultan para
/// deshabilitar lo que el servidor rechazaría. Mientras no se haya leído no se
/// bloquea nada (el servidor valida igual). Hay una sola sesión por proceso:
/// la instancia es compartida.
/// </summary>
public sealed class OperableScope
{
    private sealed record Sets(
        bool All, string Signature,
        HashSet<int> Locations, HashSet<int> Channels, HashSet<string> Areas, HashSet<string> Zones,
        HashSet<int> WholePanels, HashSet<int> Doors, HashSet<int> Fences, HashSet<int> Speakers, HashSet<int> Intercoms);

    // Antes que Current: los estáticos se inicializan en orden de declaración y
    // la instancia compartida parte de este valor.
    private static readonly Sets Everything = new(true, "all", [], [], [], [], [], [], [], [], []);

    public static OperableScope Current { get; } = new();

    /// <summary>Explicación para lo que queda deshabilitado por alcance.</summary>
    public const string DeniedHint = "Fuera de su alcance: puede verlo, pero no operarlo.";

    private volatile Sets _sets = Everything;

    /// <summary>Cambió lo que se puede operar. Se avisa en el hilo que llamó a <see cref="Update"/> (el de la interfaz).</summary>
    public event Action? Changed;

    /// <summary>Opera todo lo que ve (administrador o sin restricción por ubicación).</summary>
    public bool All => _sets.All;

    public bool CanOperateLocation(int id) => _sets.All || _sets.Locations.Contains(id);
    public bool CanOperateChannel(int id) => _sets.All || _sets.Channels.Contains(id);
    public bool CanOperateArea(int panelId, int number) => _sets.All || _sets.Areas.Contains($"{panelId}/{number}");
    public bool CanOperateZone(int panelId, int number) => _sets.All || _sets.Zones.Contains($"{panelId}/{number}");
    /// <summary>Órdenes de "todo el panel": exigen poder operar todas sus áreas.</summary>
    public bool CanOperateWholePanel(int panelId) => _sets.All || _sets.WholePanels.Contains(panelId);
    public bool CanOperateDoor(int id) => _sets.All || _sets.Doors.Contains(id);
    public bool CanOperateFence(int id) => _sets.All || _sets.Fences.Contains(id);
    public bool CanOperateSpeaker(int id) => _sets.All || _sets.Speakers.Contains(id);
    public bool CanOperateIntercom(int id) => _sets.All || _sets.Intercoms.Contains(id);

    /// <summary>Aplica lo que entregó el servidor; avisa solo si algo cambió.</summary>
    public void Update(OperableDto dto)
    {
        Sets next = dto.All ? Everything : new Sets(false,
            string.Join("|", new[]
            {
                string.Join(',', dto.Locations), string.Join(',', dto.Channels), string.Join(',', dto.Areas),
                string.Join(',', dto.Zones), string.Join(',', dto.WholePanels), string.Join(',', dto.Doors),
                string.Join(',', dto.Fences), string.Join(',', dto.Speakers), string.Join(',', dto.Intercoms),
            }),
            [.. dto.Locations], [.. dto.Channels], [.. dto.Areas], [.. dto.Zones], [.. dto.WholePanels],
            [.. dto.Doors], [.. dto.Fences], [.. dto.Speakers], [.. dto.Intercoms]);
        if (next.Signature == _sets.Signature) return;
        _sets = next;
        Changed?.Invoke();
    }
}
