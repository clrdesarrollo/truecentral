using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrueCentralVms.Migrator.Migration;

/// <summary>Huella recuperada de un terminal, lista para TrueCentral.</summary>
public sealed class PackageFingerprint
{
    /// <summary>Dedo 1..10 (numeración de los equipos).</summary>
    public int Finger { get; set; }
    /// <summary>Plantilla en base64 tal como la entrega el terminal.</summary>
    public string Template { get; set; } = "";
    /// <summary>De qué terminal salió.</summary>
    public string Source { get; set; } = "";
}

/// <summary>Una persona del paquete: lo que dijo HikCentral más lo que aportaron los terminales.</summary>
public sealed class PackagePerson
{
    public string HcpPersonId { get; set; } = "";
    public string PersonCode { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Department { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Notes { get; set; }
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }
    /// <summary>Archivo de la foto, relativo a la carpeta del paquete (null si no tiene).</summary>
    public string? PhotoFile { get; set; }
    public string? PhotoContentType { get; set; }
    public List<string> Cards { get; set; } = [];
    /// <summary>Cuántas huellas dice HikCentral que tiene (aunque no entregue la plantilla).</summary>
    public int HcpFingerprintCount { get; set; }
    public List<PackageFingerprint> Fingerprints { get; set; } = [];
    /// <summary>Legajo con el que la conocen los terminales (lo que HikCentral les bajó).</summary>
    public string? TerminalEmployeeNo { get; set; }
    /// <summary>Terminales donde se la encontró.</summary>
    public List<string> FoundOnTerminals { get; set; } = [];

    [JsonIgnore] public string FullName => $"{FirstName} {LastName}".Trim();
}

public sealed class MigrationPackage
{
    public const string FileName = "paquete.json";
    public const string PhotosFolder = "fotos";

    public string Source { get; set; } = "";
    public string SourceVersion { get; set; } = "";
    public DateTime ExportedAt { get; set; }
    public string ExportedBy { get; set; } = "";
    public List<PackagePerson> Persons { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task SaveAsync(string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, FileName);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, Json, ct);
    }

    public static async Task<MigrationPackage> LoadAsync(string folder, CancellationToken ct)
    {
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"En esa carpeta no hay un {FileName} del migrador.", path);
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<MigrationPackage>(stream, Json, ct)
               ?? throw new InvalidDataException("El paquete está vacío.");
    }
}
