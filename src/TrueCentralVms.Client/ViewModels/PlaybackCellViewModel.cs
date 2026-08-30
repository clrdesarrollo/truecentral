using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Contracts;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Un canal dentro del módulo Reproducción: dueño de su player, de sus
/// segmentos del día y de su concesión. Los cuadros comparten la línea de
/// tiempo y la aguja: al posicionarse, TODOS abren el mismo instante (la
/// reproducción multicanal va sincronizada por hora del equipo, no por
/// relojes de video independientes).
/// </summary>
public partial class PlaybackCellViewModel : ObservableObject, IDisposable
{
    private readonly ApiClient _api;
    private int _openSequence;
    private double _speed = 1;

    public ChannelNode Channel { get; }
    public Player Player { get; }

    public string Title => $"{Channel.Device.Name} · {Channel.Channel.Name}";

    /// <summary>Tramos grabados del día visible (pinta su pista de la línea de tiempo).</summary>
    [ObservableProperty] private List<RecordingSegmentDto> _segments = [];
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isAudioOn;

    /// <summary>
    /// false cuando el equipo no arranca exactamente en el instante pedido
    /// (ONVIF Perfil G reproduce desde el comienzo de la grabación).
    /// </summary>
    [ObservableProperty] private bool _isSeekExact = true;

    /// <summary>Hora local del equipo en la que arranca el tramo abierto: la
    /// aguja es esta hora más el reloj del player.</summary>
    public DateTime RangeStart { get; private set; }

    /// <summary>Hora que este cuadro está mostrando, o null si no reproduce.</summary>
    public DateTime? Playhead => IsPlaying && Player.CurTime > 0 ? RangeStart.AddTicks(Player.CurTime) : null;

    /// <summary>Se encendió el audio de este cuadro (el dueño silencia el resto).</summary>
    public event Action<PlaybackCellViewModel>? AudioActivated;

    /// <summary>El usuario cerró el cuadro con su ✕.</summary>
    public event Action<PlaybackCellViewModel>? CloseRequested;

    public PlaybackCellViewModel(ApiClient api, ClientSettings settings, ChannelNode channel)
    {
        _api = api;
        Channel = channel;

        var config = new Config();
        config.Player.AutoPlay = true;
        config.Audio.Enabled = false; // se enciende por cuadro con su botón
        config.Demuxer.FormatOpt["rtsp_transport"] = "tcp";
        // Arranque rápido, igual que en vivo: el análisis largo de FFmpeg
        // agregaría segundos antes del primer cuadro.
        config.Demuxer.FormatOpt["analyzeduration"] = "500000";
        config.Demuxer.FormatOpt["probesize"] = "524288";
        Player = new Player(config);
        Player.Audio.Volume = Math.Clamp(settings.DefaultVolume, 0, 100);
        Player.Config.Video.AspectRatio = settings.StretchVideo ? AspectRatio.Fill : AspectRatio.Keep;

        Player.OpenCompleted += (_, e) =>
        {
            // La velocidad se reafirma con cada apertura: el player arranca
            // siempre en 1× y el usuario espera seguir en la que eligió.
            if (e.Success && _speed != 1) ApplySpeed(_speed);
            if (e.Success) { Status = ""; return; }
            if (IsPlaying) Status = "No se pudo abrir la grabación: " + (e.Error ?? "error desconocido");
            IsPlaying = false;
        };
        Player.PlaybackStopped += (_, _) =>
        {
            if (!IsPlaying) return;
            IsPlaying = false;
            Status = "Fin del tramo.";
        };
    }

    /// <summary>Tramos grabados del canal para un día (hora local del equipo).</summary>
    public async Task LoadSegmentsAsync(DateTime date)
    {
        try
        {
            Segments = await _api.GetRecordingSegmentsAsync(Channel.Device.Id, Channel.Channel.ChannelNumber, date);
            Status = Segments.Count == 0 ? "Sin grabaciones este día." : "";
        }
        catch (ApiException ex)
        {
            Segments = [];
            Status = ex.Message;
        }
    }

    /// <summary>
    /// Abre la grabación desde una hora local del equipo. Cada apertura pide
    /// una concesión NUEVA: el servidor monta la ruta de reproducción en el
    /// media server y entrega un token corto.
    /// </summary>
    public async Task<bool> OpenAtAsync(DateTime localTime, DateTime localEnd)
    {
        int sequence = ++_openSequence;
        try
        {
            var grant = await _api.RequestPlaybackAsync(Channel.Device.Id, Channel.Channel.RtspChannel,
                localTime, localEnd);
            if (sequence != _openSequence) return false; // el usuario ya se movió a otra hora
            RangeStart = localTime;
            IsSeekExact = grant.ExactSeek;
            IsPlaying = true;
            Status = grant.ExactSeek
                ? ""
                : "El equipo reproduce desde el inicio de su grabación (ONVIF no permite posicionar).";
            Player.Config.Audio.Enabled = IsAudioOn;
            Player.OpenAsync(grant.RtspUrl);
            return true;
        }
        catch (ApiException ex)
        {
            if (sequence != _openSequence) return false;
            IsPlaying = false;
            Status = ex.Message;
            return false;
        }
    }

    public void Pause() => Player.Pause();

    public void Resume() => Player.Play();

    /// <summary>Velocidad de reproducción (1 = normal).</summary>
    public void ApplySpeed(double speed)
    {
        _speed = speed;
        try
        {
            Player.Speed = speed;
        }
        catch
        {
            // Velocidad fuera de rango del decodificador: se ignora y sigue en la actual.
        }
    }

    public void Stop()
    {
        _openSequence++;
        IsPlaying = false;
        Player.Stop();
        Status = "";
    }

    [RelayCommand]
    private void ToggleAudio()
    {
        IsAudioOn = !IsAudioOn;
        Player.Config.Audio.Enabled = IsAudioOn;
        if (IsAudioOn) AudioActivated?.Invoke(this);
    }

    /// <summary>Silencia el cuadro cuando otro toma el audio (exclusivo).</summary>
    public void MuteQuietly()
    {
        IsAudioOn = false;
        Player.Config.Audio.Enabled = false;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this);

    public void Dispose()
    {
        _openSequence++;
        Player.Dispose();
    }
}
