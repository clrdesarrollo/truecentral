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

/// <summary>true si la celda del template es la celda seleccionada (borde acento).</summary>
public sealed class SelectedCellComparer : IMultiValueConverter
{
    public static readonly SelectedCellComparer Instance = new();
    public object Convert(object[] values, Type t, object p, CultureInfo c) =>
        values.Length == 2 && values[0] is not null && ReferenceEquals(values[0], values[1]);
    public object[] ConvertBack(object value, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}
