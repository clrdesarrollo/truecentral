using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Módulo Vista en Vivo: árbol de dispositivos/canales, panel PTZ y grilla de
/// video. El DataContext es el MainViewModel de la ventana (heredado).
/// </summary>
public partial class LiveView : UserControl
{
    public LiveView()
    {
        InitializeComponent();
        // El teclado PTZ se captura a nivel de ventana: el foco puede estar en
        // el árbol, en la grilla o en cualquier control del shell.
        Loaded += (_, _) =>
        {
            if (_hostWindow is not null || Window.GetWindow(this) is not { } window) return;
            _hostWindow = window;
            window.PreviewKeyDown += OnPtzKeyDown;
            window.PreviewKeyUp += OnPtzKeyUp;
        };
    }

    private Window? _hostWindow;

    private MainViewModel Vm => (MainViewModel)DataContext;

    // ------------------------------------------------------------------
    // PTZ por teclado: flechas = pan/tilt, + y - = zoom (continuo: la
    // tecla presionada mueve, soltarla detiene). Shift sostenido = modo
    // precisión (velocidad mínima). No roba las teclas cuando el usuario
    // escribe en un cuadro de texto ni cuando el cuadro activo no es PTZ.
    // ------------------------------------------------------------------
    private TrueCentralVms.Core.Drivers.PtzCommand? _keyboardPtz;

    private static TrueCentralVms.Core.Drivers.PtzCommand? MapPtzKey(Key key) => key switch
    {
        Key.Up => TrueCentralVms.Core.Drivers.PtzCommand.TiltUp,
        Key.Down => TrueCentralVms.Core.Drivers.PtzCommand.TiltDown,
        Key.Left => TrueCentralVms.Core.Drivers.PtzCommand.PanLeft,
        Key.Right => TrueCentralVms.Core.Drivers.PtzCommand.PanRight,
        Key.Add or Key.OemPlus => TrueCentralVms.Core.Drivers.PtzCommand.ZoomIn,
        Key.Subtract or Key.OemMinus => TrueCentralVms.Core.Drivers.PtzCommand.ZoomOut,
        _ => null,
    };

    private static bool ShiftHeld => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

    private static bool IsTypingTarget(object? source) =>
        source is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox;

    private void UpdatePrecisionMode() => Vm.IsPrecisionMode = ShiftHeld && Vm.IsPtzAvailable;

    private void OnPtzKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return; // la Vista en Vivo está oculta (página Inicio)
        // Esc sale de la pantalla completa de la grilla.
        if (e.Key == Key.Escape && Vm.IsGridFullscreen)
        {
            Vm.IsGridFullscreen = false;
            e.Handled = true;
            return;
        }
        UpdatePrecisionMode();
        if (IsTypingTarget(e.OriginalSource)) return;
        if (MapPtzKey(e.Key) is not { } command || !Vm.IsPtzAvailable) return;

