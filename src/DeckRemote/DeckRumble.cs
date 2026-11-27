using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DeckRemote.Core;

namespace DeckRemote;

[SupportedOSPlatform("linux")]
internal sealed class DeckRumble(Func<bool> canForward)
{
    private static readonly TimeSpan FeedbackTimeout = TimeSpan.FromMilliseconds(175);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(50);
    private readonly object gate = new();
    private RumbleState feedback;
    private RumbleState applied;
    private long feedbackAt;

    public RumbleState Applied
    {
        get { lock (gate) { return applied; } }
        private set { lock (gate) { applied = value; } }
    }

    public void SetFeedback(RumbleState state)
    {
        lock (gate)
        {
            feedback = state;
            feedbackAt = Stopwatch.GetTimestamp();
        }
    }

    public async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        RumbleDevice? device = null;
        long lastReport = 0;
        long? lastFailure = null;
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var desired = canForward() ? LatestFeedback() : default;
                if (desired == default && Applied == default)
                {
                    continue;
                }
                if (desired != default && (Stopwatch.GetElapsedTime(lastReport) < ReportInterval ||
                    (lastFailure is long failed && Stopwatch.GetElapsedTime(failed) < TimeSpan.FromSeconds(1))))
                {
                    continue;
                }
                try
                {
                    device ??= RumbleDevice.Open();
                    device.Send(desired);
                    Applied = desired;
                    lastReport = Stopwatch.GetTimestamp();
                    lastFailure = null;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Trace.TraceWarning("Steam Deck rumble: {0}", error.Message);
                    device?.Dispose();
                    device = null;
                    Applied = default;
                    lastFailure = Stopwatch.GetTimestamp();
                }
            }
        }
        finally
        {
            device?.Dispose();
            Applied = default;
        }
    }

    private RumbleState LatestFeedback()
    {
        lock (gate)
        {
            return Stopwatch.GetElapsedTime(feedbackAt) < FeedbackTimeout ? feedback : default;
        }
    }

    public static async Task CheckAsync()
    {
        using var device = RumbleDevice.Open();
        device.Send(new RumbleState(96, 0));
        await Task.Delay(220).ConfigureAwait(false);
        device.Send(default);
        await Task.Delay(120).ConfigureAwait(false);
        device.Send(new RumbleState(0, 96));
        await Task.Delay(220).ConfigureAwait(false);
        device.Send(default);
    }

    private sealed class RumbleDevice(int fd) : IDisposable
    {
        // Linux x86-64 HIDIOCSFEATURE, including the report ID byte.
        private static readonly nuint SendFeature = 0xC0004806u | ((uint)DeckReport.RumbleSize << 16);
        private const int ReadWrite = 2;
        private const int CloseOnExec = 0x80000;
        private readonly byte[] buffer = new byte[DeckReport.RumbleSize];
        private bool needsStop;

        public static RumbleDevice Open()
        {
            var path = DeckInput.FindController() ?? throw new IOException("未检测到 Steam Deck 内置手柄");
            var handle = OpenDevice(path, ReadWrite | CloseOnExec);
            if (handle < 0)
            {
                throw new IOException($"无法打开手柄震动接口（errno {Marshal.GetLastPInvokeError()}）");
            }
            return new RumbleDevice(handle);
        }

        public void Send(RumbleState state)
        {
            needsStop |= state != default;
            DeckReport.WriteRumble(buffer, state);
            if (Ioctl(fd, SendFeature, buffer) < 0)
            {
                throw new IOException($"无法写入手柄震动报告（errno {Marshal.GetLastPInvokeError()}）");
            }
            needsStop = state != default;
        }

        public void Dispose()
        {
            if (needsStop)
            {
                DeckReport.WriteRumble(buffer, default);
                Ioctl(fd, SendFeature, buffer);
            }
            CloseDevice(fd);
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int OpenDevice([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int Ioctl(int descriptor, nuint request, [In] byte[] data);

        [DllImport("libc", EntryPoint = "close")]
        private static extern int CloseDevice(int descriptor);
    }
}
