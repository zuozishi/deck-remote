using Avalonia;
using System.Runtime.Versioning;

namespace DeckRemote;

internal static class Program
{
    public static bool TrayLaunch { get; private set; }
    public static bool GameMode { get; private set; }
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeckRemote");

    [STAThread]
    private static int Main(string[] args)
    {
        if (OperatingSystem.IsWindows() && args.Contains("--check-driver"))
        {
            return CheckDriver();
        }
        if (OperatingSystem.IsLinux() && args.Contains("--check-input"))
        {
            return CheckInput().GetAwaiter().GetResult();
        }
        if (OperatingSystem.IsLinux() && args.Contains("--check-rumble"))
        {
            return CheckRumble().GetAwaiter().GetResult();
        }
        TrayLaunch = OperatingSystem.IsWindows() && args.Contains("--tray");
        GameMode = OperatingSystem.IsLinux() && (args.Contains("--game-mode") ||
            string.Equals(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), "gamescope",
                StringComparison.OrdinalIgnoreCase));
        using var instance = new Mutex(true, "DeckRemote-592ED17E", out var first);
        if (!first)
        {
            return 0;
        }
        return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }

    [SupportedOSPlatform("windows")]
    private static int CheckDriver()
    {
        try
        {
            using var output = new VigemOutput();
            output.CheckXInput();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task<int> CheckRumble()
    {
        try
        {
            await DeckRumble.CheckAsync().ConfigureAwait(false);
            Console.WriteLine("Steam Deck 左右震动和停止报告已发送。");
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    [SupportedOSPlatform("linux")]
    private static async Task<int> CheckInput()
    {
        using var stop = new CancellationTokenSource();
        var capture = new DeckInput();
        capture.SetForeground(true);
        var worker = capture.ReadAsync(stop.Token);
        var previous = capture.Sample();
        var received = false;
        string? lastStatus = null;
        for (var index = 0; index < 400; index++)
        {
            var status = capture.Status;
            received |= status is "内置手柄就绪" or "手柄转发已暂停";
            if (status != lastStatus)
            {
                Console.WriteLine(status);
                lastStatus = status;
            }
            var current = capture.Sample();
            if (current.Buttons != previous.Buttons || current.Lt != previous.Lt || current.Rt != previous.Rt ||
                Math.Abs(current.Lx - previous.Lx) > 128 || Math.Abs(current.Ly - previous.Ly) > 128 ||
                Math.Abs(current.Rx - previous.Rx) > 128 || Math.Abs(current.Ry - previous.Ry) > 128)
            {
                Console.WriteLine(current);
                previous = current;
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
        await stop.CancelAsync().ConfigureAwait(false);
        await worker.ConfigureAwait(false);
        return received ? 0 : 1;
    }
}
