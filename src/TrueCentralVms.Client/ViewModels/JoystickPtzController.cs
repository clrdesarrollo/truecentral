using System.Windows.Input;
using System.Windows.Threading;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>Canal PTZ al que va una orden (equipo y número de canal del equipo).</summary>
public sealed record PtzTarget(int DeviceId, int ChannelNumber, int ChannelId);

/// <summary>Acción de un botón del joystick. <see cref="Ptz"/>: orden que dura mientras se sostiene.</summary>
public sealed record JoystickAction(string Key, string Label, PtzCommand? Ptz = null);

/// <summary>Acciones asignables a los botones: las mismas claves que el panel web (live-joystick.js).</summary>
public static class JoystickActions
{
    public static IReadOnlyList<JoystickAction> All { get; } =
    [
        new("zoomIn", "Zoom +", PtzCommand.ZoomIn),
        new("zoomOut", "Zoom −", PtzCommand.ZoomOut),
        new("focusNear", "Foco cerca", PtzCommand.FocusNear),
        new("focusFar", "Foco lejos", PtzCommand.FocusFar),
        new("irisOpen", "Iris: abrir", PtzCommand.IrisOpen),
        new("irisClose", "Iris: cerrar", PtzCommand.IrisClose),
        new("precision", "Precisión (velocidad mínima mientras se sostiene)"),
        .. Enumerable.Range(0, 10).Select(d => new JoystickAction($"d{d}", $"Dígito {d} (número de preset)")),
        new("presetGoto", "Ir al preset tecleado"),
        new("presetSet", "Guardar la posición en el preset tecleado"),
        new("presetClear", "Borrar el número tecleado"),
        new("next", "Seleccionar el cuadro siguiente"),
        new("prev", "Seleccionar el cuadro anterior"),
        new("maximize", "Maximizar el cuadro / volver a la grilla"),
    ];

    public static JoystickAction? Find(string? key) => All.FirstOrDefault(a => a.Key == key);
}

/// <summary>
/// Joystick PTZ de la Vista en Vivo: la palanca (ejes X/Y) mueve la cámara del
/// cuadro seleccionado con velocidad proporcional a la inclinación y el giro
/// (eje Z) hace zoom, todo a la vez, por /ptz/move (el servidor elige la vía
/// de cada marca). Foco e iris, en botones, van por /ptz. Mismo comportamiento
/// que el panel web: zona muerta, pasos de 0,05, renovación cada segundo (el
/// servidor detiene la cámara si deja de oírla por 3 s), filtro del rebote del
/// resorte y acciones de botones. Solo actúa con <c>canAct</c> verdadero
/// (ventana activa y Vista en Vivo visible); si no, detiene la cámara.
/// </summary>
public sealed class JoystickPtzController : IDisposable
{
    private const double Dead = 0.18, DeadZoom = 0.25, Precision = 0.2;
    private static readonly TimeSpan Renew = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(150);

    /// <summary>Orden pedida u orden en curso: velocidades, o una orden de foco/iris.</summary>
    private sealed record Want(PtzTarget Target, double Pan, double Tilt, double Zoom, PtzCommand? Command = null, int Speed = 0);

    private readonly MainViewModel _vm;
    private readonly Func<bool> _canAct;
    private readonly DispatcherTimer _timer;
    private bool[] _pressed = [];
    private Want? _want, _sent;
    private DateTime _sentAt;
    private bool _busy, _precision;
    private DateTime _stoppedAt = DateTime.MinValue, _pendingSince;
    private string _entry = "";

