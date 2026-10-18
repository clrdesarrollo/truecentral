using CommunityToolkit.Mvvm.ComponentModel;
using FlyleafLib.MediaPlayer;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Telemetría del cuadro para la apariencia Consola: la franja inferior de
/// cada cuadro muestra códec, resolución, FPS, stream y cuánto lleva con
/// imagen. Lo refresca <see cref="CellTelemetryTicker"/> (un tic por segundo);
/// con la apariencia clásica nadie lo llama y estas propiedades quedan vacías.
/// </summary>
public partial class VideoCellViewModel
{
    [ObservableProperty] private string _telemetryText = "";
    [ObservableProperty] private string _telemetryResolution = "";
    [ObservableProperty] private string _telemetryUptime = "";

    /// <summary>Player al que corresponde el "en vivo desde": un cambio de
    /// stream o un reintento traen otro player y el contador vuelve a cero.</summary>
    private Player? _telemetryPlayer;
    private DateTime _telemetrySince;

    /// <summary>El cuadro está mostrando imagen.</summary>
    internal bool IsLive => !IsEmpty && !IsConnecting &&
                            Player is { Status: FlyleafLib.MediaPlayer.Status.Playing };

    internal void RefreshTelemetry()
    {
        var player = Player;
        if (!IsLive || player is null)
        {
            _telemetryPlayer = null;
            TelemetryResolution = "";
            TelemetryUptime = "";
            TelemetryText = IsEmpty ? "" : IsConnecting ? "CONECTANDO…" : "SIN SEÑAL";
            return;
        }
        if (!ReferenceEquals(player, _telemetryPlayer))
        {
            _telemetryPlayer = player;
            _telemetrySince = DateTime.Now;
        }

        var video = player.Video;
        TelemetryResolution = video.Width > 0 ? $"{video.Width}×{video.Height}" : "";
        string stream = Profile == StreamProfile.Main ? "Principal" : "Secundario";
        TelemetryText = $"Códec: {CodecLabel(video.Codec)}   FPS: {video.FPS:0.#}   Stream: {stream}";
        TelemetryUptime = "En vivo " + Uptime(DateTime.Now - _telemetrySince);
    }

    private static string CodecLabel(string? codec) => codec?.ToLowerInvariant() switch
    {
        null or "" => "—",
        "hevc" or "h265" => "H.265",
        "h264" => "H.264",
        "mjpeg" => "MJPEG",
        var other => other.ToUpperInvariant(),
    };

    private static string Uptime(TimeSpan span) =>
        $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
}
