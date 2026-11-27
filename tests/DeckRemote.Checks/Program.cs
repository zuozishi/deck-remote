using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using DeckRemote;
using DeckRemote.Core;

if (OperatingSystem.IsLinux() && args is ["--check-gamescope", "foreground" or "background"])
{
    await CheckGamescope(args[1] == "foreground");
    return;
}
if (OperatingSystem.IsLinux() && args is ["--check-rumble-output"])
{
    await CheckRumbleOutput();
    return;
}

if (args.Length > 0)
{
    await RunLanCheck(args);
    return;
}

CheckReport();
CheckDatagrams();
if (OperatingSystem.IsLinux())
{
    CheckDeckForwarding();
}
await CheckEndpoints();
Console.WriteLine("PASS: mapping, R5 Guide negotiation, authenticated datagrams, pairing, rumble, watchdog and persistent controller lifecycle.");

[SupportedOSPlatform("linux")]
static async Task CheckGamescope(bool foreground)
{
    using var focus = GamescopeFocus.TryOpen() ?? throw new InvalidOperationException("Gamescope focus unavailable");
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    await Until(() => focus.IsForeground == foreground, limit.Token);
    if (foreground)
    {
        var input = new DeckInput(resumeOnFocus: true);
        input.SetForeground(true);
        var read = input.ReadAsync(limit.Token);
        try
        {
            await Until(() => input.CanForward(), limit.Token);
        }
        finally
        {
            await limit.CancelAsync();
            await read;
        }
    }
    Console.WriteLine($"PASS: Gamescope {(foreground ? "foreground and live HID input" : "background")}");
}

[SupportedOSPlatform("linux")]
static async Task CheckRumbleOutput()
{
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    var forwarding = true;
    var output = new DeckRumble(() => Volatile.Read(ref forwarding));
    var worker = output.RunAsync(limit.Token);
    try
    {
        var sample = new RumbleState(92, 48);
        output.SetFeedback(sample);
        await Until(() => output.Applied == sample, limit.Token);
        Volatile.Write(ref forwarding, false);
        await Until(() => output.Applied == default, limit.Token);
        Console.WriteLine("PASS: both Deck motors reported their applied strength and stopped on pause.");
    }
    finally
    {
        await limit.CancelAsync();
        try
        {
            await worker;
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested)
        {
        }
    }
}

[SupportedOSPlatform("linux")]
static void CheckDeckForwarding()
{
    var deck = new DeckInput(resumeOnFocus: true);
    var held = new ControllerState(Buttons.A, 50, 120, 200, 1000, -300, -1200);

    deck.AcceptReport(held, true);
    deck.SetForeground(true);
    Require(deck.Sample() == default, "A held Steam menu key allowed input on launch");
    deck.AcceptReport(held, false);
    Require(deck.Sample() == held && deck.CanForward(), "A menu key pressed before launch blocked forwarding");

    deck.AcceptReport(held, true);
    deck.AcceptReport(held, false);
    Require(deck.Sample() == default && deck.View.Paused && !deck.CanForward(), "The Steam menu must pause input and rumble");
    deck.SetForeground(false);
    deck.AcceptReport(held, false);
    Require(deck.Sample() == default, "Input was forwarded behind the Steam menu");
    deck.SetForeground(true);
    Require(deck.Sample() == held && !deck.View.Paused, "Forwarding did not resume after the Steam menu closed");

    deck.TogglePause();
    deck.SetForeground(false);
    deck.AcceptReport(held, false);
    deck.SetForeground(true);
    Require(deck.Sample() == default && deck.View.State == held && !deck.CanForward(),
        "Manual pause must keep the local preview live and survive focus changes");
    deck.TogglePause();
    Require(deck.Sample() == held && !deck.View.Paused, "Manual resume did not restore forwarding");

    var desktop = new DeckInput();
    desktop.SetForeground(true);
    desktop.AcceptReport(held, true);
    desktop.SetForeground(false);
    desktop.AcceptReport(held, false);
    desktop.SetForeground(true);
    Require(desktop.Sample() == default && desktop.View.Paused, "Desktop mode did not retain the manual resume behavior");
    desktop.TogglePause();
    Require(desktop.Sample() == held, "Desktop forwarding did not resume");
}

