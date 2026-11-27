using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DeckRemote.Core;

public sealed record SenderStatus(
    Peer? Peer, IReadOnlyList<DiscoveredPc> Devices, PairingRequest? Pairing,
    bool Connecting, bool Connected, bool Automatic, string? Error);

public sealed class DeckSender
{
    private readonly DeviceIdentity identity;
    private readonly string name;
    private readonly Func<ControllerState> sample;
    private readonly Func<CancellationToken, Task<IReadOnlyList<DiscoveredPc>>> scan;
    private readonly Action<RumbleState>? onRumble;
    private readonly object gate = new();
    private IReadOnlyList<DiscoveredPc> devices = [];
    private PairingRequest? request;
    private TaskCompletionSource<bool>? decision;
    private TaskCompletionSource? stopInput;
    private CancellationTokenSource? attempt;
    private string? selected;
    private string? error;
    private bool automatic = true;
    private bool connected;

    public DeckSender(DeviceIdentity identity, string name, Func<ControllerState> sample, Action<RumbleState>? onRumble = null)
        : this(identity, name, sample, LanDiscovery.ScanAsync, onRumble)
    {
    }

    internal DeckSender(DeviceIdentity identity, string name, Func<ControllerState> sample,
        Func<CancellationToken, Task<IReadOnlyList<DiscoveredPc>>> scan, Action<RumbleState>? onRumble = null)
    {
        this.identity = identity;
        this.name = LanDiscovery.CleanName(name);
        this.sample = sample;
        this.scan = scan;
        this.onRumble = onRumble;
    }

    public SenderStatus Status
    {
        get
        {
            lock (gate)
            {
                return new(identity.Peer, devices, request, attempt is not null && !connected,
                    connected, automatic, error);
            }
        }
    }

    public void Connect(string fingerprint)
    {
        lock (gate)
        {
            if (attempt is null)
            {
                selected = fingerprint;
                automatic = true;
                error = null;
            }
        }
    }

    public void DecidePairing(bool approve)
    {
        lock (gate)
        {
            if (decision is { } pending && !pending.TrySetResult(approve) && !approve)
            {
                attempt?.Cancel();
            }
        }
    }

    public void Disconnect()
    {
        lock (gate)
        {
            Stop();
        }
    }

    public void Forget()
    {
        lock (gate)
        {
            Stop();
            identity.Forget();
        }
    }

