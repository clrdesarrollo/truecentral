using Microsoft.EntityFrameworkCore;
using TrueCentralVms.Server.Data;

namespace TrueCentralVms.Server.Services.Workflows;

/// <summary>
/// El calendario único de feriados (Control de acceso → Horarios → Feriados)
/// visto por las automatizaciones: la condición «Excluir feriados» pregunta
/// <see cref="IsHoliday"/> sin ir a la base. Se recarga al cambiar un feriado y
/// cada diez minutos.
///
/// <see cref="Current"/> es estático porque las condiciones se evalúan en
/// métodos estáticos del motor (también desde la prueba manual del editor).
/// Sin instancia (pruebas sin servidor) no hay feriados.
/// </summary>
public sealed class HolidayCalendar
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    public static HolidayCalendar? Current { get; private set; }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HolidayCalendar> _logger;
    private List<(DateOnly Start, DateOnly End, bool Yearly)> _holidays = [];
    private DateTime _loadedAt = DateTime.MinValue;
    private int _loading;

    public HolidayCalendar(IServiceScopeFactory scopeFactory, ILogger<HolidayCalendar> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        Current = this;
    }

    /// <summary>Avisa que cambió el calendario (lo llama la API de feriados).</summary>
    public void Invalidate()
    {
        _loadedAt = DateTime.MinValue;
        Refresh();
    }

    /// <summary>true si la fecha cae en algún feriado (los anuales se comparan por día y mes).</summary>
    public bool IsHoliday(DateOnly date)
    {
        if (DateTime.UtcNow - _loadedAt > MaxAge) Refresh();
        foreach (var (start, end, yearly) in _holidays)
        {
            if (!yearly)
            {
                if (date >= start && date <= end) return true;
                continue;
            }
            // Anual: se lleva el tramo al año de la fecha (y al anterior, por
            // si cruza el año nuevo: 31-12 → 01-01).
            for (int shift = -1; shift <= 0; shift++)
            {
                int years = date.Year + shift - start.Year;
                var (s, e) = (Shift(start, years), Shift(end, years));
                if (date >= s && date <= e) return true;
            }
        }
        return false;
    }

    /// <summary>Carga en segundo plano: la primera consulta tras arrancar ve la copia vieja (o vacía) un instante.</summary>
    private void Refresh()
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
                _holidays = (await db.AccessHolidays.AsNoTracking().ToListAsync())
                    .Select(h => (h.StartDate, h.EndDate, h.Yearly)).ToList();
                _loadedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo leer el calendario de feriados para las automatizaciones.");
            }
            finally
            {
                Interlocked.Exchange(ref _loading, 0);
            }
        });
    }

    /// <summary>Carga ahora, esperando (al arrancar el motor, para no evaluar la primera lectura sin feriados).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VmsDbContext>();
        _holidays = (await db.AccessHolidays.AsNoTracking().ToListAsync(ct))
            .Select(h => (h.StartDate, h.EndDate, h.Yearly)).ToList();
        _loadedAt = DateTime.UtcNow;
    }

    private static DateOnly Shift(DateOnly date, int years)
    {
        int year = date.Year + years;
        return new DateOnly(year, date.Month, Math.Min(date.Day, DateTime.DaysInMonth(year, date.Month)));
    }
}
