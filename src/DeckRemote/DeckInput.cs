using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeckRemote.Core;

namespace DeckRemote;

internal readonly record struct DeckInputView(ControllerState State, bool Forwarding, bool Paused, string? Issue);

[SupportedOSPlatform("linux")]
internal sealed class DeckInput(bool resumeOnFocus = false)
{
    private const int OpenNonBlocking = 0x800;
    private const int CloseOnExec = 0x80000;
    private const short PollIn = 1;
    private const short PollFailure = 0x38;

    private readonly object gate = new();
    private ControllerState state;
    private long? lastReport;
    private bool foreground;
    private bool systemMenuDown;
    private bool menuPaused;
    private bool manuallyPaused;
    private string problem = "正在检测内置手柄";

    public string Status
    {
        get
        {
            lock (gate)
            {
                return !Fresh ? problem : IsPaused ? "手柄转发已暂停" :
                    foreground ? "内置手柄就绪" : "窗口失焦，已暂停转发";
            }
        }
    }

    public DeckInputView View
    {
        get
        {
            lock (gate)
            {
                var fresh = Fresh;
                return new(fresh ? state : default, fresh && foreground && !IsPaused, IsPaused, fresh ? null : problem);
            }
        }
    }

    public void SetForeground(bool value)
    {
        lock (gate)
        {
            if (resumeOnFocus && !foreground && value)
            {
                menuPaused = false;
            }
            foreground = value;
        }
    }

    public void TogglePause()
    {
        lock (gate)
        {
            manuallyPaused = !IsPaused;
            menuPaused = false;
        }
    }

    public ControllerState Sample()
    {
        lock (gate)
        {
            return IsForwarding ? state : default;
        }
    }

    public bool CanForward()
    {
        lock (gate)
        {
            return IsForwarding;
        }
    }

    private bool IsPaused => manuallyPaused || menuPaused || systemMenuDown;
    private bool IsForwarding => foreground && !IsPaused && Fresh;
    private bool Fresh => lastReport is long time && Stopwatch.GetElapsedTime(time).TotalMilliseconds < 150;

    public Task ReadAsync(CancellationToken token)
    {
        return Task.Factory.StartNew(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var path = FindController();
                    if (path is null)
                    {
                        SetProblem("未检测到 Steam Deck 内置手柄");
                    }
                    else
                    {
                        ReadDevice(path, token);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    SetProblem("无法读取内置手柄");
                }
                token.WaitHandle.WaitOne(500);
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void ReadDevice(string path, CancellationToken token)
    {
        var fd = Open(path, OpenNonBlocking | CloseOnExec); // Access mode 0 is O_RDONLY.
        if (fd < 0)
        {
            SetProblem(Marshal.GetLastPInvokeError() is 1 or 13
                ? "当前用户缺少手柄读取权限" : "无法打开内置手柄");
            return;
        }

        var descriptor = new PollDescriptor { Fd = fd, Events = PollIn };
        var bytes = new byte[DeckReport.Size];
        try
        {
            SetProblem("等待内置手柄数据");
            while (!token.IsCancellationRequested)
            {
                var ready = Poll(ref descriptor, 1, 100);
                if (ready < 0 && Marshal.GetLastPInvokeError() == 4)
                {
                    continue;
                }
                if (ready < 0 || (descriptor.ReturnedEvents & PollFailure) != 0)
                {
                    break;
                }
                if (ready == 0 || (descriptor.ReturnedEvents & PollIn) == 0)
                {
                    continue;
                }

                var size = Read(fd, bytes, (nuint)bytes.Length);
                if (size < 0 && Marshal.GetLastPInvokeError() is 4 or 11)
                {
                    continue;
                }
                if (size <= 0)
                {
                    break;
                }
                if (!DeckReport.TryParse(bytes.AsSpan(0, (int)size), out var next, out var systemMenu))
                {
                    continue;
                }
                AcceptReport(next, systemMenu);
            }
        }
        finally
        {
            Close(fd);
            SetProblem("内置手柄已断开");
        }
    }

    internal void AcceptReport(ControllerState next, bool systemMenu)
    {
        lock (gate)
        {
            if (systemMenu && !systemMenuDown && foreground)
            {
                menuPaused = true;
            }
            systemMenuDown = systemMenu;
            state = next;
            lastReport = Stopwatch.GetTimestamp();
            problem = "内置手柄暂时无响应";
        }
    }

    private void SetProblem(string value)
    {
        lock (gate)
        {
            lastReport = null;
            systemMenuDown = false;
            problem = value;
        }
    }

    internal static string? FindController()
    {
        foreach (var entry in Directory.EnumerateDirectories("/sys/class/hidraw", "hidraw*"))
        {
            var metadata = Path.Combine(entry, "device", "uevent");
            try
            {
                var properties = File.ReadAllLines(metadata);
                if (properties.Contains("HID_ID=0003:000028DE:00001205", StringComparer.OrdinalIgnoreCase) &&
                    properties.Any(line => line.StartsWith("HID_PHYS=", StringComparison.Ordinal) &&
                        line.TrimEnd().EndsWith("/input2", StringComparison.Ordinal)))
                {
                    return Path.Combine("/dev", Path.GetFileName(entry));
                }
            }
            catch (IOException)
            {
                // A device can disappear during enumeration.
            }
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int Fd;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int Poll(ref PollDescriptor descriptor, nuint count, int timeout);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint Read(int fd, [Out] byte[] buffer, nuint count);

    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);
}
