using System.Buffers.Binary;

namespace DeckRemote.Core;

public static class DeckReport
{
    public const int Size = 64;
    public const int RumbleSize = 65;

    public static void WriteRumble(Span<byte> report, RumbleState state)
    {
        // hidraw requires a leading report ID, followed by Steam Deck's rumble feature report.
        report[..RumbleSize].Clear();
        report[1] = 0xEB;
        report[2] = 9;
        BinaryPrimitives.WriteUInt16LittleEndian(report[6..], (ushort)(state.LargeMotor * 257));
        BinaryPrimitives.WriteUInt16LittleEndian(report[8..], (ushort)(state.SmallMotor * 257));
        report[10] = 2;
    }

    // Valve's Deck state report, as described by SDL's controller_structs.h.
    private static readonly (uint Mask, Buttons Button)[] ButtonMap =
    [
        (0x00000004, Buttons.RightShoulder), (0x00000008, Buttons.LeftShoulder),
        (0x00000010, Buttons.Y), (0x00000020, Buttons.B), (0x00000040, Buttons.X),
        (0x00000080, Buttons.A), (0x00000100, Buttons.Up), (0x00000200, Buttons.Right),
        (0x00000400, Buttons.Left), (0x00000800, Buttons.Down), (0x00001000, Buttons.Back),
        (0x00004000, Buttons.Start), (0x00010000, Buttons.Guide), // R5
        (0x00400000, Buttons.LeftStick), (0x04000000, Buttons.RightStick)
    ];

    public static bool TryParse(ReadOnlySpan<byte> report, out ControllerState state, out bool systemMenu)
    {
        state = default;
        systemMenu = false;
        if (report.Length != Size || BinaryPrimitives.ReadUInt16LittleEndian(report) != 1 ||
            report[2] != 9 || report[3] != Size)
        {
            return false;
        }

        var rawButtons = BinaryPrimitives.ReadUInt32LittleEndian(report[8..]);
        systemMenu = (rawButtons & 0x2000) != 0 || (report[14] & 0x04) != 0;
        var buttons = (Buttons)0;
        foreach (var (mask, button) in ButtonMap)
        {
            if ((rawButtons & mask) != 0)
            {
                buttons |= button;
            }
        }

        state = new(buttons, Trigger(report[44..]), Trigger(report[46..]),
            BinaryPrimitives.ReadInt16LittleEndian(report[48..]),
            BinaryPrimitives.ReadInt16LittleEndian(report[50..]),
            BinaryPrimitives.ReadInt16LittleEndian(report[52..]),
            BinaryPrimitives.ReadInt16LittleEndian(report[54..]));
        return true;
    }

    private static byte Trigger(ReadOnlySpan<byte> value)
    {
        var raw = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(value), 32767);
        return (byte)((raw * 255 + 16383) / 32767);
    }
}
