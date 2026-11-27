using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DeckRemote.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace DeckRemote;

internal sealed partial class MainWindow : Window
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly CancellationTokenSource stop = new();
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(125) };
    private readonly DispatcherTimer inputRefresh = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private DeviceIdentity? identity;
    private PcReceiver? receiver;
    private DeckSender? sender;
    private DeckInput? input;
    private DeckRumble? deckRumble;
    private DeckDashboard? deckDashboard;
    private GamescopeFocus? gamescopeFocus;
    private Task worker = Task.CompletedTask;
    private Task reader = Task.CompletedTask;
    private Task vibration = Task.CompletedTask;
    private Task? shutdown;
    private PairingRequest? shownRequest;
    private volatile string? serviceError;
    private string? notice;
    private bool updatingAutoStart;
    private bool showingPaused;

    public MainWindow()
    {
        InitializeComponent();
        if (OperatingSystem.IsLinux())
        {
            Width = 1040;
            Height = 700;
            MinWidth = 760;
            MinHeight = 610;
            if (Program.GameMode)
            {
                WindowState = WindowState.FullScreen;
            }
        }
        Opened += (_, _) => Start();
        Activated += (_, _) => SetForeground(true);
        Deactivated += (_, _) => SetForeground(false);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (sender?.Status.Connected == true)
            {
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        refresh.Tick += (_, _) => Refresh();
        inputRefresh.Tick += (_, _) => RefreshInput();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty || e.Property == WindowStateProperty)
            {
                UpdateInputRefresh();
            }
        };
    }

    public void Present()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (OperatingSystem.IsWindows() && shutdown is null && !e.IsProgrammatic)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    public Task StopAsync()
    {
        inputRefresh.Stop();
        refresh.Stop();
        if (OperatingSystem.IsLinux())
        {
            gamescopeFocus?.Dispose();
            gamescopeFocus = null;
        }
        return shutdown ??= StopWorkersAsync();
    }

    private async Task StopWorkersAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(worker, reader, vibration).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            identity?.Dispose();
            stop.Dispose();
        }
    }

    private void Start()
    {
        if (identity is not null)
        {
            return;
        }
        try
        {
            if (OperatingSystem.IsWindows())
            {
                StartPc();
            }
            else if (OperatingSystem.IsLinux())
            {
                StartDeck();
            }
            else
            {
                serviceError = "请在 Windows 或 SteamOS 上运行";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            CryptographicException or System.Text.Json.JsonException or System.Security.SecurityException)
        {
            Trace.WriteLine(error);
            serviceError = "无法读取本地配对数据";
        }
        refresh.Start();
        Refresh();
    }

    [SupportedOSPlatform("windows")]
    private void StartPc()
    {
        var device = new DeviceIdentity(Program.DataDirectory,
            data => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser),
            data => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser));
        identity = device;
        var windows = new PcReceiver(device, Environment.MachineName, new VigemOutput());
        receiver = windows;
        worker = RunWorker(() => windows.RunAsync(stop.Token));
        InputPanel.IsVisible = true;
        UpdateInputRefresh();
        PcPanel.IsVisible = true;
        StartupRow.IsVisible = true;
        updatingAutoStart = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            AutoStart.IsChecked = key?.GetValue("DeckRemote") is string;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
        {
            AutoStart.IsEnabled = false;
        }
        finally
        {
            updatingAutoStart = false;
        }
    }

    [SupportedOSPlatform("linux")]
    private void StartDeck()
    {
        var device = new DeviceIdentity(Program.DataDirectory);
        identity = device;
        var deckInput = new DeckInput(resumeOnFocus: Program.GameMode);
        input = deckInput;
        gamescopeFocus = Program.GameMode ? GamescopeFocus.TryOpen() : null;
        SetForeground(IsActive);
        reader = RunWorker(() => deckInput.ReadAsync(stop.Token));
        var rumble = new DeckRumble(deckInput.CanForward);
        deckRumble = rumble;
        vibration = RunWorker(() => rumble.RunAsync(stop.Token));
        var deck = new DeckSender(device, "Steam Deck", deckInput.Sample, rumble.SetFeedback);
        sender = deck;
        deckDashboard = new DeckDashboard(deck, deckInput.TogglePause);
        Content = deckDashboard;
        UpdateInputRefresh();
        worker = RunWorker(() => deck.RunAsync(stop.Token));
    }

    private Task RunWorker(Func<Task> run) => Task.Run(async () =>
    {
        try
        {
            await run().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (SocketException error)
        {
            Trace.WriteLine(error);
            serviceError = error.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"端口 {LanDiscovery.Port} 已被占用" : "无法启动局域网连接";
        }
        catch (Exception error)
        {
            Trace.WriteLine(error);
            serviceError = "应用服务已停止，请重新打开 Deck Remote";
        }
    });

    private void SetForeground(bool value)
    {
        if (OperatingSystem.IsLinux())
        {
            input?.SetForeground(gamescopeFocus?.IsForeground ?? value);
        }
    }

    private void Refresh()
    {
        if (OperatingSystem.IsLinux() && deckDashboard is { } dashboard && sender is { } deck && input is { } capture)
        {
            if (gamescopeFocus is { } focus)
            {
                capture.SetForeground(focus.IsForeground);
            }
            dashboard.UpdateConnection(deck.Status, capture.View, serviceError);
            return;
        }
        if (serviceError is { } unavailable)
        {
            ConnectionTitle.Text = "接收不可用";
            ConnectionDetail.Text = unavailable;
            ConnectionDetail.IsVisible = true;
            ConnectionTitle.Classes.Set("ready", false);
            ConnectionTitle.Classes.Set("warning", true);
            ConnectionDot.Classes.Set("ready", false);
            ConnectionDot.Classes.Set("warning", true);
            ConnectionAction.IsVisible = false;
            SecondaryAction.IsVisible = false;
            MoreButton.IsVisible = false;
            PairingPanel.IsVisible = false;
            return;
        }
        if (receiver is { } pc)
        {
            RefreshPc(pc.Status);
        }
    }

    private void UpdateInputRefresh()
    {
        var visible = (receiver is not null || deckDashboard is not null) && shutdown is null &&
            IsVisible && WindowState != WindowState.Minimized;
        inputRefresh.IsEnabled = visible;
        if (visible)
        {
            RefreshInput();
        }
    }

    private void RefreshInput()
    {
        if (OperatingSystem.IsLinux() && deckDashboard is { } dashboard && input is { } capture)
        {
            dashboard.UpdateInput(capture.View.State, deckRumble?.Applied ?? default);
        }
        else
        {
            InputPreview.Update(serviceError is null ? receiver?.InputState ?? default : default);
        }
    }

    private void RefreshPc(ReceiverStatus status)
    {
        DeviceName.Text = status.Peer?.Name ?? "Steam Deck";
        ConnectionTitle.Text = status.Connected ? "已连接" : status.Paused ? "接收已暂停" :
            status.PairingOpen ? "等待配对" : "未连接";
        ConnectionTitle.Classes.Set("ready", status.Connected);
        ConnectionDot.Classes.Set("ready", status.Connected);
        ConnectionTitle.Classes.Set("warning", status.Paused);
        ConnectionDot.Classes.Set("warning", status.Paused);
        ConnectionDetail.IsVisible = status.PairingOpen && status.Pairing is null;
        ConnectionDetail.Text = ConnectionDetail.IsVisible ? "在 Steam Deck 上选择这台 PC。" : null;
        ConnectionAction.IsVisible = !status.Connected && !status.Paused && status.Pairing is null;
        ConnectionAction.Content = status.PairingOpen ? "取消配对" : "配对设备";
        DriverStatus.Text = status.DriverReady ? "就绪" : status.Error is not null ? "不可用" : "检测中";
        DriverStatus.Classes.Set("ready", status.DriverReady);
        DriverStatus.Classes.Set("warning", status.Error is not null);
        DriverIcon.Classes.Set("ready", status.DriverReady);
        DriverIcon.Classes.Set("warning", status.Error is not null);
        DriverDetail.Text = status.Error;
        DriverDetail.IsVisible = status.Error is not null;
        DriverActions.IsVisible = status.Error is not null;
        SecondaryAction.IsVisible = status.Peer is not null || status.Paused;
        if (showingPaused != status.Paused)
        {
            showingPaused = status.Paused;
            PauseIcon.IsVisible = !showingPaused;
            ResumeIcon.IsVisible = showingPaused;
            var action = showingPaused ? "恢复接收" : "暂停接收";
            AutomationProperties.SetName(SecondaryAction, action);
            ToolTip.SetTip(SecondaryAction, action);
        }
        MoreButton.IsVisible = status.Peer is not null && status.Pairing is null;
        LocalStatus.Text = notice;
        LocalStatus.IsVisible = notice is not null;
        RefreshPairing(status.Pairing);
    }

    private void RefreshPairing(PairingRequest? request)
    {
        PairingPanel.IsVisible = request is not null;
        if (shownRequest == request)
        {
            return;
        }
        shownRequest = request;
        if (request is not null)
        {
            PairingTitle.Text = $"与 {request.Name} 配对";
            PcCode.Text = request.PcCode;
            DeckCode.Text = request.DeckCode;
            ApproveButton.IsEnabled = true;
            ApproveButton.Content = "确认一致";
        }
    }

    private void OnConnectionAction(object? source, RoutedEventArgs e)
    {
        notice = null;
        if (receiver is { } pc)
        {
            if (pc.Status.PairingOpen)
            {
                pc.DecidePairing(false);
            }
            else
            {
                pc.OpenPairing();
            }
        }
        Refresh();
    }

    private void OnSecondaryAction(object? source, RoutedEventArgs e)
    {
        if (receiver is { } pc)
        {
            pc.SetPaused(!pc.Status.Paused);
        }
        Refresh();
    }

    private void OnApprove(object? source, RoutedEventArgs e)
    {
        receiver?.DecidePairing(true);
        ApproveButton.IsEnabled = false;
        ApproveButton.Content = "等待另一端确认";
    }

    private void OnReject(object? source, RoutedEventArgs e)
    {
        receiver?.DecidePairing(false);
    }

    private void OnForget(object? source, RoutedEventArgs e)
    {
        try
        {
            receiver?.Forget();
            notice = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            notice = "无法更新本地配对记录";
        }
        Refresh();
    }

    private async void OnDriverCheck(object? source, RoutedEventArgs e)
    {
        if (receiver is { } pc)
        {
            DriverActions.IsEnabled = false;
            try
            {
                await Task.Run(pc.CheckDriver);
            }
            finally
            {
                DriverActions.IsEnabled = true;
                Refresh();
            }
        }
    }

    private async void OnDriverDownload(object? source, RoutedEventArgs e)
    {
        const string release = "https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0";
        try
        {
            await Launcher.LaunchUriAsync(new Uri(release));
        }
        catch (Exception error)
        {
            Trace.WriteLine(error);
            notice = "无法打开驱动下载页面";
        }
    }

    private void OnAutoStartChanged(object? source, RoutedEventArgs e)
    {
        if (updatingAutoStart || !OperatingSystem.IsWindows() || receiver is null)
        {
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (AutoStart.IsChecked == true)
            {
                var exe = Path.Combine(AppContext.BaseDirectory, "DeckRemote.exe");
                key.SetValue("DeckRemote", $"\"{exe}\" --tray");
            }
            else
            {
                key.DeleteValue("DeckRemote", throwOnMissingValue: false);
            }
            notice = null;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            notice = "无法保存登录启动设置";
            updatingAutoStart = true;
            AutoStart.IsChecked = AutoStart.IsChecked != true;
            updatingAutoStart = false;
        }
    }
}
