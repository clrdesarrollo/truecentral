using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Server.Data.Entities;

namespace TrueCentralVms.Server.Services.Reports;

/// <summary>
/// Reportes de control de acceso, armados a partir de los registros ya
/// filtrados por <see cref="Api.AccessRecordsApi"/>:
/// <list type="bullet">
/// <item><b>Detalle</b>: cada registro, tal como se ve en el buscador.</item>
/// <item><b>Asistencia</b>: por persona y día, el primer y el último acceso
/// concedido, la permanencia entre ambos y cuántos rechazos tuvo (base para
/// control de asistencia).</item>
/// <item><b>Resumen por puerta</b>: cuánto movimiento tuvo cada puerta.</item>
/// </list>
/// Las horas van en la hora local del servidor, que es la del sitio.
/// </summary>
public static class AccessReports
{
    public sealed record Person(string? Department, string? Position);

    public static string KindLabel(AccessEventKind kind) => kind switch
    {
        AccessEventKind.Granted => "Acceso concedido",
        AccessEventKind.Denied => "Acceso denegado",
        AccessEventKind.DoorOpen => "Puerta abierta",
        AccessEventKind.DoorClose => "Puerta cerrada",
        AccessEventKind.Alarm => "Alarma",
        _ => "Otro",
    };

    public static string CredentialLabel(AccessCredentialKind credential) => credential switch
    {
        AccessCredentialKind.Card => "Tarjeta",
        AccessCredentialKind.Fingerprint => "Huella",
        AccessCredentialKind.Face => "Rostro",
        AccessCredentialKind.Pin => "Clave",
        AccessCredentialKind.Remote => "Remoto",
        AccessCredentialKind.ExitButton => "Botón de salida",
        AccessCredentialKind.Qr => "Código QR",
        AccessCredentialKind.Plate => "Patente",
        _ => "",
    };

    private static string DoorOf(AccessEvent e) =>
        e.DoorName ?? (e.DoorNumber is int n ? $"Puerta {n}" : "");

    private static List<(string, string)> Totals(IReadOnlyCollection<AccessEvent> events) =>
    [
        ("Registros", events.Count.ToString("N0")),
        ("Concedidos", events.Count(e => e.Kind == AccessEventKind.Granted).ToString("N0")),
        ("Denegados", events.Count(e => e.Kind == AccessEventKind.Denied).ToString("N0")),
        ("Alarmas", events.Count(e => e.Kind == AccessEventKind.Alarm).ToString("N0")),
        ("Personas", events.Select(e => e.AccessPersonId?.ToString() ?? e.EmployeeNo).OfType<string>().Distinct().Count().ToString("N0")),
    ];

    public static ReportTable Detail(List<AccessEvent> events, Dictionary<int, Person> people, ReportMeta meta)
    {
        Person? PersonOf(AccessEvent e) => e.AccessPersonId is int id ? people.GetValueOrDefault(id) : null;
        var columns = new List<ReportColumn>
        {
            new("Fecha y hora", ReportCellKind.DateTime, 3.0, 20),
            new("Persona", ReportCellKind.Text, 4.0, 28),
            new("ID", ReportCellKind.Text, 1.8, 12),
            new("Tarjeta", ReportCellKind.Text, 2.2, 14),
            new("Área", ReportCellKind.Text, 2.7, 18),
            new("Cargo", ReportCellKind.Text, 0, 18),
            new("Puerta", ReportCellKind.Text, 0, 22),
            new("Equipo", ReportCellKind.Text, 0, 24),
            new("Punto de acceso", ReportCellKind.Text, 4.0, 0),
            new("Credencial", ReportCellKind.Text, 2.0, 14),
            new("Resultado", ReportCellKind.Result, 2.6, 18),
            new("Detalle", ReportCellKind.Text, 4.4, 48),
        };
        var rows = events.Select(e =>
        {
            var person = PersonOf(e);
            string door = DoorOf(e);
            return new object?[]
            {
                e.Timestamp.ToLocalTime(),
                e.PersonName ?? "",
                e.EmployeeNo ?? "",
                e.CardNumber ?? "",
                person?.Department ?? "",
                person?.Position ?? "",
                door,
                e.DeviceName,
                door.Length > 0 ? $"{door}\n{e.DeviceName}" : e.DeviceName,
                CredentialLabel(e.Credential),
                KindLabel(e.Kind),
                e.Description,
            };
        }).ToList();
        return new ReportTable("Registros de acceso", "registros_acceso", meta, columns, rows, Totals(events));
    }

