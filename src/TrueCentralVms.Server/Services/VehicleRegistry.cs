using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Padrón de vehículos en memoria: a cada lectura de patente le dice de quién
/// es el vehículo y si está autorizado, sin ir a la base por cada auto que
/// pasa. La copia se recarga cuando la API avisa un cambio (vehículo o persona)
/// y, por las dudas, cada minuto: así una vigencia que vence a medianoche se
/// nota sola.
///
/// Autorizado = vehículo habilitado y vigente, y su persona habilitada y
/// vigente (decisión del usuario: sin nivel ni horario personal; el horario lo
/// pone la automatización).
/// </summary>
public sealed class VehicleRegistry(IServiceScopeFactory scopeFactory, ILogger<VehicleRegistry> logger)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    /// <summary>Lo que se sabe de una patente al momento de leerla.</summary>
    public sealed record Match(
        string Status,
        string? Reason,
        int? VehicleId = null,
        string? VehicleLabel = null,
        int? PersonId = null,
        string? PersonName = null,
        string? EmployeeNo = null,
        string? Department = null);

    private sealed record Entry(
        int VehicleId, string Plate, string? Label, bool Enabled, DateTime? ValidFrom, DateTime? ValidTo,
        int PersonId, string PersonName, string EmployeeNo, string? Department,
        bool PersonEnabled, DateTime PersonValidFrom, DateTime PersonValidTo);

    private Dictionary<string, Entry> _byPlate = new(StringComparer.Ordinal);
    private DateTime _loadedAt = DateTime.MinValue;
    private volatile bool _stale = true;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    /// <summary>Solo letras y dígitos, en mayúsculas: «ab-cd·12» → «ABCD12».</summary>
    public static string Normalize(string? plate) =>
        new((plate ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Avisa que el padrón cambió (alta, edición o baja de vehículo o persona).</summary>
    public void Invalidate() => _stale = true;

    /// <summary>Busca la patente leída en el padrón y evalúa si está autorizada ahora.</summary>
    public async Task<Match> ResolveAsync(string? plate, CancellationToken ct)
    {
        string key = Normalize(plate);
        if (key.Length == 0) return new Match(PlateRegistryStatuses.Unregistered, "La lectura no trae patente.");
        try { await EnsureLoadedAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Con la base caída se sigue con la última copia (o vacía): nunca se
            // inventa una autorización.
            logger.LogWarning(ex, "No se pudo recargar el padrón de vehículos.");
        }
        return _byPlate.TryGetValue(key, out var entry)
            ? Evaluate(entry, DateTime.UtcNow)
            : new Match(PlateRegistryStatuses.Unregistered, "La patente no está en el padrón.");
    }

    /// <summary>Estado de un vehículo ya cargado (lo usa la API para mostrarlo en la ficha).</summary>
    public static (string Status, string? Reason) StatusOf(AccessVehicle vehicle, AccessPerson person, DateTime nowUtc)
    {
        var match = Evaluate(ToEntry(vehicle, person), nowUtc);
        return (match.Status, match.Reason);
    }

    private static Match Evaluate(Entry e, DateTime nowUtc)
    {
        string? reason =
            !e.Enabled ? "El vehículo está deshabilitado."
            : e.ValidFrom is { } from && nowUtc < from ? $"El vehículo rige desde el {from.ToLocalTime():dd-MM-yyyy}."
            : e.ValidTo is { } to && nowUtc > to ? $"El vehículo venció el {to.ToLocalTime():dd-MM-yyyy}."
            : !e.PersonEnabled ? $"{e.PersonName} está desactivada."
            : nowUtc < e.PersonValidFrom ? $"{e.PersonName} rige desde el {e.PersonValidFrom.ToLocalTime():dd-MM-yyyy}."
            : nowUtc > e.PersonValidTo ? $"La vigencia de {e.PersonName} venció el {e.PersonValidTo.ToLocalTime():dd-MM-yyyy}."
            : null;
        return new Match(reason is null ? PlateRegistryStatuses.Authorized : PlateRegistryStatuses.Unauthorized, reason,
            e.VehicleId, e.Label, e.PersonId, e.PersonName, e.EmployeeNo, e.Department);
    }

    private static Entry ToEntry(AccessVehicle v, AccessPerson p) => new(
        v.Id, v.Plate, v.Label, v.Enabled, v.ValidFrom, v.ValidTo,
        p.Id, $"{p.FirstName} {p.LastName}".Trim(), p.EmployeeNo, p.Department, p.Enabled, p.ValidFrom, p.ValidTo);

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (!_stale && DateTime.UtcNow - _loadedAt < MaxAge) return;
        await _loadLock.WaitAsync(ct);
        try
        {
            if (!_stale && DateTime.UtcNow - _loadedAt < MaxAge) return;
            _stale = false;
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
            var rows = await db.AccessVehicles.AsNoTracking().Include(v => v.AccessPerson).ToListAsync(ct);
            _byPlate = rows.Where(v => v.AccessPerson is not null)
                .ToDictionary(v => v.Plate, v => ToEntry(v, v.AccessPerson!), StringComparer.Ordinal);
            _loadedAt = DateTime.UtcNow;
        }
        catch
        {
            _stale = true;
            throw;
        }
        finally
        {
            _loadLock.Release();
        }
    }
}
