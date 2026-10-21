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
    // Servidor (tras el engranaje): protocolo, dirección y puerto. Lo que se
    // guarda sigue siendo una sola URL, igual que antes, para que las cuentas
    // y contraseñas recordadas sigan calzando con su servidor.
    // ------------------------------------------------------------------
    private const int DefaultServerPort = 443;
    private bool _fillingServer;

    /// <summary>Reparte una URL guardada en protocolo, dirección y puerto.</summary>
    private void ShowServer(string? url)
    {
        _fillingServer = true;
        try
        {
            if (TryParseServer(url, out string scheme, out string host, out int port))
            {
                ProtocolBox.SelectedIndex = scheme == Uri.UriSchemeHttps ? 1 : 0;
                HostBox.Text = host;
                PortBox.Text = port.ToString();
            }
            else
            {
                ProtocolBox.SelectedIndex = 1;
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
        // Durante InitializeComponent la lista dispara antes de que existan los demás campos.
        if (_fillingServer || HostBox is null || PortBox is null || ServerSummary is null) return;
        // Cambiar de protocolo con el puerto propio del otro (80 ↔ 443) lo cambia también.
        if (ReferenceEquals(sender, ProtocolBox))
        {
            string port = PortBox.Text.Trim();
            if (ProtocolBox.SelectedIndex == 1 && port == "80") PortBox.Text = "443";
            else if (ProtocolBox.SelectedIndex == 0 && port == "443") PortBox.Text = "80";
        }
        // Pegaron la dirección completa (http://…:5090): se reparte en sus partes.
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

    private void UpdateServerSummary() =>
        ServerSummary.Text = TryComposeServerUrl(out string url, out _) ? url : "Servidor sin configurar";

    /// <summary>"https://host:puerto" → sus partes. Sin protocolo se asume https; sin
    /// puerto, el del protocolo (443/80), como hacía el campo único de antes.</summary>
    private static bool TryParseServer(string? url, out string scheme, out string host, out int port)
    {
        scheme = Uri.UriSchemeHttps;
        host = "";
        port = DefaultServerPort;
        if (string.IsNullOrWhiteSpace(url)) return false;
        string text = url.Trim();
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || uri.Host.Length == 0)
            return false;
        scheme = uri.Scheme;
        host = uri.Host;
        port = uri.Port;
        return true;
    }

    /// <summary>
    /// Arma la URL con las tres partes. El puerto se omite si es el propio del
    /// protocolo (80 con http, 443 con https): así queda idéntica a la que se
    /// guardaba antes y las cuentas recordadas siguen encontrándose.
    /// </summary>
    private bool TryComposeServerUrl(out string url, out string error)
    {
        url = "";
        string scheme = ProtocolBox.SelectedIndex == 1 ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        string host = HostBox.Text.Trim().TrimEnd('/');
        if (host.Length == 0)
        {
            error = "Indique la dirección IP o el nombre del servidor.";
            return false;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port is < 1 or > 65535)
        {
            error = "El puerto del servidor debe ser un número entre 1 y 65535.";
            return false;
        }
        // IPv6 sin corchetes (fe80::1) → [fe80::1]
        if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]";
        bool defaultPort = (scheme == Uri.UriSchemeHttp && port == 80) || (scheme == Uri.UriSchemeHttps && port == 443);
        string candidate = defaultPort ? $"{scheme}://{host}" : $"{scheme}://{host}:{port}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/" || uri.Host.Length == 0)
        {
            error = "La dirección del servidor no es válida: escriba solo la IP o el nombre (sin rutas ni espacios).";
            return false;
        }
        url = candidate;
        error = "";
        return true;
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
            if (!TryComposeServerUrl(out string serverUrl, out string serverError))
            {
                ErrorText.Text = serverError;
                ErrorText.Visibility = Visibility.Visible;
                ServerSettingsToggle.IsChecked = true;
                HostBox.Focus();
                return;
            }
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
        catch (ApiException) when (TryComposeServerUrl(out string url, out _) && ServerTrust.TakeRejected(url) is { } untrusted)
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
