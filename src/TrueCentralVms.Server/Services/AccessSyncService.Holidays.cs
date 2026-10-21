using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;
using TrueCentralVms.Server.Data;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Feriados. Hay UN calendario para toda la instalación y cada horario decide
/// qué hacer en esos días (<see cref="AccessHolidayMode"/>). Lo que llega a los
/// equipos depende de la marca:
/// <list type="bullet">
/// <item>Hikvision cuelga los feriados de cada horario: van dentro del plan
/// (<see cref="AccessWeekPlan.Holidays"/>) y un horario "como día normal"
/// simplemente no los lleva.</item>
/// <item>ZKTeco C3 tiene un calendario por controladora que rige para TODOS
/// sus horarios: si alguno de los que usa esa controladora ignora los
/// feriados, no se le escribe ninguno (nunca dar acceso de más ni quitarlo
/// donde no corresponde) y queda el motivo en el equipo.</item>
/// </list>
/// A los equipos van fechas concretas (la próxima ocurrencia), no reglas: así
/// todas las marcas los entienden igual, y cuando un feriado anual pasa el
/// servidor escribe el del año siguiente.
/// </summary>
public sealed partial class AccessSyncService
{
    /// <summary>
    /// Los feriados como los reciben los equipos: la próxima ocurrencia de cada
    /// uno (la de este año si todavía no terminó, si no la del siguiente). Los
    /// de un solo año que ya pasaron no van.
    /// </summary>
    public static List<AccessHolidayDate> UpcomingHolidays(IEnumerable<AccessHoliday> holidays, DateOnly today)
    {
        var result = new List<AccessHolidayDate>();
        foreach (var h in holidays)
        {
            var (start, end) = (h.StartDate, h.EndDate);
            if (h.Yearly)
            {
                int years = today.Year - start.Year;
                (start, end) = (Shift(start, years), Shift(end, years));
                if (end < today) (start, end) = (Shift(start, 1), Shift(end, 1));
            }
            if (end < today) continue;
            result.Add(new AccessHolidayDate(h.Name, start, end, h.Yearly));
        }
        return result.OrderBy(h => h.Start).ThenBy(h => h.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Mueve una fecha N años (un 29 de febrero cae el 28 en los años que no lo tienen).</summary>
    private static DateOnly Shift(DateOnly date, int years)
    {
        int year = date.Year + years;
        return new DateOnly(year, date.Month, Math.Min(date.Day, DateTime.DaysInMonth(year, date.Month)));
    }

    /// <summary>
    /// Los tramos de un horario tal como van a las ranuras de los equipos: los
    /// de la semana y, según su modo de feriado, los del día 7. "Sin acceso en
    /// feriados" se marca con un tramo de largo cero, para que su ranura sea
    /// distinta de la del mismo horario "como día normal".
    /// </summary>
    public static List<AccessTimeSegment> PlanSegmentsOf(AccessSchedule schedule)
    {
        var segments = schedule.Segments.Where(s => s.Day < AccessTimeSegment.HolidayDay)
            .Select(s => new AccessTimeSegment(s.Day, s.StartMinutes, s.EndMinutes)).ToList();
        switch (schedule.HolidayMode)
        {
            case AccessHolidayMode.Closed:
                segments.Add(new AccessTimeSegment(AccessTimeSegment.HolidayDay, 0, 0));
                break;
            case AccessHolidayMode.Special:
                segments.AddRange(schedule.Segments.Where(s => s.Day == AccessTimeSegment.HolidayDay)
                    .Select(s => new AccessTimeSegment(s.Day, s.StartMinutes, s.EndMinutes)));
                break;
        }
        return segments;
    }

    /// <summary>
    /// Une los tramos de feriado de varios horarios que dan la misma puerta. Si
    /// alguno ignora los feriados, la unión también (ese horario deja pasar en
    /// un feriado como en cualquier día); si no, se juntan sus tramos de
    /// feriado y el marcador de "sin acceso" sobra apenas hay alguno real.
    /// </summary>
    private static List<AccessTimeSegment> MergeHolidays(List<AccessTimeSegment> canonical, bool anyIgnoresHolidays)
    {
        if (anyIgnoresHolidays) return canonical.Where(s => s.Day != AccessTimeSegment.HolidayDay).ToList();
        bool anyReal = canonical.Any(s => s.Day == AccessTimeSegment.HolidayDay && s.EndMinutes > s.StartMinutes);
        return anyReal
            ? canonical.Where(s => s.Day != AccessTimeSegment.HolidayDay || s.EndMinutes > s.StartMinutes).ToList()
            : canonical;
    }

    /// <summary>
    /// El calendario cambió (o un feriado anual pasó): se reescribe a quienes
    /// tienen algún horario que NO ignora los feriados, y los calendarios de
    /// las controladoras que llevan uno propio. No guarda.
    /// </summary>
    public static async Task MarkHolidayDependentsPendingAsync(VmsDbContext db, CancellationToken ct)
    {
        var levelIds = await db.AccessLevels
            .Where(l => l.AccessSchedule != null && l.AccessSchedule.HolidayMode != AccessHolidayMode.Normal)
            .Select(l => l.Id).ToListAsync(ct);
        var personIds = await db.AccessLevelPersons.Where(x => levelIds.Contains(x.AccessLevelId))
            .Select(x => x.AccessPersonId).ToListAsync(ct);
        await MarkPendingAsync(db, personIds, ct);
        await MarkHolidayCalendarsStaleAsync(db, ct);
    }

    /// <summary>Que se vuelvan a calcular los calendarios propios de las controladoras. No guarda.</summary>
    public static async Task MarkHolidayCalendarsStaleAsync(VmsDbContext db, CancellationToken ct)
    {
        foreach (var device in await db.AccessDevices.Where(d => d.AppliedHolidayHash != null).ToListAsync(ct))
            device.AppliedHolidayHash = null;
    }

    private DateOnly _holidayCheckDay;
    private string? _holidayCheckHash;

    /// <summary>
    /// Una vez por día: si la lista de próximas ocurrencias cambió (un feriado
    /// anual pasó y hay que escribir el del año siguiente, o uno de un solo año
    /// ya no corre), se reescribe a quien corresponde. Al arrancar se hace
    /// siempre: el servidor pudo estar apagado justo ese día.
    /// </summary>
    private async Task RefreshHolidaysAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today == _holidayCheckDay) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var upcoming = UpcomingHolidays(await db.AccessHolidays.AsNoTracking().ToListAsync(ct), today);
        string hash = HashOf(JsonSerializer.Serialize(upcoming));
        if (hash != _holidayCheckHash)
        {
            await MarkHolidayDependentsPendingAsync(db, ct);
            await db.SaveChangesAsync(ct);
        }
        _holidayCheckDay = today;
        _holidayCheckHash = hash;
    }

