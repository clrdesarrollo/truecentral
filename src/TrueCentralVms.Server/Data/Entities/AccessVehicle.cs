using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Data.Entities;

/// <summary>
/// Vehículo del padrón del control de acceso: la patente es una credencial más
/// de la persona (una persona puede tener varios). Lo usan las automatizaciones
/// de patente para decidir si se abre el acceso vehicular.
///
/// No confundir con <see cref="Vehicle"/>, que es un vehículo del módulo Flota
/// (con su grabador móvil). Los vehículos de la empresa sin conductor fijo se
/// cargan a una persona creada para eso (decisión del usuario).
/// </summary>
public class AccessVehicle
{
    public int Id { get; set; }

    public int AccessPersonId { get; set; }
    public AccessPerson? AccessPerson { get; set; }

    /// <summary>Patente normalizada: solo letras y dígitos, en mayúsculas («ABCD12»). Única.</summary>
    public string Plate { get; set; } = "";
    /// <summary>Nombre corto para reconocerlo en los registros («Camioneta 3»).</summary>
    public string? Label { get; set; }
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? Color { get; set; }
    public string? Notes { get; set; }

    public bool Enabled { get; set; } = true;
    /// <summary>Vigencia propia del vehículo (null = sin límite); la de la persona también se exige.</summary>
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Un ingreso o salida de vehículo, anotado por la acción «Registrar paso
/// vehicular» de una automatización. Es un registro histórico: copia los
/// nombres (persona, vehículo, cámara) como estaban en ese momento, y el
/// recorte de la patente vive aparte de la lectura ANPR, que se purga antes.
/// No se edita desde la API; solo lo borra la retención.
/// </summary>
public class VehicleAccessRecord
{
    public long Id { get; set; }

    /// <summary>Hora UTC del paso.</summary>
    public DateTime At { get; set; } = DateTime.UtcNow;
    /// <summary>Nombre del acceso («Acceso principal»).</summary>
    public string AccessPoint { get; set; } = "";
    public VehicleAccessDirection Direction { get; set; }
    public VehicleAccessResult Result { get; set; }

    public string Plate { get; set; } = "";
    public int? Confidence { get; set; }
    /// <summary>Estado en el padrón al momento del paso (<see cref="PlateRegistryStatuses"/>).</summary>
    public string PlateStatus { get; set; } = PlateRegistryStatuses.Unregistered;

    public int? DeviceId { get; set; }
    public int? ChannelId { get; set; }
    public string CameraName { get; set; } = "";

    public int? AccessVehicleId { get; set; }
    public string? VehicleLabel { get; set; }
    public int? AccessPersonId { get; set; }
    public string? PersonName { get; set; }
    public string? EmployeeNo { get; set; }
    public string? Department { get; set; }

    public int? WorkflowId { get; set; }
    public string? WorkflowName { get; set; }
    /// <summary>Lectura ANPR de origen (puede haberse purgado).</summary>
    public long? PlateEventId { get; set; }
    /// <summary>Copia del recorte de la patente, relativa al almacén ANPR (carpeta «vehicular»).</summary>
    public string? PlateImagePath { get; set; }
    public string? Detail { get; set; }
}
