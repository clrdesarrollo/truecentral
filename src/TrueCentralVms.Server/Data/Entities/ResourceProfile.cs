using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Ficha de un recurso: lo que el VMS sabe de él más allá de lo que informa su
/// equipo (descripción, consignas para el operador y cámaras asociadas). Se
/// crea recién cuando alguien escribe algo en la ficha.
///
/// Exactamente UNA de las referencias apunta al recurso (arco exclusivo con
/// restricción CHECK en la base) y todas borran en cascada: la ficha se va
/// con su recurso, sea porque se eliminó el equipo o porque una revalidación
/// ya no trae esa puerta o ese canal.
/// </summary>
public class ResourceProfile
{
    public int Id { get; set; }

    public int? ChannelId { get; set; }
    public int? AccessDoorId { get; set; }
    public int? AlarmAreaId { get; set; }
    public int? AlarmZoneId { get; set; }
    public int? CercoPanelId { get; set; }
    public int? SpeakerId { get; set; }
    public int? IntercomId { get; set; }

    /// <summary>Qué es y dónde está exactamente ("Puerta de vidrio doble, lado estacionamiento").</summary>
    public string? Description { get; set; }

    /// <summary>Consignas para el operador: qué hacer cuando este recurso avisa algo.</summary>
    public string? Instructions { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Cámaras asociadas, en orden (la de Position 0 es la principal).</summary>
    public List<ResourceProfileCamera> Cameras { get; set; } = [];

    /// <summary>Ficha nueva apuntando al recurso dado.</summary>
    public static ResourceProfile For(ResourceKind kind, int id) => kind switch
    {
        ResourceKind.Camera => new() { ChannelId = id },
        ResourceKind.Door => new() { AccessDoorId = id },
        ResourceKind.Partition => new() { AlarmAreaId = id },
        ResourceKind.Zone => new() { AlarmZoneId = id },
        ResourceKind.Fence => new() { CercoPanelId = id },
        ResourceKind.Speaker => new() { SpeakerId = id },
        ResourceKind.Intercom => new() { IntercomId = id },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>La ficha del recurso dado (consulta; a lo más una fila).</summary>
    public static IQueryable<ResourceProfile> Of(IQueryable<ResourceProfile> profiles, ResourceKind kind, int id) => kind switch
    {
        ResourceKind.Camera => profiles.Where(p => p.ChannelId == id),
        ResourceKind.Door => profiles.Where(p => p.AccessDoorId == id),
        ResourceKind.Partition => profiles.Where(p => p.AlarmAreaId == id),
        ResourceKind.Zone => profiles.Where(p => p.AlarmZoneId == id),
        ResourceKind.Fence => profiles.Where(p => p.CercoPanelId == id),
        ResourceKind.Speaker => profiles.Where(p => p.SpeakerId == id),
        ResourceKind.Intercom => profiles.Where(p => p.IntercomId == id),
        _ => profiles.Where(_ => false),
    };
}

/// <summary>Cámara asociada a la ficha de un recurso.</summary>
public class ResourceProfileCamera
{
    public int Id { get; set; }
    public int ResourceProfileId { get; set; }
    public ResourceProfile Profile { get; set; } = null!;
    public int ChannelId { get; set; }
    public Channel Channel { get; set; } = null!;
    /// <summary>Orden en la ficha: 0 = la principal.</summary>
    public int Position { get; set; }
}
