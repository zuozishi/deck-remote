using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DeckRemote.Core;

public sealed record DiscoveredPc(Peer Peer, IPEndPoint Endpoint, bool PairingOpen, bool DriverReady, bool Paused);

public static class LanDiscovery
{
    public const int Port = 39871;
    private const int QuerySize = 20;
    private const int OfferHeaderSize = QuerySize + 33;
    private const int MaxNameSize = 96;

    public static Task<IReadOnlyList<DiscoveredPc>> ScanAsync(CancellationToken token)
    {
        var targets = GetSubnets().Select(subnet => new IPEndPoint(subnet.Broadcast, Port)).Distinct().ToArray();
        return ScanAsync(targets, token);
    }

    internal static async Task<IReadOnlyList<DiscoveredPc>> ScanAsync(
        IReadOnlyCollection<IPEndPoint> targets, CancellationToken token)
    {
        if (targets.Count == 0)
        {
            await Task.Delay(850, token).ConfigureAwait(false);
            return [];
        }
        using var socket = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var query = new byte[QuerySize];
        "DRQ1"u8.CopyTo(query);
        RandomNumberGenerator.Fill(query.AsSpan(4));
        foreach (var target in targets)
        {
            try
            {
                await socket.SendAsync(query, target, token).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // A route can disappear between enumeration and send.
            }
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(750));
        var found = new Dictionary<string, DiscoveredPc>(StringComparer.Ordinal);
        try
        {
            while (true)
            {
                var reply = await socket.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                var pc = ReadOffer(reply, query);
                if (pc is not null && targets.Any(target => target.Port == pc.Endpoint.Port) &&
                    (IPAddress.IsLoopback(pc.Endpoint.Address) || IsOnLocalSubnet(pc.Endpoint.Address)))
                {
                    found.TryAdd(pc.Peer.Fingerprint, pc);
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return found.Values.OrderBy(pc => pc.Peer.Name, StringComparer.CurrentCulture).ToArray();
        }
    }

    internal static byte[]? Reply(ReadOnlySpan<byte> query, Peer owner, bool pairing, bool driverReady, bool paused)
    {
        if (query.Length != QuerySize || !query[..4].SequenceEqual("DRQ1"u8))
        {
            return null;
        }
        var name = Encoding.UTF8.GetBytes(CleanName(owner.Name));
        var reply = new byte[OfferHeaderSize + name.Length];
        "DRO1"u8.CopyTo(reply);
        query[4..].CopyTo(reply.AsSpan(4));
        Convert.FromHexString(owner.Fingerprint).CopyTo(reply, QuerySize);
        reply[OfferHeaderSize - 1] = (byte)((pairing ? 1 : 0) | (driverReady ? 2 : 0) | (paused ? 4 : 0));
        name.CopyTo(reply, OfferHeaderSize);
        return reply;
    }

    private static DiscoveredPc? ReadOffer(UdpReceiveResult reply, ReadOnlySpan<byte> query)
    {
        var data = reply.Buffer.AsSpan();
        if (data.Length <= OfferHeaderSize || data.Length > OfferHeaderSize + MaxNameSize ||
            !data[..4].SequenceEqual("DRO1"u8) || !data[4..QuerySize].SequenceEqual(query[4..]))
        {
            return null;
        }
        var peer = new Peer(CleanName(Encoding.UTF8.GetString(data[OfferHeaderSize..])),
            Convert.ToHexString(data.Slice(QuerySize, 32)));
        var flags = data[OfferHeaderSize - 1];
        return new DiscoveredPc(peer, reply.RemoteEndPoint, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0);
    }

    internal static bool IsOnLocalSubnet(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && GetSubnets().Any(subnet => subnet.Contains(bytes));
    }

    internal static string CleanName(string? value)
    {
        var cleaned = string.Concat((value ?? "").EnumerateRunes()
            .Where(rune => !Rune.IsControl(rune) && Rune.GetUnicodeCategory(rune) != System.Globalization.UnicodeCategory.Format)
            .Take(MaxNameSize / 4).Select(rune => rune.ToString())).Trim();
        return cleaned.Length == 0 ? "Deck Remote" : cleaned;
    }

    private static IEnumerable<Subnet> GetSubnets()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }
            foreach (var ip in adapter.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork && ip.PrefixLength is > 0 and < 31)
                {
                    yield return new Subnet(ip.Address.GetAddressBytes(), ip.IPv4Mask.GetAddressBytes());
                }
            }
        }
    }

    private sealed record Subnet(byte[] Address, byte[] Mask)
    {
        public IPAddress Broadcast => new(Address.Select((octet, index) => (byte)(octet | ~Mask[index])).ToArray());

        public bool Contains(byte[] remote)
        {
            return Enumerable.Range(0, 4).All(i => (remote[i] & Mask[i]) == (Address[i] & Mask[i]));
        }
    }
}
