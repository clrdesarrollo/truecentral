namespace TrueCentralVms.Core.Contracts;

// DTOs del acceso vehicular por patente: el padrón de vehículos de las
// personas del control de acceso y el registro de ingresos y salidas que
// anotan las automatizaciones (cámara ANPR → relés → «Registrar paso vehicular»).

/// <summary>Estado de una patente leída frente al padrón de vehículos.</summary>
public static class PlateRegistryStatuses
{
    /// <summary>En el padrón, con el vehículo y su persona habilitados y vigentes.</summary>
    public const string Authorized = "authorized";
    /// <summary>En el padrón, pero el vehículo o su persona están deshabilitados o fuera de vigencia.</summary>
    public const string Unauthorized = "unauthorized";
    /// <summary>No está en el padrón.</summary>
    public const string Unregistered = "unregistered";

    public static readonly string[] All = [Authorized, Unauthorized, Unregistered];

    public static string Label(string? status) => status switch
    {
        Authorized => "Autorizada",
        Unauthorized => "No autorizada",
        Unregistered => "No registrada",
        _ => "—",
    };
}

/// <summary>Sentido de un paso vehicular.</summary>
public enum VehicleAccessDirection { Entry, Exit }

/// <summary>Qué pasó con el vehículo en el acceso.</summary>
public enum VehicleAccessResult
{
    /// <summary>El sistema abrió solo (portón y barrera).</summary>
    AutoOpened,
    /// <summary>Patente autorizada fuera del horario automático: abre el guardia.</summary>
    OutsideSchedule,
    /// <summary>Patente no registrada o no autorizada: no se abrió.</summary>
    Rejected,
    /// <summary>Se intentó abrir y falló la orden a los relés.</summary>
    OpenFailed,
    /// <summary>Solo se anotó el paso, sin decisión de apertura.</summary>
    Logged,
}

/// <summary>Un vehículo del padrón. Pertenece siempre a una persona del control de acceso.</summary>
public sealed record AccessVehicleDto(
    int Id,
    int AccessPersonId,
    string PersonName,
    string EmployeeNo,
    string? Department,
    bool PersonEnabled,
    /// <summary>Patente normalizada (solo letras y dígitos, en mayúsculas).</summary>
    string Plate,
    /// <summary>Nombre corto para reconocerlo («Camioneta 3»).</summary>
    string? Label,
    string? Brand,
    string? Model,
    string? Color,
    string? Notes,
    bool Enabled,
    DateTime? ValidFrom,
    DateTime? ValidTo,
    /// <summary>Estado efectivo ahora mismo (ver <see cref="PlateRegistryStatuses"/>) y por qué.</summary>
    string Status,
    string? StatusReason,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record AccessVehicleWriteDto(
    int AccessPersonId,
    string Plate,
    string? Label = null,
    string? Brand = null,
    string? Model = null,
    string? Color = null,
    string? Notes = null,
    bool Enabled = true,
    DateTime? ValidFrom = null,
    DateTime? ValidTo = null);

/// <summary>Un ingreso o salida del registro de accesos vehiculares.</summary>
public sealed record VehicleAccessRecordDto(
    long Id,
    /// <summary>Hora UTC del paso (la de recepción de la lectura en el servidor).</summary>
    DateTime At,
    string AccessPoint,
    VehicleAccessDirection Direction,
    VehicleAccessResult Result,
    string Plate,
    int? Confidence,
    string PlateStatus,
    int? DeviceId,
    int? ChannelId,
    string CameraName,
    int? AccessVehicleId,
    string? VehicleLabel,
    int? AccessPersonId,
    string? PersonName,
    string? EmployeeNo,
    string? Department,
    string? WorkflowName,
    string? Detail,
    bool HasPlateImage);

public sealed record VehicleAccessRecordPageDto(int Total, int Page, int PageSize, IReadOnlyList<VehicleAccessRecordDto> Items);
