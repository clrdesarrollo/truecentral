using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using FlyleafLib.Controls.WPF;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Client.ViewModels;
using TrueCentralVms.Core.Contracts;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Lo que la ventana de alarma necesita del puesto para la repetición del
/// evento: si el operador puede ver y descargar grabaciones, el Centro de
/// descargas y cómo abrir Reproducción en una cámara y una hora.
/// </summary>
public sealed record AlertReplayHost(bool CanPlayback, bool CanExport, DownloadCenterViewModel Downloads,
    Func<ChannelNode, DateTime, Task> OpenInPlayback);

/// <summary>
/// Pestaña «Repetición del evento»: el video grabado de unos segundos antes a
/// unos segundos después del evento, en bucle, con una marca en el instante.
///
/// El servidor dice si el clip ya está (<see cref="AlertClipStates"/>): al
/// abrirse el aviso el equipo todavía está grabando el después, así que
/// mientras tanto se ve el vivo de la misma cámara con una cuenta regresiva
/// y se vuelve a preguntar; cuando la grabación lo cubre, la pestaña pasa sola
/// a la repetición. Se pide al grabador solo la cámara elegida y solo con la
/// pestaña a la vista: las sesiones de reproducción de un DVR son pocas.
/// </summary>
public partial class AlertWindow
{
    private AlertReplayHost? _replayHost;
    private List<ChannelNode> _replayCameras = [];
    /// <summary>Repetición en curso (cuadro de reproducción de la cámara elegida).</summary>
    private PlaybackCellViewModel? _replayCell;
    /// <summary>Vivo de la cámara mientras el equipo termina de grabar el después.</summary>
    private VideoCellViewModel? _replayLive;
    private AlertClipDto? _clip;
    /// <summary>Aviso y cámara de la repetición preparada (cambiar cualquiera la reinicia).</summary>
    private (long AlertId, int ChannelId)? _replayKey;
    /// <summary>Cada preparación nueva invalida las respuestas de las anteriores.</summary>
    private int _replaySequence;
    private DispatcherTimer? _replayTimer;
    private DateTime _nextPoll;
    private int _remaining;
    private double _replaySpeed = 1;
    private bool _replayPaused;
    private bool _replayDragging;
    /// <summary>Último punto visto de la reproducción: distingue el fin del clip de una apertura fallida.</summary>
    private double _replayLastOffset;
    private bool _fillingReplayCameras;
    /// <summary>
    /// Hasta cuándo no se mira la posición tras pedir una apertura: mientras
    /// llega la nueva, el reproductor sigue informando la posición vieja (al
    /// final del clip) y el bucle volvería a pedirla en cada tic, cancelando
    /// cada apertura con la siguiente.
    /// </summary>
    private DateTime _replayOpenedAt;
    /// <summary>Lo que dice la franja mientras se reproduce (hora del evento, si es parcial).</summary>
    private string _replayDescription = "";

    private bool ReplayAvailable(WorkflowAlertDto alert) => alert.HasClip && _replayHost?.CanPlayback == true;

    /// <summary>Muestra u oculta la pestaña según el aviso y llena su selector de cámaras.</summary>
    private void RenderReplay(WorkflowAlertDto alert)
    {
        bool available = ReplayAvailable(alert);
        ReplayTab.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        ReplaySaveButton.Visibility = _replayHost?.CanExport == true ? Visibility.Visible : Visibility.Collapsed;
        if (!available)
        {
            StopReplay();
            if (ReferenceEquals(Tabs.SelectedItem, ReplayTab))
            {
                _choosingTab = true;
                try { Tabs.SelectedItem = PhotosTab; }
                finally { _choosingTab = false; }
            }
            return;
        }

        var cameras = alert.ChannelIds.Select(_findChannel).Where(n => n is not null).Select(n => n!).ToList();
        if (!cameras.Select(c => c.Channel.Id).SequenceEqual(_replayCameras.Select(c => c.Channel.Id)))
        {
            _replayCameras = cameras;
            _fillingReplayCameras = true;
            try
            {
                ReplayCameraBox.ItemsSource = cameras;
                ReplayCameraBox.SelectedIndex = cameras.Count > 0 ? 0 : -1;
            }
            finally { _fillingReplayCameras = false; }
        }
        ReplayCameraBox.Visibility = cameras.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (cameras.Count == 0)
        {
            StopReplay();
            ShowReplayMessage("Las cámaras del aviso ya no están disponibles en este cliente.");
            return;
        }
        _ = StartReplayAsync();
    }

