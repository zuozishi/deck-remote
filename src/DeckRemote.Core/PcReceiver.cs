using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DeckRemote.Core;

public sealed record ReceiverStatus(
    Peer? Peer, bool PairingOpen, PairingRequest? Pairing, bool Paused, bool Connected,
    bool DriverReady, string? Error);

public sealed class PcReceiver(DeviceIdentity identity, string name, IControllerOutput output, int port = LanDiscovery.Port)
{
    private static readonly TimeSpan PairingWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DriverRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InputTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan FirstInputTimeout = TimeSpan.FromMilliseconds(1800);
    private readonly object gate = new();
    private readonly Peer owner = new(LanDiscovery.CleanName(name), identity.Fingerprint);
    private long? pairingSince;
    private PairingRequest? request;
    private TaskCompletionSource<bool>? decision;
    private bool paused;
    private bool running;
    private bool driverReady;
    private long lastDriverAttempt;
    private string? error;
    private Session? session;

    public ReceiverStatus Status
    {
        get
        {
            lock (gate)
            {
                return new(identity.Peer, IsPairingOpen, request, paused, session?.Received == true, driverReady, error);
            }
        }
    }

    public ControllerState InputState
    {
        get
        {
            lock (gate)
            {
                return session?.State ?? default;
            }
        }
    }

    public void OpenPairing()
    {
        lock (gate)
        {
            if (!paused && session is null && request is null)
            {
                pairingSince = Stopwatch.GetTimestamp();
            }
        }
    }

    public void DecidePairing(bool approve)
    {
        lock (gate)
        {
            decision?.TrySetResult(approve && IsPairingOpen);
            if (!approve)
            {
                pairingSince = null;
            }
        }
    }

    public void SetPaused(bool value)
    {
        lock (gate)
        {
            paused = value;
            if (value)
            {
                CancelPairing();
                EndSession();
            }
        }
    }

    public void Forget()
    {
        lock (gate)
        {
            CancelPairing();
            EndSession();
            identity.Forget();
        }
    }

    public void CheckDriver()
    {
        lock (gate)
        {
            if (running && !driverReady)
            {
                TryOpenOutput();
            }
        }
    }

    public async Task RunAsync(CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var listener = new TcpListener(IPAddress.Any, port);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        listener.ExclusiveAddressUse = true;
        listener.Start(backlog: 8);
        lock (gate)
        {
            running = true;
        }
        Task[] workers = [];
        try
        {
            token.ThrowIfCancellationRequested();
            CheckDriver();
            workers = [ListenAsync(listener, stop.Token), ReceiveAsync(udp, stop.Token), WatchAsync(stop.Token)];
            await await Task.WhenAny(workers).ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            lock (gate)
            {
                running = false;
                CancelPairing();
                EndSession();
                if (driverReady)
                {
                    CloseOutput();
                }
            }
            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }
    }

    private bool IsPairingOpen => pairingSince is long since && Stopwatch.GetElapsedTime(since) < PairingWindow;

