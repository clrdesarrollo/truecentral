using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>true → Visible; false → Collapsed.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public static readonly BoolToVisibility Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>true → Collapsed; false → Visible.</summary>
public sealed class InverseBoolToVisibility : IValueConverter
{
    public static readonly InverseBoolToVisibility Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Visible si la sección activa coincide con el parámetro (conmutador de páginas).</summary>
public sealed class SectionToVisibility : IValueConverter
{
    public static readonly SectionToVisibility Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        Equals(value as string, p as string) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>
/// ActiveSection ⇄ IsChecked de una pestaña del navbar: marcada si coincide con
/// el parámetro; al marcarla, la sección activa pasa a ser el parámetro (des-
/// marcarla no hace nada: siempre hay exactamente una pestaña activa).
/// </summary>
public sealed class SectionEquals : IValueConverter
{
    public static readonly SectionEquals Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        Equals(value as string, p as string);
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is true ? (p as string ?? "") : Binding.DoNothing;
}

/// <summary>0 → Visible; cualquier otro número → Collapsed (mensajes de "aún no hay nada").</summary>
public sealed class ZeroToVisibility : IValueConverter
{
    public static readonly ZeroToVisibility Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is int and 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>null → Visible; con valor → Collapsed (estados "no hay nada seleccionado").</summary>
public sealed class NullToVisibility : IValueConverter
{
    public static readonly NullToVisibility Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Con valor → Visible; null → Collapsed.</summary>
public sealed class NotNullToVisibility : IValueConverter
{
    public static readonly NotNullToVisibility Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Texto con contenido → Visible; vacío o null → Collapsed (avisos y errores).</summary>
public sealed class NonEmptyToVisibility : IValueConverter
{
    public static readonly NonEmptyToVisibility Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>true si la celda del template es la celda seleccionada (borde acento).</summary>
public sealed class SelectedCellComparer : IMultiValueConverter
{
    public static readonly SelectedCellComparer Instance = new();
    public object Convert(object[] values, Type t, object p, CultureInfo c) =>
        values.Length == 2 && values[0] is not null && ReferenceEquals(values[0], values[1]);
    public object[] ConvertBack(object value, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Porcentaje (0..100) → ancho en píxeles sobre un ancho total dado por ConverterParameter (vúmetro).</summary>
public sealed class PercentToWidth : IValueConverter
{
    public static readonly PercentToWidth Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double total = parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double t) ? t : 100;
        double percent = value is int i ? i : value is double d ? d : 0;
        return Math.Clamp(percent, 0, 100) / 100.0 * total;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true si el número supera el umbral de ConverterParameter (vúmetro en rojo al saturar).</summary>
public sealed class GreaterThan : IValueConverter
{
    public static readonly GreaterThan Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double threshold = parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double t) ? t : 0;
        double number = value is int i ? i : value is double d ? d : 0;
        return number > threshold;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