static void CheckReport()
{
    var report = new byte[64];
    report[0] = 1;
    report[2] = 9;
    report[3] = 64;
    BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(8), 0x04407FFC);
    BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(44), 32767);
    BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(46), 16384);
    BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(48), short.MinValue);
    BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(50), 16000);
    BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(52), short.MaxValue);
    BinaryPrimitives.WriteInt16LittleEndian(report.AsSpan(54), -16000);

    Require(DeckReport.TryParse(report, out var mapped, out var menu), "Valid Deck report rejected");
    Require(menu, "Steam key must be handled locally");
    Require(mapped == new ControllerState((Buttons)0xF3FF, 255, 128, short.MinValue, 16000, short.MaxValue, -16000),
        $"Incorrect controller mapping: {mapped}");
    BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(8), 0x00010080);
    Require(DeckReport.TryParse(report, out mapped, out menu) && mapped.Buttons == (Buttons.A | Buttons.Guide) && !menu,
        "R5 must forward Xbox Guide together with standard buttons without opening the Steam menu");
    BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(8), 0x00000080);
    Require(DeckReport.TryParse(report, out mapped, out menu) && mapped.Buttons == Buttons.A && !menu,
        "Releasing R5 must release Guide independently of other buttons");
    report[14] = 0x04;
    Require(DeckReport.TryParse(report, out mapped, out menu) && mapped.Buttons == Buttons.A && menu,
        "The Steam quick access key must still be handled locally");
    report[2] = 0;
    Require(!DeckReport.TryParse(report, out _, out _), "Wrong HID report type accepted");
    Require(!DeckReport.TryParse(report.AsSpan(0, 10), out _, out _), "Truncated HID report accepted");

    var rumble = new byte[DeckReport.RumbleSize];
    Array.Fill(rumble, byte.MaxValue);
    DeckReport.WriteRumble(rumble, new RumbleState(0x60, 0xA5));
    Require(rumble.AsSpan(0, 12).SequenceEqual(
        new byte[] { 0, 0xEB, 9, 0, 0, 0, 0x60, 0x60, 0xA5, 0xA5, 2, 0 }),
        "Incorrect Steam Deck rumble feature report");
    Require(!rumble.AsSpan(12).ContainsAnyExcept((byte)0), "Reserved rumble bytes must be zero");
    DeckReport.WriteRumble(rumble, default);
    Require(BinaryPrimitives.ReadUInt32LittleEndian(rumble.AsSpan(6)) == 0, "Rumble stop must clear both motors");
}

static void CheckDatagrams()
{
    var id = RandomNumberGenerator.GetBytes(16);
    var key = RandomNumberGenerator.GetBytes(32);
    using var sender = new InputPackets(id, key);
    using var receiver = new InputPackets(id, key);
    var early = new byte[InputPackets.Size];
    var latest = new byte[InputPackets.Size];
    var held = new ControllerState(Buttons.X, 20, 210, -877, 15891, 0, 897);
    sender.Encode(default, early);
    sender.Encode(held, latest);
    latest[^1] ^= 1;
    Require(!receiver.TryDecode(latest, out _), "Altered authentication tag accepted");
    latest[^1] ^= 1;
    Require(receiver.TryDecode(latest, out var decoded) && decoded == held, "Authenticated state mismatch");
    Require(!receiver.TryDecode(latest, out _), "Replay accepted");
    Require(!receiver.TryDecode(early, out _), "Out-of-order datagram accepted");
    using var other = new InputPackets(RandomNumberGenerator.GetBytes(16), key);
    Require(!other.TryDecode(latest, out _), "Cross-session datagram accepted");

    var withGuide = held with { Buttons = held.Buttons | Buttons.Guide };
    sender.Encode(withGuide, latest);
    Require(receiver.TryDecode(latest, out decoded) && decoded == held,
        "Guide must be stripped when the session does not support it");

    var guideId = RandomNumberGenerator.GetBytes(16);
    var guideKey = RandomNumberGenerator.GetBytes(32);
    using var guideSender = new InputPackets(guideId, guideKey, supportsGuide: true);
    using var guideReceiver = new InputPackets(guideId, guideKey, supportsGuide: true);
    using var legacyReceiver = new InputPackets(guideId, guideKey);
    guideSender.Encode(withGuide, latest);
    Require(guideReceiver.TryDecode(latest, out decoded) && decoded == withGuide,
        "Negotiated Guide did not survive the input packet");
    Require(!legacyReceiver.TryDecode(latest, out _), "A legacy session accepted an unnegotiated Guide bit");
}

