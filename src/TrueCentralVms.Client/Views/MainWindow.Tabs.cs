using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Viñetas de módulos: el operador las ordena a su gusto arrastrándolas a lo
/// largo de la barra, como las pestañas de un navegador. El orden se recuerda
/// para la próxima vez (ClientSettings.TabOrder).
/// </summary>
public partial class MainWindow
{
    /// <summary>Viñeta donde se apretó el botón (todavía puede ser solo un clic).</summary>
    private FrameworkElement? _tabPressed;
    private Point _tabPressPoint;
    private bool _tabDragging;

    /// <summary>Clave de sección de una viñeta: la del ConverterParameter de su enlace (Home, Live…).</summary>
    private static string? TabKey(FrameworkElement tab) =>
        TabButton(tab) is { } button &&
        BindingOperations.GetBinding(button, ToggleButton.IsCheckedProperty) is { ConverterParameter: string key }
            ? key : null;

    /// <summary>El botón de la viñeta (la viñeta es el botón solo, o un Grid con el botón y su ✕).</summary>
    private static RadioButton? TabButton(FrameworkElement tab) =>
        tab as RadioButton ?? (tab as Panel)?.Children.OfType<RadioButton>().FirstOrDefault();

    /// <summary>Pone las viñetas en el orden guardado; las que no figuran quedan al final, como siempre.</summary>
    private void ApplyTabOrder()
    {
        var order = _vm.Settings.TabOrder;
        if (order.Count == 0) return;
        var tabs = TabStrip.Children.OfType<FrameworkElement>().ToList();
        var sorted = tabs
            .Select((tab, index) => (tab, index, rank: order.IndexOf(TabKey(tab) ?? "") is var r and >= 0 ? r : int.MaxValue))
            .OrderBy(x => x.rank).ThenBy(x => x.index)
            .Select(x => x.tab).ToList();
        if (sorted.SequenceEqual(tabs)) return;
        TabStrip.Children.Clear();
        foreach (var tab in sorted) TabStrip.Children.Add(tab);
    }

    /// <summary>La viñeta (hijo directo de la barra) que contiene ese elemento.</summary>
    private FrameworkElement? TabOf(object? source)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Button) return null; // la ✕ de la viñeta cierra, no arrastra
            if (node is FrameworkElement element && ReferenceEquals(VisualTreeHelper.GetParent(element), TabStrip))
                return element;
        }
        return null;
    }

    private void OnTabStripMouseDown(object sender, MouseButtonEventArgs e)
    {
        _tabPressed = TabOf(e.OriginalSource);
        _tabPressPoint = e.GetPosition(TabStrip);
    }

    /// <summary>
    /// El arrastre parte recién al superar el umbral del sistema (así el clic
    /// sigue abriendo la viñeta) y la viñeta se va moviendo en vivo a medida
    /// que el puntero pasa la mitad de su vecina.
    /// </summary>
    private void OnTabStripMouseMove(object sender, MouseEventArgs e)
    {
        if (_tabPressed is not { } tab) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndTabDrag();
            return;
        }
        var position = e.GetPosition(TabStrip);
        if (!_tabDragging)
        {
            if (Math.Abs(position.X - _tabPressPoint.X) < SystemParameters.MinimumHorizontalDragDistance) return;
            _tabDragging = true;
            // La barra se queda con el mouse: el botón de la viñeta no llega a hacer clic.
            TabStrip.CaptureMouse();
            tab.Opacity = 0.7;
            Mouse.OverrideCursor = Cursors.SizeWE;
        }

        // Cuántas de las otras viñetas visibles quedan a la izquierda del puntero.
        var others = TabStrip.Children.OfType<FrameworkElement>()
            .Where(t => t != tab && t.Visibility == Visibility.Visible).ToList();
        int before = others.Count(t =>
            t.TranslatePoint(new Point(t.ActualWidth / 2, 0), TabStrip).X < position.X);
        int target = before == 0
            ? (others.Count > 0 ? TabStrip.Children.IndexOf(others[0]) : 0)
            : TabStrip.Children.IndexOf(others[before - 1]) + 1;
        int current = TabStrip.Children.IndexOf(tab);
        if (target > current) target--; // al sacarla, las de la derecha se corren una
        if (target == current) return;
        TabStrip.Children.RemoveAt(current);
        TabStrip.Children.Insert(target, tab);
    }

    private void OnTabStripMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_tabDragging)
        {
            _tabPressed = null;
            return;
        }
        // La viñeta arrastrada queda activa, como en un navegador.
        if (_tabPressed is { } tab && TabButton(tab) is { } button) button.IsChecked = true;
        EndTabDrag();
        e.Handled = true;
    }

    /// <summary>La barra perdió el mouse a mitad del arrastre (otra ventana, Alt+Tab): se termina ahí.
    /// El aviso sube también desde el botón de la viñeta, que lo suelta justo cuando la barra lo
    /// toma al empezar el arrastre: ese no cuenta.</summary>
    private void OnTabStripLostCapture(object sender, MouseEventArgs e)
    {
        if (_tabDragging && ReferenceEquals(e.OriginalSource, TabStrip)) EndTabDrag();
    }

    private void EndTabDrag()
    {
        bool dragged = _tabDragging;
        if (_tabPressed is { } tab) tab.Opacity = 1;
        _tabPressed = null;
        _tabDragging = false;
        Mouse.OverrideCursor = null;
        if (TabStrip.IsMouseCaptured) TabStrip.ReleaseMouseCapture();
        if (!dragged) return;
        _vm.Settings.TabOrder = TabStrip.Children.OfType<FrameworkElement>()
            .Select(TabKey).OfType<string>().ToList();
        _vm.Settings.Save();
    }
}
