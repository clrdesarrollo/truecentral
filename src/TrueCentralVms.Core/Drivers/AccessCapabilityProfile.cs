namespace TrueCentralVms.Core.Drivers;

/// <summary>
/// Lo que el equipo DECLARA que sabe hacer, leído de sus rutas de capacidades
/// oficiales al validarlo (en Hikvision: <c>/ISAPI/AccessControl/capabilities</c>
/// y las <c>…/capabilities</c> de cada función). Es lo que se puede esperar del
/// equipo antes de pedirle nada, y lo que el driver mira para elegir cómo
/// hablarle en vez de probar rutas a ciegas.
///
/// Es una ficha para mostrar: grupos de renglones con su estado, en palabras
/// del VMS. El driver guarda aparte su propia lectura tipada para decidir.
/// </summary>
public sealed record AccessCapabilityProfile(
    /// <summary>Cuándo se leyó del equipo (UTC).</summary>
    DateTime ReadAtUtc,
    IReadOnlyList<AccessCapabilityGroup> Groups,
    /// <summary>Avisos sobre la lectura (rutas que el equipo no contestó, por ejemplo).</summary>
    IReadOnlyList<string> Notes);

public sealed record AccessCapabilityGroup(string Title, IReadOnlyList<AccessCapabilityItem> Items);

public sealed record AccessCapabilityItem(
    string Label,
    AccessCapabilityState State,
    /// <summary>Topes, opciones o cómo lo usa el VMS ("hasta 3.000 personas").</summary>
    string? Detail = null,
    /// <summary>Ruta del fabricante de donde sale el dato, para el técnico.</summary>
    string? Source = null);

public enum AccessCapabilityState
{
    /// <summary>El equipo declara que lo soporta.</summary>
    Supported,
    /// <summary>El equipo declara que NO lo soporta.</summary>
    NotSupported,
    /// <summary>El equipo no dice nada (ni sí ni no): el VMS lo prueba al usarlo.</summary>
    NotDeclared,
}
