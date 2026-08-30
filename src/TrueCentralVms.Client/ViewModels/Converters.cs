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

/// <summary>true si la celda del template es la celda seleccionada (borde acento).</summary>
public sealed class SelectedCellComparer : IMultiValueConverter
{
    public static readonly SelectedCellComparer Instance = new();
    public object Convert(object[] values, Type t, object p, CultureInfo c) =>
        values.Length == 2 && values[0] is not null && ReferenceEquals(values[0], values[1]);
    public object[] ConvertBack(object value, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}