    public JoystickPtzController(MainViewModel vm, Func<bool> canAct)
    {
        _vm = vm;
        _canAct = canAct;
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>Suspende la lectura (p. ej. mientras se asignan botones en Configuración).</summary>
    public bool Paused { get; set; }

    private void Tick()
    {
        var state = Paused ? null : Joystick.Read();
        if (state is null)
        {
            _pressed = [];
            Halt();
            _vm.IsJoystickActive = false;
            return;
        }
        var cfg = _vm.Settings.JoystickFor(state.Id);
        bool[] rising = state.Buttons.Select((p, i) => p && (i >= _pressed.Length || !_pressed[i])).ToArray();
        _pressed = state.Buttons;
        var target = _vm.CurrentPtzTarget;
        _vm.IsJoystickActive = cfg.Enabled && target is not null;
        if (!cfg.Enabled || target is null || !_canAct())
        {
            Halt();
            return;
        }

        PtzCommand? held = null;
        bool precision = false;
        for (int i = 0; i < state.Buttons.Length; i++)
        {
            if (!cfg.Buttons.TryGetValue(i, out string? key) || JoystickActions.Find(key) is not { } action) continue;
            if (state.Buttons[i] && action.Ptz is { } ptz) held = ptz;
            if (state.Buttons[i] && action.Key == "precision") precision = true;
            if (rising[i] && action.Ptz is null && action.Key != "precision") RunAction(action.Key);
        }
        if (precision != _precision) { _precision = precision; _vm.IsPrecisionMode = precision; }
        Set(Debounce(Desired(state, cfg, target, held)));
    }

    /// <summary>Foco o iris sostenidos (orden de /ptz) o las velocidades de palanca, giro y botones de zoom.</summary>
    private Want? Desired(JoystickState state, JoystickConfig cfg, PtzTarget target, PtzCommand? held)
    {
        bool slow = _precision || (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        double scale = slow ? Precision : 1;
        if (held is { } command && command is not (PtzCommand.ZoomIn or PtzCommand.ZoomOut))
            return new Want(target, 0, 0, 0, command, slow ? 1 : _vm.PtzSpeed);
        double x = state.Axes.ElementAtOrDefault(0);
        double y = state.Axes.ElementAtOrDefault(1) * (cfg.InvertY ? -1 : 1);
        // Zona muerta radial: fuera de ella la velocidad crece de 0 a 1 con la inclinación.
        double magnitude = Math.Min(1, Math.Sqrt(x * x + y * y));
        double k = magnitude > Dead ? (magnitude - Dead) / (1 - Dead) / magnitude : 0;
        double zoom = 0;
        if (held is { } zoomCommand)
            zoom = (zoomCommand == PtzCommand.ZoomIn ? 1 : -1) * _vm.PtzSpeed / 7.0;
        else if (state.Axes.Length > 2)
        {
            double z = state.Axes[2] * (cfg.InvertZoom ? -1 : 1);
            if (Math.Abs(z) > DeadZoom) zoom = Math.Sign(z) * Math.Min(1, (Math.Abs(z) - DeadZoom) / (1 - DeadZoom));
        }
        // Pasos de 0,05: un temblor de la mano no genera una orden por lectura. Y negativo = arriba.
        static double Q(double v) => Math.Round(Math.Clamp(v, -1, 1) * 20) / 20 + 0.0;
        var move = new Want(target, Q(x * k * scale), Q(-y * k * scale), Q(zoom * scale));
        return move.Pan != 0 || move.Tilt != 0 || move.Zoom != 0 ? move : null;
    }

    /// <summary>Al soltar, el resorte rebota hacia el lado contrario: justo después de detenerse, lo nuevo debe sostenerse.</summary>
    private Want? Debounce(Want? want)
    {
        var now = DateTime.UtcNow;
        if (want is null)
        {
            if (_sent is not null || _want is not null) _stoppedAt = now;
            _pendingSince = default;
            return null;
        }
        if (_sent is not null || now - _stoppedAt > Settle) return want;
        if (_pendingSince == default) _pendingSince = now;
        return now - _pendingSince >= Hold ? want : null;
    }

    /// <summary>Detiene lo que el joystick tenga en marcha.</summary>
    private void Halt()
    {
        if (_precision) { _precision = false; _vm.IsPrecisionMode = false; }
        Set(null);
    }

    private void Set(Want? want)
    {
        _want = want;
        if (_busy) return;
        // Un movimiento sostenido se renueva: el servidor detiene la cámara si deja de oírlo.
        bool renew = _sent is { Command: null } && DateTime.UtcNow - _sentAt >= Renew;
        if (renew || !Same(want, _sent)) _ = FlushAsync();
    }

    private static bool Same(Want? a, Want? b) => a == b; // record: compara canal, velocidades y orden

    /// <summary>
    /// Una orden en vuelo a la vez: lo que cambió mientras tanto se resume en la
    /// siguiente (sin cola). Lo que se estaba haciendo se detiene al cambiar de
    /// cámara o de tipo de orden, y el "detener" va a la cámara que se movía.
    /// </summary>
    private async Task FlushAsync()
    {
        _busy = true;
        try
        {
            bool renew = _sent is not null && Same(_want, _sent);
            while (renew || !Same(_want, _sent))
            {
                renew = false;
                var want = _want;
                var sent = _sent;
                if (sent is not null && (want is null || want.Target != sent.Target || want.Command != sent.Command))
                    await _vm.JoystickPtzAsync(sent.Target, 0, 0, 0, sent.Command, sent.Speed, stop: true);
                if (want is not null)
                    await _vm.JoystickPtzAsync(want.Target, want.Pan, want.Tilt, want.Zoom, want.Command, want.Speed);
                _sent = want;
                _sentAt = DateTime.UtcNow;
                if (_want is not null) await Task.Delay(Gap);
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void RunAction(string key)
    {
        if (key.Length == 2 && key[0] == 'd' && char.IsDigit(key[1]))
        {
            _entry = (_entry + key[1]).TrimStart('0');
            if (_entry.Length > 3) _entry = _entry[^3..];
            if (int.TryParse(_entry, out int index)) _vm.PtzPresetIndex = Math.Clamp(index, 1, 300);
            _vm.StatusMessage = _entry.Length > 0
                ? $"Preset {_entry}: apriete «Ir al preset» en el joystick para llamarlo."
                : "Número de preset borrado.";
            return;
        }
        switch (key)
        {
            case "presetClear":
                _entry = "";
                _vm.StatusMessage = "Número de preset borrado.";
                break;
            case "presetGoto":
            case "presetSet":
                _entry = "";
                _ = _vm.PtzPresetAsync(key == "presetGoto" ? PtzPresetAction.Goto : PtzPresetAction.Set);
                break;
            case "next":
            case "prev":
                var cells = _vm.Cells.Where(c => c.AssignedChannel is not null).ToList();
                if (cells.Count == 0) return;
                int i = _vm.SelectedCell is { } selected ? cells.IndexOf(selected) : -1;
                int step = key == "next" ? 1 : -1;
                _vm.SelectedCell = cells[((i < 0 && step < 0 ? 0 : i) + step + cells.Count) % cells.Count];
                break;
            case "maximize":
                if (_vm.SelectedCell is { AssignedChannel: not null } cell) _vm.ToggleMaximize(cell);
                break;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        Halt();
    }
}
