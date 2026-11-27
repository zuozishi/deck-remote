using Avalonia.Controls;
using Avalonia.Media;
using System.Globalization;

namespace DeckRemote;

internal sealed partial class DeckStickReadout : UserControl
{
    private static readonly IBrush Idle = Brush.Parse("#A8BCC9");
    private static readonly IBrush Active = Brush.Parse("#26EDB6");
    private short previousX;
    private short previousY;
    private bool wasPressed;

    public DeckStickReadout() => InitializeComponent();

    public void SetLabel(string label) => ClickLabel.Text = label;

    public void Update(short x, short y, bool pressed)
    {
        if (x != previousX)
        {
            AxisX.Text = "X " + x.ToString(CultureInfo.InvariantCulture);
            previousX = x;
        }
        if (y != previousY)
        {
            AxisY.Text = "Y " + y.ToString(CultureInfo.InvariantCulture);
            previousY = y;
        }
        if (pressed != wasPressed)
        {
            ClickLabel.Foreground = pressed ? Active : Idle;
            wasPressed = pressed;
        }
        Plot.Update(x, y, pressed);
    }
}
