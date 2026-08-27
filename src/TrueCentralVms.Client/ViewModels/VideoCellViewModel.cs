using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlyleafLib;
using FlyleafLib.MediaPlayer;
using TrueCentralVms.Client.Services;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Una celda de la grilla de video: dueña de su propio Player de FlyleafLib.
/// Cada apertura pide una concesión NUEVA al servidor (los tokens expiran):
/// nunca se reutiliza una URL antigua.
/// </summary>
public partial class VideoCellViewModel : ObservableObject, IDisposable
{
    private readonly ApiClient _api;
    private ChannelNode? _assigned;
    private int _openSequence;

    public Player Player { get; }

    [ObservableProperty] private string? _title;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isEmpty = true;
    /// <summary>Número del cuadro en la grilla (1..N), visible en su barra.</summary>
    [ObservableProperty] private int _index;
    /// <summary>Canal asignado al cuadro (null = libre). Lo usa el panel PTZ.</summary>
    [ObservableProperty] private ChannelNode? _assignedChannel;

    public VideoCellViewModel(ApiClient api)
    {
        _api = api;

        var config = new Config();
        config.Player.AutoPlay = true;
        config.Audio.Enabled = false; // el audio por celda llega en un hito posterior
        // RTSP por TCP y con la menor latencia posible para vigilancia en vivo.
        config.Demuxer.FormatOpt["rtsp_transport"] = "tcp";
        config.Demuxer.FormatOpt["fflags"] = "nobuffer";
        Player = new Player(config);

        Player.OpenCompleted += (_, e) =>
        {
            if (!e.Success && _assigned is not null)
                Status = "No se pudo abrir el video: " + (e.Error ?? "error desconocido");
            else if (e.Success)
                Status = "";
        };
    }

    /// <summary>Abre un canal en esta celda (perfil main/sub según el tamaño de la grilla).</summary>
    public async Task OpenAsync(ChannelNode node, StreamProfile profile)
    {
        int sequence = ++_openSequence;
        _assigned = node;
        AssignedChannel = node;
        IsEmpty = false;
        Title = $"{node.Device.Name} · {node.Channel.Name}";
        Status = "Conectando…";

        try
        {
            var grant = await _api.RequestStreamAsync(node.Device.Id, node.Channel.RtspChannel, profile);
            if (sequence != _openSequence) return; // la celda ya se reasignó
            Player.OpenAsync(grant.RtspUrl);
        }
        catch (ApiException ex)
        {
            if (sequence == _openSequence)
                Status = ex.Message;
        }
    }

    [RelayCommand]
    public void Clear()
    {
        _openSequence++;
        _assigned = null;
        AssignedChannel = null;
        Player.Stop();
        Title = null;
        Status = "";
        IsEmpty = true;
    }

    public void Dispose()
    {
        _openSequence++;
        Player.Dispose();
    }
}
