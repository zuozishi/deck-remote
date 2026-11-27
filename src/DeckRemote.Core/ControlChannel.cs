using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace DeckRemote.Core;

internal enum Command
{
    Pair = 1, Connect, Pending, Approve, Paired, Start, Ping, Pong, Stop, Reject
}

internal sealed record ControlMessage(
    Command Type, string? Text = null, byte[]? SessionId = null, byte[]? Key = null,
    bool SupportsRumble = false, RumbleState? Rumble = null, bool SupportsGuide = false);

internal sealed class ControlChannel(SslStream stream)
{
    private const int MaxMessageSize = 2048;
    private static readonly SslApplicationProtocol Protocol = new("deck-remote/1");

    public static async Task<ControlChannel> AcceptAsync(SslStream stream, DeviceIdentity identity,
        Func<string, bool> allow, CancellationToken token)
    {
        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificateContext = SslStreamCertificateContext.Create(identity.Certificate, null, offline: true),
            ClientCertificateRequired = true,
            CertificateChainPolicy = LocalChainPolicy(),
            ApplicationProtocols = [Protocol],
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            AllowRenegotiation = false,
            AllowTlsResume = false,
            RemoteCertificateValidationCallback = (_, remote, _, _) =>
                remote is not null && allow(DeviceIdentity.GetFingerprint(remote))
        }, token).ConfigureAwait(false);
        return Open(stream);
    }

    public static async Task<ControlChannel> ConnectAsync(SslStream stream, DeviceIdentity identity,
        string fingerprint, CancellationToken token)
    {
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "Deck Remote",
            ClientCertificateContext = SslStreamCertificateContext.Create(identity.Certificate, null, offline: true),
            CertificateChainPolicy = LocalChainPolicy(),
            ApplicationProtocols = [Protocol],
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            AllowRenegotiation = false,
            AllowTlsResume = false,
            RemoteCertificateValidationCallback = (_, remote, _, _) =>
                remote is not null && DeviceIdentity.GetFingerprint(remote) == fingerprint
        }, token).ConfigureAwait(false);
        return Open(stream);
    }

    public string RemoteFingerprint => DeviceIdentity.GetFingerprint(
        stream.RemoteCertificate ?? throw new AuthenticationException("Missing peer certificate."));

    public async Task SendAsync(ControlMessage message, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > MaxMessageSize)
        {
            throw new InvalidDataException("Control message is too large.");
        }
        var frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, sizeof(int));
        await stream.WriteAsync(frame, token).ConfigureAwait(false);
    }

    public async Task<ControlMessage> ReadAsync(CancellationToken token)
    {
        var length = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32BigEndian(length);
        if (size is <= 0 or > MaxMessageSize)
        {
            throw new InvalidDataException("Invalid control message size.");
        }
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ControlMessage>(payload)
            ?? throw new InvalidDataException("Missing control message.");
    }

    private static ControlChannel Open(SslStream stream)
    {
        if (stream.NegotiatedApplicationProtocol != Protocol || stream.RemoteCertificate is null)
        {
            throw new AuthenticationException("Unexpected peer or protocol.");
        }
        return new ControlChannel(stream);
    }

    private static X509ChainPolicy LocalChainPolicy() => new()
    {
        DisableCertificateDownloads = true,
        RevocationMode = X509RevocationMode.NoCheck
    };

    internal static bool IsConnectionError(Exception error) => error is
        IOException or System.Net.Sockets.SocketException or AuthenticationException or JsonException or OperationCanceledException;
}