    private async Task ListenAsync(TcpListener listener, CancellationToken token)
    {
        Task handler = Task.CompletedTask;
        try
        {
            while (true)
            {
                var tcp = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                if (handler.IsCompleted && tcp.Client.RemoteEndPoint is IPEndPoint remote &&
                    LanDiscovery.IsOnLocalSubnet(remote.Address))
                {
                    await handler.ConfigureAwait(false);
                    handler = HandleAsync(tcp, remote.Address, token);
                }
                else
                {
                    tcp.Dispose();
                }
            }
        }
        finally
        {
            await handler.ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(TcpClient tcp, IPAddress address, CancellationToken token)
    {
        using var connection = tcp;
        using var close = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var stream = new SslStream(tcp.GetStream());
        var connectionToken = close.Token;
        Session? current = null;
        try
        {
            tcp.NoDelay = true;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            var channel = await ControlChannel.AcceptAsync(stream, identity, fingerprint =>
            {
                lock (gate)
                {
                    return running && !paused && fingerprint != owner.Fingerprint &&
                        (identity.Peer?.Fingerprint == fingerprint || IsPairingOpen);
                }
            }, deadline.Token).ConfigureAwait(false);
            var hello = await channel.ReadAsync(deadline.Token).ConfigureAwait(false);
            var peer = new Peer(LanDiscovery.CleanName(hello.Text), channel.RemoteFingerprint);

            if (hello.Type == Command.Pair && !await PairAsync(channel, peer, connectionToken).ConfigureAwait(false))
            {
                return;
            }
            if (hello.Type is not (Command.Pair or Command.Connect))
            {
                return;
            }

            var id = RandomNumberGenerator.GetBytes(16);
            var key = RandomNumberGenerator.GetBytes(32);
            string? rejection;
            lock (gate)
            {
                rejection = !running || paused || identity.Peer?.Fingerprint != peer.Fingerprint
                    ? "接收已暂停或设备未配对"
                    : driverReady ? null : "PC 无法创建 Xbox 手柄";
                if (rejection is null)
                {
                    output.ResetRumble();
                    current = new Session(address, new InputPackets(id, key, hello.SupportsGuide), close);
                    session = current;
                }
            }
            try
            {
                if (current is null)
                {
                    await channel.SendAsync(new(Command.Reject, rejection), connectionToken).ConfigureAwait(false);
                    return;
                }
                await channel.SendAsync(new(Command.Start, SessionId: id, Key: key,
                    SupportsRumble: hello.SupportsRumble, SupportsGuide: hello.SupportsGuide),
                    connectionToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            while (true)
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var message = await channel.ReadAsync(deadline.Token).ConfigureAwait(false);
                if (message.Type == Command.Stop)
                {
                    lock (gate)
                    {
                        if (session == current)
                        {
                            EndSession();
                        }
                    }
                    return;
                }
                if (message.Type != Command.Ping)
                {
                    return;
                }
                RumbleState? rumble = null;
                if (hello.SupportsRumble)
                {
                    lock (gate)
                    {
                        rumble = session == current && current.Received ? output.Rumble : default(RumbleState);
                    }
                }
                await channel.SendAsync(new(Command.Pong, Rumble: rumble), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (ControlChannel.IsConnectionError(exception))
        {
            // The watchdog or the control stream ends an interrupted connection.
        }
        finally
        {
            lock (gate)
            {
                if (current is not null && session == current)
                {
                    EndSession();
                }
            }
        }
    }

    private async Task<bool> PairAsync(ControlChannel channel, Peer peer, CancellationToken token)
    {
        TaskCompletionSource<bool> approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan remaining;
        lock (gate)
        {
            if (paused || !IsPairingOpen)
            {
                return false;
            }
            remaining = PairingWindow - Stopwatch.GetElapsedTime(pairingSince!.Value);
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }
            request = new(peer.Name, DeviceIdentity.DisplayCode(owner.Fingerprint), DeviceIdentity.DisplayCode(peer.Fingerprint));
            decision = approval;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(remaining);
        try
        {
            await channel.SendAsync(new(Command.Pending), deadline.Token).ConfigureAwait(false);
            var remoteApproval = channel.ReadAsync(deadline.Token);
            var localApproval = approval.Task.WaitAsync(deadline.Token);
            var first = await Task.WhenAny(remoteApproval, localApproval).ConfigureAwait(false);
            var firstApproved = first == remoteApproval
                ? (await remoteApproval.ConfigureAwait(false)).Type == Command.Approve
                : await localApproval.ConfigureAwait(false);
            if (!firstApproved || !await localApproval.ConfigureAwait(false) ||
                (await remoteApproval.ConfigureAwait(false)).Type != Command.Approve)
            {
                await deadline.CancelAsync().ConfigureAwait(false);
                return false;
            }
            lock (gate)
            {
                if (paused || !IsPairingOpen)
                {
                    return false;
                }
                identity.Trust(peer);
            }
            await channel.SendAsync(new(Command.Paired), deadline.Token).ConfigureAwait(false);
            return true;
        }
        finally
        {
            lock (gate)
            {
                pairingSince = null;
                request = null;
                decision = null;
            }
        }
    }

    private async Task ReceiveAsync(UdpClient udp, CancellationToken token)
    {
        while (true)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await udp.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (SocketException exception) when (exception.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
            {
                continue;
            }
            byte[]? reply = null;
            lock (gate)
            {
                if (session is { } expired && IsExpired(expired))
                {
                    EndSession();
                }
                if (datagram.Buffer.Length == InputPackets.Size && session is { } current &&
                    current.Address.Equals(datagram.RemoteEndPoint.Address) &&
                    current.Packets.TryDecode(datagram.Buffer, out var state))
                {
                    try
                    {
                        if (current.State != state)
                        {
                            output.Submit(state);
                        }
                        current.State = state;
                        current.LastInput = Stopwatch.GetTimestamp();
                        current.Received = true;
                    }
                    catch (IOException exception)
                    {
                        DriverError(exception);
                    }
                }
                else if (datagram.Buffer.AsSpan().StartsWith("DRQ1"u8) &&
                    LanDiscovery.IsOnLocalSubnet(datagram.RemoteEndPoint.Address))
                {
                    reply = LanDiscovery.Reply(datagram.Buffer, owner, IsPairingOpen, driverReady, paused);
                }
            }
            if (reply is not null)
            {
                try
                {
                    await udp.SendAsync(reply, datagram.RemoteEndPoint, token).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    // The peer may have left the subnet.
                }
            }
        }
    }

    private async Task WatchAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            lock (gate)
            {
                if (session is { } current && IsExpired(current))
                {
                    EndSession();
                }
                if (running && !driverReady && Stopwatch.GetElapsedTime(lastDriverAttempt) >= DriverRetryDelay)
                {
                    TryOpenOutput();
                }
            }
        }
    }

    private static bool IsExpired(Session current) =>
        Stopwatch.GetElapsedTime(current.LastInput) >= (current.Received ? InputTimeout : FirstInputTimeout);

    private void CancelPairing()
    {
        pairingSince = null;
        decision?.TrySetResult(false);
    }

    private void ClearSession()
    {
        if (session is { } previous)
        {
            session = null;
            previous.Packets.Dispose();
            previous.Close.Cancel();
        }
    }

    private void EndSession()
    {
        if (session is null)
        {
            return;
        }
        ClearSession();
        output.ResetRumble();
        try
        {
            output.Submit(default);
        }
        catch (IOException exception)
        {
            DriverError(exception);
        }
    }

    private void TryOpenOutput()
    {
        lastDriverAttempt = Stopwatch.GetTimestamp();
        try
        {
            // Keep the device registered while the receiver runs so games can retain the same controller.
            output.Open();
            output.Submit(default);
            driverReady = true;
            error = null;
        }
        catch (IOException exception)
        {
            DriverError(exception);
        }
    }

    private void DriverError(IOException exception)
    {
        ClearSession();
        CloseOutput();
        lastDriverAttempt = Stopwatch.GetTimestamp();
        error = exception.Message;
    }

    private void CloseOutput()
    {
        driverReady = false;
        output.Close();
    }

    private sealed class Session(IPAddress address, InputPackets packets, CancellationTokenSource close)
    {
        public IPAddress Address { get; } = address;
        public InputPackets Packets { get; } = packets;
        public CancellationTokenSource Close { get; } = close;
        public long LastInput { get; set; } = Stopwatch.GetTimestamp();
        public bool Received { get; set; }
        public ControllerState State { get; set; }
    }
}
