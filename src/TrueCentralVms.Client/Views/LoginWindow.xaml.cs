using System.Windows;
using System.Windows.Controls;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

public partial class LoginWindow : Window
{
    private readonly ClientSettings _settings = ClientSettings.Load();

    /// <summary>El auto-login corre una sola vez por ejecución: si falla (o el
    /// usuario vuelve a esta ventana) no debe reintentar en bucle.</summary>
    private static bool _autoLoginAttempted;

    /// <param name="skipAutoLogin">true al volver desde un cierre de sesión:
    /// sin esto el auto-login volvería a entrar de inmediato con la cuenta que
    /// el usuario acaba de cerrar.</param>
    /// <param name="notice">Aviso a la vista al abrir (p. ej. por qué terminó
    /// la sesión anterior).</param>
    public LoginWindow(bool skipAutoLogin = false, string? notice = null)
    {
        InitializeComponent();
        // Abre donde quedó la última vez (ver WindowPlacements).
        WindowPlacements.Remember(this, "login");
        if (skipAutoLogin) _autoLoginAttempted = true;
        if (notice is not null)
        {
            ErrorText.Text = notice;
            ErrorText.Visibility = Visibility.Visible;
        }
        ShowServer(_settings.ServerUrl);
        UserBox.Text = _settings.Username;
        RememberCheck.IsChecked = _settings.RememberPassword;
        AutoLoginCheck.IsChecked = _settings.AutoLogin;
        RefreshServersList();
        RefreshAccountsList();
        RestoreRememberedPassword();
        (string.IsNullOrEmpty(_settings.Username) ? (UIElement)UserBox : PasswordBox).Focus();

        Loaded += async (_, _) =>
        {
            if (_settings.AutoLogin && !_autoLoginAttempted && CurrentPassword().Length > 0)
            {
                _autoLoginAttempted = true;
                await AttemptLoginAsync();
            }
        };
    }