    /// <summary>
    /// Prepara la repetición de la cámara elegida si la pestaña está a la
    /// vista; si no, la suelta (no se le pide al grabador lo que nadie mira).
    /// </summary>
    private async Task StartReplayAsync()
    {
        bool visible = ReferenceEquals(Tabs.SelectedItem, ReplayTab) && IsVisible && ReplayTab.Visibility == Visibility.Visible;
        if (!visible || Current is not { } alert || ReplayCameraBox.SelectedItem is not ChannelNode camera)
        {
            StopReplay();
            return;
        }
        if (_replayKey == (alert.Id, camera.Channel.Id)) return; // ya está preparada o en curso

        StopReplay();
        _replayKey = (alert.Id, camera.Channel.Id);
        int sequence = ++_replaySequence;
        _replaySpeed = 1;
        _replayPaused = false;
        SyncSpeedButtons();
        ReplayStateText.Text = "Buscando la grabación del evento…";
        ShowReplayMessage("");
        EnsureReplayTimer();
        await PollClipAsync(alert, camera, sequence);
    }

    /// <summary>Pregunta al servidor por el clip y actúa según su estado.</summary>
    private async Task PollClipAsync(WorkflowAlertDto alert, ChannelNode camera, int sequence)
    {
        AlertClipDto clip;
        try
        {
            clip = await _api.GetAlertClipAsync(alert.Id, camera.Channel.Id);
        }
        catch (ApiException ex)
        {
            if (sequence != _replaySequence) return;
            ReplayStateText.Text = "";
            ShowLive(null);
            ShowReplayMessage(ex.StatusCode == 403
                ? "Sus roles no incluyen ver grabaciones. El video en vivo sigue disponible en su pestaña."
                : "No se pudo preparar la repetición: " + ex.Message);
            _nextPoll = DateTime.MaxValue;
            return;
        }
        if (sequence != _replaySequence) return;
        _clip = clip;

        switch (clip.State)
        {
            case AlertClipStates.Recording:
                _remaining = clip.RemainingSeconds;
                _nextPoll = DateTime.Now.AddSeconds(Math.Max(3, Math.Min(clip.RemainingSeconds, 10)));
                RenderRecordingState();
                ShowLive(camera);
                break;
            case AlertClipStates.Ready:
            case AlertClipStates.Partial:
                _nextPoll = DateTime.MaxValue;
                ShowLive(null);
                await PlayClipAsync(alert, camera, clip, sequence);
                break;
            default:
                _nextPoll = DateTime.MaxValue;
                ShowLive(null);
                ReplayStateText.Text = "";
                ShowReplayMessage(clip.Message ?? "El equipo no tiene grabación de ese momento.");
                break;
        }
    }

    private void RenderRecordingState()
    {
        ReplayStateText.Text = _remaining > 0
            ? $"El equipo está grabando el después del evento: la repetición estará en {_remaining} s. Mientras, en vivo."
            : "Esperando que el equipo deje disponible la grabación. Mientras, en vivo.";
    }

    /// <summary>El vivo de la cámara (o nada) mientras se espera la grabación.</summary>
    private void ShowLive(ChannelNode? camera)
    {
        if (camera is null)
        {
            ReplayLiveHost.Visibility = Visibility.Collapsed;
            BindingOperations.ClearBinding(ReplayLiveHost, FlyleafHost.PlayerProperty);
            _replayLive?.Dispose();
            _replayLive = null;
            return;
        }
        if (_replayLive is not null) return;
        ShowReplayMessage("");
        _replayLive = new VideoCellViewModel(_api, _settings) { Index = 1 };
        // El cuadro en vivo puede cambiar de Player (cambio suave de stream): se enlaza.
        ReplayLiveHost.SetBinding(FlyleafHost.PlayerProperty, new Binding(nameof(VideoCellViewModel.Player)) { Source = _replayLive });
        ReplayLiveHost.Visibility = Visibility.Visible;
        var live = _replayLive;
        _ = OpenLiveAsync(live, camera);
    }

    private static async Task OpenLiveAsync(VideoCellViewModel live, ChannelNode camera)
    {
        try { await live.OpenAsync(camera, StreamProfile.Sub); }
        catch (Exception) { /* el cuadro muestra su propio error; la repetición sigue esperando */ }
    }