static async Task CheckEndpoints()
{
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(40));
    var token = limit.Token;
    var root = Path.Combine(Path.GetTempPath(), "deckremote-check-" + Guid.NewGuid().ToString("N"));
    try
    {
        using var pcIdentity = new DeviceIdentity(Path.Combine(root, "pc"));
        using var deckIdentity = new DeviceIdentity(Path.Combine(root, "deck"));
        using var stranger = new DeviceIdentity(Path.Combine(root, "unpaired"));
        var port = FreePort();
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        Task<IReadOnlyList<DiscoveredPc>> Scan(CancellationToken ct) => LanDiscovery.ScanAsync([endpoint], ct);

        var output = new RecordingOutput();
        var pc = new PcReceiver(pcIdentity, "Test PC", output, port);
        var receive = pc.RunAsync(token);
        var standardHeld = new ControllerState(Buttons.A | Buttons.Up, 61, 198, -12345, 18218, 230, -3971);
        var held = standardHeld with { Buttons = standardHeld.Buttons | Buttons.Guide };
        var press = false;
        var feedback = new RecordingOutput();
        var deck = new DeckSender(deckIdentity, "Test Deck", () => Volatile.Read(ref press) ? held : default,
            Scan, feedback.SetRumble);
        var send = deck.RunAsync(token);
        try
        {
            await Until(() => deck.Status.Devices.Count == 1, token);
            Require(deck.Status.Devices[0].Peer.Fingerprint == pcIdentity.Fingerprint, "Wrong advertised identity");
            Require(!deck.Status.Devices[0].PairingOpen, "Pairing should start closed");
            Require(deck.Status.Devices[0].DriverReady && output.State == default, "Controller was not ready before pairing");
            Require(output.IsOpen && output.OpenCount == 1, "Receiver must start with one controller");
            pc.CheckDriver();
            await RejectUntrusted(stranger, pcIdentity.Fingerprint, endpoint, token);
            pc.OpenPairing();
            await Until(() => deck.Status.Devices[0].PairingOpen, token);
            deck.Connect(pcIdentity.Fingerprint);
            await Until(() => pc.Status.Pairing is not null && deck.Status.Pairing is not null, token);
            Require(pc.Status.Pairing!.PcCode == deck.Status.Pairing!.PcCode &&
                pc.Status.Pairing.DeckCode == deck.Status.Pairing.DeckCode, "Pairing codes differ between endpoints");
            pc.DecidePairing(true);
            await Task.Delay(100, token);
            Require(pcIdentity.Peer is null && !pc.Status.Connected && output.State == default,
                "Single-sided approval authorized input");
            deck.DecidePairing(true);
            await Until(() => pc.Status.Connected && output.State == default, token);
            Require(deckIdentity.Peer?.Fingerprint == pcIdentity.Fingerprint &&
                pcIdentity.Peer?.Fingerprint == deckIdentity.Fingerprint, "Pairing record mismatch");

            var vibration = new RumbleState(170, 44);
            output.SetRumble(vibration);
            await Until(() => feedback.Rumble == vibration, token);
            output.ResetRumble();
            await Until(() => feedback.Rumble == default, token);

            Volatile.Write(ref press, true);
            await Until(() => output.State == held, token);
            Require(pc.InputState == held, "Input preview missed the received buttons and triggers");
            Volatile.Write(ref press, false);
            await Until(() => output.State == default, token);
            Require(pc.InputState == default, "Input preview missed the released buttons and triggers");
            Require(pc.Status.Connected && output.IsOpen, "Press and release recreated the active controller");
            Volatile.Write(ref press, true);
            await Until(() => output.State == held, token);
            output.SetRumble(vibration);
            await Until(() => feedback.Rumble == vibration, token);

            deck.Disconnect();
            await Until(() => !pc.Status.Connected && !deck.Status.Connected && output.State == default &&
                feedback.Rumble == default, token);
            Require(pc.InputState == default, "Input preview retained disconnected input");
            Require(output.Rumble == default, "A disconnected session retained Windows rumble");
            Require(output.IsOpen && output.OpenCount == 1 && output.CloseCount == 0,
                "Disconnect removed or recreated the controller");
            using (var tcp = new TcpClient())
            {
                await tcp.ConnectAsync(endpoint, token);
                using var tls = new SslStream(tcp.GetStream());
                var peer = await ControlChannel.ConnectAsync(tls, deckIdentity, pcIdentity.Fingerprint, token);
                await peer.SendAsync(new(Command.Connect, "Test Deck"), token);
                var start = await peer.ReadAsync(token);
                Require(start.Type == Command.Start, "Previously paired Deck could not reconnect");
                Require(!start.SupportsRumble, "Legacy clients must not negotiate rumble");
                Require(!start.SupportsGuide, "Legacy clients must not negotiate Guide");
                await peer.SendAsync(new(Command.Ping), token);
                var pong = await peer.ReadAsync(token);
                Require(pong.Type == Command.Pong && pong.Rumble is null, "Legacy heartbeat changed");
                using var wire = new InputPackets(start.SessionId!, start.Key!);
                using var udp = new UdpClient();
                var packet = new byte[InputPackets.Size];
                wire.Encode(standardHeld, packet);
                await udp.SendAsync(packet, endpoint, token);
                await Until(() => output.State == standardHeld, token);
                var watchdog = Stopwatch.StartNew();
                await Until(() => output.State == default, token);
                Require(watchdog.ElapsedMilliseconds <= 250, $"Watchdog took {watchdog.ElapsedMilliseconds} ms");
                Require(output.IsOpen, "Temporary interruption removed the virtual controller");
                await udp.SendAsync(packet, endpoint, token);
                Require(output.State == default, "Retired session changed controller state");
            }

            await Task.Delay(TimeSpan.FromSeconds(10.5), token);
            Require(output.IsOpen && output.OpenCount == 1 && output.CloseCount == 0,
                "Extended network interruption removed the controller");
            deck.Connect(pcIdentity.Fingerprint);
            await Until(() => pc.Status.Connected && output.State == held, token);
            output.SetRumble(vibration);
            await Until(() => feedback.Rumble == vibration, token);
            pc.SetPaused(true);
            Require(output.IsOpen && output.State == default, "Pausing must neutralize the existing controller");
            Require(pc.InputState == default, "Input preview retained paused input");
            await Until(() => feedback.Rumble == default, token);
            await Until(() => deck.Status.Devices.Any(device => device.Paused), token);
            pc.SetPaused(false);
            await Until(() => pc.Status.Connected && output.State == held, token);
            Require(output.OpenCount == 1 && output.CloseCount == 0,
                "Reconnection must reuse the same controller");
            Require(feedback.Rumble == default, "Reconnect replayed the previous session's rumble");

            pc.Forget();
            Require(output.IsOpen && !pc.Status.Connected && output.State == default,
                "Revoking pairing must neutralize the existing controller");
            await RejectUntrusted(deckIdentity, pcIdentity.Fingerprint, endpoint, token);
        }
        finally
        {
            await limit.CancelAsync();
            await IgnoreCancellation(Task.WhenAll(send, receive));
        }
        Require(!output.IsOpen && output.OpenCount == 1 && output.CloseCount == 1,
            "Receiver shutdown must release its controller exactly once");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task RejectUntrusted(DeviceIdentity stranger, string pcFingerprint, IPEndPoint endpoint, CancellationToken token)
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(endpoint, token);
    using var tls = new SslStream(tcp.GetStream());
    try
    {
        var channel = await ControlChannel.ConnectAsync(tls, stranger, pcFingerprint, token);
        await channel.SendAsync(new(Command.Connect, "Unpaired"), token);
        var reply = await channel.ReadAsync(token);
        Require(reply.Type != Command.Start, "Unpaired endpoint was authorized");
    }
    catch (Exception exception) when (exception is IOException or System.Security.Authentication.AuthenticationException)
    {
    }
}

