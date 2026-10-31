namespace TrueCentralVms.Core.Contracts;

// ---------------------------------------------------------------------------
// Tableros del Centro de mando (antes "vistas guardadas"): la división de la
// grilla + qué hay en cada cuadro (una cámara, un recurso, una tarjeta de
// reporte o una página web), para volver a dejar el puesto como estaba con un
// clic. Los hay personales y compartidos (con todos o con ciertos roles); los
// compartidos los administra quien tiene "Administrar tableros compartidos".
// La API sigue en /api/live-views por compatibilidad.
// ---------------------------------------------------------------------------

/// <summary>Qué hay en un cuadro del tablero (<see cref="LiveViewItemDto.Kind"/>).</summary>
public static class DashboardItemKinds
{
    /// <summary>Una cámara (<see cref="LiveViewItemDto.ChannelId"/>).</summary>
    public const string Camera = "camera";
    /// <summary>Un recurso que no es cámara: tipo en Ref (<see cref="ResourceKind"/>), id en RefId.</summary>
    public const string Resource = "resource";
    /// <summary>Una tarjeta de reporte: su clave en Ref (<see cref="Cards"/>).</summary>
    public const string Card = "card";
    /// <summary>Una página web: de la lista del servidor (RefId) o una dirección libre (Ref).</summary>
    public const string Web = "web";
    /// <summary>Un grupo de parlantes (RefId): hablarle a todos de una vez.</summary>
    public const string SpeakerGroup = "speaker-group";

    /// <summary>Tarjetas de reporte que puede llevar un cuadro.</summary>
    public static readonly IReadOnlyList<string> Cards =
        ["card-equipment", "card-server", "card-alerts", "card-plates", "card-access", "card-watch"];

    public static bool IsCamera(string? kind) => string.IsNullOrEmpty(kind) || kind == Camera;

    /// <summary>La clave de la tarjeta sin sus opciones: "card-plates?devices=3,7" → "card-plates".</summary>
    public static string CardKey(string? reference)
    {
        string text = reference ?? "";
        int q = text.IndexOf('?');
        return q < 0 ? text : text[..q];
    }

    /// <summary>Es una tarjeta conocida (con o sin opciones).</summary>
    public static bool IsKnownCard(string? reference) => Cards.Contains(CardKey(reference));

    /// <summary>Zoom de una página entre 25 % y 500 %; 100 % (o nada) = null.</summary>
    public static int? CleanZoom(int? zoom) => zoom is { } z && z != 100 ? Math.Clamp(z, 25, 500) : null;
}

/// <summary>Un cuadro del tablero.</summary>
/// <param name="CellIndex">Posición en la grilla (0 = primer cuadro).</param>
/// <param name="ChannelId">Canal de la cámara (0 si el cuadro no es una cámara).</param>
/// <param name="StreamType">0 = principal, 1 = secundario (<c>StreamProfile</c>).</param>
/// <param name="Kind">Qué hay en el cuadro (<see cref="DashboardItemKinds"/>); null = cámara
/// (así lo leen los puestos de versiones anteriores).</param>
/// <param name="RefId">Id del recurso o de la página web de la lista.</param>
/// <param name="Ref">Tipo del recurso, clave de la tarjeta o dirección web libre.</param>
/// <param name="Title">Rótulo del cuadro (el de la página web, por ejemplo).</param>
/// <param name="Zoom">Zoom de una página web en porcentaje (null = 100 %).</param>
public sealed record LiveViewItemDto(int CellIndex, int ChannelId, int StreamType,
    string? Kind = null, int? RefId = null, string? Ref = null, string? Title = null, int? Zoom = null);

/// <summary>Tablero tal como lo ve el puesto que lo pide.</summary>
/// <param name="LayoutName">Clave de la división estándar ("16", "13"), uniforme
/// ("8×5") o a medida ("m…" con cuadros unidos); <paramref name="Columns"/> y
/// <paramref name="Rows"/> permiten rearmarla aunque no sea una del selector.</param>
/// <param name="Shared">Compartido (con todos o con <paramref name="RoleIds"/>), no solo de su dueño.</param>
/// <param name="Owner">Usuario que lo creó (vacío si se perdió la cuenta).</param>
/// <param name="CanEdit">El usuario que consulta puede modificarlo o borrarlo.</param>
/// <param name="RoleIds">Compartido solo con estos roles (vacío = con todos).</param>
/// <param name="Roles">Nombres de esos roles, para mostrarlos.</param>
/// <param name="Mine">Es un tablero personal de quien consulta.</param>
/// <param name="RestrictToRoles">Compartido solo con <paramref name="RoleIds"/>; sin
/// roles (se borraron) lo ve solo quien administra los tableros.</param>
public sealed record LiveViewDto(
    int Id, string Name, string LayoutName, int Columns, int Rows,
    bool Shared, string Owner, bool CanEdit, DateTime UpdatedAt,
    IReadOnlyList<LiveViewItemDto> Items,
    IReadOnlyList<int>? RoleIds = null, IReadOnlyList<string>? Roles = null, bool Mine = false,
    bool RestrictToRoles = false);

/// <summary>Alta o actualización de un tablero.</summary>
/// <param name="RoleIds">Solo para compartidos: con qué roles (vacío o null = con todos).</param>
public sealed record LiveViewSaveRequest(
    string Name, string LayoutName, int Columns, int Rows,
    bool Shared, List<LiveViewItemDto> Items, List<int>? RoleIds = null);

/// <summary>Un rol al que se le puede asignar un tablero compartido.</summary>
public sealed record DashboardRoleDto(int Id, string Name);

/// <summary>Página web de la lista del servidor: lo que los cuadros pueden mostrar sin permiso aparte.</summary>
/// <param name="Personal">Es una página de «Mis páginas» de quien consulta (la guardó al
/// escribirla en un cuadro): solo la ve él, no las demás cuentas.</param>
public sealed record WebLinkDto(int Id, string Name, string Url, string? Description, bool Personal = false);

/// <summary>Alta o modificación de una página web de la lista.</summary>
/// <param name="Personal">Alta en «Mis páginas» de quien la guarda (pide "Abrir cualquier
/// página web"); sin esto, va a la lista compartida (pide administrar los tableros).</param>
public sealed record WebLinkSaveRequest(string Name, string Url, string? Description = null, bool Personal = false);
