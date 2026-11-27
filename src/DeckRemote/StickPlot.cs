using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DeckRemote;

internal sealed class StickPlot : Control
{
    private static readonly IBrush PlotBackground = Brush.Parse("#142029");
    private static readonly IBrush IdleDot = Brush.Parse("#788E9C");
    private static readonly IBrush ActiveDot = Brush.Parse("#26EDB6");
    private static readonly Pen Axis = new(Brush.Parse("#435764"), 1.5);
    private static readonly Pen Rim = new(Brush.Parse("#687E8D"), 2.5);
    private static readonly Pen ActiveStroke = new(ActiveDot, 2.5);
    private short x;
    private short y;
    private bool pressed;

    public void Update(short horizontal, short vertical, bool clicked)
    {
        if ((x, y, pressed) != (horizontal, vertical, clicked))
        {
            (x, y, pressed) = (horizontal, vertical, clicked);
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Max(0, Math.Min(center.X, center.Y) - 2);
        if (radius < 12)
        {
            return;
        }
        context.DrawEllipse(PlotBackground, pressed ? ActiveStroke : Rim, center, radius, radius);
        context.DrawLine(Axis, new Point(center.X - radius, center.Y), new Point(center.X + radius, center.Y));
        context.DrawLine(Axis, new Point(center.X, center.Y - radius), new Point(center.X, center.Y + radius));

        var horizontal = Normalize(x);
        var vertical = -Normalize(y);
        var distance = Math.Sqrt(horizontal * horizontal + vertical * vertical);
        var dotRadius = Math.Min(8.5, radius * 0.08);
        var travel = (radius - dotRadius - 2.5) / Math.Max(1, distance);
        var position = new Point(center.X + horizontal * travel, center.Y + vertical * travel);
        var moving = distance * travel >= 2;
        if (moving)
        {
            context.DrawLine(ActiveStroke, center, position);
        }
        context.DrawEllipse(moving || pressed ? ActiveDot : IdleDot, null, position, dotRadius, dotRadius);
    }

    private static double Normalize(short value) => value / (value < 0 ? 32768d : 32767d);
}