    private async Task PlayClipAsync(WorkflowAlertDto alert, ChannelNode camera, AlertClipDto clip, int sequence)
    {
        _replayCell = new PlaybackCellViewModel(_api, _settings, camera) { AlertId = alert.Id };
        ReplayHost.Player = _replayCell.Player;
        ReplayHost.Visibility = Visibility.Visible;
        ShowReplayMessage("");

        double total = Math.Max(1, (clip.End - clip.Start).TotalSeconds);
        double eventOffset = Math.Clamp((clip.EventTime - clip.Start).TotalSeconds, 0, total);
        ReplaySlider.Maximum = total;
        ReplaySlider.Value = 0;
        ReplayBeforeColumn.Width = new GridLength(Math.Max(eventOffset, 0.001), GridUnitType.Star);
        ReplayAfterColumn.Width = new GridLength(Math.Max(total - eventOffset, 0.001), GridUnitType.Star);
        ReplayMarkerGrid.Visibility = eventOffset > 0 && eventOffset < total ? Visibility.Visible : Visibility.Collapsed;
        ReplayStartText.Text = $"−{eventOffset:0} s";
        ReplayEndText.Text = $"+{total - eventOffset:0} s";
        ReplayControls.IsEnabled = true;
        ReplayOpenPlaybackButton.IsEnabled = true;
        _replayDescription = (clip.State == AlertClipStates.Partial ? clip.Message + " " : "") +
                             $"Evento a las {clip.EventTime:HH:mm:ss}" +
                             (clip.ExactTime ? "" : " (hora aproximada: la informó otro equipo)") + ".";
        ReplayStateText.Text = _replayDescription;

        if (sequence != _replaySequence) return;
        await OpenClipAtAsync(0);
    }

    /// <summary>Abre el clip desde un segundo del clip, a la velocidad elegida.</summary>
    private async Task OpenClipAtAsync(double offset)
    {
        if (_replayCell is not { } cell || _clip is not { } clip) return;
        var from = clip.Start.AddSeconds(Math.Clamp(offset, 0, (clip.End - clip.Start).TotalSeconds - 0.5));
        _replayLastOffset = offset;
        _replayPaused = false;
        _replayOpenedAt = DateTime.Now;
        ReplayPlayButton.Content = "\uE769";
        await cell.OpenAtAsync(from, clip.End, _replaySpeed);
    }

    private void EnsureReplayTimer()
    {
        if (_replayTimer is not null) return;
        _replayTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _replayTimer.Tick += (_, _) => OnReplayTick();
        _replayTimer.Start();
    }

    private DateTime _lastCountdown;

    private void OnReplayTick()
    {
        // Esperando la grabación: cuenta regresiva y nueva consulta a su hora.
        if (_clip?.State == AlertClipStates.Recording && _replayKey is not null)
        {
            if (DateTime.Now - _lastCountdown >= TimeSpan.FromSeconds(1))
            {
                _lastCountdown = DateTime.Now;
                if (_remaining > 0) _remaining--;
                RenderRecordingState();
            }
            if (DateTime.Now >= _nextPoll && Current is { } alert && ReplayCameraBox.SelectedItem is ChannelNode camera)
            {
                _nextPoll = DateTime.MaxValue; // una consulta a la vez
                _ = PollClipAsync(alert, camera, _replaySequence);
            }
            return;
        }

        if (_replayCell is not { } cell || _clip is not { } clip) return;
        // Recién pedida una apertura: la posición todavía es la de antes.
        if (cell.IsOpening || DateTime.Now - _replayOpenedAt < TimeSpan.FromSeconds(1.5)) return;
        double total = (clip.End - clip.Start).TotalSeconds;
        if (cell.Playhead is { } at)
        {
            double offset = Math.Clamp((at - clip.Start).TotalSeconds, 0, total);
            _replayLastOffset = offset;
            if (!_replayDragging) ReplaySlider.Value = offset;
            // Reproduciendo: la franja vuelve a decir la hora del evento (un
            // corte o un error anterior ya no vale).
            if (ReplayStateText.Text != _replayDescription) ReplayStateText.Text = _replayDescription;
            double eventOffset = (clip.EventTime - clip.Start).TotalSeconds;
            double fromEvent = offset - eventOffset;
            ReplayPositionText.Text = $"{(fromEvent < 0 ? "−" : "+")}{Math.Abs(fromEvent):0.0} s del evento  ·  {at:HH:mm:ss}";
            // Bucle: al llegar al final del clip vuelve al comienzo.
            if (!_replayPaused && offset >= total - 0.4) _ = OpenClipAtAsync(0);
            return;
        }
        // El equipo cerró la entrega al terminar el rango pedido: también es
        // el final del clip. Una apertura que falló (sin haber avanzado) no se
        // reintenta en bucle: queda su mensaje.
        if (!cell.IsPlaying && !cell.IsOpening && !_replayPaused)
        {
            if (_replayLastOffset >= total - 2) _ = OpenClipAtAsync(0);
            else if (cell.Status is { Length: > 0 } status && status != "Fin del tramo.") ReplayStateText.Text = status;
        }
    }

