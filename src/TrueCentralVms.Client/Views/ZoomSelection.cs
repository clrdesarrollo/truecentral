using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TrueCentralVms.Client.Views;

/// <summary>
/// Cursor de lupa del modo zoom. Se dibuja y se arma en memoria (formato .CUR)
/// en vez de distribuir un archivo: así acompaña al tema del producto y no
/// agrega un recurso binario más al instalador. Si algo falla, cae en la cruz
/// del sistema — el modo tiene que seguir funcionando igual.
/// </summary>
internal static class MagnifierCursor
{
    private const int Size = 32;
    private const int HotspotX = 13, HotspotY = 13; // el centro del lente

    private static Cursor? _cursor;

    public static Cursor Instance => _cursor ??= Build() ?? Cursors.Cross;

    private static Cursor? Build()
    {
        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // Contorno oscuro debajo: la lupa tiene que leerse tanto sobre
                // una escena clara como sobre una nocturna.
                var halo = new Pen(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), 4.5)
                    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var stroke = new Pen(Brushes.White, 2.2)
                    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                var lens = new Point(HotspotX, HotspotY);

                dc.DrawEllipse(null, halo, lens, 8, 8);
                dc.DrawLine(halo, new Point(19, 19), new Point(28, 28));
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)), stroke, lens, 8, 8);
                dc.DrawLine(stroke, new Point(19, 19), new Point(28, 28));
            }

            var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            int stride = Size * 4;
            byte[] pixels = new byte[stride * Size];
            bitmap.CopyPixels(pixels, stride, 0);

            return new Cursor(BuildCursorStream(pixels, stride));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Arma un .CUR de 32 bpp en memoria (la transparencia la da el canal alfa).</summary>
    private static Stream BuildCursorStream(byte[] pixels, int stride)
    {
        // La máscara AND va en 1 bpp con filas alineadas a 4 bytes; queda toda
        // en cero porque el alfa de la imagen es el que decide qué se ve.
        int maskStride = (Size + 31) / 32 * 4;
        int imageBytes = 40 + stride * Size + maskStride * Size;

        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);

        // ICONDIR: reservado, tipo 2 = cursor, 1 imagen
        writer.Write((ushort)0);
        writer.Write((ushort)2);
        writer.Write((ushort)1);

        // ICONDIRENTRY (en cursores, los "planos" llevan el punto activo)
        writer.Write((byte)Size);
        writer.Write((byte)Size);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)HotspotX);
        writer.Write((ushort)HotspotY);
        writer.Write(imageBytes);
        writer.Write(22); // los datos arrancan después de las cabeceras

        // BITMAPINFOHEADER: alto doble (imagen + máscara), 32 bpp sin comprimir
        writer.Write(40);
        writer.Write(Size);
        writer.Write(Size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(stride * Size);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);

        // Los DIB se guardan de abajo hacia arriba.
        for (int y = Size - 1; y >= 0; y--)
            writer.Write(pixels, y * stride, stride);
        writer.Write(new byte[maskStride * Size]);

        writer.Flush();
        stream.Position = 0;
        return stream;
    }
}

/// <summary>
/// Recuadro elástico que el operador dibuja sobre el video para elegir el área
/// a acercar.
///
/// Va en un <see cref="Popup"/> y no en la interfaz de la celda porque el
/// video de Flyleaf se pinta en ventanas propias superpuestas: cualquier
/// elemento del árbol visual normal quedaría TAPADO por el video. El popup es
/// su propia ventana, se dibuja encima y no recibe mouse, así que no altera
/// en nada el clic, el doble clic ni el arrastrar-y-soltar que ya funcionan
/// sobre el video.
/// </summary>
internal sealed class ZoomRubberBand
{
    private readonly Border _box = new()
    {
        BorderThickness = new Thickness(1.6),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
        Background = new SolidColorBrush(Color.FromArgb(0x30, 0x3B, 0x82, 0xF6)),
        CornerRadius = new CornerRadius(2),
        IsHitTestVisible = false,
    };

    private readonly Popup _popup;

    public ZoomRubberBand()
    {
        _popup = new Popup
        {
            Placement = PlacementMode.Absolute,
            AllowsTransparency = true,
            StaysOpen = true,
            Focusable = false,
            IsHitTestVisible = false,
            Child = _box,
        };
    }

    /// <summary>Dibuja el recuadro entre dos puntos de la ventana de video.</summary>
    public void Update(Window videoWindow, Point from, Point to)
    {
        double width = Math.Abs(to.X - from.X);
        double height = Math.Abs(to.Y - from.Y);
        if (width < 1 || height < 1)
        {
            Hide();
            return;
        }

        var origin = new Point(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y));
        // PointToScreen entrega píxeles físicos; el popup se ubica en unidades
        // independientes del dispositivo.
        var dpi = VisualTreeHelper.GetDpi(videoWindow);
        var screen = videoWindow.PointToScreen(origin);

        _box.Width = width;
        _box.Height = height;
        _popup.HorizontalOffset = screen.X / dpi.DpiScaleX;
        _popup.VerticalOffset = screen.Y / dpi.DpiScaleY;
        _popup.IsOpen = true;
    }

    public void Hide() => _popup.IsOpen = false;
}
