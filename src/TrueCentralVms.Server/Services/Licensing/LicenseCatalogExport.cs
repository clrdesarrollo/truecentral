using System.Reflection;
using System.Text;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Server.Services.Licensing;

/// <summary>
/// <c>TrueCentralVms.Server.exe --export-license-catalog [archivo]</c>: valida
/// <see cref="LicenseCatalog"/> y escribe el JSON que importa el servidor de
/// licencias (backoffice → Productos → Importar catálogo, o
/// <c>manage.py sync_catalog &lt;archivo&gt;</c>). Sin archivo lo escribe en la
/// salida estándar. Termina con código 1 si el catálogo no cuadra con
/// <see cref="LicenseFeatures"/> o con la prueba incorporada, para que el build
/// falle antes de publicar un contrato roto. No levanta el servidor.
/// </summary>
public static class LicenseCatalogExport
{
    public const string Argument = "--export-license-catalog";

    public static int Run(string? path)
    {
        // Sin archivo el JSON sale por stdout: en UTF-8, no en la página OEM de la consola
        Console.OutputEncoding = new UTF8Encoding(false);
        var errors = LicenseCatalog.Validate().ToList();
        foreach (var (key, value) in LicensingConstants.TrialFeatures)
        {
            var feature = LicenseCatalog.Features.FirstOrDefault(f => f.Key == key);
            if (feature is null)
                errors.Add($"La prueba incorporada usa '{key}', que no está en el catálogo.");
            else if (!LicenseCatalog.IsValidValue(feature.Type, value))
                errors.Add($"La prueba incorporada da a '{key}' un valor de otro tipo.");
        }
        if (errors.Count > 0)
        {
            foreach (var error in errors)
                Console.Error.WriteLine($"ERROR catálogo de licencias: {error}");
            return 1;
        }

        string version = typeof(LicenseCatalogExport).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        string json = LicenseCatalog.ToJson($"TrueCentralVms.Server {version}");
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Out.Write(json);
            return 0;
        }

        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, json, new UTF8Encoding(false));
        Console.WriteLine(
            $"Catálogo de licencias v{LicenseCatalog.Version}: {LicenseCatalog.Features.Count} características, " +
            $"{LicenseCatalog.Packages.Count} packages → {full}");
        return 0;
    }
}
