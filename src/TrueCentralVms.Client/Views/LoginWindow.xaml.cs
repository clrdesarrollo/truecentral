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

    public LoginWindow()
    {
        InitializeComponent();
        ServerBox.Text = _settings.ServerUrl;
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
        ServerBox.Text = account.ServerUrl;
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
            string serverUrl = ServerBox.Text.Trim();
            if (!serverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                serverUrl = "http://" + serverUrl;
            string username = UserBox.Text.Trim();
            string password = CurrentPassword();

            var api = new ApiClient();
            await api.LoginAsync(serverUrl, username, password);

            // Preferencias y cuenta reciente. La contraseña solo se conserva si
            // se pidió recordarla, y siempre cifrada con DPAPI (ClientSettings).
            _settings.RememberPassword = RememberCheck.IsChecked == true;
            _settings.AutoLogin = AutoLoginCheck.IsChecked == true;
            _settings.RecordLogin(serverUrl, username, _settings.RememberPassword ? password : null);

            var hub = new VmsHubClient(serverUrl, api.Token!);
            try { await hub.StartAsync(); }
            catch { /* el hub reintenta; el cliente funciona igual sin tiempo real */ }

            var main = new MainWindow(new MainViewModel(api, hub));
            Application.Current.MainWindow = main;
            main.Show();
            Close();
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            LoginButton.IsEnabled = true;
            LoginButton.Content = "Ingresar";
        }
    }
}
