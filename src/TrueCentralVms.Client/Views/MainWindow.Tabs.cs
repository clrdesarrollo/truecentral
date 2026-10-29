using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Viñetas de módulos, como las pestañas de un navegador: el operador las
/// ordena arrastrándolas a lo largo de la barra (el orden se recuerda,
/// ClientSettings.TabOrder) y, arrastradas hacia abajo o fuera de la ventana,
/// se EXTRAEN a su propia ventana para llevarlas a otro monitor
/// (<see cref="DetachedTabWindow"/>). La vista del módulo se muda entera —no
/// es una copia— y vuelve al soltar su viñeta sobre esta barra o con el botón
/// de devolver. Reemplaza al botón «Pantalla auxiliar».
/// </summary>
public partial class MainWindow
{
    /// <summary>Viñeta donde se apretó el botón (todavía puede ser solo un clic).</summary>
    private FrameworkElement? _tabPressed;
    private Point _tabPressPoint;
    private bool _tabDragging;

    /// <summary>Cuánto hay que alejar la viñeta de la barra (hacia abajo o arriba) para extraerla.</summary>
    private const double TearOffDistance = 28;

    /// <summary>La vista de cada viñeta que se puede extraer (Inicio no: es la base de la ventana).</summary>
    private Dictionary<string, FrameworkElement> _sections = [];

    /// <summary>Viñetas que están afuera, cada una con su ventana.</summary>
    private readonly Dictionary<string, DetachedTabWindow> _detached = [];

    /// <summary>Lo que se le quitó a la vista y a la viñeta al extraerla (el enlace de su visibilidad).</summary>
    private readonly Dictionary<string, (BindingBase? View, BindingBase? Tab)> _detachedBindings = [];

    /// <summary>Se está cerrando la aplicación: las ventanas de las viñetas se cierran sin cerrar sus módulos.</summary>
    private bool _closingDetached;

    /// <summary>Clave de sección de una viñeta: la del ConverterParameter de su enlace (Home, Live…).</summary>
    private static string? TabKey(FrameworkElement tab) =>
        TabButton(tab) is { } button &&
        BindingOperations.GetBinding(button, ToggleButton.IsCheckedProperty) is { ConverterParameter: string key }
            ? key : null;

    /// <summary>El botón de la viñeta (la viñeta es el botón solo, o un Grid con el botón y su ✕).</summary>
    private static RadioButton? TabButton(FrameworkElement tab) =>
        tab as RadioButton ?? (tab as Panel)?.Children.OfType<RadioButton>().FirstOrDefault();

    /// <summary>La ✕ de la viñeta (su orden de cerrar el módulo).</summary>
    private static Button? TabCloseOf(FrameworkElement tab) => (tab as Panel)?.Children.OfType<Button>().FirstOrDefault();

