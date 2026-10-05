using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MabiCommerceNewLife;

// Keeps the planner's always-on-top windows above other windows. Two things push them under:
// - Other programs (screenshot tools) can clear the native always-on-top style while WPF still reports Topmost=true,
//   so WPF never re-applies it. The style is restored whenever it goes missing.
// - Another always-on-top window (for example a game client restored from minimized) takes the top of the topmost band.
//   When another program's window becomes foreground and stays there, the planner windows are raised back above it,
//   without taking focus. Screenshot-tool capture screens are left alone so they can still be used.
// Activating any watched window raises the whole group, so the overlay comes back with the planner.
internal static class TopmostGuard
{
    private const int ExStyleIndex = -20;
    private const int TopmostStyle = 0x00000008;
    private const uint NoSize = 0x0001, NoMove = 0x0002, NoActivate = 0x0010, NoOwnerZOrder = 0x0200;
    private static readonly IntPtr TopmostInsertAfter = new(-1);
    private static readonly string[] CaptureTitlePrefixes = ["ShareX", "Snipping Tool", "Screen Snipping", "Greenshot"];
    private static readonly List<Window> Windows = [];
    private static DispatcherTimer? _timer;
    private static IntPtr _lastForeground;
    private static bool _raisePending;
    private static bool _raising;

    public static void Watch(Window window)
    {
        if (Windows.Contains(window)) return;
        Windows.Add(window);
        window.Closed += (_, _) => Windows.Remove(window);
        window.Activated += (_, _) => RaiseAll();
        window.Deactivated += (_, _) => Restore(window);
        _timer ??= CreateTimer();
    }

    private static DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        return timer;
    }

    private static void Tick()
    {
        foreach (var window in Windows.ToArray()) Restore(window);
        var foreground = GetForegroundWindow();
        if (foreground != _lastForeground)
        {
            // Wait one tick so a window that is still restoring or re-ordering itself has settled.
            _lastForeground = foreground;
            _raisePending = foreground != IntPtr.Zero && !IsOwnWindow(foreground);
            return;
        }
        if (!_raisePending) return;
        _raisePending = false;
        if (!IsCaptureWindow(foreground)) RaiseAll();
    }

    private static bool IsOwnWindow(IntPtr handle) =>
        Application.Current?.Windows.Cast<Window>().Any(window => new WindowInteropHelper(window).Handle == handle) == true;

    private static bool IsCaptureWindow(IntPtr handle)
    {
        var title = new StringBuilder(256);
        GetWindowText(handle, title, title.Capacity);
        var text = title.ToString();
        return CaptureTitlePrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    // Raised in watch order (planner first), so the route overlay ends up above the planner.
    private static void RaiseAll()
    {
        if (_raising) return;
        _raising = true;
        try
        {
            foreach (var window in Windows.ToArray())
            {
                if (!ShouldStayOnTop(window, out var handle)) continue;
                SetWindowPos(handle, TopmostInsertAfter, 0, 0, 0, 0, NoSize | NoMove | NoActivate);
            }
        }
        finally
        {
            _raising = false;
        }
    }

    private static void Restore(Window window)
    {
        if (!ShouldStayOnTop(window, out var handle) || (GetWindowLong(handle, ExStyleIndex) & TopmostStyle) != 0) return;
        SetWindowPos(handle, TopmostInsertAfter, 0, 0, 0, 0, NoSize | NoMove | NoActivate | NoOwnerZOrder);
    }

    private static bool ShouldStayOnTop(Window window, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        if (!window.Topmost || !window.IsVisible || window.WindowState == WindowState.Minimized) return false;
        handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
