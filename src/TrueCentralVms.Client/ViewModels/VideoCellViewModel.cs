using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Una celda de la grilla de video: dueña de su propio Player de FlyleafLib.
/// Cada apertura (incluidos los reintentos) pide una concesión NUEVA al
/// servidor: los tokens expiran y nunca se reutiliza una URL antigua. Si el
/// stream se corta (red, equipo reiniciado, expulsión), la celda reintenta
/// sola mientras siga asignada.
/// </summary>
public partial class VideoCellViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly ApiClient _api;
    private ChannelNode? _assigned;
    private StreamProfile _profile;
    private int _openSequence;
    private bool _retryPending;

    public Player Player { get; }

    /// <summary>Se encendió el audio de este cuadro (el dueño silencia el resto).</summary>
    public event Action<VideoCellViewModel>? AudioActivated;

    [ObservableProperty] private string? _title;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isEmpty = true;
    /// <summary>Número del cuadro en la grilla (1..N), visible en su barra.</summary>
    [ObservableProperty] private int _index;
    /// <summary>Canal asignado al cuadro (null = libre). Lo usa el panel PTZ.</summary>
    [ObservableProperty] private ChannelNode? _assignedChannel;
    /// <summary>Audio del cuadro. Exclusivo: la grilla silencia los demás.</summary>
    [ObservableProperty] private bool _isAudioOn;

    public VideoCellViewModel(ApiClient api)
    {
        _api = api;

        var config = new Config();
        config.Player.AutoPlay = true;
        config.Audio.Enabled = false; // se enciende por cuadro con su botón 🔊
        // RTSP por TCP y con la menor latencia posible para vigilancia en vivo.
        config.Demuxer.FormatOpt["rtsp_transport"] = "tcp";
        config.Demuxer.FormatOpt["fflags"] = "nobuffer";
        // Arranque rápido: sin estos límites, FFmpeg puede pasar varios
        // segundos "analizando" el stream antes del primer cuadro. El SDP de
        // MediaMTX ya anuncia los parámetros del códec (sprop), así que un
        // análisis corto basta; la imagen aparece con el primer keyframe.
        config.Demuxer.FormatOpt["analyzeduration"] = "500000"; // 0,5 s (µs)
        config.Demuxer.FormatOpt["probesize"] = "524288";       // 512 KB
        Player = new Player(config);

        Player.OpenCompleted += (_, e) =>
        {
            if (_assigned is null) return;
            if (e.Success) { Status = ""; return; }
            Status = "No se pudo abrir el video: " + (e.Error ?? "error desconocido");
            ScheduleRetry(_openSequence);
        };

        // Fin de reproducción no pedido (caída de red, equipo apagado,
        // expulsión desde el panel): reintentar. Clear/Dispose/reasignación
        // invalidan la secuencia ANTES de detener, así que no reintentan.
        Player.PlaybackStopped += (_, e) =>
        {
            if (_assigned is null) return;
            if (!e.Success) Status = "Sin señal — reintentando…";
            ScheduleRetry(_openSequence);
        };
    }

    /// <summary>Abre un canal en esta celda (perfil main/sub según el tamaño de la grilla).</summary>
    public async Task OpenAsync(ChannelNode node, StreamProfile profile)
    {
        int sequence = ++_openSequence;
        _assigned = node;
        _profile = profile;
        AssignedChannel = node;
        IsEmpty = false;
        Title = $"{node.Device.Name} · {node.Channel.Name}";
        Status = "Conectando…";
        await ConnectAsync(sequence);
    }

    /// <summary>Pide una concesión nueva y abre la URL RTSP resultante.</summary>
    private async Task ConnectAsync(int sequence)
    {
        if (_assigned is not { } node) return;
        try
        {
            var grant = await _api.RequestStreamAsync(node.Device.Id, node.Channel.RtspChannel, _profile);
            if (sequence != _openSequence) return; // la celda ya se reasignó
            Player.OpenAsync(grant.RtspUrl);
        }
        catch (ApiException ex)
        {
            if (sequence != _openSequence) return;
            Status = ex.Message;
            ScheduleRetry(sequence);
        }
    }

    /// <summary>
    /// Reintento con espera fija. Se descarta si la celda cambió de asignación
    /// o si el player ya volvió a estar activo (una reasignación detiene el
    /// video anterior y ese stop también dispara PlaybackStopped).
    /// </summary>
    private async void ScheduleRetry(int sequence)
    {
        if (_retryPending) return;
        _retryPending = true;
        try { await Task.Delay(RetryDelay); }
        finally { _retryPending = false; }

        if (sequence != _openSequence || _assigned is null) return;
        if (Player.Status is FlyleafLib.MediaPlayer.Status.Playing
            or FlyleafLib.MediaPlayer.Status.Opening
            or FlyleafLib.MediaPlayer.Status.Paused) return;
        Status = "Reconectando…";
        await ConnectAsync(sequence);
    }

    [RelayCommand]
    private void ToggleAudio() => IsAudioOn = !IsAudioOn;

    partial void OnIsAudioOnChanged(bool value)
    {
        // Flyleaf abre/cierra el stream de audio en caliente con este flag:
        // apagado no se decodifica (ahorra CPU en grillas grandes).
        Player.Config.Audio.Enabled = value;
        if (value) AudioActivated?.Invoke(this);
    }

    [RelayCommand]
    public void Clear()
    {
        _openSequence++;
        _assigned = null;
        AssignedChannel = null;
        IsAudioOn = false;
        Player.Stop();
        Title = null;
        Status = "";
        IsEmpty = true;
    }

    public void Dispose()
    {
        _openSequence++;
        _assigned = null;
        Player.Dispose();
    }
}
