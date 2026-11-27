using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeckRemote;

[SupportedOSPlatform("linux")]
internal sealed class GamescopeFocus(nint display, nuint root, nuint focusedApp, uint appId) : IDisposable
{
    private const nuint Cardinal = 6;

    public static GamescopeFocus? TryOpen()
    {
        if (!uint.TryParse(Environment.GetEnvironmentVariable("SteamAppId"), out var appId) || appId == 0)
        {
            return null;
        }

        var windowDisplay = XOpenDisplay(null);
        if (windowDisplay == 0)
        {
            return null;
        }

        var gameDisplay = windowDisplay;
        var opened = false;
        try
        {
            var windowRoot = XDefaultRootWindow(windowDisplay);
            var compositor = ReadCardinal(windowDisplay, windowRoot, "GAMESCOPE_PID");
            if (compositor is null)
            {
                return null;
            }

            // SteamOS can launch games on a second Xwayland server. Gamescope publishes focus on the first.
            if (ReadCardinal(windowDisplay, windowRoot, "GAMESCOPE_XWAYLAND_SERVER_ID") is > 0)
            {
                var displayName = ReadSessionDisplay();
                if (displayName is null)
                {
                    return null;
                }
                gameDisplay = XOpenDisplay(displayName);
            }

            if (gameDisplay == 0)
            {
                return null;
            }
            var root = XDefaultRootWindow(gameDisplay);
            var focusedApp = XInternAtom(gameDisplay, "GAMESCOPE_FOCUSED_APP", 1);
            if (focusedApp == 0 || ReadCardinal(gameDisplay, root, "GAMESCOPE_PID") != compositor ||
                ReadCardinal(gameDisplay, root, "GAMESCOPE_XWAYLAND_SERVER_ID") is > 0)
            {
                return null;
            }

            opened = true;
            return new GamescopeFocus(gameDisplay, root, focusedApp, appId);
        }
        finally
        {
            if (windowDisplay != gameDisplay || !opened)
            {
                XCloseDisplay(windowDisplay);
            }
            if (gameDisplay != 0 && gameDisplay != windowDisplay && !opened)
            {
                XCloseDisplay(gameDisplay);
            }
        }
    }

    public bool IsForeground => ReadCardinal(display, root, focusedApp) == appId;

    public void Dispose() => XCloseDisplay(display);

    private static string? ReadSessionDisplay()
    {
        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is not { Length: > 0 } runtime)
        {
            return null;
        }
        try
        {
            return File.ReadLines(Path.Combine(runtime, "gamescope-environment"))
                .FirstOrDefault(line => line.StartsWith("DISPLAY=:", StringComparison.Ordinal))?[8..];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static uint? ReadCardinal(nint connection, nuint root, string name) =>
        ReadCardinal(connection, root, XInternAtom(connection, name, 1));

    private static uint? ReadCardinal(nint connection, nuint root, nuint atom)
    {
        if (atom == 0)
        {
            return null;
        }
        var result = XGetWindowProperty(connection, root, atom, 0, 1, 0, Cardinal, out var type,
            out var format, out var count, out _, out var data);
        try
        {
            // Xlib returns 32-bit X11 properties in native unsigned longs.
            return result == 0 && type == Cardinal && format == 32 && count == 1 && data != 0
                ? unchecked((uint)Marshal.ReadIntPtr(data))
                : null;
        }
        finally
        {
            if (data != 0)
            {
                XFree(data);
            }
        }
    }

    [DllImport("libX11.so.6")]
    private static extern nint XOpenDisplay([MarshalAs(UnmanagedType.LPUTF8Str)] string? displayName);

    [DllImport("libX11.so.6")]
    private static extern nuint XDefaultRootWindow(nint display);

    [DllImport("libX11.so.6")]
    private static extern nuint XInternAtom(nint display, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int onlyIfExists);

    [DllImport("libX11.so.6")]
    private static extern int XGetWindowProperty(nint display, nuint window, nuint property, nint offset, nint length,
        int delete, nuint requestedType, out nuint actualType, out int format, out nuint count, out nuint remaining,
        out nint data);

    [DllImport("libX11.so.6")]
    private static extern int XFree(nint data);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(nint display);
}
