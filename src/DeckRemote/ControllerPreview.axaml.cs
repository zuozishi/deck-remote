using Avalonia.Controls;
using DeckRemote.Core;

namespace DeckRemote;

internal sealed partial class ControllerPreview : UserControl
{
    private readonly (Buttons Button, Border Indicator)[] indicators;
    private Buttons buttons;
    private byte left;
    private byte right;

    public ControllerPreview()
    {
        InitializeComponent();
        LeftStickPosition.SetLabel("L3");
        RightStickPosition.SetLabel("R3");
        indicators =
        [
            (Buttons.Up, DpadUp), (Buttons.Down, DpadDown), (Buttons.Left, DpadLeft), (Buttons.Right, DpadRight),
            (Buttons.LeftShoulder, Lb), (Buttons.RightShoulder, Rb), (Buttons.Back, ViewKey), (Buttons.Start, MenuKey),
            (Buttons.Guide, GuideKey),
            (Buttons.A, FaceA), (Buttons.B, FaceB), (Buttons.X, FaceX), (Buttons.Y, FaceY)
        ];
    }

    public void Update(ControllerState state)
    {
        var changed = buttons ^ state.Buttons;
        if (changed != 0)
        {
            buttons = state.Buttons;
            foreach (var (button, indicator) in indicators)
            {
                if ((changed & button) != 0)
                {
                    indicator.Classes.Set("pressed", (buttons & button) != 0);
                }
            }
        }
        if (left != state.Lt)
        {
            left = state.Lt;
            LeftTrigger.Value = left;
            LeftTriggerValue.Text = FormatTrigger(left);
        }
        if (right != state.Rt)
        {
            right = state.Rt;
            RightTrigger.Value = right;
            RightTriggerValue.Text = FormatTrigger(right);
        }
        LeftStickPosition.Update(state.Lx, state.Ly, (state.Buttons & Buttons.LeftStick) != 0);
        RightStickPosition.Update(state.Rx, state.Ry, (state.Buttons & Buttons.RightStick) != 0);
    }

    private static string FormatTrigger(byte value) => $"{(value * 100 + 127) / 255}%";
}
