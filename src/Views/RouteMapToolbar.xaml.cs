using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MabiCommerceNewLife;

// The overlay's grab bar is its own window so it stays clickable while the map window is click-through.
public partial class RouteMapToolbar : Window
{
    private readonly RouteMapWindow _map;

    public RouteMapToolbar(RouteMapWindow map)
    {
        InitializeComponent();
        _map = map;
        var leftCap = LoadArt("TopBarEndcap.png");
        LeftCapImage.Source = leftCap;
        RightCapImage.Source = leftCap;
        if (LoadArt("TopBarSegment.png") is BitmapSource segment)
            SegmentFill.Background = new ImageBrush(segment)
            {
                TileMode = TileMode.Tile,
                Stretch = Stretch.None,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, segment.PixelWidth, segment.PixelHeight)
            };
        PreviousImage.Source = LoadArt("CenterArrowLeft.png");
        NextImage.Source = LoadArt("CenterArrowRight.png");
        CloseImage.Source = LoadArt("CloseButton.png");
        // Clicking the bar must not also reach a foreground game; RouteMapWindow.HoverEnter makes it foreground on hover.
        // ShowActivated=False still avoids stealing focus when the overlay opens.
        SourceInitialized += (_, _) => NativeWindowStyles.Add(this, NativeWindowStyles.ToolWindow);
        MouseEnter += (_, _) => _map.HoverEnter(this);
    }

    internal static BitmapImage? LoadArt(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "CommerceUI", fileName);
        if (!File.Exists(path)) return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public void ShowState(bool linesMode, bool showOthers, bool clickThrough, string title, int pageIndex, int pageCount)
    {
        MapModeButton.IsChecked = !linesMode;
        MyRouteButton.IsChecked = !showOthers;
        AllRoutesButton.IsChecked = showOthers;
        ClickThroughButton.IsChecked = clickThrough;
        TitleText.Text = title;
        TitleText.ToolTip = string.IsNullOrEmpty(title) ? null : title;
        PageText.Text = pageCount > 0 ? $"{pageIndex + 1}/{pageCount}" : "0/0";
        PreviousButton.IsEnabled = pageIndex > 0;
        NextButton.IsEnabled = pageIndex < pageCount - 1;
    }

    private void Bar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement handle) _map.BeginDrag(handle, e);
    }

    private void MapModeButton_Click(object sender, RoutedEventArgs e) => _map.SetLinesMode(MapModeButton.IsChecked != true);

    private void RoutesButton_Click(object sender, RoutedEventArgs e) =>
        _map.SetOptions(AllRoutesButton.IsChecked == true, ClickThroughButton.IsChecked == true);

    private void ClickThroughButton_Click(object sender, RoutedEventArgs e) =>
        _map.SetOptions(AllRoutesButton.IsChecked == true, ClickThroughButton.IsChecked == true);

    private void PreviousButton_Click(object sender, RoutedEventArgs e) => _map.ShowPage(-1);

    private void NextButton_Click(object sender, RoutedEventArgs e) => _map.ShowPage(1);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => _map.Close();
}

internal static class NativeDrag
{
    private const uint NoSize = 0x0001, NoZOrder = 0x0004, NoActivate = 0x0010;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct NativePoint { public int X; public int Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    public static void MoveTogether(IntPtr first, int firstLeft, int firstTop, IntPtr second, int secondLeft, int secondTop)
    {
        var batch = BeginDeferWindowPos(2);
        if (batch != IntPtr.Zero) batch = DeferWindowPos(batch, first, IntPtr.Zero, firstLeft, firstTop, 0, 0, NoSize | NoZOrder | NoActivate);
        if (batch != IntPtr.Zero) batch = DeferWindowPos(batch, second, IntPtr.Zero, secondLeft, secondTop, 0, 0, NoSize | NoZOrder | NoActivate);
        if (batch != IntPtr.Zero && EndDeferWindowPos(batch)) return;
        SetWindowPos(first, IntPtr.Zero, firstLeft, firstTop, 0, 0, NoSize | NoZOrder | NoActivate);
        SetWindowPos(second, IntPtr.Zero, secondLeft, secondTop, 0, 0, NoSize | NoZOrder | NoActivate);
    }

    // Hovering is not input that grants foreground rights, so SetForegroundWindow is refused while another app is active;
    // SwitchToThisWindow (the Alt+Tab switch) is not subject to that lock.
    public static void Activate(IntPtr handle)
    {
        if (SetForegroundWindow(handle) && GetForegroundWindow() == handle) return;
        SwitchToThisWindow(handle, true);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr handle, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool altTab);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr BeginDeferWindowPos(int count);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr DeferWindowPos(IntPtr batch, IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EndDeferWindowPos(IntPtr batch);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out NativePoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

internal static class NativeWindowStyles
{
    public const int Transparent = 0x00000020;
    public const int ToolWindow = 0x00000080;
    public const int Layered = 0x00080000;
    public const int NoActivate = 0x08000000;
    private const int ExStyleIndex = -20;

    public static void Add(Window window, int flags) => Update(window, style => style | flags);

    public static void Remove(Window window, int flags) => Update(window, style => style & ~flags);

    private static void Update(Window window, Func<int, int> change)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLong(handle, ExStyleIndex);
        SetWindowLong(handle, ExStyleIndex, change(style));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);
}