    private FrameworkElement? TabOfKey(string key) =>
        TabStrip.Children.OfType<FrameworkElement>().FirstOrDefault(t => TabKey(t) == key);

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
    /// que el puntero pasa la mitad de su vecina. Alejada de la barra, se
    /// extrae a su propia ventana.
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
            if (Math.Abs(position.X - _tabPressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(position.Y - _tabPressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _tabDragging = true;
            // La barra se queda con el mouse: el botón de la viñeta no llega a hacer clic.
            TabStrip.CaptureMouse();
            tab.Opacity = 0.7;
            Mouse.OverrideCursor = Cursors.SizeAll;
        }

        // Fuera de la barra (abajo, arriba o fuera de la ventana): la viñeta se extrae.
        if ((position.Y > TabStrip.ActualHeight + TearOffDistance || position.Y < -TearOffDistance) &&
            TabKey(tab) is { } key && key != "Home")
        {
            var grab = _tabPressPoint - tab.TranslatePoint(new Point(0, 0), TabStrip);
            EndTabDrag();
            TearOff(key, PointToScreenDip(e.GetPosition(this)), grab);
            return;
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

    /// <summary>Un punto de esta ventana en coordenadas de pantalla, en unidades de WPF.</summary>
    private Point PointToScreenDip(Point local)
    {
        var device = PointToScreen(local);
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(device.X / dpi.DpiScaleX, device.Y / dpi.DpiScaleY);
    }

    /// <summary>Un punto de la pantalla (unidades de WPF) cae sobre la barra de viñetas.</summary>
    private bool IsOverTabStrip(Point screen)
    {
        if (!IsVisible || WindowState == WindowState.Minimized || !TabStrip.IsVisible) return false;
        var topLeft = PointToScreenDip(TabStrip.TranslatePoint(new Point(0, 0), this));
        // Toda la franja de la barra de título a partir de las viñetas: la barra
        // crece con las viñetas visibles y soltar a su derecha también vale.
        var right = PointToScreenDip(new Point(ActualWidth, 0)).X;
        return screen.X >= topLeft.X - 8 && screen.X <= right &&
               screen.Y >= topLeft.Y - 8 && screen.Y <= topLeft.Y + TabStrip.ActualHeight + 8;
    }

    // ------------------------------------------------------------------
    // Extraer y devolver viñetas
    // ------------------------------------------------------------------

    private void SetUpTearOff()
    {
        _sections = new Dictionary<string, FrameworkElement>
        {
            ["Live"] = LiveSection, ["Playback"] = PlaybackSection, ["Wall"] = WallSection,
            ["Alarms"] = AlarmsSection, ["Cerco"] = CercoSection, ["Lpr"] = LprSection,
            ["Intercom"] = IntercomSection,
        };
        // El riel u otra orden pidió un módulo que está afuera: su ventana al frente.
        _vm.DetachedSectionRequested += key =>
        {
            if (!_detached.TryGetValue(key, out var window)) return;
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        };
        // Lo mismo con el menú del clic derecho de cada viñeta (para quien no arrastra).
        foreach (var tab in TabStrip.Children.OfType<FrameworkElement>())
        {
            if (TabKey(tab) is not { } key || key == "Home") continue;
            var item = new MenuItem { Header = "Llevar a una ventana nueva" };
            item.Click += (_, _) => TearOff(key, null, new Vector(60, 16));
            var menu = new ContextMenu { Items = { item } };
            // Como «+»: otro Centro de mando, con su propia grilla, en su ventana.
            if (key == "Live")
            {
                var another = new MenuItem { Header = "Abrir otro Centro de mando" };
                another.Click += (_, _) => _vm.OpenCommandCenterInstanceCommand.Execute(null);
                menu.Items.Add(another);
            }
            if (TryFindResource("DarkContextMenu") is Style style) menu.Style = style;
            tab.ContextMenu = menu;
        }
    }

    /// <summary>
    /// Saca la viñeta a su propia ventana. Con <paramref name="screen"/> (se
    /// arrastró) la ventana aparece bajo el puntero y lo sigue mientras el
    /// botón esté apretado; sin él (menú) queda donde estuvo la última vez.
    /// </summary>
    private void TearOff(string key, Point? screen, Vector grab)
    {
        if (key == "Events")
        {
            // El Centro de eventos ya tenía su ventana aparte: es esa.
            _vm.PopOutEventCenterAt(screen);
            return;
        }
        if (_detached.ContainsKey(key) || !_sections.ContainsKey(key)) return;
        // Del tamaño del área de los módulos de esta ventana, como el navegador.
        var window = DetachSection(key, new Size(SectionHost.ActualWidth, SectionHost.ActualHeight + 42));
        if (window is null) return;
        if (screen is { } p)
        {
            window.WindowState = WindowState.Normal;
            window.Left = p.X - grab.X - 170; // la viñeta de la ventana nueva (tras el logo) queda bajo el puntero
            window.Top = p.Y - grab.Y;
        }
        window.Show();
        window.Activate();
        if (screen is not null)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(window.FollowPointer));
        _vm.StatusMessage = $"«{TabTitleOf(key)}» quedó en su propia ventana: arrastre su viñeta de vuelta a esta barra para devolverla.";
    }

    /// <summary>Muda la vista del módulo a una ventana nueva (sin mostrarla todavía).</summary>
    private DetachedTabWindow? DetachSection(string key, Size? size)
    {
        if (TabOfKey(key) is not { } tab || !_sections.TryGetValue(key, out var view)) return null;
        // Saca la pantalla completa de esta ventana antes de que se vaya el módulo que la pidió.
        if (_vm.IsGridFullscreen && _vm.FullscreenSection == key) _vm.IsGridFullscreen = false;

        _detachedBindings[key] = (BindingOperations.GetBindingBase(view, VisibilityProperty),
                                  BindingOperations.GetBindingBase(tab, VisibilityProperty));
        SectionHost.Children.Remove(view);
        BindingOperations.ClearBinding(view, VisibilityProperty);
        view.Visibility = Visibility.Visible;
        BindingOperations.ClearBinding(tab, VisibilityProperty);
        tab.Visibility = Visibility.Collapsed;

        _vm.SetDetached(key, true);
        if (_vm.ActiveSection == key) _vm.ActiveSection = NextVisibleSection() ?? "Home";

        var close = TabCloseOf(tab);
        var window = new DetachedTabWindow(_vm, key, TabTitleOf(key), TabGlyphOf(key),
            close?.ToolTip as string ?? "Cerrar", view, $"tab-{key}",
            () => _vm.IsGridFullscreen && _vm.FullscreenSection == key, canReturn: true, canOpenAnother: key == "Live")
        {
            IsOverMainTabs = IsOverTabStrip,
        };
        window.NewCommandCenterRequested += () => _vm.OpenCommandCenterInstanceCommand.Execute(null);
        if (size is { Width: > 300, Height: > 200 } s && WindowPlacements.Get($"tab-{key}") is null)
        {
            window.Width = s.Width;
            window.Height = s.Height;
        }
        window.ReturnRequested += w => DockSection(w, activate: true);
        window.CloseRequested += w =>
        {
            // Su ✕ (o la de la ventana): el módulo se cierra, como con la ✕ de la viñeta.
            // La ventana ya se está cerrando: no se le pide cerrarse otra vez.
            string current = _vm.ActiveSection;
            DockSection(w, activate: false, closeWindow: false);
            if (_closingDetached) return;
            close?.Command?.Execute(close.CommandParameter);
            // Cerrar el módulo deja la principal en Inicio: que siga en lo que estaba.
            if (current != key && _vm.IsSectionOpen(current)) _vm.ActiveSection = current;
        };
        _detached[key] = window;
        return window;
    }

    /// <summary>La vista vuelve a esta ventana y la viñeta reaparece (activa, si se devolvió a propósito).</summary>
    private void DockSection(DetachedTabWindow window, bool activate, bool closeWindow = true)
    {
        string key = window.Section;
        if (!_detached.Remove(key)) return;
        // Su pantalla completa era de la otra ventana: se termina antes de volver.
        if (_vm.IsGridFullscreen && _vm.FullscreenSection == key) _vm.IsGridFullscreen = false;
        var view = window.ReleaseView();
        SectionHost.Children.Add(view);
        var (viewBinding, tabBinding) = _detachedBindings.GetValueOrDefault(key);
        _detachedBindings.Remove(key);
        if (viewBinding is not null) view.SetBinding(VisibilityProperty, viewBinding);
        if (TabOfKey(key) is { } tab && tabBinding is not null) tab.SetBinding(VisibilityProperty, tabBinding);
        _vm.SetDetached(key, false);
        if (closeWindow && !_closingDetached) window.Close();
        if (!activate) return;
        _vm.ActiveSection = key;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>La primera viñeta visible que no es la que se va (para no dejar la ventana en blanco).</summary>
    private string? NextVisibleSection() =>
        TabStrip.Children.OfType<FrameworkElement>()
            .Where(t => t.Visibility == Visibility.Visible)
            .Select(TabKey).FirstOrDefault(k => k is not null && !_vm.IsDetached(k));

    private string TabTitleOf(string key) =>
        TabOfKey(key) is { } tab && TabButton(tab)?.Content is Panel content
            ? content.Children.OfType<TextBlock>().LastOrDefault()?.Text ?? key : key;

    private string TabGlyphOf(string key) =>
        TabOfKey(key) is { } tab && TabButton(tab)?.Content is Panel content
            ? content.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "" : "";

    /// <summary>
    /// Al salir: se anota qué viñetas estaban afuera (su lugar lo guarda cada
    /// ventana al cerrarse) y vuelven aquí sin cerrar sus módulos, así la
    /// última sesión se toma completa.
    /// </summary>
    private void CloseDetachedTabs()
    {
        _closingDetached = true;
        _vm.Settings.DetachedTabs = _detached.Keys.ToList();
        _vm.Settings.Save();
        foreach (var window in _detached.Values.ToList())
        {
            DockSection(window, activate: false);
            window.Close();
        }
    }

    /// <summary>
    /// Al abrir (con «Volver a abrir la última sesión»): las viñetas que
    /// estaban en su propia ventana vuelven a ella, donde quedó. Un módulo
    /// cerrado se abre para eso.
    /// </summary>
    private void RestoreDetachedTabs()
    {
        if (!_vm.Settings.RestoreLastSession || _vm.Settings.DetachedTabs.Count == 0) return;
        string home = _vm.ActiveSection;
        foreach (var key in _vm.Settings.DetachedTabs.Where(_sections.ContainsKey).ToList())
        {
            if (!_vm.IsSectionOpen(key)) _vm.OpenSection(key);
            if (!_vm.IsSectionOpen(key)) continue; // sin permiso o licencia: no se abre
            DetachSection(key, null)?.Show();
        }
        if (!_vm.IsDetached(home)) _vm.ActiveSection = home;
    }
}
