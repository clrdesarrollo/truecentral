using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Server.Services;

/// <summary>
/// Deja la foto del rostro como la acepta CUALQUIER terminal antes de
/// mandarla. La foto guardada no se toca: esto se hace al escribir.
///
/// Medido el 2026-10-08 contra un DS-K1T321MFWX V3.9.20 con la misma foto de
/// 1200×1600 (de frente, bien iluminada):
/// <list type="bullet">
/// <item>JPEG progresivo —el que entrega WhatsApp— de 184 KB:
/// <c>SubpicAnalysisModelingError · saveFacePic</c>, que el panel mostraba como
/// "no pudo reconocer una cara";</item>
/// <item>la misma en JPEG normal (baseline) de 214 KB:
/// <c>badJsonContent · faceURL</c>, porque pasa su tope de unos 200 KB;</item>
/// <item>JPEG normal de 160 KB, o reducida a 600×800 (76 KB): OK.</item>
/// </list>
/// El DS-K1T323MBWX V4.23 aceptaba la progresiva: es el firmware viejo el que
/// no la lee, y el operador no tiene cómo saber en qué formato vino su foto.
///
/// Además se aplica la orientación EXIF (los teléfonos guardan la foto
/// acostada y la marcan para girarla; el navegador la muestra derecha pero el
/// equipo la recibe acostada) y se agrandan las fotos chicas (con 135×189 el
/// DS-K1T321MFWX no encuentra la cara y con el doble sí; ver el migrador).
///
/// La salida es determinista —misma foto, mismos bytes—, y no es un detalle:
/// el plan de cada equipo se compara por hash, y una foto que saliera distinta
/// en cada pasada se volvería a escribir siempre.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AccessFacePhotoNormalizer
{
    /// <summary>Lado mayor máximo: sobra para el modelado y deja la foto liviana.</summary>
    private const int MaxLongSide = 1024;

    /// <summary>Por debajo de este lado corto los DS-K1T no encuentran la cara...</summary>
    private const int MinShortSide = 300;

    /// <summary>...y se lleva a este.</summary>
    private const int UpscaleShortSide = 400;

    /// <summary>Peso máximo de lo que se manda, con margen bajo los ~200 KB del DS-K1T321MFWX.</summary>
    private const int MaxDeviceBytes = 180 * 1024;

    /// <summary>Calidades JPEG que se prueban, de mejor a peor, antes de achicar la foto.</summary>
    private static readonly long[] Qualities = [90, 85, 80, 75, 70, 60];

    /// <summary>
    /// La foto lista para los equipos: JPEG baseline, derecha, de tamaño
    /// razonable y bajo el tope de peso. Si no se puede leer se devuelve tal
    /// cual y que el equipo diga qué le pasa.
    /// </summary>
    public static AccessFaceData Normalize(AccessFaceData face)
    {
        try
        {
            using var input = new MemoryStream(face.Image);
            using var source = Image.FromStream(input);
            if (OrientationOf(source) is var flip && flip != RotateFlipType.RotateNoneFlipNone)
                source.RotateFlip(flip);

            int longSide = Math.Max(source.Width, source.Height);
            int shortSide = Math.Min(source.Width, source.Height);
            double scale = longSide > MaxLongSide ? (double)MaxLongSide / longSide
                : shortSide < MinShortSide ? (double)UpscaleShortSide / shortSide
                : 1;

            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            for (int round = 0; round < 4; round++, scale *= 0.75)
            {
                int width = Math.Max(1, (int)Math.Round(source.Width * scale));
                int height = Math.Max(1, (int)Math.Round(source.Height * scale));
                using var bitmap = Redraw(source, width, height);
                foreach (long quality in Qualities)
                {
                    byte[] jpeg = Encode(bitmap, codec, quality);
                    if (jpeg.Length <= MaxDeviceBytes) return new AccessFaceData(jpeg, "image/jpeg");
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        {
            // GDI+ no la entiende: se manda como vino.
        }
        return face;
    }

    /// <summary>
    /// La foto redibujada a ese tamaño sobre fondo blanco (un PNG con
    /// transparencia quedaría con fondo negro) y sin los metadatos de origen.
    /// </summary>
    private static Bitmap Redraw(Image source, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        // Sin esto el bicúbico mezcla el borde con lo que hay fuera de la
        // imagen y deja un marco gris.
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height,
            GraphicsUnit.Pixel, attributes);
        return bitmap;
    }

    /// <summary>JPEG baseline (es lo único que escribe GDI+) con esa calidad.</summary>
    private static byte[] Encode(Bitmap bitmap, ImageCodecInfo codec, long quality)
    {
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        using var output = new MemoryStream();
        bitmap.Save(output, codec, parameters);
        return output.ToArray();
    }

    /// <summary>Etiqueta EXIF de orientación (0x0112).</summary>
    private const int ExifOrientation = 0x0112;

    /// <summary>Cómo girar la foto para que quede como se sacó, según su EXIF.</summary>
    private static RotateFlipType OrientationOf(Image image)
    {
        if (!image.PropertyIdList.Contains(ExifOrientation)) return RotateFlipType.RotateNoneFlipNone;
        var item = image.GetPropertyItem(ExifOrientation);
        if (item?.Value is not { Length: >= 2 } value) return RotateFlipType.RotateNoneFlipNone;
        return BitConverter.ToUInt16(value, 0) switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };
    }
}
