using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Compara huellas del lector USB de Hikvision (DS-K1F820-F / DS-K1F800-F) con
/// el mismo SDK que usa el Complemento (FPModule_SDK.dll, x64). El motor de
/// comparación viene dentro del DLL y no necesita el lector conectado, así que
/// el servidor puede impedir que se enrole dos veces la misma huella (el índice
/// capturado como índice y de nuevo como pulgar) o la de otra persona ANTES de
/// que el terminal la rechace al escribirla (FingerPrint/SetUp responde
/// cardReaderRecvStatus 5, «la huella ya existe»).
///
/// Solo entiende las plantillas de ese lector (512 bytes, formato "hikvision");
/// lo que no puede comparar lo deja pasar, y lo valida el equipo al escribir.
/// </summary>
public sealed class FingerprintMatcher
{
    private const string Dll = "FPModule_SDK.dll";
    private const int TemplateSize = 512;
    private const int FpSuccess = 0;
    private const int FpMatchFail = 6;

    /// <summary>0 = son el mismo dedo, 6 = no coinciden, 4 = parámetro inválido (plantilla que no es de este lector).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    private static extern int FPModule_MatchTemplate(byte[] template1, byte[] template2, int securityLevel);

    public enum Outcome { Same, Different, NotComparable }

    /// <summary>Una huella nueva que ya estaba enrolada.</summary>
    /// <param name="Number">Dedo con el que se capturó la nueva.</param>
    /// <param name="OtherNumber">Dedo con el que ya estaba.</param>
    /// <param name="OtherPersonId">De quién es; null = de la misma persona.</param>
    public sealed record Clash(int Number, int OtherNumber, int? OtherPersonId, string? OtherPersonName, string? OtherEmployeeNo);

    private readonly Lock _sync = new();
    private readonly ILogger<FingerprintMatcher> _logger;
    private readonly int _level;
    private bool _missing;

    public FingerprintMatcher(IConfiguration config, ILogger<FingerprintMatcher> logger)
    {
        _logger = logger;
        // Nivel de seguridad del SDK (1 a 5): más alto exige más parecido para
        // dar dos plantillas por el mismo dedo.
        _level = Math.Clamp(config.GetValue("Access:FingerprintMatchLevel", 3), 1, 5);
    }

    /// <summary>Compara dos plantillas del lector USB.</summary>
    public Outcome Compare(byte[] a, byte[] b)
    {
        if (a.Length != TemplateSize || b.Length != TemplateSize) return Outcome.NotComparable;
        // El SDK es de un solo hilo (lo escribieron para un único lector).
        lock (_sync)
        {
            if (_missing) return Outcome.NotComparable;
            try
            {
                return FPModule_MatchTemplate(a, b, _level) switch
                {
                    FpSuccess => Outcome.Same,
                    FpMatchFail => Outcome.Different,
                    _ => Outcome.NotComparable,
                };
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _missing = true;
                _logger.LogWarning("No se pueden comparar huellas: falta {Dll} junto al servidor ({Error}). " +
                                   "Las huellas repetidas las rechaza el terminal al escribirlas.", Dll, ex.Message);
                return Outcome.NotComparable;
            }
        }
    }

    /// <summary>
    /// La primera huella nueva de <paramref name="fingers"/> (las que traen
    /// plantilla) que ya esté enrolada en OTRO dedo: otra captura nueva de la
    /// misma lista, una guardada que la persona conserva, o cualquiera de otra
    /// persona. Recapturar el mismo dedo no cuenta. Con
    /// <paramref name="onlyNumber"/> revisa solo ese dedo (la comprobación justo
    /// después de capturarlo). <c>Compared</c> dice si se pudo comparar algo.
    /// </summary>
    public async Task<(Clash? Clash, bool Compared)> FindDuplicateAsync(VmsDbContext db, CredentialProtector protector,
        int? personId, IReadOnlyList<AccessFingerprintWriteDto> fingers, int? onlyNumber, CancellationToken ct)
    {
        var fresh = fingers
            .Where(f => f.Template is not null)
            .Select(f => (f.Number, Bytes: Decode(f.Template)))
            .Where(f => f.Bytes is not null)
            .Select(f => (f.Number, Bytes: f.Bytes!))
            .ToList();
        var candidates = fresh.Where(f => onlyNumber is null || f.Number == onlyNumber).ToList();
        if (candidates.Count == 0) return (null, false);

        bool compared = false;
        bool SameFinger(byte[] a, byte[] b)
        {
            var outcome = Compare(a, b);
            if (outcome != Outcome.NotComparable) compared = true;
            return outcome == Outcome.Same;
        }

        foreach (var candidate in candidates)
            foreach (var other in fresh.Where(o => o.Number != candidate.Number))
                if (SameFinger(candidate.Bytes, other.Bytes))
                    return (new Clash(candidate.Number, other.Number, null, null, null), true);

        // Las guardadas: de la persona, solo las que conserva (vienen sin
        // plantilla en la lista); de los demás, todas las del lector USB.
        var kept = fingers.Where(f => f.Template is null).Select(f => f.Number).ToHashSet();
        var stored = await db.AccessFingerprints.AsNoTracking()
            .Where(f => f.Format == AccessFingerprintFormats.Hikvision)
            .Select(f => new
            {
                f.AccessPersonId, f.Number, f.TemplateCiphertext,
                f.AccessPerson!.FirstName, f.AccessPerson.LastName, f.AccessPerson.EmployeeNo,
            })
            .ToListAsync(ct);
        var own = stored.Where(s => s.AccessPersonId == personId && kept.Contains(s.Number));
        var others = stored.Where(s => s.AccessPersonId != personId);

        foreach (var row in own.Concat(others))
        {
            byte[]? template;
            try { template = Decode(protector.Unprotect(row.TemplateCiphertext)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { continue; }   // fila ilegible: la valida el equipo
            if (template is null) continue;
            bool samePerson = row.AccessPersonId == personId;
            foreach (var candidate in candidates)
            {
                if (samePerson && row.Number == candidate.Number) continue;
                if (SameFinger(candidate.Bytes, template))
                    return (new Clash(candidate.Number, row.Number, samePerson ? null : row.AccessPersonId,
                        samePerson ? null : $"{row.FirstName} {row.LastName}".Trim(), samePerson ? null : row.EmployeeNo), true);
            }
        }
        return (null, compared);
    }

    /// <summary>Lo que se le dice al operador cuando la huella ya estaba.</summary>
    public static string Describe(Clash clash) => clash.OtherPersonId is null
        ? $"La huella capturada como {Lower(AccessFingers.NameOf(clash.Number))} es la misma que ya está como " +
          $"{Lower(AccessFingers.NameOf(clash.OtherNumber))}: se apoyó dos veces el mismo dedo. Capture el dedo que corresponde."
        : $"Esa huella ({Lower(AccessFingers.NameOf(clash.Number))}) ya está registrada para {clash.OtherPersonName} " +
          $"({clash.OtherEmployeeNo}) como {Lower(AccessFingers.NameOf(clash.OtherNumber))}: revise que esté enrolando " +
          "a la persona correcta y con su propio dedo.";

    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    private static byte[]? Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try { return Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }
    }
}
