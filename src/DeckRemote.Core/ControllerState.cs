using System.Buffers.Binary;

namespace DeckRemote.Core;

[Flags]
public enum Buttons : ushort
{
    Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008,
    Start = 0x0010, Back = 0x0020, LeftStick = 0x0040, RightStick = 0x0080,
    LeftShoulder = 0x0100, RightShoulder = 0x0200,
    Guide = 0x0400,
    A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000
}

public readonly record struct ControllerState(
    Buttons Buttons, byte Lt, byte Rt, short Lx, short Ly, short Rx, short Ry)
{
    internal const int Size = 12;
    internal const Buttons StandardButtons = (Buttons)0xF3FF;

    internal void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)Buttons);
        destination[2] = Lt;
        destination[3] = Rt;
        BinaryPrimitives.WriteInt16BigEndian(destination[4..], Lx);
        BinaryPrimitives.WriteInt16BigEndian(destination[6..], Ly);
        BinaryPrimitives.WriteInt16BigEndian(destination[8..], Rx);
        BinaryPrimitives.WriteInt16BigEndian(destination[10..], Ry);
    }

    internal static bool TryRead(ReadOnlySpan<byte> source, Buttons allowedButtons, out ControllerState state)
    {
        var buttons = (Buttons)BinaryPrimitives.ReadUInt16BigEndian(source);
        state = new(buttons, source[2], source[3],
            BinaryPrimitives.ReadInt16BigEndian(source[4..]),
            BinaryPrimitives.ReadInt16BigEndian(source[6..]),
            BinaryPrimitives.ReadInt16BigEndian(source[8..]),
            BinaryPrimitives.ReadInt16BigEndian(source[10..]));
        return (buttons & ~allowedButtons) == 0;
    }
}

public readonly record struct RumbleState(byte LargeMotor, byte SmallMotor);

public interface IControllerOutput
{
    RumbleState Rumble { get; }
    void Open();
    void Submit(ControllerState state);
    void ResetRumble();
    void Close();
}
