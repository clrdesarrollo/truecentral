using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Client.ViewModels;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Diálogo de exportación de grabaciones (estilo grabador profesional): se
/// elige cámara, rango exacto de fecha/hora, carpeta de destino, formato y si
/// el tramo va en una sola cápsula o dividido en varios archivos. Al aceptar,
/// cada archivo entra como una descarga al Centro de descargas (el diálogo no
/// descarga nada por sí mismo).
/// </summary>
public partial class ExportDialog : Window
{
    /// <summary>Límite del servidor por descarga (tramo de una solicitud).</summary>
    private static readonly TimeSpan MaxPerFile = TimeSpan.FromHours(2);
    /// <summary>Tope sano de un pedido dividido (un día completo).</summary>
    private static readonly TimeSpan MaxTotal = TimeSpan.FromHours(24);
    private const int MaxFiles = 48;

    private static readonly string[] DateFormats = ["dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy"];
    private static readonly string[] TimeFormats = [@"hh\:mm\:ss", @"h\:mm\:ss", @"hh\:mm", @"h\:mm"];

    private readonly ClientSettings _settings;
    private readonly DownloadCenterViewModel _downloads;
    private bool _ready; // los eventos de los controles disparan antes de terminar el constructor

    /// <summary>Cuántas descargas dejó encoladas (0 = canceló).</summary>
    public int EnqueuedCount { get; private set; }

    public ExportDialog(IReadOnlyList<ChannelNode> channels, ChannelNode? preselected,
        DateTime from, DateTime to, ClientSettings settings, DownloadCenterViewModel downloads)
    {
        _settings = settings;
        _downloads = downloads;
        InitializeComponent();

        CameraBox.ItemsSource = channels;
        CameraBox.SelectedItem = preselected ?? channels.FirstOrDefault();

        FromDateBox.Text = from.ToString("dd-MM-yyyy");
        FromTimeBox.Text = from.ToString("HH:mm:ss");
        ToDateBox.Text = to.ToString("dd-MM-yyyy");
        ToTimeBox.Text = to.ToString("HH:mm:ss");
        FolderBox.Text = settings.EffectiveExportFolder;

        FormatBox.SelectedIndex = settings.ExportFormat.Equals("mkv", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (settings.ExportSplitMinutes > 0)
        {
            SplitRadio.IsChecked = true;
            SplitBox.SelectedItem = SplitBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag as string == settings.ExportSplitMinutes.ToString());
        }
        SplitBox.SelectedItem ??= SplitBox.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == "30");

