using System.Windows;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

public partial class LoginWindow : Window
{
    private readonly ClientSettings _settings = ClientSettings.Load();

    public LoginWindow()
    {
        InitializeComponent();
        ServerBox.Text = _settings.ServerUrl;
        UserBox.Text = _settings.Username;
        (string.IsNullOrEmpty(_settings.Username) ? (UIElement)UserBox : PasswordBox).Focus();
    }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
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

            var api = new ApiClient();
            await api.LoginAsync(serverUrl, UserBox.Text.Trim(), PasswordBox.Password);

            _settings.ServerUrl = serverUrl;
            _settings.Username = UserBox.Text.Trim();
            _settings.Save();

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
