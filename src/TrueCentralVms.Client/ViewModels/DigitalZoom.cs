using System.Windows;
using FlyleafLib.MediaPlayer;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Zoom digital sobre el video ya decodificado: recorta el viewport en GPU
/// (no pide nada al equipo, así que no consume ancho de banda ni depende del
/// PTZ). Lo comparten la Vista en Vivo y la Reproducción.
///
/// Cómo lo entiende Flyleaf: <c>Config.Video.Zoom</c> es un PORCENTAJE
/// (100 = 1×) y <c>ZoomCenter</c> es un punto normalizado 0..1 que actúa como
/// ANCLA — el punto que se queda quieto en pantalla mientras la imagen crece,
/// no el que queda centrado. El viewport resultante es:
///
///     Viewport.X = x − ancho·(z−1)·centro.X
///
/// donde <c>x</c>/<c>ancho</c> son los del video sin zoom. De ahí sale la
/// fórmula de <see cref="ApplyArea"/>: para dejar un punto <c>p</c> (0..1 de
/// la fuente) en el centro de la vista hace falta un ancla
/// <c>(p·z − 0,5)/(z − 1)</c>.
/// </summary>
public static class DigitalZoom
{
    /// <summary>Sin zoom, en el porcentaje que usa Flyleaf.</summary>
    public const double NoZoom = 100;

    /// <summary>Tope de acercamiento: más allá la imagen es solo píxeles gigantes.</summary>
    public const double MaxZoom = 2000; // 20×

    /// <summary>Área mínima seleccionable, como fracción del video (evita que un clic accidental salte a 20×).</summary>
    private const double MinArea = 0.02;

    /// <summary>
    /// Lleva el área marcada a ocupar toda la vista.
    /// <paramref name="areaPx"/> es el rectángulo dibujado por el usuario en
    /// PÍXELES FÍSICOS de la ventana de video (el mismo espacio en que Flyleaf
    /// informa su viewport). Devuelve el zoom resultante en porcentaje.
    /// </summary>
    public static double ApplyArea(Player player, Rect areaPx)
    {
        var viewport = player.Renderer?.Viewport;
        if (viewport is not { Width: > 0, Height: > 0 } vp)
            return player.Config.Video.Zoom;

        // Píxeles de la ventana → posición 0..1 dentro de la imagen. El
        // viewport es el rectángulo que ocupa el video COMPLETO (con zoom
        // puede salirse de la ventana y tener origen negativo), así que esta
        // cuenta vale igual sobre una imagen ya acercada: los zoom se componen.
        double u1 = ((areaPx.Left - vp.X) / vp.Width).Clamp01();
        double u2 = ((areaPx.Right - vp.X) / vp.Width).Clamp01();
        double v1 = ((areaPx.Top - vp.Y) / vp.Height).Clamp01();
        double v2 = ((areaPx.Bottom - vp.Y) / vp.Height).Clamp01();

        double du = u2 - u1, dv = v2 - v1;
        if (du < MinArea || dv < MinArea)
            return player.Config.Video.Zoom; // marca demasiado chica: no se toca nada

        // Un solo factor para los dos ejes (el zoom de Flyleaf es uniforme):
        // manda el lado más exigente para que el área marcada entre completa.
        double zoom = Math.Clamp(Math.Min(1 / du, 1 / dv), 1, MaxZoom / 100);
        if (zoom <= 1.001)
        {
            Reset(player);
            return NoZoom;
        }

        var anchor = new Point(
            AnchorFor(u1 + du / 2, zoom),
            AnchorFor(v1 + dv / 2, zoom));
        player.Config.Video.SetZoomAndCenter(zoom * 100, anchor);
        return zoom * 100;
    }

    /// <summary>Cuánto acerca o aleja cada golpe de rueda.</summary>
    private const double WheelStep = 1.25;

    /// <summary>
    /// Acerca o aleja un paso dejando QUIETO el punto que está bajo el cursor
    /// (<paramref name="pointPx"/>, en píxeles físicos de la ventana de video):
    /// la imagen crece alrededor del puntero en vez de saltar al centro.
    /// Devuelve el zoom resultante en porcentaje.
    /// </summary>
    public static double StepAtPoint(Player player, bool zoomIn, Point pointPx)
    {
        var viewport = player.Renderer?.Viewport;
        if (viewport is not { Width: > 0, Height: > 0 } vp)
            return player.Config.Video.Zoom;

        double previous = Math.Max(player.Config.Video.Zoom / 100.0, 0.01);
        double zoom = Math.Clamp(previous * (zoomIn ? WheelStep : 1 / WheelStep), 1, MaxZoom / 100);
        if (zoom <= 1.001)
        {
            Reset(player);
            return NoZoom;
        }

        // Rectángulo del video SIN zoom, reconstruido desde el estado actual:
        // es el marco contra el que Flyleaf calcula el desplazamiento.
        var center = player.Config.Video.ZoomCenter;
        double w = vp.Width / previous, h = vp.Height / previous;
        double x = vp.X + w * (previous - 1) * center.X;
        double y = vp.Y + h * (previous - 1) * center.Y;

        // Punto de la imagen que hay bajo el cursor, que debe seguir ahí.
        double u = (pointPx.X - vp.X) / vp.Width;
        double v = (pointPx.Y - vp.Y) / vp.Height;

        var anchor = new Point(
            ((x + u * w * zoom - pointPx.X) / (w * (zoom - 1))).Clamp01(),
            ((y + v * h * zoom - pointPx.Y) / (h * (zoom - 1))).Clamp01());
        player.Config.Video.SetZoomAndCenter(zoom * 100, anchor);
        return zoom * 100;
    }

    /// <summary>Ancla que deja el punto <paramref name="center"/> (0..1) al medio de la vista.</summary>
    private static double AnchorFor(double center, double zoom) =>
        ((center * zoom - 0.5) / (zoom - 1)).Clamp01();

    public static void Reset(Player player)
    {
        player.Config.Video.Zoom = NoZoom;
        player.Config.Video.ZoomCenter = new Point(0.5, 0.5);
    }

    /// <summary>Texto para la barra de estado ("Zoom 2,5×" / "Zoom 1×").</summary>
    public static string Describe(double zoomPercent) =>
        zoomPercent <= NoZoom ? "Zoom 1×" : $"Zoom digital {zoomPercent / 100.0:0.#}×";

    private static double Clamp01(this double value) => Math.Clamp(value, 0, 1);
}
