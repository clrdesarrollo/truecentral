using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Navegador de un cuadro con página web (<see cref="WebCellPanel"/>). Todos
/// los cuadros comparten un perfil propio
/// (%LocalAppData%\CLRTrueCentralVMS\WebView2-Tableros), aparte del de Flota,
/// que guarda la sesión del servidor. La página navega dentro del cuadro (solo
/// http/https); un enlace a otra ventana se abre en el mismo cuadro; las
/// descargas y los permisos (cámara, micrófono, ubicación) se niegan.
/// El navegador se crea al cargarse el cuadro y se suelta al descargarse. Es el
/// WebView2 de ventana propia (como el video): WebView2CompositionControl pide
/// las proyecciones WinRT de un TFM con versión de Windows, que el cliente no
/// usa. Como esa ventana se tragaría el mouse, la página se esconde durante
/// un arrastre (<see cref="DragActive"/>) y con el modo Unir de su grilla
/// (<see cref="MergeMode"/>).
/// </summary>
public partial class WebCellView : UserControl
{
    private static Task<CoreWebView2Environment>? _environment;
    private WebView2? _web;
    private WebCellPanel? _panel;

    public WebCellView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            DragActiveChanged += OnDragActiveChanged;
            OnDragActiveChanged();
            await SafeStartAsync();
        };
        Unloaded += (_, _) =>
        {
            DragActiveChanged -= OnDragActiveChanged;
            Stop();
        };
        // La plantilla puede reutilizarse con otra página (otro cuadro movido acá).
        DataContextChanged += async (_, _) =>
        {
            if (!IsLoaded) return;
            Stop();
            await SafeStartAsync();
        };
    }

    /// <summary>Los manejadores son async void: un error del navegador queda en el cuadro, no cierra el cliente.</summary>
    private async Task SafeStartAsync()
    {
        try { await StartAsync(); }
        catch (Exception ex)
        {
            if (DataContext is WebCellPanel panel)
            {
                panel.Status = "";
                panel.Error = "No se pudo abrir la página: " + ex.Message;
            }
        }
    }

    private static bool _dragActive;
    private static event Action? DragActiveChanged;

    /// <summary>
    /// Hay un arrastre en curso (desde la lista o de un cuadro a otro): las
    /// páginas se esconden mientras dura, así el cuadro recibe el arrastre y
    /// se puede soltar una cámara o un recurso encima de una página (el
    /// navegador es una ventana propia y se lo tragaría).
    /// </summary>
    public static bool DragActive
    {
        get => _dragActive;
        set
        {
            if (_dragActive == value) return;
            _dragActive = value;
            DragActiveChanged?.Invoke();
        }
    }

    /// <summary>
    /// La grilla del cuadro está en modo Unir: la selección se arrastra de
    /// cuadro en cuadro, también por encima de una página (la plantilla lo ata
    /// al IsMergeMode de la Vista en vivo o de la pantalla auxiliar).
    /// </summary>
    public bool MergeMode
    {
        get => (bool)GetValue(MergeModeProperty);
        set => SetValue(MergeModeProperty, value);
    }

    public static readonly DependencyProperty MergeModeProperty = DependencyProperty.Register(
        nameof(MergeMode), typeof(bool), typeof(WebCellView),
        new PropertyMetadata(false, (d, _) => ((WebCellView)d).OnDragActiveChanged()));

    /// <summary>La página se esconde (arrastre o modo Unir) y en su lugar va un aviso.</summary>
    private bool PageHidden => _dragActive || MergeMode;

    private void OnDragActiveChanged()
    {
        if (_web is not null) _web.Visibility = PageHidden ? Visibility.Hidden : Visibility.Visible;
        DragHint.Visibility = PageHidden && DataContext is WebCellPanel ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = _dragActive ? "Suelte aquí para reemplazar la página"
            : "Modo Unir: arrastre de un cuadro a otro (la página vuelve al salir)";
    }

    private static Task<CoreWebView2Environment> EnvironmentAsync() =>
        _environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CLRTrueCentralVMS", "WebView2-Tableros"));

    private static bool IsWebUrl(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp || u.Scheme == "about");

    private async Task StartAsync()
    {
        if (_web is not null || DataContext is not WebCellPanel panel) return;
        _panel = panel;
        panel.ReloadRequested += OnReload;
        panel.PropertyChanged += OnPanelPropertyChanged;
        var web = _web = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x15, 0x18, 0x1D),
            AllowExternalDrop = false,
            Visibility = PageHidden ? Visibility.Hidden : Visibility.Visible,
            ZoomFactor = panel.Zoom / 100.0,
        };
        // Ctrl + rueda dentro de la página: el porcentaje de la barra (y lo guardado) lo sigue.
        web.ZoomFactorChanged += (_, _) =>
        {
            int zoom = (int)Math.Round(web.ZoomFactor * 100);
            if (_panel == panel && zoom != panel.Zoom) panel.Zoom = zoom;
        };
        Host.Child = web;
        try
        {
            await web.EnsureCoreWebView2Async(await EnvironmentAsync());
        }
        catch (Exception ex)
        {
            // Un cuadro web nunca puede llevarse el cliente (esto corre en un Loaded async).
            if (_web != web) return;
            _environment = null; // el próximo cuadro vuelve a intentarlo
            // El navegador es una ventana encima del cuadro: se quita para que el aviso se vea.
            Host.Child = null;
            web.Dispose();
            _web = null;
            panel.Status = "";
            panel.Error = ex is WebView2RuntimeNotFoundException or COMException
                ? "Este PC no tiene el componente WebView2 de Microsoft, que las páginas web necesitan " +
                  "(viene con Windows 10/11 actualizados).\n" + ex.Message
                : "No se pudo abrir el navegador del cuadro: " + ex.Message;
            return;
        }
        if (_web != web) return; // el cuadro se descargó mientras arrancaba

        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (IsWebUrl(e.Uri)) core.Navigate(e.Uri);
        };
        core.NavigationStarting += (_, e) => { if (!IsWebUrl(e.Uri)) e.Cancel = true; };
        // Si no carga, el propio navegador muestra su página de error; la barra dice «No cargó».
        core.NavigationCompleted += (_, e) =>
        {
            panel.CurrentUrl = core.Source;
            panel.Status = e.IsSuccess ? "" : "No cargó";
            ApplyZoom(); // otro sitio puede volver al 100 %
        };
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.Navigate(panel.Url);
    }

    private void OnReload()
    {
        if (_panel is null) return;
        bool failed = string.IsNullOrEmpty(_panel.CurrentUrl) || _panel.Status == "No cargó";
        _panel.Error = "";
        _panel.Status = "Cargando…";
        // El navegador no arrancó: se vuelve a intentar desde cero.
        if (_web?.CoreWebView2 is not { } core)
        {
            Stop();
            _ = SafeStartAsync();
            return;
        }
        // Si no cargó, vuelve a la dirección original; si cargó, recarga donde está.
        if (failed) core.Navigate(_panel.Url);
        else core.Reload();
    }

    private void OnPanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WebCellPanel.Zoom)) ApplyZoom();
    }

    /// <summary>El zoom del cuadro (botones −/+ de la barra o lo guardado) en el navegador.</summary>
    private void ApplyZoom()
    {
        if (_web is not { } web || _panel is not { } panel) return;
        double factor = panel.Zoom / 100.0;
        if (Math.Abs(web.ZoomFactor - factor) > 0.001) web.ZoomFactor = factor;
    }

    private void Stop()
    {
        if (_panel is not null)
        {
            _panel.ReloadRequested -= OnReload;
            _panel.PropertyChanged -= OnPanelPropertyChanged;
        }
        _panel = null;
        if (_web is null) return;
        var web = _web;
        _web = null;
        Host.Child = null;
        web.Dispose();
    }
}
