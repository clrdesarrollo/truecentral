namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Viñetas extraídas, como las pestañas de un navegador: la vista de un módulo
/// pasa a su propia ventana con todo lo suyo (el Centro de mando con su grilla,
/// su lista y su PTZ). La llevan y la traen la ventana principal y la ventana
/// de la viñeta; aquí queda qué módulos están afuera, para que el riel y las
/// órdenes que abren un módulo traigan al frente su ventana en vez de
/// mostrarlo en la principal. Reemplaza al botón «Pantalla auxiliar».
/// </summary>
public partial class MainViewModel : ICommandCenter
{
    private readonly HashSet<string> _detachedSections = [];

    /// <summary>Se pidió ver un módulo que está en su propia ventana: hay que traerla al frente.</summary>
    public event Action<string>? DetachedSectionRequested;

    /// <summary>El módulo de esa viñeta ("Live", "Alarms"…) está en su propia ventana.</summary>
    public bool IsDetached(string? section) => section is not null && _detachedSections.Contains(section);

    public void SetDetached(string section, bool detached)
    {
        if (detached ? !_detachedSections.Add(section) : !_detachedSections.Remove(section)) return;
        OnPropertyChanged(nameof(IsShellFullscreen));
    }

    /// <summary>
    /// Un módulo extraído no se muestra en la principal: la página se queda en
    /// la que estaba y se trae al frente la ventana del módulo.
    /// </summary>
    partial void OnActiveSectionChanged(string? oldValue, string newValue)
    {
        if (!IsDetached(newValue)) return;
        ActiveSection = oldValue is { } previous && !IsDetached(previous) ? previous : "Home";
        DetachedSectionRequested?.Invoke(newValue);
    }

    /// <summary>Módulo que pidió la pantalla completa ("Live" o "Playback"): la cubre la
    /// ventana donde está ese módulo, la principal o la suya.</summary>
    public string FullscreenSection { get; set; } = "Live";

    /// <summary>La pantalla completa es de la ventana principal: ahí se esconde su barra, su riel y su barra de estado.</summary>
    public bool IsShellFullscreen => IsGridFullscreen && !IsDetached(FullscreenSection);

    partial void OnIsGridFullscreenChanged(bool value) => OnPropertyChanged(nameof(IsShellFullscreen));

    /// <summary>El módulo de esa viñeta está abierto (su viñeta existe).</summary>
    public bool IsSectionOpen(string section) => section switch
    {
        "Live" => IsLiveViewOpen,
        "Playback" => IsPlaybackOpen,
        "Wall" => IsWallOpen,
        "Alarms" => IsAlarmsOpen,
        "Cerco" => IsCercoOpen,
        "Intercom" => IsIntercomOpen,
        "Lpr" => IsLprOpen,
        "Events" => IsEventsOpen,
        _ => false,
    };

    /// <summary>Abre un módulo por la clave de su viñeta (al rearmar las viñetas extraídas de la sesión anterior).</summary>
    public void OpenSection(string section)
    {
        switch (section)
        {
            case "Live": OpenLiveView(); break;
            case "Playback": OpenPlayback(); break;
            case "Wall": OpenWall(); break;
            case "Alarms": OpenAlarms(); break;
            case "Cerco": OpenCerco(); break;
            case "Intercom": OpenIntercom(); break;
            case "Lpr": OpenLpr(); break;
        }
    }

    /// <summary>Saca el Centro de eventos a su ventana (la que ya tenía: «Ventana aparte»),
    /// en <paramref name="position"/> si viene de arrastrar su viñeta.</summary>
    public void PopOutEventCenterAt(System.Windows.Point? position)
    {
        PopOutEventCenter();
        if (position is { } p && _eventWindow is { } window)
        {
            window.WindowState = System.Windows.WindowState.Normal;
            window.Left = p.X - 60;
            window.Top = p.Y - 16;
        }
    }
}
