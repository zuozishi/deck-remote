using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeckRemote.Core;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace DeckRemote;

[SupportedOSPlatform("windows")]
internal sealed class VigemOutput : IControllerOutput, IDisposable
{
    private ViGEmClient? client;
    private IXbox360Controller? controller;
    private int motors;

    public RumbleState Rumble
    {
        get
        {
            var value = Volatile.Read(ref motors);
            return new RumbleState((byte)value, (byte)(value >> 8));
        }
    }

    public void ResetRumble() => Volatile.Write(ref motors, 0);

    public void Open()
    {
        if (controller is not null)
        {
            return;
        }
        try
        {
            client = new ViGEmClient();
            var target = client.CreateXbox360Controller();
            target.AutoSubmitReport = false;
            controller = target;
            target.FeedbackReceived += OnFeedbackReceived;
            target.Connect();
        }
        catch (Exception error)
        {
            Close();
            throw new IOException(error.GetType().Name == "VigemBusNotFoundException"
                ? "未检测到可用的 ViGEmBus 驱动"
                : $"无法创建 Xbox 手柄（{error.GetType().Name}）", error);
        }
    }

    public void Submit(ControllerState state)
    {
        var target = controller ?? throw new IOException("Xbox 手柄未就绪");
        try
        {
            target.SetButtonsFull((ushort)state.Buttons);
            target.SetSliderValue(Xbox360Slider.LeftTrigger, state.Lt);
            target.SetSliderValue(Xbox360Slider.RightTrigger, state.Rt);
            target.SetAxisValue(Xbox360Axis.LeftThumbX, state.Lx);
            target.SetAxisValue(Xbox360Axis.LeftThumbY, state.Ly);
            target.SetAxisValue(Xbox360Axis.RightThumbX, state.Rx);
            target.SetAxisValue(Xbox360Axis.RightThumbY, state.Ry);
            target.SubmitReport();
        }
        catch (Exception error)
        {
            throw new IOException("ViGEmBus 写入失败，请重新检测驱动", error);
        }
    }

    public void Close()
    {
        var target = Interlocked.Exchange(ref controller, null);
        try
        {
            if (target is not null)
            {
                target.FeedbackReceived -= OnFeedbackReceived;
                target.Disconnect();
            }
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine(error);
        }
        finally
        {
            ResetRumble();
            client?.Dispose();
            client = null;
        }
    }

    private void OnFeedbackReceived(object sender, Xbox360FeedbackReceivedEventArgs feedback)
    {
        if (ReferenceEquals(Volatile.Read(ref controller), sender))
        {
            Volatile.Write(ref motors, feedback.LargeMotor | (feedback.SmallMotor << 8));
        }
    }

    public void Dispose() => Close();

    public void CheckXInput()
    {
        var sample = new ControllerState(Buttons.A | Buttons.RightShoulder, 41, 207, -1234, 16890, 2022, -9319);
        Open();
        try
        {
            Submit(sample);
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (FindXInputSlot(sample) is uint slot)
                {
                    Submit(default);
                    if (XInputGetState(slot, out var observed) == 0 && observed.Gamepad == default)
                    {
                        Console.WriteLine($"ViGEmBus/XInput slot {slot}: state and neutral verified.");
                        VerifyRumble(slot);
                        return;
                    }
                    Submit(sample);
                }
                Thread.Sleep(50);
            }
            throw new IOException("XInput did not report the expected controller state.");
        }
        finally
        {
            Close();
        }
    }

    private void VerifyRumble(uint slot)
    {
        var test = new XInputVibration { Left = 0x6060, Right = 0x3030 };
        try
        {
            if (XInputSetState(slot, ref test) != 0)
            {
                throw new IOException("XInput did not accept a vibration request.");
            }
            WaitForRumble(new RumbleState(0x60, 0x30));
        }
        finally
        {
            test = default;
            XInputSetState(slot, ref test);
        }
        WaitForRumble(default);
        Console.WriteLine("ViGEmBus/XInput: both rumble channels and stop verified.");
    }

    private void WaitForRumble(RumbleState expected)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (Rumble == expected)
            {
                return;
            }
            Thread.Sleep(20);
        }
        throw new IOException("ViGEmBus did not return the expected vibration feedback.");
    }

    internal static uint? FindXInputSlot(ControllerState expected)
    {
        for (uint slot = 0; slot < 4; slot++)
        {
            if (XInputGetState(slot, out var observed) == 0 && observed.Gamepad == expected)
            {
                return slot;
            }
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftX;
        public short LeftY;
        public short RightX;
        public short RightY;

        public readonly ControllerState Gamepad =>
            new((Buttons)Buttons, LeftTrigger, RightTrigger, LeftX, LeftY, RightX, RightY);
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint slot, out XInputState state);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort Left;
        public ushort Right;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputSetState(uint slot, ref XInputVibration vibration);
}
