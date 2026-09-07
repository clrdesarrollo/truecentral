using System.Windows;
using Microsoft.Win32;
using TrueCentralVms.Client.Services;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Ventana de Configuración, por apartados (Video / Imagen / Sonido / Red).
/// Edita la instancia de ClientSettings del shell: al Guardar se persiste y
/// los cambios rigen de inmediato (salvo los marcados "al reiniciar").
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ClientSettings _settings;

    public SettingsWindow(ClientSettings settings, ApiClient? api = null)
    {
        InitializeComponent();
        _settings = settings;
        if (api is not null) _ = LoadLicenseAsync(api);

        RecordingFolderBox.Text = settings.RecordingFolder ?? "";
        SnapshotFolderBox.Text = settings.SnapshotFolder ?? "";
        RecordingHint.Text = $"Vacío = carpeta por defecto: {ClientSettings.DefaultRecordingFolder}";
        SnapshotHint.Text = $"Vacío = carpeta por defecto: {ClientSettings.DefaultSnapshotFolder}";

        (settings.DefaultProfile switch
        {
            "main" => ProfileMain,
            "sub" => ProfileSub,
            _ => ProfileAuto,
        }).IsChecked = true;

        (settings.SnapshotFormat.Equals("png", StringComparison.OrdinalIgnoreCase)
            ? FormatPng : FormatJpg).IsChecked = true;

        (settings.StretchVideo ? FitStretch : FitKeep).IsChecked = true;
        (settings.FitGridToDevice ? GridFit : GridStandard).IsChecked = true;

        VolumeSlider.Value = Math.Clamp(settings.DefaultVolume, 0, 100);
        TimeoutBox.Text = settings.ApiTimeoutSeconds.ToString();
    }

    /// <summary>Apartado Licencia: solo lectura del estado que informa el servidor.</summary>
    private async Task LoadLicenseAsync(ApiClient api)
    {
        try
        {
            var s = await api.GetLicenseAsync();
            string state = s.State switch
            {
                TrueCentralVms.Core.Contracts.LicenseState.Active => "Licencia activa",
                TrueCentralVms.Core.Contracts.LicenseState.Trial => "Período de prueba",
                TrueCentralVms.Core.Contracts.LicenseState.GracePeriod => "Licencia en período de gracia",
                TrueCentralVms.Core.Contracts.LicenseState.Restricted => "Sistema restringido por licencia",
                _ => "Sin licencia",
            };
            LicenseStateText.Text = s.LicenseKey is null ? state : $"{state} — {s.LicenseKey}";
            LicenseWarningText.Text = s.Warning ?? "";
            LicenseWarningText.Visibility = string.IsNullOrEmpty(s.Warning) ? Visibility.Collapsed : Visibility.Visible;
            var detail = new List<string>();
            if (s.CustomerName is { Length: > 0 }) detail.Add($"Cliente: {s.CustomerName}");
            if (s.Package is { Length: > 0 }) detail.Add($"Package: {s.Package}");
            detail.Add(s.ExpiresAt is { } exp ? $"Vence: {exp.ToLocalTime():yyyy-MM-dd}" : (s.LicenseKey is null ? "" : "Perpetua"));
            if (s.Mode == "ONLINE" && s.LastValidatedAt is { } v) detail.Add($"Última validación en línea: {v.ToLocalTime():yyyy-MM-dd HH:mm}");
            detail.Add($"Servidor: {s.Hostname} ({s.HardwareId}) v{s.ServerVersion}");
            LicenseDetailText.Text = string.Join("   ·   ", detail.Where(d => d.Length > 0));
            LicenseModulesText.Text = string.Join(Environment.NewLine, s.Modules.Select(m =>
                m.Quota is { } q
                    ? $"{m.Name,-34} {(m.Enabled ? $"{m.InUse ?? 0} de {q} {m.Unit}" : "no incluido")}"
                    : $"{m.Name,-34} {(m.Enabled ? "incluido" : "no incluido")}"));
        }
        catch (ApiException ex)
        {
            LicenseStateText.Text = "No se pudo consultar la licencia";
            LicenseDetailText.Text = ex.Message;
        }
    }

    private void OnSectionChanged(object sender, RoutedEventArgs e)
    {
        if (SectionVideo is null) return; // aún inicializando el XAML
        SectionVideo.Visibility = NavVideo.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SectionImagen.Visibility = NavImagen.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SectionSonido.Visibility = NavSonido.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SectionRed.Visibility = NavRed.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SectionLicencia.Visibility = NavLicencia.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeLabel is not null) VolumeLabel.Text = $"{(int)e.NewValue}";
    }

    private void OnBrowseRecording(object sender, RoutedEventArgs e) =>
        BrowseFolder(RecordingFolderBox, ClientSettings.DefaultRecordingFolder);

    private void OnBrowseSnapshot(object sender, RoutedEventArgs e) =>
        BrowseFolder(SnapshotFolderBox, ClientSettings.DefaultSnapshotFolder);

    private static void BrowseFolder(System.Windows.Controls.TextBox target, string fallback)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Elegir carpeta",
            InitialDirectory = target.Text.Trim().Length > 0 && System.IO.Directory.Exists(target.Text.Trim())
                ? target.Text.Trim() : fallback,
        };
        if (dialog.ShowDialog() == true)
            target.Text = dialog.FolderName;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TimeoutBox.Text.Trim(), out int timeout) || timeout < 5 || timeout > 120)
        {
            ErrorText.Text = "El tiempo de espera de la API debe ser un número entre 5 y 120.";
            ErrorText.Visibility = Visibility.Visible;
            NavRed.IsChecked = true;
            return;
        }

        string recording = RecordingFolderBox.Text.Trim();
        string snapshot = SnapshotFolderBox.Text.Trim();
        _settings.RecordingFolder = recording.Length == 0 ? null : recording;
        _settings.SnapshotFolder = snapshot.Length == 0 ? null : snapshot;
        _settings.DefaultProfile = ProfileMain.IsChecked == true ? "main"
            : ProfileSub.IsChecked == true ? "sub" : "auto";
        _settings.SnapshotFormat = FormatPng.IsChecked == true ? "png" : "jpg";
        _settings.StretchVideo = FitStretch.IsChecked == true;
        _settings.FitGridToDevice = GridFit.IsChecked == true;
        _settings.DefaultVolume = (int)VolumeSlider.Value;
        _settings.ApiTimeoutSeconds = timeout;
        _settings.Save();
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
