using System.Globalization;

namespace TrueCentralVms.Core.Drivers;

/// <summary>
/// Desde dónde empezar una grabación que el equipo no posiciona por la URL.
/// Hikvision y Dahua llevan el instante en la propia URL; Axis (y el
/// estándar ONVIF) reproducen una grabación entera y se posicionan con la
/// cabecera RTSP <c>Range: npt=&lt;segundos&gt;-</c> del PLAY. El driver
/// marca ese desplazamiento en el fragmento de la URL (que nunca viaja por
/// la red) y cada consumidor —MediaMTX, la exportación con FFmpeg, el relé de
/// velocidad— lo separa y lo pide a su manera.
/// </summary>
public static class PlaybackSource
{
    private const string Marker = "#tc-npt=";

    /// <summary>La URL con el desplazamiento desde el comienzo de la grabación.</summary>
    public static string WithStartOffset(string url, TimeSpan offset)
    {
        string clean = Split(url).Url;
        if (offset <= TimeSpan.Zero) return clean;
        return clean + Marker + offset.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>URL limpia (sin la marca) y el desplazamiento pedido, si lo hay.</summary>
    public static (string Url, TimeSpan? Offset) Split(string source)
    {
        int at = source.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0) return (source, null);
        string url = source[..at];
        return double.TryParse(source[(at + Marker.Length)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0
            ? (url, TimeSpan.FromSeconds(seconds))
            : (url, null);
    }

    /// <summary>Valor de la cabecera Range para el PLAY ("npt=0-" sin desplazamiento).</summary>
    public static string RangeHeader(TimeSpan? offset) =>
        $"npt={(offset ?? TimeSpan.Zero).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}-";
}
