using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DeckRemote.Core;

internal sealed class InputPackets : IDisposable
{
    private const int SessionIdSize = 16;
    private const int HeaderSize = 4 + SessionIdSize + sizeof(ulong);
    private const int TagSize = 16;
    internal const int Size = HeaderSize + ControllerState.Size + TagSize;

    private readonly byte[] sessionId;
    private readonly AesGcm cipher;
    private readonly Buttons allowedButtons;
    private ulong sequence;

    public InputPackets(byte[] sessionId, byte[] key, bool supportsGuide = false)
    {
        if (sessionId.Length != SessionIdSize || key.Length != 32)
        {
            throw new InvalidDataException("Invalid session credentials.");
        }
        this.sessionId = sessionId;
        cipher = new AesGcm(key, TagSize);
        allowedButtons = ControllerState.StandardButtons | (supportsGuide ? Buttons.Guide : 0);
    }

    public void Encode(ControllerState state, Span<byte> packet)
    {
        "DRI1"u8.CopyTo(packet);
        sessionId.CopyTo(packet[4..]);
        BinaryPrimitives.WriteUInt64BigEndian(packet[(HeaderSize - sizeof(ulong))..], checked(++sequence));
        Span<byte> nonce = stackalloc byte[12];
        MakeNonce(packet, nonce);
        Span<byte> payload = stackalloc byte[ControllerState.Size];
        (state with { Buttons = state.Buttons & allowedButtons }).Write(payload);
        cipher.Encrypt(nonce, payload, packet.Slice(HeaderSize, ControllerState.Size),
            packet[^TagSize..], packet[..HeaderSize]);
    }

    public bool TryDecode(ReadOnlySpan<byte> packet, out ControllerState state)
    {
        state = default;
        if (packet.Length != Size || !packet[..4].SequenceEqual("DRI1"u8) ||
            !packet.Slice(4, SessionIdSize).SequenceEqual(sessionId))
        {
            return false;
        }
        var incoming = BinaryPrimitives.ReadUInt64BigEndian(packet[(HeaderSize - sizeof(ulong))..]);
        if (incoming <= sequence)
        {
            return false;
        }

        Span<byte> nonce = stackalloc byte[12];
        MakeNonce(packet, nonce);
        Span<byte> payload = stackalloc byte[ControllerState.Size];
        try
        {
            cipher.Decrypt(nonce, packet.Slice(HeaderSize, ControllerState.Size),
                packet[^TagSize..], payload, packet[..HeaderSize]);
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }
        if (!ControllerState.TryRead(payload, allowedButtons, out state))
        {
            return false;
        }
        sequence = incoming;
        return true;
    }

    private static void MakeNonce(ReadOnlySpan<byte> packet, Span<byte> nonce)
    {
        packet.Slice(4, 4).CopyTo(nonce);
        packet.Slice(HeaderSize - sizeof(ulong), sizeof(ulong)).CopyTo(nonce[4..]);
    }

    public void Dispose() => cipher.Dispose();
}
