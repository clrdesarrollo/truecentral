using System.IO;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using TrueCentralVms.Client.Services;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Mapa de flota del cliente de escritorio: la página Flota → Mapa del panel web (MapLibre, la misma en ambos lados) dentro de un
/// WebView2. Entra con la sesión del cliente (el token se deja en el localStorage de la página antes de que cargue), acepta el
/// certificado del servidor solo si su llave es la que el cliente ya confía (<see cref="ServerTrust"/>) y atiende lo que la página
/// le pide: "Abrir en grilla" abre el vehículo en la ventana nativa Flota en vivo.
/// </summary>
public partial class FleetMapWindow : Window
{
    private readonly ApiClient _api;
    private readonly Action<int> _openVehicle;

    /// <param name="openVehicle">Abre las cámaras de un grabador (id de equipo) en Flota en vivo.</param>
    public FleetMapWindow(ApiClient api, Action<int> openVehicle)
    {
        _api = api;
        _openVehicle = openVehicle;
        InitializeComponent();
        // Abre donde quedó la última vez (ver WindowPlacements).
        WindowPlacements.Remember(this, "fleet-map");
        StateChanged += OnWindowStateChanged;
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => Web.Dispose();
    }

    private Uri? ServerUri => _api.BaseUrl is { } url ? new Uri(url) : null;

    private async Task StartAsync()
    {
        if (ServerUri is not { } server || _api.Token is null)
        {
            ShowError("No hay sesión con el servidor.");
            return;
        }
        try
        {
            // Perfil propio del navegador incrustado (cachés y localStorage), aparte del de cualquier otro programa.
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CLRTrueCentralVMS", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, folder);
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            ShowError("Este PC no tiene el componente WebView2 de Microsoft, que el mapa necesita. Instálelo desde " +
                      "https://go.microsoft.com/fwlink/p/?LinkId=2124703 (viene incluido en Windows 10/11 actualizados) y vuelva a abrir el mapa.\n\n" +
                      ex.Message);
            return;
        }
        var core = Web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.ServerCertificateErrorDetected += OnCertificateError;
        core.WebMessageReceived += OnWebMessage;
        // Abrir ventanas o ir a otro sitio no corresponde dentro del mapa.
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) &&
                !string.Equals(target.GetLeftPart(UriPartial.Authority), server.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
                e.Cancel = true;
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(SessionScript(server));
        core.Navigate($"{server.GetLeftPart(UriPartial.Authority)}/?embed=1#/fleet/map");
    }

    /// <summary>Deja la sesión del cliente en la página (solo en el origen del servidor) antes de que corra el panel.</summary>
    private string SessionScript(Uri server)
    {
        string origin = server.GetLeftPart(UriPartial.Authority);
        string Js(string? s) => JsonSerializer.Serialize(s ?? "");
        return $$"""
            if (location.origin === {{Js(origin)}}) {
              localStorage.setItem("tcvms_token", {{Js(_api.Token)}});
              localStorage.setItem("tcvms_user", {{Js(_api.Username)}});
              localStorage.setItem("tcvms_role", {{Js(_api.Role)}});
            }
            """;
    }

    /// <summary>El certificado propio del servidor no es de una CA pública: vale si su llave es la que el cliente confía.</summary>
    private void OnCertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        bool trusted = false;
        try
        {
            using var cert = e.ServerCertificate.ToX509Certificate2();
            trusted = Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var uri)
                && string.Equals(uri.GetLeftPart(UriPartial.Authority), ServerUri?.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
                && ServerTrust.Validate(uri, cert, SslPolicyErrors.RemoteCertificateChainErrors);
        }
        catch (Exception) { trusted = false; }
        e.Action = trusted ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow : CoreWebView2ServerCertificateErrorAction.Cancel;
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "fleet-open" &&
                root.TryGetProperty("deviceId", out var id) && id.TryGetInt32(out int deviceId))
                _openVehicle(deviceId);
        }
        catch (JsonException) { /* mensaje que no es nuestro */ }
    }

    private void ShowError(string message)
    {
        Web.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        // La sesión pudo renovarse (re-login automático del cliente): el script vuelve a dejar el token vigente.
        if (Web.CoreWebView2 is { } core && ServerUri is { } server)
        {
            _ = core.ExecuteScriptAsync(SessionScript(server));
            core.Reload();
        }
    }

    // ------------------------------------------------------------------
    // Maximizado que respeta la barra de tareas (como AuxLiveWindow) y barra de título propia.
    // ------------------------------------------------------------------
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WindowProc);
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        mmi.ptMaxTrackSize.X = Math.Max(mmi.ptMaxTrackSize.X, info.rcMonitor.Right - info.rcMonitor.Left);
        mmi.ptMaxTrackSize.Y = Math.Max(mmi.ptMaxTrackSize.Y, info.rcMonitor.Bottom - info.rcMonitor.Top);
        Marshal.StructureToPtr(mmi, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        RootShell.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaxGlyph.Text = maximized ? "" : "";
        MaxButton.ToolTip = maximized ? "Restaurar" : "Maximizar";
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
