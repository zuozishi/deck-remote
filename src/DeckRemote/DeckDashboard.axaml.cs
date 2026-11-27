using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DeckRemote.Core;

namespace DeckRemote;

internal sealed partial class DeckDashboard : UserControl
{
    private static readonly IBrush Online = Brush.Parse("#26EDB6");
    private static readonly IBrush Offline = Brush.Parse("#788E9C");
    private readonly DeckSender sender;
    private readonly Action togglePause;
    private readonly (Buttons Button, Border Indicator)[] keys;
    private ControllerState shownInput;
    private RumbleState shownRumble;
    private DiscoveredPc[] shownDevices = [];
    private string? shownTrusted;
    private PairingRequest? shownRequest;
    private string? notice;
    private bool showingPaused;

    public DeckDashboard(DeckSender sender, Action togglePause)
    {
        this.sender = sender;
        this.togglePause = togglePause;
        InitializeComponent();
        LeftStick.SetLabel("L3");
        RightStick.SetLabel("R3");
        keys =
        [
            (Buttons.Up, DpadUp), (Buttons.Down, DpadDown), (Buttons.Left, DpadLeft), (Buttons.Right, DpadRight),
            (Buttons.LeftShoulder, LeftShoulder), (Buttons.RightShoulder, RightShoulder),
            (Buttons.Back, ViewKey), (Buttons.Start, MenuKey),
            (Buttons.A, FaceA), (Buttons.B, FaceB), (Buttons.X, FaceX), (Buttons.Y, FaceY)
        ];
    }

    public void UpdateInput(ControllerState state, RumbleState rumble)
    {
        var changed = shownInput.Buttons ^ state.Buttons;
        if (changed != 0)
        {
            foreach (var (button, indicator) in keys)
            {
                if ((changed & button) != 0)
                {
                    indicator.Classes.Set("pressed", (state.Buttons & button) != 0);
                }
            }
        }
        LeftStick.Update(state.Lx, state.Ly, (state.Buttons & Buttons.LeftStick) != 0);
        RightStick.Update(state.Rx, state.Ry, (state.Buttons & Buttons.RightStick) != 0);
        UpdateMeter(LeftTrigger, LeftTriggerValue, shownInput.Lt, state.Lt);
        UpdateMeter(RightTrigger, RightTriggerValue, shownInput.Rt, state.Rt);
        UpdateMeter(LeftRumble, LeftRumbleValue, shownRumble.LargeMotor, rumble.LargeMotor);
        UpdateMeter(RightRumble, RightRumbleValue, shownRumble.SmallMotor, rumble.SmallMotor);
        shownInput = state;
        shownRumble = rumble;
    }

    public void UpdateConnection(SenderStatus status, DeckInputView input, string? error)
    {
        var connected = status.Connected && error is null;
        ConnectionLabel.Text = connected ? "已连接" : error is not null ? "连接不可用" :
            status.Connecting ? "连接中" : status.Peer is not null && !status.Automatic ? "已断开" : "未连接";
        ConnectionDot.Fill = connected ? Online : Offline;
        ConnectedPeer.Text = status.Peer?.Name;
        ConnectedPeer.IsVisible = PeerDivider.IsVisible = connected && status.Peer is not null;
        PauseButton.IsVisible = DisconnectButton.IsVisible = connected;
        if (showingPaused != input.Paused)
        {
            showingPaused = input.Paused;
            PauseIcon.IsVisible = !showingPaused;
            ResumeIcon.IsVisible = showingPaused;
            var actionName = showingPaused ? "继续转发" : "暂停转发";
            AutomationProperties.SetName(PauseButton, actionName);
            ToolTip.SetTip(PauseButton, actionName);
        }

        var forwarding = connected && input.Forwarding;
        ForwardingLabel.Text = input.Issue ?? (forwarding ? "转发中" : connected ? "已暂停" : "本地输入");
        ForwardingLabel.Classes.Set("active", forwarding);
        ForwardingLabel.Classes.Set("paused", connected && !forwarding && input.Issue is null);
        Telemetry.IsVisible = connected;
        Devices.IsVisible = !connected;
        if (!connected)
        {
            UpdateDevices(status, error);
        }
    }

    private void UpdateDevices(SenderStatus status, string? error)
    {
        var request = error is null ? status.Pairing : null;
        DeviceHeading.Text = request is null ? "连接 PC" : "配对";
        Pairing.IsVisible = request is not null;
        if (shownRequest != request)
        {
            shownRequest = request;
            PcCode.Text = request?.PcCode;
            DeckCode.Text = request?.DeckCode;
            ApproveButton.IsEnabled = true;
            ApproveButton.Content = "确认一致";
        }

        var detail = error ?? notice ?? status.Error ??
            (request is not null ? request.Name : status.Connecting ? status.Peer?.Name :
                status.Devices.Count == 0 ? "确认 PC 接收端已启动，并与 Steam Deck 连接到同一局域网。" : null);
        ConnectionHelp.Text = detail;
        ConnectionHelp.IsVisible = detail is not null;
        var canPick = error is null && !status.Connecting && request is null;
        DeviceRows.IsVisible = canPick;
        ReconnectButton.IsVisible = error is null && request is null &&
            (status.Connecting || (!status.Automatic && status.Peer is not null && status.Devices.Count == 0));
        ReconnectButton.Content = status.Connecting ? "取消连接" : "重新连接";
        ForgetButton.IsVisible = canPick && status.Peer is not null;
        if (!canPick || (shownTrusted == status.Peer?.Fingerprint && shownDevices.SequenceEqual(status.Devices)))
        {
            return;
        }

        shownDevices = status.Devices.ToArray();
        shownTrusted = status.Peer?.Fingerprint;
        DeviceRows.Children.Clear();
        foreach (var pc in shownDevices)
        {
            var trusted = pc.Peer.Fingerprint == shownTrusted;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            var name = new TextBlock
            {
                Text = pc.Peer.Name, FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var action = new Button { Content = trusted ? "连接" : "配对", Padding = new(13, 8) };
            action.Classes.Add("deck-action");
            AutomationProperties.SetName(action, $"{action.Content} {pc.Peer.Name}");
            action.Click += (_, _) =>
            {
                notice = null;
                sender.Connect(pc.Peer.Fingerprint);
            };
            Grid.SetColumn(action, 1);
            row.Children.Add(name);
            row.Children.Add(action);
            DeviceRows.Children.Add(row);
        }
    }

    private static void UpdateMeter(ProgressBar meter, TextBlock label, byte oldValue, byte value)
    {
        if (oldValue != value)
        {
            meter.Value = value;
            label.Text = $"{(value * 100 + 127) / 255}%";
        }
    }

    private void OnPause(object? source, RoutedEventArgs e) => togglePause();

    private void OnDisconnect(object? source, RoutedEventArgs e) => sender.Disconnect();

    private void OnReconnect(object? source, RoutedEventArgs e)
    {
        notice = null;
        var status = sender.Status;
        if (status.Connecting)
        {
            sender.Disconnect();
        }
        else if (status.Peer is { } peer)
        {
            sender.Connect(peer.Fingerprint);
        }
    }

    private void OnApprove(object? source, RoutedEventArgs e)
    {
        sender.DecidePairing(true);
        ApproveButton.IsEnabled = false;
        ApproveButton.Content = "等待另一端确认";
    }

    private void OnReject(object? source, RoutedEventArgs e) => sender.DecidePairing(false);

    private void OnForget(object? source, RoutedEventArgs e)
    {
        try
        {
            sender.Forget();
            notice = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            notice = "无法更新本地配对记录";
        }
    }
}