static async Task RunLanCheck(string[] arguments)
{
    using var identity = new DeviceIdentity(arguments[1]);
    if (arguments[0] == "identity")
    {
        Console.WriteLine(identity.Fingerprint);
        return;
    }
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var token = timeout.Token;
    var expected = arguments[2];
    var sample = new ControllerState(Buttons.A, 55, 199, -6417, 17617, 27, -4741);
    if (arguments[0] == "lan-pc")
    {
        using var actual = OperatingSystem.IsWindows() ? new DeckRemote.VigemOutput() : null;
        var output = new RecordingOutput(actual);
        var receiver = new PcReceiver(identity, "Deck Remote LAN check", output);
        receiver.OpenPairing();
        var worker = receiver.RunAsync(token);
        try
        {
            Console.WriteLine("Listening for Deck on UDP/TCP " + LanDiscovery.Port);
            var paired = Until(() => receiver.Status.Pairing is not null, token);
            await await Task.WhenAny(worker, paired);
            Require(receiver.Status.Pairing!.DeckCode == DeviceIdentity.DisplayCode(expected), "Unexpected Deck identity");
            receiver.DecidePairing(true);
            await Until(() => output.State == sample, token);
            Console.WriteLine("PASS: received authenticated Deck input over LAN.");
            if (OperatingSystem.IsWindows())
            {
                await Until(() => DeckRemote.VigemOutput.FindXInputSlot(sample) is not null, token);
                Console.WriteLine("PASS: Windows XInput reported the same state.");
            }
            await Until(() => !receiver.Status.Connected && output.State == default, token);
            Require(output.IsOpen && output.OpenCount == 1, "Remote disconnect removed the controller");
            Console.WriteLine("PASS: remote disconnect neutralized the existing controller.");
        }
        finally
        {
            await timeout.CancelAsync();
            await IgnoreCancellation(worker);
        }
    }
    else if (arguments[0] == "lan-deck")
    {
        var sender = new DeckSender(identity, "Deck LAN check", () => sample);
        var worker = sender.RunAsync(token);
        try
        {
            await Until(() => sender.Status.Devices.Any(pc => pc.Peer.Fingerprint == expected && pc.PairingOpen), token);
            Console.WriteLine("PASS: discovered Windows receiver by broadcast.");
            sender.Connect(expected);
            await Until(() => sender.Status.Pairing is not null, token);
            Require(sender.Status.Pairing!.PcCode == DeviceIdentity.DisplayCode(expected), "Unexpected PC identity");
            sender.DecidePairing(true);
            await Until(() => sender.Status.Connected, token);
            await Task.Delay(500, token);
            sender.Disconnect();
            await Until(() => !sender.Status.Connected, token);
        }
        finally
        {
            await timeout.CancelAsync();
            await IgnoreCancellation(worker);
        }
    }
    else
    {
        throw new ArgumentException("Unknown check.");
    }
}

