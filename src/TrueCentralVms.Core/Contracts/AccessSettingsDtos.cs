namespace TrueCentralVms.Core.Contracts;

// Configuración PROPIA de un equipo de control de acceso: los parámetros que
// viven en el equipo y que hasta ahora había que ir a tocar a su página web
// o a HikCentral (contacto de puerta, tiempo de apertura, umbrales del
// reconocimiento facial, polaridad de los LED del lector…). El VMS no los
// guarda: los lee del equipo cada vez y se los escribe de vuelta.
//
// Cada marca expone cosas distintas y cada firmware un subconjunto, así que
// la página se arma con lo que el equipo CONTESTÓ, no con una lista fija.

/// <summary>Cómo se edita un parámetro en la página del equipo.</summary>
public enum AccessSettingType
{
    /// <summary>Casilla (true/false).</summary>
    Boolean,
    /// <summary>Número entero, con rango y unidad si el equipo los declara.</summary>
    Integer,
    /// <summary>Texto corto.</summary>
    Text,
    /// <summary>Clave: el equipo no la devuelve; vacío = no cambiar.</summary>
    Password,
    /// <summary>Una opción de una lista cerrada.</summary>
    Choice,
    /// <summary>Solo lectura: lo informa el equipo y no se edita desde acá.</summary>
    Info,
}

/// <summary>Una opción de un parámetro de lista: el valor del fabricante y cómo se muestra.</summary>
public sealed record AccessSettingOption(string Value, string Label);

/// <summary>Un parámetro del equipo, con su valor actual y lo que hace falta para editarlo.</summary>
public sealed record AccessSettingDto(
    /// <summary>Clave del fabricante ("openDuration"): es la que vuelve al guardar.</summary>
    string Key,
    string Label,
    AccessSettingType Type,
    /// <summary>Valor actual como texto ("true"/"false", "5", "alwaysClose"); null en las claves.</summary>
    string? Value,
    int? Min = null,
    int? Max = null,
    /// <summary>Unidad para mostrar junto al número ("s", "%").</summary>
    string? Unit = null,
    IReadOnlyList<AccessSettingOption>? Options = null,
    /// <summary>Qué hace el parámetro, en una línea (va bajo el campo).</summary>
    string? Help = null);

/// <summary>Un bloque de la configuración: una puerta o un lector, con sus parámetros.</summary>
public sealed record AccessSettingsSectionDto(
    /// <summary>Identifica el bloque al guardar ("door:1", "reader:2").</summary>
    string Key,
    /// <summary>"door" o "reader".</summary>
    string Kind,
    /// <summary>Número de la puerta o del lector EN EL EQUIPO.</summary>
    int Number,
    string Title,
    /// <summary>Lo que el equipo dice de ese bloque (nombre de la puerta, modelo y funciones del lector).</summary>
    string? Subtitle,
    /// <summary>Parámetros conocidos, editables.</summary>
    IReadOnlyList<AccessSettingDto> Settings,
    /// <summary>
    /// Todo lo demás que el equipo informó en ese bloque y el VMS no sabe
    /// editar todavía, tal cual (clave y valor), para no esconder nada.
    /// </summary>
    IReadOnlyList<AccessSettingDto> Others);

/// <summary>La configuración completa de un equipo, leída del propio equipo.</summary>
public sealed record AccessDeviceSettingsDto(
    IReadOnlyList<AccessSettingsSectionDto> Sections,
    /// <summary>Avisos de la lectura (un bloque que el equipo no contestó, por ejemplo).</summary>
    IReadOnlyList<string> Notes);

/// <summary>Lo que el administrador cambió en un bloque: solo las claves que tocó.</summary>
public sealed record AccessSettingsWriteDto(Dictionary<string, string?> Values);
