using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>Una casilla del calendario.</summary>
public sealed partial class CalendarDay(DateTime date, bool isCurrentMonth) : ObservableObject
{
    public DateTime Date { get; } = date.Date;

    public string Label { get; } = date.Day.ToString(CultureInfo.InvariantCulture);

    /// <summary>false para los días de relleno del mes anterior/siguiente.</summary>
    public bool IsCurrentMonth { get; } = isCurrentMonth;

    public bool IsToday => Date == DateTime.Today;

    /// <summary>Un día que todavía no llega no puede tener grabaciones.</summary>
    public bool IsFuture => Date > DateTime.Today;

    /// <summary>El equipo informó grabación este día (marca del calendario).</summary>
    [ObservableProperty] private bool _hasRecordings;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Calendario del módulo Reproducción: reemplaza el ir día por día con las
/// flechas, que era el camino largo para llegar, por ejemplo, al viernes de la
/// semana pasada.
///
/// Las casillas se marcan con los días que el equipo dice tener grabados, así
/// el operador ve de un vistazo hasta dónde llega el disco del grabador (que
/// es lo que de verdad limita la búsqueda) en vez de tantear días vacíos.
/// </summary>
public sealed partial class RecordingCalendar : ObservableObject
{
    /// <summary>Primer día del mes visible.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthLabel))]
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    /// <summary>
    /// Se abre por el enlace del botón de la barra, no por un comando: por eso
    /// la grilla se arma AQUÍ. Construirla solo en un método de apertura la
    /// dejaba vacía, porque nadie lo llamaba.
    /// </summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>Día elegido; lo mantiene sincronizado el módulo Reproducción.</summary>
    [ObservableProperty] private DateTime _selected = DateTime.Today;

    partial void OnIsOpenChanged(bool value)
    {
        if (!value) return;
        var month = new DateTime(Selected.Year, Selected.Month, 1);
        if (Month == month)
        {
            // Mismo mes: el cambio de propiedad no dispara solo, hay que
            // rearmar y volver a pedir las marcas a mano.
            Build();
            MonthChanged?.Invoke();
        }
        else
        {
            Month = month; // dispara OnMonthChanged: arma la grilla y pide marcas
        }
    }

    partial void OnSelectedChanged(DateTime value)
    {
        foreach (var day in Days)
            day.IsSelected = day.Date == value.Date;
    }

    /// <summary>Consulta de marcas en curso (el equipo tarda en responder el mes).</summary>
    [ObservableProperty] private bool _isLoading;

    public ObservableCollection<CalendarDay> Days { get; } = [];

    /// <summary>Encabezado del mes, con la inicial en mayúscula ("Agosto 2026").</summary>
    public string MonthLabel
    {
        get
        {
            string text = Month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
            return text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
        }
    }

    /// <summary>Iniciales de la semana, empezando el lunes (uso local).</summary>
    public IReadOnlyList<string> WeekdayHeaders { get; } = ["L", "M", "M", "J", "V", "S", "D"];

    /// <summary>El usuario eligió un día.</summary>
    public event Action<DateTime>? DayPicked;

    /// <summary>Cambió el mes visible: hay que volver a pedir sus marcas.</summary>
    public event Action? MonthChanged;

    partial void OnMonthChanged(DateTime value)
    {
        Build();
        MonthChanged?.Invoke();
    }

    [RelayCommand]
    private void PreviousMonth() => Month = Month.AddMonths(-1);

    /// <summary>No tiene sentido navegar a meses que todavía no ocurren.</summary>
    [RelayCommand]
    private void NextMonth()
    {
        var next = Month.AddMonths(1);
        if (next <= new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1))
            Month = next;
    }

    [RelayCommand]
    private void PickDay(CalendarDay? day)
    {
        if (day is null || day.IsFuture) return;
        IsOpen = false;
        DayPicked?.Invoke(day.Date);
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>Rellena la grilla de 6×7 (semanas completas, de lunes a domingo).</summary>
    private void Build()
    {
        Days.Clear();
        // DayOfWeek pone el domingo en 0; acá la semana parte el lunes.
        int offset = ((int)Month.DayOfWeek + 6) % 7;
        var first = Month.AddDays(-offset);
        for (int i = 0; i < 42; i++)
        {
            var date = first.AddDays(i);
            Days.Add(new CalendarDay(date, date.Month == Month.Month && date.Year == Month.Year)
            {
                IsSelected = date == Selected.Date,
            });
        }
    }

    /// <summary>Pinta las marcas del mes visible con los días que informó el equipo.</summary>
    public void ApplyMarks(IEnumerable<int> daysWithRecordings)
    {
        var marked = daysWithRecordings.ToHashSet();
        foreach (var day in Days)
            day.HasRecordings = day.IsCurrentMonth && marked.Contains(day.Date.Day);
    }
}
