using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Recorte de la patente sacado de la escena, para las lecturas que llegan sin
/// él (cámaras configuradas sin "recorte de placa" o firmwares que no lo
/// mandan por el SDK). Se corta el recuadro que informó la propia cámara con
/// un margen, para que se lea la placa entera aunque el recuadro venga justo.
/// Si algo falla devuelve null y la lectura queda solo con la escena.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PlateCutout
{
    /// <summary>Margen alrededor del recuadro, en proporción de su ancho y su alto.</summary>
    private const double MarginX = 0.15, MarginY = 0.35;

    /// <param name="x">Recuadro de la patente normalizado 0..1 sobre la escena (como <see cref="Core.Drivers.PlateRecognition"/>).</param>
    public static byte[]? FromScene(byte[] scene, double x, double y, double width, double height)
    {
        if (width <= 0 || height <= 0 || x < 0 || y < 0 || x + width > 1.001 || y + height > 1.001) return null;
        try
        {
            using var input = new MemoryStream(scene);
            using var source = Image.FromStream(input);
            int left = (int)Math.Floor((x - width * MarginX) * source.Width);
            int top = (int)Math.Floor((y - height * MarginY) * source.Height);
            int right = (int)Math.Ceiling((x + width * (1 + MarginX)) * source.Width);
            int bottom = (int)Math.Ceiling((y + height * (1 + MarginY)) * source.Height);
            var area = Rectangle.FromLTRB(Math.Max(0, left), Math.Max(0, top),
                Math.Min(source.Width, right), Math.Min(source.Height, bottom));
            // Un recuadro de pocos píxeles no es una patente legible.
            if (area.Width < 16 || area.Height < 8) return null;

            using var crop = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(crop))
                g.DrawImage(source, new Rectangle(0, 0, area.Width, area.Height), area, GraphicsUnit.Pixel);

            using var output = new MemoryStream();
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 92L);
            crop.Save(output, codec, parameters);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }
}
