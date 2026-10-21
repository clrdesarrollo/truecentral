namespace TrueCentralVms.Server.Api;

/// <summary>
/// Regla común a los formularios de equipos: con la contraseña vacía se usa la guardada ("vacío = no cambiar"), pero SOLO
/// contra la misma dirección. Si no, quien puede editar o probar un equipo sin conocer su clave cambiaría el host a una IP
/// suya y el servidor le entregaría el login guardado (en claro si el equipo usa Basic, o un Digest que se ataca sin conexión).
/// </summary>
public static class StoredPassword
{
    public const string RetypeMessage =
        "Cambió la dirección del equipo: escriba de nuevo la contraseña. La guardada no se envía a una dirección distinta.";

    /// <summary>¿Se puede usar la contraseña guardada para <paramref name="storedHost"/> contra <paramref name="requestedHost"/>?</summary>
    public static bool CanReuse(string? storedHost, string? requestedHost) =>
        !string.IsNullOrWhiteSpace(storedHost)
        && string.Equals(storedHost.Trim(), requestedHost?.Trim(), StringComparison.OrdinalIgnoreCase);
}
