using Avalonia.Data.Converters;
using Avalonia.Media;

namespace TrueCentralVms.AlarmScreen.ViewModels;

/// <summary>Conversiones de presentación que no merecen ensuciar el ViewModel.</summary>
public static class StatusConverters
{
    /// <summary>Verde conectada, rojo desconectada. Es el indicador que mira el guardia de reojo.</summary>
    public static readonly IValueConverter OnlineBrush =
        new FuncValueConverter<bool, IBrush>(online =>
            new SolidColorBrush(Color.Parse(online ? "#66BB6A" : "#EF5350")));

    /// <summary>true cuando el contador es mayor que cero (para mostrar u ocultar el contador de pendientes).</summary>
    public static readonly IValueConverter IsPositive =
        new FuncValueConverter<int, bool>(count => count > 0);

    /// <summary>"3 sin ver".</summary>
    public static readonly IValueConverter PendingText =
        new FuncValueConverter<int, string>(count => $"{count} sin ver");
}