        e.Handled = true;
        if (e.IsRepeat || _keyboardPtz == command) return;
        // Cambio de tecla sin soltar la anterior: detener el movimiento previo.
        if (_keyboardPtz is { } previous)
            _ = Vm.PtzAsync(previous, stop: true);
        _keyboardPtz = command;
        _ = Vm.PtzAsync(command, stop: false, speed: ShiftHeld ? 1 : null);
    }

    private void OnPtzKeyUp(object sender, KeyEventArgs e)
    {
        UpdatePrecisionMode();
        if (MapPtzKey(e.Key) is not { } command || _keyboardPtz != command) return;
        _keyboardPtz = null;
        _ = Vm.PtzAsync(command, stop: true);
        e.Handled = true;
    }

    /// <summary>Doble clic en el árbol: un canal se abre en la grilla; un
    /// equipo abre todos sus canales adaptando la división.</summary>
    private async void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeviceTree.SelectedItem is ChannelNode node)
            await Vm.OpenChannelAsync(node);
        else if (DeviceTree.SelectedItem is DeviceNode device)
            await Vm.OpenDeviceAsync(device);
    }

    /// <summary>El nodo seleccionado (por clic o desde la grilla) queda a la
    /// vista. Diferido: seleccionar un cuadro PTZ también abre el panel PTZ,
    /// que encoge el árbol DESPUÉS del evento — el scroll debe correr con el
    /// layout ya asentado.</summary>
    private void OnTreeItemSelected(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => item.BringIntoView()));
    }

    // ------------------------------------------------------------------
    // Drag & drop: arrastrar un canal del árbol y soltarlo sobre un cuadro.
    // El arrastre parte recién tras superar el umbral del sistema, para no
    // interferir con el clic ni el doble clic.
    // ------------------------------------------------------------------
    private ChannelNode? _dragCandidate;
    private Point _dragStart;

    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = (e.OriginalSource as FrameworkElement)?.DataContext as ChannelNode;
        _dragStart = e.GetPosition(this);
    }

    private void OnTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragCandidate = null; return; }
        if (_dragCandidate is not { } node) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragCandidate = null;
        DragDrop.DoDragDrop(DeviceTree, new DataObject(typeof(ChannelNode), node), DragDropEffects.Copy);
    }

    /// <summary>Elegir una división cierra el popup (el comando ya la aplicó).</summary>
    private void OnLayoutPicked(object sender, RoutedEventArgs e) => LayoutPickerToggle.IsChecked = false;

    /// <summary>Soltar sobre un cuadro (barra o cuerpo vacío): abrir ahí.</summary>
    private async void OnCellDrop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VideoCellViewModel cell } &&
            e.Data.GetData(typeof(ChannelNode)) is ChannelNode node)
            await Vm.OpenChannelInCellAsync(node, cell);
    }

    /// <summary>Clic en la barra del cuadro (o en el hueco vacío): seleccionarlo.</summary>
    private void OnCellClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VideoCellViewModel cell }) return;
        // Doble clic: alternar entre el cuadro maximizado y la grilla.
        if (e.ClickCount == 2)
            Vm.ToggleMaximize(cell);
        else
            SelectCell(cell);
    }

    // ------------------------------------------------------------------
    // Selección con clic sobre el video: FlyleafLib pinta el video en
    // ventanas WPF propias ("Surface" y, si existe, "Overlay") superpuestas
    // a nuestro árbol visual — un clic ahí jamás llega a OnCellClick.
    // Como sí son ventanas WPF, al cargarse cada host nos suscribimos a su
    // MouseLeftButtonDown a nivel de ventana, con handledEventsToo porque
    // Flyleaf usa esos mismos eventos para su foco/arrastre y puede
    // marcarlos como manejados.
    // ------------------------------------------------------------------
    private readonly HashSet<FlyleafLib.Controls.WPF.FlyleafHost> _hookedHosts = [];

    /// <summary>Modo zoom digital (lupa + recuadro sobre el video).</summary>
    private readonly ZoomDragController _zoom = new();

    private void OnFlyleafHostLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FlyleafLib.Controls.WPF.FlyleafHost host || !_hookedHosts.Add(host))
            return;
        // Diferido a prioridad Loaded: garantiza que Flyleaf ya creó sus
        // ventanas en su propio manejador de Loaded, sea cual sea el orden.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            HookVideoWindow(host, host.Surface);
            HookVideoWindow(host, host.Overlay);
        }));
    }

    /// <summary>El botón de la barra enciende el modo zoom en todas las ventanas de video.</summary>
    private void OnDigitalZoomToggled(object sender, RoutedEventArgs e) =>
        _zoom.SetEnabled(Vm.IsDigitalZoomMode);

    private void HookVideoWindow(FlyleafLib.Controls.WPF.FlyleafHost host, Window? window)
    {
        if (window is null) return;
        // El modo zoom se engancha PRIMERO: mientras está activo, el arrastre
        // sobre el video marca el área y no debe seleccionar ni maximizar.
        _zoom.Attach(window, () => host.DataContext as VideoCellViewModel);
        window.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (_zoom.IsEnabled) return;
            if (host.DataContext is not VideoCellViewModel cell) return;
            // Doble clic sobre el video: maximizar/restaurar el cuadro
            // (el fullscreen propio de Flyleaf está deshabilitado).
            if (e.ClickCount == 2)
                Vm.ToggleMaximize(cell);
            else
                SelectCell(cell);
        }), handledEventsToo: true);
        // El video tapa la celda WPF: el drop de un canal arrastrado desde el
        // árbol se acepta directamente en la ventana de Flyleaf.
        window.AllowDrop = true;
        window.AddHandler(DragDrop.DropEvent, new DragEventHandler(async (_, e) =>
        {
            if (host.DataContext is VideoCellViewModel cell &&
                e.Data.GetData(typeof(ChannelNode)) is ChannelNode node)
                await Vm.OpenChannelInCellAsync(node, cell);
        }), handledEventsToo: true);
        // Un clic en el video deja el foco del teclado en la ventana de
        // Flyleaf: el PTZ por teclado también debe funcionar desde ahí.
        window.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnPtzKeyDown), handledEventsToo: true);
        window.AddHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(OnPtzKeyUp), handledEventsToo: true);
        // Rueda del mouse sobre el video = zoom digital, centrado en el cursor
        // (posición normalizada 0..1 dentro de la ventana de video).
        window.AddHandler(MouseWheelEvent, new MouseWheelEventHandler((_, e) =>
        {
            // Con el modo zoom activo manda el controlador, que ancla el paso
            // en el puntero; si no, se aplicaría el zoom dos veces.
            if (_zoom.IsEnabled) return;
            if (host.DataContext is not VideoCellViewModel cell || cell.IsEmpty) return;
            var position = e.GetPosition(window);
            var center = new Point(
                window.ActualWidth > 0 ? position.X / window.ActualWidth : 0.5,
                window.ActualHeight > 0 ? position.Y / window.ActualHeight : 0.5);
            cell.DigitalZoomStep(e.Delta > 0, center);
            e.Handled = true;
        }), handledEventsToo: true);
    }

    private void SelectCell(VideoCellViewModel cell) => Vm.SelectedCell = Vm.SelectedCell == cell ? null : cell;

    // ------------------------------------------------------------------
    // PTZ: movimiento continuo — presionar inicia, soltar (o salir del
    // botón con el mouse presionado) detiene.
    // ------------------------------------------------------------------
    private string? _activePtzCommand;

    private void OnPtzPress(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string command } || command.Length == 0) return;
        _activePtzCommand = command;
        _ = Vm.PtzAsync(Enum.Parse<TrueCentralVms.Core.Drivers.PtzCommand>(command), stop: false);
    }

    private void OnPtzRelease(object sender, MouseButtonEventArgs e) => StopPtz(sender);

    private void OnPtzLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            StopPtz(sender);
    }

    private void StopPtz(object sender)
    {
        if (sender is not FrameworkElement { Tag: string command } || command.Length == 0) return;
        if (_activePtzCommand != command) return;
        _activePtzCommand = null;
        _ = Vm.PtzAsync(Enum.Parse<TrueCentralVms.Core.Drivers.PtzCommand>(command), stop: true);
    }

    /// <summary>Botones de preset (Ir / Guardar / Borrar): órdenes de un solo clic.</summary>
    private void OnPtzPreset(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string action })
            _ = Vm.PtzPresetAsync(Enum.Parse<TrueCentralVms.Core.Drivers.PtzPresetAction>(action));
    }
}
