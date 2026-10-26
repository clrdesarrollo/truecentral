using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TrueCentralVms.Watchdog;

/// <summary>Color del punto del ícono junto al reloj: el mismo que el estado general de la ventana.</summary>
internal enum TrayState { Unknown, Ok, Warn, Danger }

/// <summary>
/// Ícono del Watchdog en el área de notificación (junto al reloj): el logo con un punto del color del estado, el estado
/// en el tooltip, avisos cuando el servicio cae o se vuelve a iniciar, y un menú con las acciones de la ventana.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Dictionary<TrayState, Icon> _icons = [];
    private readonly ToolStripMenuItem _start;
    private readonly ToolStripMenuItem _stop;
    private TrayState? _state;

    public event Action? OpenRequested;
    public event Action? StartRequested;
    public event Action? StopRequested;
    public event Action? OpenPanelRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Abrir el Watchdog", null, (_, _) => OpenRequested?.Invoke()) { Font = BoldMenuFont() };
        _start = new ToolStripMenuItem("Iniciar servicio", null, (_, _) => StartRequested?.Invoke()) { Enabled = false };
        _stop = new ToolStripMenuItem("Detener servicio", null, (_, _) => StopRequested?.Invoke()) { Enabled = false };
        menu.Items.AddRange([
            open,
            new ToolStripSeparator(),
            _start,
            _stop,
            new ToolStripMenuItem("Abrir panel web", null, (_, _) => OpenPanelRequested?.Invoke()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Salir del Watchdog", null, (_, _) => ExitRequested?.Invoke()),
        ]);

        using var baseIcon = AppIcon();
        foreach (var state in Enum.GetValues<TrayState>())
            _icons[state] = WithDot(baseIcon, state);

        _icon = new NotifyIcon
        {
            Icon = _icons[TrayState.Unknown],
            Text = "CLR TrueCentral VMS — Watchdog",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenRequested?.Invoke(); };
    }

    public void Update(TrayState state, string status, bool canStart, bool canStop)
    {
        if (_state != state)
        {
            _icon.Icon = _icons[state];
            _state = state;
        }
        string text = $"TrueCentral VMS: {status}";
        _icon.Text = text.Length > 127 ? text[..126] + "…" : text;   // tope de Windows para el tooltip
        _start.Enabled = canStart;
        _stop.Enabled = canStop;
    }

    public void Notify(string title, string message, ToolTipIcon kind) => _icon.ShowBalloonTip(8000, title, message, kind);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values)
        {
            DestroyIcon(icon.Handle);
            icon.Dispose();
        }
    }

    private static Font BoldMenuFont() => new(SystemFonts.MenuFont ?? Control.DefaultFont, System.Drawing.FontStyle.Bold);

    private static Icon AppIcon()
    {
        Icon? own = Environment.ProcessPath is { } exe ? Icon.ExtractAssociatedIcon(exe) : null;
        return own is null ? (Icon)SystemIcons.Application.Clone() : new Icon(own, SystemInformation.SmallIconSize);
    }

    /// <summary>El logo con un punto de color abajo a la derecha (gris = todavía sin datos).</summary>
    private static Icon WithDot(Icon baseIcon, TrayState state)
    {
        var size = SystemInformation.SmallIconSize;
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawIcon(baseIcon, new Rectangle(Point.Empty, size));
            Color color = state switch
            {
                TrayState.Ok => Color.FromArgb(0x22, 0xC5, 0x5E),
                TrayState.Warn => Color.FromArgb(0xF5, 0x9E, 0x0B),
                TrayState.Danger => Color.FromArgb(0xEF, 0x44, 0x44),
                _ => Color.FromArgb(0x8B, 0x98, 0xA5),
            };
            float d = size.Width * 0.5f;
            var dot = new RectangleF(size.Width - d - 0.5f, size.Height - d - 0.5f, d, d);
            using var brush = new SolidBrush(color);
            using var border = new Pen(Color.FromArgb(0x0F, 0x14, 0x1A), Math.Max(1f, size.Width / 16f));
            g.FillEllipse(brush, dot);
            g.DrawEllipse(border, dot);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
