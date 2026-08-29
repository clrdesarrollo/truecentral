using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Módulo Reproducción: las grabaciones viven en el DVR/NVR del equipo. Se
/// elige canal y día, la línea de tiempo muestra los segmentos grabados, y el
/// clic sobre ella pide una concesión de reproducción al servidor (que monta
/// la ruta en MediaMTX) y abre el video con Flyleaf, igual que el vivo.
/// </summary>
public partial class PlaybackViewModel : ObservableObject, IDisposable
{
    private readonly ApiClient _api;
    private readonly ClientSettings _settings;
    private readonly DispatcherTimer _clock;
    private int _openSequence;
    private DateTime _rangeStart;

    public Player Player { get; }

    /// <summary>Canal en reproducción (se elige con doble clic en el árbol).</summary>
    [ObservableProperty] private ChannelNode? _channel;
    /// <summary>Día visible en la línea de tiempo (hora local del equipo).</summary>
    [ObservableProperty] private DateTime _date = DateTime.Today;
    /// <summary>Segmentos grabados del día (pinta la línea de tiempo).</summary>
    [ObservableProperty] private List<RecordingSegmentDto> _segments = [];
    [ObservableProperty] private string _status = "Elija un canal del árbol (doble clic) para ver sus grabaciones.";
    /// <summary>Hora local que se está reproduciendo (aguja de la línea de tiempo).</summary>
    [ObservableProperty] private DateTime? _playhead;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isAudioOn;
    [ObservableProperty] private bool _isLoadingSegments;

    public PlaybackViewModel(ApiClient api, ClientSettings settings)
    {
        _api = api;
        _settings = settings;

        var config = new Config();
        config.Player.AutoPlay = true;
        config.Audio.Enabled = false;
        config.Demuxer.FormatOpt["rtsp_transport"] = "tcp";
        config.Demuxer.FormatOpt["analyzeduration"] = "500000";
        config.Demuxer.FormatOpt["probesize"] = "524288";
        Player = new Player(config);
        Player.Audio.Volume = Math.Clamp(settings.DefaultVolume, 0, 100);
        Player.Config.Video.AspectRatio = settings.StretchVideo ? AspectRatio.Fill : AspectRatio.Keep;

        Player.OpenCompleted += (_, e) =>
        {
            if (e.Success) { Status = ""; return; }
            if (IsPlaying) Status = "No se pudo abrir la grabación: " + (e.Error ?? "error desconocido");
            IsPlaying = false;
        };
        Player.PlaybackStopped += (_, _) =>
        {
            if (!IsPlaying) return;
            IsPlaying = false;
            Status = "Fin del tramo reproducido.";
        };

        // Aguja de la línea de tiempo: hora de inicio del rango + reloj del player.
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clock.Tick += (_, _) =>
        {
            if (IsPlaying && Player.CurTime > 0)
                Playhead = _rangeStart.AddTicks(Player.CurTime);
        };
        _clock.Start();
    }

    /// <summary>Doble clic en un canal del árbol estando en Reproducción.</summary>
    public async Task SelectChannelAsync(ChannelNode node)
    {
        Channel = node;
        Stop();
        await LoadSegmentsAsync();
    }

    partial void OnDateChanged(DateTime value) => _ = LoadSegmentsAsync();

    [RelayCommand]
    private void PreviousDay() => Date = Date.AddDays(-1);

    [RelayCommand]
    private void NextDay()
    {
        if (Date < DateTime.Today) Date = Date.AddDays(1);
    }

    [RelayCommand]
    private void GoToday() => Date = DateTime.Today;

    public async Task LoadSegmentsAsync()
    {
        if (Channel is not { } node) return;
        IsLoadingSegments = true;
        Status = $"Consultando grabaciones de \"{node.Channel.Name}\" del {Date:dd-MM-yyyy}…";
        try
        {
            Segments = await _api.GetRecordingSegmentsAsync(node.Device.Id, node.Channel.ChannelNumber, Date);
            Status = Segments.Count == 0
                ? $"Sin grabaciones el {Date:dd-MM-yyyy}. El equipo conserva según su propio disco y política."
                : $"{Segments.Count} tramo(s) grabado(s). Clic en la línea de tiempo para reproducir.";
        }
        catch (ApiException ex)
        {
            Segments = [];
            Status = ex.Message;
        }
        finally
        {
            IsLoadingSegments = false;
        }
    }

    /// <summary>Clic en la línea de tiempo: reproducir desde esa hora.</summary>
    public async Task SeekToAsync(DateTime localTime)
    {
        if (Channel is not { } node) return;
        // Fuera de un tramo grabado: saltar al inicio del siguiente tramo.
        if (!Segments.Any(s => localTime >= s.Start && localTime < s.End))
        {
            var next = Segments.Where(s => s.End > localTime).OrderBy(s => s.Start).FirstOrDefault();
            if (next is null)
            {
                Status = "No hay grabación desde esa hora en adelante.";
                return;
            }
            if (localTime < next.Start) localTime = next.Start;
        }

        int sequence = ++_openSequence;
        Status = $"Abriendo grabación de las {localTime:HH:mm:ss}…";
        try
        {
            var grant = await _api.RequestPlaybackAsync(node.Device.Id, node.Channel.RtspChannel,
                localTime, Date.AddDays(1));
            if (sequence != _openSequence) return;
            _rangeStart = localTime;
            Playhead = localTime;
            IsPlaying = true;
            IsPaused = false;
            Player.Config.Audio.Enabled = IsAudioOn;
            Player.OpenAsync(grant.RtspUrl);
        }
        catch (ApiException ex)
        {
            if (sequence == _openSequence) Status = ex.Message;
        }
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (!IsPlaying) return;
        if (IsPaused) { Player.Play(); IsPaused = false; }
        else { Player.Pause(); IsPaused = true; }
    }

    [RelayCommand]
    private void Stop()
    {
        _openSequence++;
        IsPlaying = false;
        IsPaused = false;
        Playhead = null;
        Player.Stop();
        Status = Channel is null ? "Elija un canal del árbol (doble clic) para ver sus grabaciones." : "";
    }

    [RelayCommand]
    private void ToggleAudio()
    {
        IsAudioOn = !IsAudioOn;
        Player.Config.Audio.Enabled = IsAudioOn;
    }

    public void Dispose()
    {
        _clock.Stop();
        _openSequence++;
        Player.Dispose();
    }
}