    private void Stop()
    {
        automatic = false;
        selected = null;
        if (stopInput is not null)
        {
            stopInput.TrySetResult();
        }
        else
        {
            attempt?.Cancel();
        }
    }

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            IReadOnlyList<DiscoveredPc> available;
            try
            {
                available = await scan(token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is SocketException or System.Net.NetworkInformation.NetworkInformationException)
            {
                lock (gate)
                {
                    devices = [];
                    error = "局域网暂不可用";
                }
                await Task.Delay(750, token).ConfigureAwait(false);
                continue;
            }

            DiscoveredPc? pc;
            bool pair;
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (gate)
            {
                devices = available;
                var target = selected ?? (automatic ? identity.Peer?.Fingerprint : null);
                pc = target is null ? null : devices.FirstOrDefault(item => item.Peer.Fingerprint == target);
                pair = pc is not null && identity.Peer?.Fingerprint != pc.Peer.Fingerprint;
                if (pc is not null)
                {
                    selected = null;
                    error = pc.Paused ? "PC 已暂停接收" :
                        pair && !pc.PairingOpen ? "请在 PC 上开启配对" :
                        !pair && !pc.DriverReady ? "PC 的 ViGEmBus 不可用" : null;
                    if (error is not null)
                    {
                        pc = null;
                    }
                    else
                    {
                        attempt = cancel;
                    }
                }
            }
            if (pc is null)
            {
                continue;
            }
            try
            {
                await ConnectAsync(pc, pair, cancel.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (ControlChannel.IsConnectionError(exception))
            {
                lock (gate)
                {
                    if (automatic)
                    {
                        error = pair && identity.Peer?.Fingerprint != pc.Peer.Fingerprint
                            ? "配对未完成，请重试" : "连接中断，正在重连";
                    }
                }
            }
            finally
            {
                lock (gate)
                {
                    attempt = null;
                    stopInput = null;
                    connected = false;
                }
            }
        }
    }

    private async Task ConnectAsync(DiscoveredPc pc, bool pair, CancellationToken token)
    {
        using var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        await tcp.ConnectAsync(pc.Endpoint, deadline.Token).ConfigureAwait(false);
        using var stream = new SslStream(tcp.GetStream());
        var channel = await ControlChannel.ConnectAsync(stream, identity, pc.Peer.Fingerprint, deadline.Token)
            .ConfigureAwait(false);
        await channel.SendAsync(new(pair ? Command.Pair : Command.Connect, name,
            SupportsRumble: onRumble is not null, SupportsGuide: true),
            deadline.Token).ConfigureAwait(false);

        if (pair)
        {
            if ((await channel.ReadAsync(deadline.Token).ConfigureAwait(false)).Type != Command.Pending ||
                !await ApproveAsync(channel, pc.Peer, token).ConfigureAwait(false))
            {
                return;
            }
        }
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        var start = await channel.ReadAsync(deadline.Token).ConfigureAwait(false);
        if (start.Type == Command.Reject)
        {
            lock (gate)
            {
                error = "PC 暂时无法接收手柄输入";
            }
            return;
        }
        if (start is not { Type: Command.Start, SessionId: { Length: 16 }, Key: { Length: 32 } })
        {
            throw new InvalidDataException("Invalid session offer.");
        }

        using var packets = new InputPackets(start.SessionId, start.Key, start.SupportsGuide);
        CryptographicOperations.ZeroMemory(start.Key);
        var binding = (IPEndPoint)tcp.Client.LocalEndPoint!;
        using var udp = new UdpClient(new IPEndPoint(binding.Address, 0));
        udp.Connect(pc.Endpoint);

        TaskCompletionSource stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (!automatic)
            {
                return;
            }
            stopInput = stop;
            connected = true;
            error = null;
        }
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        var inputs = SendInputAsync(udp, packets, stop.Task, session.Token);
        var heartbeats = KeepAliveAsync(channel, stop.Task, start.SupportsRumble ? onRumble : null, session.Token);
        try
        {
            await await Task.WhenAny(inputs, heartbeats).ConfigureAwait(false);
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(inputs, heartbeats).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
            }
        }
    }

    private async Task<bool> ApproveAsync(ControlChannel channel, Peer pc, CancellationToken token)
    {
        TaskCompletionSource<bool> approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            request = new(pc.Name, DeviceIdentity.DisplayCode(pc.Fingerprint), DeviceIdentity.DisplayCode(identity.Fingerprint));
            decision = approval;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            if (!await approval.Task.WaitAsync(deadline.Token).ConfigureAwait(false))
            {
                await channel.SendAsync(new(Command.Stop), deadline.Token).ConfigureAwait(false);
                return false;
            }
            await channel.SendAsync(new(Command.Approve), deadline.Token).ConfigureAwait(false);
            if ((await channel.ReadAsync(deadline.Token).ConfigureAwait(false)).Type != Command.Paired)
            {
                return false;
            }
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                identity.Trust(pc);
            }
            return true;
        }
        finally
        {
            lock (gate)
            {
                request = null;
                decision = null;
            }
        }
    }

    private async Task SendInputAsync(UdpClient udp, InputPackets packets, Task stopped, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(2));
        var packet = new byte[InputPackets.Size];
        var previous = default(ControllerState);
        long? sent = null;
        while (!token.IsCancellationRequested)
        {
            var state = stopped.IsCompleted ? default : sample();
            var interval = previous == state ? 50 : 1000d / 120;
            if (sent is null || previous.Buttons != state.Buttons ||
                Stopwatch.GetElapsedTime(sent.Value).TotalMilliseconds >= interval)
            {
                packets.Encode(state, packet);
                await udp.SendAsync(packet, token).ConfigureAwait(false);
                previous = state;
                sent = Stopwatch.GetTimestamp();
            }
            await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
        }
    }

    private static async Task KeepAliveAsync(ControlChannel channel, Task stopped, Action<RumbleState>? onRumble,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var interval = onRumble is null ? 350 : 25;
        try
        {
            while (true)
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(1));
                if (stopped.IsCompleted)
                {
                    await channel.SendAsync(new(Command.Stop), deadline.Token).ConfigureAwait(false);
                    return;
                }
                await channel.SendAsync(new(Command.Ping), deadline.Token).ConfigureAwait(false);
                var reply = await channel.ReadAsync(deadline.Token).ConfigureAwait(false);
                if (reply.Type != Command.Pong)
                {
                    throw new InvalidDataException("Unexpected heartbeat response.");
                }
                onRumble?.Invoke(reply.Rumble.GetValueOrDefault());
                await Task.WhenAny(stopped, Task.Delay(interval, token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            onRumble?.Invoke(default);
        }
    }
}
