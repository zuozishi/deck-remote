using Avalonia.Controls;
using System.Globalization;

namespace DeckRemote;

internal sealed partial class StickPreview : UserControl
{
    private short previousX;
    private short previousY;
    private bool wasPressed;

    public StickPreview() => InitializeComponent();

    public void SetLabel(string label) => StickLabel.Text = label;

    public void Update(short x, short y, bool pressed)
    {
        if (x != previousX)
        {
            previousX = x;
            AxisX.Text = x.ToString(CultureInfo.InvariantCulture);
        }
        if (y != previousY)
        {
            previousY = y;
            AxisY.Text = y.ToString(CultureInfo.InvariantCulture);
        }
        if (pressed != wasPressed)
        {
            wasPressed = pressed;
            StickLabel.Classes.Set("pressed", pressed);
            Card.Classes.Set("pressed", pressed);
        }
        Plot.Update(x, y, pressed);
    }
}
