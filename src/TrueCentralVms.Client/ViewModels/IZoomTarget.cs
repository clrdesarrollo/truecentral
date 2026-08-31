using System.Windows;

namespace TrueCentralVms.Client.ViewModels;

/// <summary>
/// Cuadro de video que admite zoom digital. Lo implementan los cuadros de la
/// Vista en Vivo y de la Reproducción para que el modo zoom (el recuadro que
/// se dibuja con el mouse) sea exactamente el mismo en los dos módulos.
/// </summary>
public interface IZoomTarget
{
    /// <summary>false cuando el cuadro no tiene video que acercar.</summary>
    bool CanDigitalZoom { get; }

    /// <summary>Acerca el área marcada (píxeles físicos de la ventana de video).</summary>
    void DigitalZoomToArea(Rect areaPx);

    /// <summary>Acerca o aleja un paso dejando quieto el punto bajo el cursor.</summary>
    void DigitalZoomStepAt(Point pointPx, bool zoomIn);

    /// <summary>Vuelve a 1×.</summary>
    void ResetDigitalZoom();
}