    /// <summary>Suelta todo lo de la repetición (al cambiar de aviso, de pestaña o cerrar).</summary>
    private void StopReplay()
    {
        _replaySequence++;
        _replayKey = null;
        _clip = null;
        _nextPoll = DateTime.MaxValue;
        ShowLive(null);
        ReplayHost.Visibility = Visibility.Collapsed;
        ReplayHost.Player = null;
        _replayCell?.Dispose();
        _replayCell = null;
        ReplayControls.IsEnabled = false;
        ReplaySlider.Value = 0;
        ReplayPositionText.Text = ReplayStartText.Text = ReplayEndText.Text = "";
        _replayTimer?.Stop();
        _replayTimer = null;
    }

    private void ShowReplayMessage(string text)
    {
        ReplayMessageText.Text = text;
        ReplayMessageText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncSpeedButtons()
    {
        ReplayHalfSpeed.IsChecked = _replaySpeed == 0.5;
        ReplayNormalSpeed.IsChecked = _replaySpeed == 1;
        ReplayDoubleSpeed.IsChecked = _replaySpeed == 2;
    }

    // ------------------------------------------------------------------
    // Controles
    // ------------------------------------------------------------------

    private void OnReplayCameraChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingReplayCameras) return;
        _ = StartReplayAsync();
    }

    private void OnReplayPlayPause(object sender, RoutedEventArgs e)
    {
        if (_replayCell is not { } cell) return;
        if (!cell.IsPlaying && !_replayPaused)
        {
            _ = OpenClipAtAsync(_replayLastOffset);
            return;
        }
        _replayPaused = !_replayPaused;
        if (_replayPaused) cell.Pause();
        else cell.Resume();
        ReplayPlayButton.Content = _replayPaused ? "\uE768" : "\uE769";
    }

    private void OnReplayRestart(object sender, RoutedEventArgs e) => _ = OpenClipAtAsync(0);

    private void OnReplaySpeed(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } ||
            !double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double speed))
            return;
        _replaySpeed = speed;
        SyncSpeedButtons();
        // La velocidad viaja en la concesión: se vuelve a pedir desde donde va.
        _ = OpenClipAtAsync(_replayLastOffset);
    }

    private void OnReplayDragStarted(object sender, DragStartedEventArgs e) => _replayDragging = true;

    private void OnReplayDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _replayDragging = false;
        _ = OpenClipAtAsync(ReplaySlider.Value);
    }

    private void OnReplaySliderClick(object sender, MouseButtonEventArgs e)
    {
        // Un clic en la línea (no un arrastre del pulgar) salta a ese punto.
        if (_replayDragging || e.OriginalSource is Thumb) return;
        _ = OpenClipAtAsync(ReplaySlider.Value);
    }

    private async void OnReplayOpenPlayback(object sender, RoutedEventArgs e)
    {
        if (_replayHost is not { } host || _clip is not { } clip || ReplayCameraBox.SelectedItem is not ChannelNode camera) return;
        try { await host.OpenInPlayback(camera, clip.Start); }
        catch (Exception ex) { ReplayStateText.Text = "No se pudo abrir Reproducción: " + ex.Message; }
    }

    private void OnReplaySave(object sender, RoutedEventArgs e)
    {
        if (_replayHost is not { CanExport: true } host || _clip is not { } clip ||
            ReplayCameraBox.SelectedItem is not ChannelNode camera) return;
        var dialog = new ExportDialog(_replayCameras, camera, clip.Start, clip.End, _settings, host.Downloads) { Owner = this };
        dialog.ShowDialog();
    }
}
