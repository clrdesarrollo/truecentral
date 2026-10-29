using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrueCentralVms.Client.Services;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Versión nueva del cliente en el servidor: cuando la suite se actualiza deja
/// el instalador nuevo del cliente, y este puesto lo nota al abrir y cada
/// pocos minutos (el servidor se reinicia al actualizarse). El ícono de la
/// barra de título avisa; «Actualizar ahora» lo descarga, lo instala y el
/// cliente se vuelve a abrir solo (ver <see cref="ClientUpdater"/>).
/// </summary>
public partial class MainViewModel
{
    private static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromMinutes(10);
    private ClientInstallerInfo? _update;
    private DispatcherTimer? _updateTimer;

    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private bool _isUpdatePopupOpen;
    [ObservableProperty] private string _updateTitle = "";
    [ObservableProperty] private string _updateDetail = "";
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private string _updateStatus = "";

    /// <summary>La ventana cierra el cliente sin preguntar: el instalador lo reemplaza y lo vuelve a abrir.</summary>
    public event Action? ExitForUpdateRequested;

    /// <summary>Revisa ahora y cada <see cref="UpdateCheckEvery"/> (lo arranca la ventana al abrirse).</summary>
    public void StartUpdateChecks()
    {
        if (_updateTimer is not null) return;
        _updateTimer = new DispatcherTimer { Interval = UpdateCheckEvery };
        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
        _updateTimer.Start();
        _ = CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        if (IsUpdating) return;
        ClientInstallerInfo? info;
        try { info = await ClientUpdater.CheckAsync(_api); }
        catch (ApiException) { return; } // sin conexión o servidor anterior: se vuelve a mirar después
        _update = info;
        IsUpdateAvailable = info is not null;
        if (info is null) return;
        UpdateTitle = $"Versión {info.Version} disponible";
        UpdateDetail = $"Este puesto tiene la {ApiClient.ClientVersion}. La actualización se descarga del servidor" +
                       (info.SizeMb is { } mb ? $" ({mb:0} MB)" : "") +
                       ", se instala (Windows pide permiso de administrador) y el cliente se vuelve a abrir solo, " +
                       "con lo que tenía en pantalla si está activada «Volver a abrir la última sesión».";
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_update is not { } update || IsUpdating) return;
        if (Downloads.HasActiveJobs)
        {
            var answer = Views.AppMessageBox.Show(Application.Current.MainWindow,
                "Hay descargas de grabaciones en curso: actualizar cierra el cliente y las cancela.\n\n¿Actualizar de todos modos?",
                "Actualizar el cliente", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }
        IsUpdating = true;
        UpdateProgress = 0;
        UpdateStatus = "Descargando la actualización…";
        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress = p * 100;
                UpdateStatus = $"Descargando la actualización… {p:P0}";
            });
            string installer = await ClientUpdater.DownloadAsync(_api, update, progress);
            UpdateStatus = "Abriendo el instalador…";
            if (!ClientUpdater.Launch(installer))
            {
                UpdateStatus = "No se dio el permiso de administrador: la actualización quedó sin instalar.";
                return;
            }
            UpdateStatus = "Instalando: el cliente se cierra y se vuelve a abrir solo.";
            StatusMessage = UpdateStatus;
            if (Downloads.HasActiveJobs) Downloads.CancelAll();
            ExitForUpdateRequested?.Invoke();
        }
        catch (Exception ex) when (ex is ApiException or System.IO.IOException or UnauthorizedAccessException)
        {
            UpdateStatus = $"No se pudo actualizar: {ex.Message}";
        }
        finally
        {
            IsUpdating = false;
        }
    }

    private void StopUpdateChecks() => _updateTimer?.Stop();
}
