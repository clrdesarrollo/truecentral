using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace TrueCentralVms.Client.Services;

/// <summary>Lo que el servidor dice de su instalador del cliente (/api/system/client-installer/info).</summary>
public sealed class ClientInstallerInfo
{
    public bool Available { get; set; }
    public string? FileName { get; set; }
    public double? SizeMb { get; set; }
    public string? Version { get; set; }
}

/// <summary>
/// Actualización del cliente desde su servidor: la suite deja el instalador
/// del cliente en el servidor al instalarse o actualizarse, y cada puesto lo
/// compara con su versión. Con una más nueva, el ícono de la barra de título
/// avisa; «Actualizar ahora» lo descarga, lanza el instalador en modo
/// silencioso (Windows pide permiso de administrador) y cierra el cliente; el
/// instalador espera a que se cierre, lo reemplaza y lo vuelve a abrir
/// (parámetro /AUTOUPDATE de client.iss).
/// </summary>
public static class ClientUpdater
{
    /// <summary>Carpeta temporal del instalador descargado (se reemplaza en cada actualización).</summary>
    private static string DownloadFolder => Path.Combine(Path.GetTempPath(), "CLRTrueCentralVMS-actualizacion");

    /// <summary>La versión del servidor es más nueva que la de este cliente (x.y.z).</summary>
    public static bool IsNewer(string? serverVersion, string current) =>
        Version.TryParse(serverVersion, out var server) && Version.TryParse(current, out var mine) && server > mine;

    /// <summary>Lo que publica el servidor si es más nuevo que este cliente; null si no hay nada que instalar.</summary>
    public static async Task<ClientInstallerInfo?> CheckAsync(ApiClient api, CancellationToken ct = default)
    {
        var info = await api.GetClientInstallerInfoAsync(ct);
        return info.Available && IsNewer(info.Version, ApiClient.ClientVersion) ? info : null;
    }

    /// <summary>Descarga el instalador (con su avance) y devuelve dónde quedó.</summary>
    public static async Task<string> DownloadAsync(ApiClient api, ClientInstallerInfo info, IProgress<double>? progress,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(DownloadFolder);
        foreach (var old in Directory.GetFiles(DownloadFolder, "*.exe"))
            try { File.Delete(old); } catch { /* otro en uso: se pisa abajo */ }
        // Nombre sin la dirección ni la huella del servidor que agrega la descarga del
        // panel web: este puesto ya conoce y confía en su servidor, no hay que tocarlo.
        string path = Path.Combine(DownloadFolder, $"CLRTrueCentralVMS-Client-Setup-{info.Version}.exe");
        string partial = path + ".partial";
        await api.DownloadClientInstallerAsync(partial, progress, ct);
        File.Move(partial, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Lanza el instalador en silencio (con su barra de avance) para que
    /// reemplace este cliente y lo vuelva a abrir. Devuelve false si el
    /// operador no dio el permiso de administrador (el cliente sigue abierto).
    /// </summary>
    public static bool Launch(string installer)
    {
        try
        {
            Process.Start(new ProcessStartInfo(installer, "/SILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /AUTOUPDATE")
            {
                UseShellExecute = true, // el instalador pide administrador: Windows muestra el aviso de permiso
            })?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: no aceptó el aviso
        {
            return false;
        }
    }
}