    /// <summary>
    /// Escribe el calendario de feriados de las controladoras que llevan uno
    /// propio (<see cref="IAccessControlDriver.UsesDeviceHolidayCalendar"/>).
    /// Se recalcula en cada vuelta (cambian horarios, niveles, puertas o el
    /// calendario) y solo se escribe si difiere de lo que el equipo ya tiene.
    /// Si alguno de los horarios que usan sus puertas ignora los feriados, el
    /// calendario queda vacío y se anota por qué.
    /// </summary>
    private async Task SyncHolidayCalendarsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        var devices = await db.AccessDevices
            .Where(d => d.Enabled && d.Status == AccessDeviceStatus.Online)
            .ToListAsync(ct);
        if (devices.Count == 0) return;

        var upcoming = UpcomingHolidays(await db.AccessHolidays.AsNoTracking().ToListAsync(ct),
            DateOnly.FromDateTime(DateTime.Now));
        foreach (var device in devices)
        {
            if (!access.SupportsPersonSync(device.DriverKey)) continue;
            var driver = access.DriverOf(device);
            if (!driver.UsesDeviceHolidayCalendar) continue;

            // Horarios de los niveles que dan alguna puerta de esta controladora.
            var schedules = await db.AccessLevelDoors
                .Where(x => x.AccessDoor != null && x.AccessDoor.AccessDeviceId == device.Id)
                .Select(x => x.AccessLevel!.AccessSchedule!)
                .Distinct().ToListAsync(ct);
            // Una controladora a la que el VMS no le da ningún nivel no se toca:
            // sus feriados (los de ZKAccess, por ejemplo) siguen siendo de quien la administra.
            if (schedules.Count == 0) continue;
            var ignoring = schedules.Where(s => s.HolidayMode == AccessHolidayMode.Normal)
                .Select(s => s.Name).OrderBy(n => n).ToList();

            List<AccessHolidayDate> calendar;
            string? note = null;
            if (upcoming.Count == 0) calendar = [];
            else if (ignoring.Count > 0)
            {
                calendar = [];
                note = $"Sin feriados: esta controladora tiene un solo calendario para todos sus horarios y " +
                       $"{(ignoring.Count == 1 ? "el horario" : "los horarios")} «{string.Join("», «", ignoring)}» " +
                       $"{(ignoring.Count == 1 ? "los ignora" : "los ignoran")}. Para aplicarlos, elija «Sin acceso» u " +
                       "«Horario especial» en feriados para todos los horarios que usan sus puertas.";
            }
            else calendar = upcoming;

            string hash = HashOf(JsonSerializer.Serialize(calendar));
            if (hash == device.AppliedHolidayHash && note == device.HolidayNote) continue;
            try
            {
                await driver.ApplyHolidayCalendarAsync(access.ConnectionOf(device), calendar, ct);
                if (device.HolidayNote != note)
                    await audit.LogSystemAsync("access", "device-holidays-applied",
                        targetType: "access-device", targetId: device.Id.ToString(), targetName: device.Name,
                        detail: note is null
                            ? $"Escribió en '{device.Name}' {calendar.Count} feriado(s) del calendario."
                            : $"Dejó a '{device.Name}' sin feriados. {note}");
                device.AppliedHolidayHash = hash;
                device.HolidayNote = note;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                device.HolidayNote = $"No se pudieron escribir los feriados: {ex.Message}";
                logger.LogWarning(ex, "No se pudo escribir el calendario de feriados en {Device}.", device.Name);
            }
            device.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