static int FreePort()
{
    using var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    return ((IPEndPoint)probe.LocalEndpoint).Port;
}

static async Task Until(Func<bool> condition, CancellationToken token)
{
    while (!condition())
    {
        await Task.Delay(4, token);
    }
}

static async Task IgnoreCancellation(Task task)
{
    try
    {
        await task;
    }
    catch (OperationCanceledException)
    {
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class RecordingOutput(IControllerOutput? actual = null) : IControllerOutput
{
    private readonly object gate = new();
    private ControllerState state;
    private RumbleState rumble;
    private volatile bool open;
    private int openCount;
    private int closeCount;
    public bool IsOpen => open;
    public int OpenCount => Volatile.Read(ref openCount);
    public int CloseCount => Volatile.Read(ref closeCount);
    public ControllerState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    public RumbleState Rumble
    {
        get
        {
            lock (gate)
            {
                return actual?.Rumble ?? rumble;
            }
        }
    }

    public void SetRumble(RumbleState state)
    {
        lock (gate)
        {
            rumble = state;
        }
    }

    public void ResetRumble()
    {
        actual?.ResetRumble();
        SetRumble(default);
    }

    public void Open()
    {
        actual?.Open();
        Interlocked.Increment(ref openCount);
        open = true;
    }

    public void Submit(ControllerState current)
    {
        lock (gate)
        {
            actual?.Submit(current);
            state = current;
        }
    }

    public void Close()
    {
        actual?.Close();
        Interlocked.Increment(ref closeCount);
        open = false;
    }
}