    // ------------------------------------------------------------------
    // Usuarios recientes
    // ------------------------------------------------------------------
    private void RefreshAccountsList()
    {
        UsersList.ItemsSource = null;
        UsersList.ItemsSource = _settings.Accounts;
        UsersToggle.Visibility = _settings.Accounts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPickAccount(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SavedAccount account }) return;
        ShowServer(account.ServerUrl);
        UserBox.Text = account.Username;
        SetPassword(CredentialVault.Unprotect(account.ProtectedPassword) ?? "");
        UsersToggle.IsChecked = false;
        (CurrentPassword().Length > 0 ? (UIElement)LoginButton : PasswordBox).Focus();
    }

    private void OnDeleteAccount(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SavedAccount account }) return;
        _settings.RemoveAccount(account);
        RefreshAccountsList();
        if (_settings.Accounts.Count == 0)
            UsersToggle.IsChecked = false;
        e.Handled = true; // que el clic no seleccione la cuenta que se está borrando
    }

    // ------------------------------------------------------------------
    // Servidores recientes (lista del campo Dirección)
    // ------------------------------------------------------------------
    private void RefreshServersList()
    {
        var servers = _settings.Servers ?? [];
        ServersList.ItemsSource = null;
        ServersList.ItemsSource = servers;
        ServersToggle.Visibility = servers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPickServer(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SavedServer server }) return;
        ShowServer(server.Url);
        ServersToggle.IsChecked = false;
        // La contraseña escrita o recordada era para el servidor anterior: se
        // trae la recordada de este usuario en este servidor, o se pide.
        var account = _settings.FindAccount(server.Url, UserBox.Text.Trim());
        SetPassword(CredentialVault.Unprotect(account?.ProtectedPassword) ?? "");
        (UserBox.Text.Trim().Length == 0 ? (UIElement)UserBox
            : CurrentPassword().Length > 0 ? LoginButton : PasswordBox).Focus();
    }

    private void OnDeleteServer(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SavedServer server }) return;
        _settings.RemoveServer(server);
        RefreshServersList();
        if (_settings.Servers is not { Count: > 0 })
            ServersToggle.IsChecked = false;
        e.Handled = true; // que el clic no seleccione el servidor que se está quitando
    }

    // ------------------------------------------------------------------
    // Servidor (tras el engranaje): dirección y puerto; el protocolo lo
    // detecta ServerProtocol al ingresar. Lo que se guarda sigue siendo una
    // sola URL (con su protocolo), igual que antes, para que las cuentas y
    // contraseñas recordadas sigan calzando con su servidor.
    // ------------------------------------------------------------------
    private const int DefaultServerPort = 443;
    private bool _fillingServer;
    /// <summary>La URL con la que se está intentando ingresar (ya con su protocolo detectado).</summary>
    private string? _attemptUrl;

    /// <summary>Reparte una URL guardada en dirección y puerto.</summary>
    private void ShowServer(string? url)
    {
        _fillingServer = true;
        try
        {
            if (TryParseServer(url, out _, out string host, out int port))
            {
                HostBox.Text = host;
                PortBox.Text = port.ToString();
            }
            else
            {
                HostBox.Text = url?.Trim() ?? "";
                PortBox.Text = DefaultServerPort.ToString();
            }
        }
        finally
        {
            _fillingServer = false;
        }
        UpdateServerSummary();
    }

    private void OnServerPartChanged(object sender, RoutedEventArgs e)
    {
        // Durante InitializeComponent un campo dispara antes de que existan los demás.
        if (_fillingServer || HostBox is null || PortBox is null || ServerSummary is null) return;
        // Pegaron la dirección completa (https://…:5090): se reparte en sus partes.
        if (ReferenceEquals(sender, HostBox) && HostBox.Text.Contains("://") &&
            TryParseServer(HostBox.Text, out _, out _, out _))
        {
            string pasted = HostBox.Text;
            Dispatcher.BeginInvoke(() =>
            {
                ShowServer(pasted);
                HostBox.CaretIndex = HostBox.Text.Length;
            });
            return;
        }
        UpdateServerSummary();
    }

    /// <summary>La dirección con el protocolo con que se ingresó la última vez, o "por detectar".</summary>
    private void UpdateServerSummary()
    {
        if (!TryReadServer(out string host, out int port, out _))
        {
            ServerSummary.Text = "Servidor sin configurar";
            return;
        }
        ServerSummary.Text = KnownUrl(host, port) ?? $"{ServerProtocol.Compose(Uri.UriSchemeHttps, host, port)[8..]} · protocolo automático";
    }

    /// <summary>"https://host:puerto" → sus partes. Sin protocolo se asume https; sin
    /// puerto, el del protocolo (443/80), como hacía el campo único de antes.</summary>
    private static bool TryParseServer(string? url, out string scheme, out string host, out int port)
    {
        scheme = Uri.UriSchemeHttps;
        host = "";
        port = DefaultServerPort;
        if (!ClientSettings.TryParseServerUrl(url, out var uri)) return false;
        scheme = uri.Scheme;
        host = uri.Host;
        port = uri.Port;
        return true;
    }

    /// <summary>Dirección y puerto escritos, validados (sin protocolo: se detecta al ingresar).</summary>
    private bool TryReadServer(out string host, out int port, out string error)
    {
        host = HostBox.Text.Trim().TrimEnd('/');
        port = 0;
        if (host.Length == 0)
        {
            error = "Indique la dirección IP o el nombre del servidor.";
            return false;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out port) || port is < 1 or > 65535)
        {
            error = "El puerto del servidor debe ser un número entre 1 y 65535.";
            return false;
        }
        if (!Uri.TryCreate(ServerProtocol.Compose(Uri.UriSchemeHttps, host, port), UriKind.Absolute, out var uri)
            || uri.AbsolutePath != "/" || uri.Host.Length == 0)
        {
            error = "La dirección del servidor no es válida: escriba solo la IP o el nombre (sin rutas ni espacios).";
            return false;
        }
        error = "";
        return true;
    }

    /// <summary>La URL guardada (ajustes o cuentas) para esa dirección y puerto, con su protocolo.</summary>
    private string? KnownUrl(string host, int port)
    {
        string? Match(string? url) =>
            TryParseServer(url, out _, out string h, out int p) && p == port &&
            string.Equals(h, host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) ? url : null;
        return Match(_settings.ServerUrl)
               ?? (_settings.Servers ?? []).Select(s => s.Url).Concat(_settings.Accounts.Select(a => a.ServerUrl))
                   .Select(Match).FirstOrDefault(u => u is not null);
    }

    /// <summary>
    /// ¿Esa dirección ya se usó por https en este equipo? (cuenta o ajuste guardado
    /// con https, o una llave confiada). Entonces no se acepta que pase a http.
    /// </summary>
    private bool KnownHttps(string host, int port)
    {
        string https = ServerProtocol.Compose(Uri.UriSchemeHttps, host, port);
        bool saved = new[] { _settings.ServerUrl, _settings.LastSession?.ServerUrl }
            .Concat(_settings.Accounts.Select(a => a.ServerUrl))
            .Concat((_settings.Servers ?? []).Select(s => s.Url))
            .Any(u => string.Equals(u?.TrimEnd('/'), https, StringComparison.OrdinalIgnoreCase));
        return saved || (Uri.TryCreate(https, UriKind.Absolute, out var uri) && ServerTrust.IsPinned(uri));
    }

    // ------------------------------------------------------------------
    // Mostrar / ocultar contraseña (PasswordBox no puede mostrar texto:
    // se alterna con un TextBox espejo)
    // ------------------------------------------------------------------
    private void OnShowPassword(object sender, RoutedEventArgs e)
    {
        PasswordPlain.Text = PasswordBox.Password;
        PasswordPlain.Visibility = Visibility.Visible;
        PasswordBox.Visibility = Visibility.Collapsed;
    }

    private void OnHidePassword(object sender, RoutedEventArgs e)
    {
        PasswordBox.Password = PasswordPlain.Text;
        PasswordBox.Visibility = Visibility.Visible;
        PasswordPlain.Visibility = Visibility.Collapsed;
    }

    private string CurrentPassword() =>
        ShowPasswordToggle.IsChecked == true ? PasswordPlain.Text : PasswordBox.Password;

    private void SetPassword(string value)
    {
        PasswordBox.Password = value;
        PasswordPlain.Text = value;
    }

    // ------------------------------------------------------------------
    // Recordar / auto-login (el auto-login implica recordar la contraseña)
    // ------------------------------------------------------------------
    private void OnAutoLoginChecked(object sender, RoutedEventArgs e) => RememberCheck.IsChecked = true;

    private void OnRememberUnchecked(object sender, RoutedEventArgs e) => AutoLoginCheck.IsChecked = false;

    private void RestoreRememberedPassword()
    {
        if (!_settings.RememberPassword) return;
        var account = _settings.FindAccount(_settings.ServerUrl, _settings.Username);
        if (account is not null && CredentialVault.Unprotect(account.ProtectedPassword) is { Length: > 0 } password)
            SetPassword(password);
    }

    /// <summary>Botón "Cerrar" del formulario (ventana sin marco de Windows).</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Arrastre de la ventana sin marco: un clic sostenido sobre la
    /// tarjeta (los campos y botones se quedan con su propio clic).</summary>
    private void OnDragWindow(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    // ------------------------------------------------------------------
    // Inicio de sesión
    // ------------------------------------------------------------------
    private async void OnLoginClick(object sender, RoutedEventArgs e) => await AttemptLoginAsync();

    private async Task AttemptLoginAsync()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        LoginButton.IsEnabled = false;
        LoginButton.Content = "Conectando…";
        try
        {
            if (!TryReadServer(out string host, out int port, out string serverError))
            {
                ErrorText.Text = serverError;
                ErrorText.Visibility = Visibility.Visible;
                ServerSettingsToggle.IsChecked = true;
                HostBox.Focus();
                return;
            }
            var detection = await ServerProtocol.DetectAsync(host, port, KnownHttps(host, port));
            if (detection.Url is not { } serverUrl)
            {
                ErrorText.Text = detection.Error;
                ErrorText.Visibility = Visibility.Visible;
                ServerSettingsToggle.IsChecked = true;
                return;
            }
            _attemptUrl = serverUrl;
            ServerSummary.Text = serverUrl;
            string username = UserBox.Text.Trim();
            string password = CurrentPassword();

            var api = new ApiClient();
            await api.LoginAsync(serverUrl, username, password);

            // Preferencias y cuenta reciente. La contraseña solo se conserva si
            // se pidió recordarla, y siempre cifrada con DPAPI (ClientSettings).
            _settings.RememberPassword = RememberCheck.IsChecked == true;
            _settings.AutoLogin = AutoLoginCheck.IsChecked == true;
            _settings.RecordLogin(serverUrl, username, _settings.RememberPassword ? password : null);

            var hub = new VmsHubClient(serverUrl, () => api.Token, token => api.RenewSessionAsync(token));
            try { await hub.StartAsync(); }
            catch { /* el hub reintenta; el cliente funciona igual sin tiempo real */ }

            var main = new MainWindow(new MainViewModel(api, hub));
            Application.Current.MainWindow = main;
            main.Show();
            Close();
        }
        catch (ApiException) when (_attemptUrl is { } url && ServerTrust.TakeRejected(url) is { } untrusted)
        {
            // Certificado que este equipo no conoce (el propio del servidor, o cambió): lo decide el operador.
            if (AskTrust(untrusted))
            {
                ServerTrust.Trust(untrusted);
                await AttemptLoginAsync();
                return;
            }
            ErrorText.Text = "No se ingresó: no se confió en el certificado del servidor.";
            ErrorText.Visibility = Visibility.Visible;
            ServerSettingsToggle.IsChecked = true;
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            // Sin respuesta del servidor (no un usuario o clave errados): lo
            // más probable es la dirección, así que se muestra para revisarla.
            if (ex.StatusCode is null) ServerSettingsToggle.IsChecked = true;
        }
        finally
        {
            LoginButton.IsEnabled = true;
            LoginButton.Content = "Ingresar";
        }
    }

    /// <summary>Pregunta si se confía en un certificado que este equipo no conoce.</summary>
    private bool AskTrust(ServerTrust.Untrusted cert)
    {
        string fingerprint = ServerTrust.Format(cert.Spki);
        string message = cert.KeyChanged
            ? $"¡ATENCIÓN! El servidor {cert.Authority} se presenta con una llave DISTINTA de la que este equipo conocía.\n\n" +
              "Puede ser un certificado nuevo instalado por el administrador, pero también alguien interceptando la conexión. " +
              "Confíe solo si el administrador le confirma la huella nueva.\n\n"
            : $"El servidor {cert.Authority} se presenta con un certificado que este equipo no conoce " +
              "(lo normal la primera vez con el certificado propio del servidor).\n\n";
        message += $"Emitido para: {cert.Subject}\nVence: {cert.NotAfter:dd-MM-yyyy}\n\nHuella de la llave (SHA-256):\n{fingerprint}\n\n" +
                   "Compárela con la de Sistema → Certificado HTTPS en el panel web del servidor. ¿Confiar en este servidor?";
        return AppMessageBox.Show(this, message, cert.KeyChanged ? "La llave del servidor cambió" : "Certificado del servidor",
            MessageBoxButton.YesNo, cert.KeyChanged ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