        _ready = true;
        RefreshSummary();
    }

    // ------------------------------------------------------------------
    // Lectura y validación del formulario
    // ------------------------------------------------------------------

    private ChannelNode? SelectedChannel => CameraBox.SelectedItem as ChannelNode;

    private string SelectedFormat => (FormatBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "mp4";

    private int SplitMinutes => SplitRadio.IsChecked == true &&
        int.TryParse((SplitBox.SelectedItem as ComboBoxItem)?.Tag as string, out int minutes) ? minutes : 0;

    private static DateTime? ParseMoment(string date, string time)
    {
        if (!DateTime.TryParseExact(date.Trim(), DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var day))
            return null;
        if (!TimeSpan.TryParseExact(time.Trim(), TimeFormats, CultureInfo.InvariantCulture, out var clock))
            return null;
        return day.Date + clock;
    }

    /// <summary>Valida el formulario completo; null = correcto.</summary>
    private string? Validate(out ChannelNode channel, out DateTime from, out DateTime to)
    {
        channel = null!;
        from = to = default;

        if (SelectedChannel is not { } selected)
            return "Elija la cámara a exportar.";
        channel = selected;

        if (ParseMoment(FromDateBox.Text, FromTimeBox.Text) is not { } start)
            return "El inicio no es válido: use fecha dd-mm-aaaa y hora hh:mm:ss.";
        if (ParseMoment(ToDateBox.Text, ToTimeBox.Text) is not { } end)
            return "El fin no es válido: use fecha dd-mm-aaaa y hora hh:mm:ss.";
        if (end <= start)
            return "El fin debe ser posterior al inicio.";
        from = start;
        to = end;

        var duration = end - start;
        if (SplitMinutes == 0 && duration > MaxPerFile)
            return "Una sola cápsula admite hasta 2 horas: acorte el tramo o elija dividir en varios archivos.";
        if (duration > MaxTotal)
            return "El rango no puede superar 24 horas.";
        if (SplitMinutes > 0 && FileCount(duration) > MaxFiles)
            return $"La división genera más de {MaxFiles} archivos: use archivos más largos o acorte el rango.";

        if (string.IsNullOrWhiteSpace(FolderBox.Text))
            return "Indique la carpeta de destino.";
        return null;
    }

    private int FileCount(TimeSpan duration) =>
        SplitMinutes == 0 ? 1 : (int)Math.Ceiling(duration.TotalMinutes / SplitMinutes);

    // ------------------------------------------------------------------
    // Interacción
    // ------------------------------------------------------------------

    private void OnAnyInputChanged(object sender, RoutedEventArgs e) => RefreshSummary();

    /// <summary>Resumen vivo del pie: cuántos archivos y cuánto video en total.</summary>
    private void RefreshSummary()
    {
        if (!_ready) return;
        SplitBox.IsEnabled = SplitRadio.IsChecked == true;

        if (ParseMoment(FromDateBox.Text, FromTimeBox.Text) is not { } from ||
            ParseMoment(ToDateBox.Text, ToTimeBox.Text) is not { } to || to <= from)
        {
            SummaryText.Text = "";
            return;
        }
        var duration = to - from;
        int files = FileCount(duration);
        SummaryText.Text = $"Se exportará{(files == 1 ? "" : "n")} {files} archivo{(files == 1 ? "" : "s")} " +
                           $"{SelectedFormat.ToUpperInvariant()} · {(int)duration.TotalMinutes} min de video en total";
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Carpeta de destino de la exportación",
            InitialDirectory = Directory.Exists(FolderBox.Text.Trim())
                ? FolderBox.Text.Trim()
                : _settings.EffectiveRecordingFolder,
        };
        if (dialog.ShowDialog() == true)
            FolderBox.Text = dialog.FolderName;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (Validate(out var channel, out var from, out var to) is { } error)
        {
            ErrorText.Text = error;
            ErrorBox.Visibility = Visibility.Visible;
            return;
        }

        string folder = FolderBox.Text.Trim();
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"No se pudo usar la carpeta de destino: {ex.Message}";
            ErrorBox.Visibility = Visibility.Visible;
            return;
        }

        // Un pedido = una descarga por tramo; dividido, cada archivo lleva la
        // fecha/hora de SU inicio (así el nombre identifica la evidencia).
        string format = SelectedFormat;
        var chunk = SplitMinutes == 0 ? to - from : TimeSpan.FromMinutes(SplitMinutes);
        for (var start = from; start < to; start += chunk)
        {
            var end = start + chunk > to ? to : start + chunk;
            string file = Path.Combine(folder,
                $"{SafeName(channel.Device.Name)}_{SafeName(channel.Channel.Name)}_{start:yyyyMMdd_HHmmss}.{format}");
            _downloads.Enqueue(channel, start, end, file, format);
            EnqueuedCount++;
        }

        // El diálogo recuerda lo elegido para la próxima exportación.
        _settings.ExportFolder = folder;
        _settings.ExportFormat = format;
        _settings.ExportSplitMinutes = SplitMinutes;
        _settings.Save();

        DialogResult = true;
        Close();
    }

    /// <summary>Nombre de equipo/canal apto para un archivo de Windows.</summary>
    private static string SafeName(string name)
    {
        var clean = name.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
            clean = clean.Replace(invalid, '_');
        clean = clean.Replace(' ', '_');
        return clean.Length > 0 ? clean : "canal";
    }
}
