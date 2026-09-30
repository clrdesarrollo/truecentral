using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using TrueCentralVms.Core.Drivers;

namespace TrueCentralVms.Server.Services.Workflows;

/// <summary>
/// Marca sobre la foto de un evento de cámara DÓNDE fue la detección: la
/// línea (o región) de la regla tal como está configurada en la cámara y el
/// recuadro del objeto que la cruzó. Las cámaras Hikvision no lo dibujan en
/// la captura que mandan con la alarma; la geometría viene aparte, en la
/// propia alarma (<see cref="VideoEventOverlay"/>, coordenadas 0..1).
/// Si algo falla devuelve null y se usa la foto tal cual.
/// </summary>
[SupportedOSPlatform("windows")]
public static class EventPhotoAnnotator
{
    private static readonly Color RuleColor = Color.FromArgb(255, 230, 0);      // amarillo, como la web de la cámara
    private static readonly Color TargetColor = Color.FromArgb(255, 45, 45);
    private static readonly Color Shadow = Color.FromArgb(170, 0, 0, 0);

    /// <param name="includeTarget">false cuando la foto no es la del instante del evento (captura posterior): el objeto ya se movió.</param>
    public static byte[]? Annotate(byte[] jpeg, VideoEventOverlay overlay, bool includeTarget = true)
    {
        if (overlay.Rule.Count < 2 && (!includeTarget || overlay.Target is null)) return null;
        try
        {
            using var input = new MemoryStream(jpeg);
            using var source = Image.FromStream(input);
            using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.DrawImage(source, 0, 0, source.Width, source.Height);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float w = bitmap.Width, h = bitmap.Height;
                float stroke = Math.Max(2f, Math.Min(w, h) / 220f);

                if (overlay.Rule.Count >= 2)
                {
                    var points = overlay.Rule.Select(p => new PointF(p.X * w, p.Y * h)).ToArray();
                    DrawShape(g, points, overlay.Closed, RuleColor, stroke);
                    if (!overlay.Closed)
                    {
                        // Extremos marcados, como en la web de la cámara.
                        foreach (var p in new[] { points[0], points[^1] })
                        {
                            float r = stroke * 2.2f;
                            using var fill = new SolidBrush(RuleColor);
                            using var edge = new Pen(Shadow, stroke * 0.6f);
                            g.FillEllipse(fill, p.X - r, p.Y - r, r * 2, r * 2);
                            g.DrawEllipse(edge, p.X - r, p.Y - r, r * 2, r * 2);
                        }
                    }
                    if (overlay.Label is { Length: > 0 } label)
                        DrawLabel(g, label, TopMost(points), RuleColor, stroke, w, h);
                }

                if (includeTarget && overlay.Target is { } t)
                {
                    var box = new RectangleF(t.X * w, t.Y * h, t.Width * w, t.Height * h);
                    DrawShape(g, [box.Location, new(box.Right, box.Top), new(box.Right, box.Bottom), new(box.Left, box.Bottom)],
                        true, TargetColor, stroke);
                }
            }

            using var output = new MemoryStream();
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
            bitmap.Save(output, codec, parameters);
            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Trazo con sombra oscura debajo, para que se lea sobre fondos claros y oscuros.</summary>
    private static void DrawShape(Graphics g, PointF[] points, bool closed, Color color, float stroke)
    {
        using var shadow = new Pen(Shadow, stroke * 2.2f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var pen = new Pen(color, stroke) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (closed)
        {
            g.DrawPolygon(shadow, points);
            g.DrawPolygon(pen, points);
        }
        else
        {
            g.DrawLines(shadow, points);
            g.DrawLines(pen, points);
        }
    }

    private static PointF TopMost(PointF[] points) => points.OrderBy(p => p.Y).First();

    private static void DrawLabel(Graphics g, string text, PointF anchor, Color color, float stroke, float w, float h)
    {
        using var font = new Font(FontFamily.GenericSansSerif, Math.Max(10f, stroke * 5f), FontStyle.Bold, GraphicsUnit.Pixel);
        var size = g.MeasureString(text, font);
        float pad = stroke * 1.5f;
        float x = Math.Clamp(anchor.X + stroke * 3, 0, Math.Max(0, w - size.Width - pad * 2));
        float y = Math.Clamp(anchor.Y + stroke * 3, 0, Math.Max(0, h - size.Height - pad * 2));
        using var back = new SolidBrush(Shadow);
        using var fore = new SolidBrush(color);
        g.FillRectangle(back, x, y, size.Width + pad * 2, size.Height + pad * 2);
        g.DrawString(text, font, fore, x + pad, y + pad);
    }
}