    public static ReportTable Attendance(List<AccessEvent> events, Dictionary<int, Person> people, ReportMeta meta)
    {
        // Una persona se reconoce por su ficha del padrón; si el evento no se
        // pudo atar a una, por el ID con que la tiene el equipo.
        var relevant = events
            .Where(e => e.Kind is AccessEventKind.Granted or AccessEventKind.Denied)
            .Where(e => e.AccessPersonId is not null || !string.IsNullOrEmpty(e.EmployeeNo))
            .Select(e => (Event: e, Local: e.Timestamp.ToLocalTime()))
            .ToList();

        var rows = relevant
            .GroupBy(x => (Day: x.Local.Date, Key: x.Event.AccessPersonId?.ToString() ?? "emp:" + x.Event.EmployeeNo))
            .Select(g =>
            {
                var granted = g.Where(x => x.Event.Kind == AccessEventKind.Granted).OrderBy(x => x.Local).ToList();
                var any = g.OrderByDescending(x => x.Local).First().Event;
                var person = any.AccessPersonId is int id ? people.GetValueOrDefault(id) : null;
                DateTime? first = granted.Count > 0 ? granted[0].Local : null;
                DateTime? last = granted.Count > 0 ? granted[^1].Local : null;
                var doors = granted.Select(x => DoorOf(x.Event)).Where(d => d.Length > 0).Distinct().ToList();
                return new
                {
                    g.Key.Day,
                    Name = any.PersonName ?? "",
                    Row = new object?[]
                    {
                        g.Key.Day,
                        any.PersonName ?? "",
                        any.EmployeeNo ?? "",
                        person?.Department ?? "",
                        first?.TimeOfDay,
                        last?.TimeOfDay,
                        first is { } f && last is { } l && l > f ? l - f : null,
                        granted.Count,
                        g.Count(x => x.Event.Kind == AccessEventKind.Denied),
                        string.Join(", ", doors),
                    },
                };
            })
            .OrderBy(r => r.Day).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(r => r.Row)
            .ToList();

        var columns = new List<ReportColumn>
        {
            new("Fecha", ReportCellKind.Date, 2.3, 12),
            new("Persona", ReportCellKind.Text, 4.8, 28),
            new("ID", ReportCellKind.Text, 2.0, 12),
            new("Área", ReportCellKind.Text, 3.4, 18),
            new("Primer acceso", ReportCellKind.Time, 2.3, 14),
            new("Último acceso", ReportCellKind.Time, 2.3, 14),
            new("Permanencia", ReportCellKind.Duration, 2.3, 13),
            new("Accesos", ReportCellKind.Int, 1.8, 10),
            new("Rechazos", ReportCellKind.Int, 1.8, 10),
            new("Puertas", ReportCellKind.Text, 3.7, 40),
        };
        var totals = new List<(string, string)>
        {
            ("Días-persona", rows.Count.ToString("N0")),
            ("Personas", relevant.Select(x => x.Event.AccessPersonId?.ToString() ?? x.Event.EmployeeNo).Distinct().Count().ToString("N0")),
            ("Accesos concedidos", relevant.Count(x => x.Event.Kind == AccessEventKind.Granted).ToString("N0")),
            ("Rechazos", relevant.Count(x => x.Event.Kind == AccessEventKind.Denied).ToString("N0")),
        };
        return new ReportTable("Asistencia por persona y día", "asistencia_acceso", meta, columns, rows, totals);
    }

    public static ReportTable Doors(List<AccessEvent> events, Dictionary<int, Person> people, ReportMeta meta)
    {
        var rows = events
            .GroupBy(e => (e.AccessDeviceId, e.DoorNumber))
            .Select(g =>
            {
                var latest = g.OrderByDescending(e => e.Timestamp).First();
                return new
                {
                    Device = latest.DeviceName,
                    Door = DoorOf(latest) is { Length: > 0 } d ? d : "(del equipo)",
                    Granted = g.Count(e => e.Kind == AccessEventKind.Granted),
                    Denied = g.Count(e => e.Kind == AccessEventKind.Denied),
                    Moves = g.Count(e => e.Kind is AccessEventKind.DoorOpen or AccessEventKind.DoorClose),
                    Alarms = g.Count(e => e.Kind == AccessEventKind.Alarm),
                    Others = g.Count(e => e.Kind == AccessEventKind.Other),
                    Total = g.Count(),
                    People = g.Where(e => e.Kind == AccessEventKind.Granted)
                        .Select(e => e.AccessPersonId?.ToString() ?? e.EmployeeNo).OfType<string>().Distinct().Count(),
                    Last = latest.Timestamp.ToLocalTime(),
                };
            })
            .OrderBy(r => r.Device, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Door, StringComparer.CurrentCultureIgnoreCase)
            .Select(r => new object?[] { r.Device, r.Door, r.Granted, r.Denied, r.Moves, r.Alarms, r.Others, r.Total, r.People, r.Last })
            .ToList();

        var columns = new List<ReportColumn>
        {
            new("Equipo", ReportCellKind.Text, 4.6, 28),
            new("Puerta", ReportCellKind.Text, 4.4, 24),
            new("Concedidos", ReportCellKind.Int, 2.1, 12),
            new("Denegados", ReportCellKind.Int, 2.1, 12),
            new("Aperturas y cierres", ReportCellKind.Int, 2.4, 14),
            new("Alarmas", ReportCellKind.Int, 1.9, 10),
            new("Otros", ReportCellKind.Int, 1.7, 10),
            new("Total", ReportCellKind.Int, 1.9, 10),
            new("Personas distintas", ReportCellKind.Int, 2.3, 12),
            new("Último registro", ReportCellKind.DateTime, 3.3, 20),
        };
        return new ReportTable("Resumen por puerta", "resumen_puertas", meta, columns, rows, Totals(events));
    }
}
