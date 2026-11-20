using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PulseGuard.Helpers;

/// <summary>
/// Generates an in-memory vector-rendered icon for the system tray so no external .ico asset is required.
/// </summary>
public static class TrayIconHelper
{
    public static ImageSource CreatePulseIcon()
    {
        const int size = 32;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bgBrush = new SolidColorBrush(Color.FromRgb(18, 22, 32));
            var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(0, 178, 255)), 2.0);
            dc.DrawRoundedRectangle(bgBrush, borderPen, new Rect(2, 2, size - 4, size - 4), 7, 7);

            var pulsePen = new Pen(new SolidColorBrush(Color.FromRgb(0, 229, 160)), 2.4)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(6, 17), false, false);
                ctx.LineTo(new Point(11, 17), true, false);
                ctx.LineTo(new Point(14, 9), true, false);
                ctx.LineTo(new Point(18, 23), true, false);
                ctx.LineTo(new Point(21, 14), true, false);
                ctx.LineTo(new Point(23, 17), true, false);
                ctx.LineTo(new Point(26, 17), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pulsePen, geometry);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}

