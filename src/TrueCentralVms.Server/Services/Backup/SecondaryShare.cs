using System.Runtime.InteropServices;

namespace TrueCentralVms.Server.Services.Backup;

/// <summary>
/// Acceso al segundo destino. Una carpeta de red con usuario propio se conecta con WNetAddConnection2 (el servicio corre
/// como cuenta de sistema, que no tiene las credenciales del usuario de Windows que configuró el destino).
/// </summary>
internal static class SecondaryShare
{
    /// <summary>Conecta el recurso \\servidor\recurso de <paramref name="path"/> si hay usuario. Devuelve el error o null.</summary>
    public static string? Connect(string path, string? user, string? password)
    {
        if (string.IsNullOrWhiteSpace(user) || !path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        string[] parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return "La carpeta de red debe tener la forma \\\\servidor\\recurso\\carpeta.";
        var resource = new NetResource { Type = 1 /* RESOURCETYPE_DISK */, RemoteName = $@"\\{parts[0]}\{parts[1]}" };
        int code = WNetAddConnection2(ref resource, password ?? "", user, 0);
        // 1219 = ya hay una conexión a ese servidor (con estas u otras credenciales): se intenta usar igual.
        return code is 0 or 1219 ? null
            : $"No se pudo conectar a {resource.RemoteName} como {user}: {new System.ComponentModel.Win32Exception(code).Message}";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource resource, string password, string username, int flags);
}
