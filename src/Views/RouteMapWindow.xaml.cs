using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace MabiCommerceNewLife;

public sealed class RouteMapOverlaySettings
{
    public bool LinesMode { get; set; }
    public bool ShowOtherRoutes { get; set; } = true;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
}

// Topmost, resizable route overlay. The grab bar lives in RouteMapToolbar so it stays usable in click-through mode.
public partial class RouteMapWindow : Window
{
    private const double SideMargin = 30;
    private const double EdgeMargin = 6;
    private static readonly Brush SelectedLineBrush = Frozen(new SolidColorBrush(Color.FromRgb(255, 196, 58)));
    private static readonly Brush HaloBrush = Frozen(new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)));
    private static readonly Brush OtherLineBrush = Frozen(new SolidColorBrush(Color.FromArgb(150, 90, 170, 255)));
    private static readonly Brush StartBrush = Frozen(new SolidColorBrush(Color.FromRgb(70, 210, 90)));

    private readonly RouteMapData _data;
    private readonly RouteMapOverlaySettings _settings;
    private readonly Action _saveSettings;
    private readonly RouteMapToolbar _bar;
    private RouteMapChevronWindow? _previousChevronWindow;
    private RouteMapChevronWindow? _nextChevronWindow;
    private readonly Dictionary<string, BitmapImage> _images = new(StringComparer.OrdinalIgnoreCase);
    private RouteMapPlan _plan = new([], []);
    private int _pageIndex;
    private bool _clickThrough;
    private bool _syncingBar;
    private bool _dragging;
    // The user's own window size while lines mode sizes the window to a calibrated in-game map.
    private Size? _freeSize;

    public RouteMapWindow(RouteMapData data, RouteMapOverlaySettings settings, Action saveSettings)
    {
        InitializeComponent();
        _data = data;
        _settings = settings;
        _saveSettings = saveSettings;
        PreviousChevronImage.Source = RouteMapToolbar.LoadArt("CenterArrowLeft.png");
        NextChevronImage.Source = RouteMapToolbar.LoadArt("CenterArrowRight.png");
        ApplySavedBounds(settings);
        _bar = new RouteMapToolbar(this);
        LocationChanged += (_, _) => { PlaceBar(); PlaceChevronWindows(); };
        SizeChanged += (_, _) => PlaceBar();
        _bar.LocationChanged += (_, _) => FollowBar();
        Loaded += (_, _) =>
        {
            // WPF only accepts an owner that has already been shown.
            _bar.Owner = this;
            PlaceBar();
            _bar.Show();
            TopmostGuard.Watch(this);
            TopmostGuard.Watch(_bar);
            Render();
        };
        Closing += (_, _) => SaveBounds();
        MouseEnter += (_, _) => { if (!_clickThrough) HoverEnter(this); };
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private void ApplySavedBounds(RouteMapOverlaySettings settings)
    {
        if (settings.Width is double width && settings.Height is double height && width >= MinWidth && height >= MinHeight)
        {
            Width = width;
            Height = height;
        }
        if (settings.Left is not double left || settings.Top is not double top)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }
        // Keep the grab bar reachable when the screen layout changed since the last session.
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        Left = Math.Clamp(left, screen.Left - Width + 80, screen.Right - 80);
        Top = Math.Clamp(top, screen.Top + 22, screen.Bottom - 40);
    }

    private void SaveBounds()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty) return;
        // Layout sizing keeps the right edge fixed, so save the left edge that pairs with the saved free width.
        _settings.Left = _freeSize is Size free ? bounds.Right - free.Width : bounds.Left;
        _settings.Top = bounds.Top;
        _settings.Width = _freeSize?.Width ?? bounds.Width;
        _settings.Height = _freeSize?.Height ?? bounds.Height;
        _saveSettings();
    }

    public const double DefaultWidth = 520;
    public const double DefaultHeight = 420;

    // Failsafe from Settings: default size, centered on the primary work area with the grab bar visible.
    public void ResetPosition()
    {
        WindowState = WindowState.Normal;
        _freeSize = null;
        Width = DefaultWidth;
        Height = DefaultHeight;
        var area = SystemParameters.WorkArea;
        var barHeight = _bar.Height is double h && !double.IsNaN(h) ? h : 0;
        Left = area.Left + Math.Max(0, (area.Width - DefaultWidth) / 2);
        Top = area.Top + barHeight + Math.Max(0, (area.Height - barHeight - DefaultHeight) / 2);
        PlaceBar();
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Width = DefaultWidth;
        _settings.Height = DefaultHeight;
        _saveSettings();
        Render();
    }

    private void PlaceBar()
    {
        if (_syncingBar || _dragging || (!_bar.IsLoaded && !IsLoaded)) return;
        _syncingBar = true;
        try
        {
            _bar.Width = Math.Max(ActualWidth, _bar.MinWidth);
            _bar.Left = Left;
            _bar.Top = Top - _bar.Height;
        }
        finally
        {
            _syncingBar = false;
        }
    }

    private void FollowBar()
    {
        if (_syncingBar || _dragging) return;
        _syncingBar = true;
        try
        {
            Left = _bar.Left;
            Top = _bar.Top + _bar.Height;
        }
        finally
        {
            _syncingBar = false;
        }
    }

    // The game reads the mouse itself while it is the foreground window, so a click on the overlay would also reach it.
    // Taking focus on hover makes the overlay foreground before the button goes down. Focus is not handed back on
    // leave: the game would see a press without its release and act as if the button were held. The user clicks back in.
    internal void HoverEnter(Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || IsOverlayWindow(NativeDrag.GetForegroundWindow())) return;
        NativeDrag.Activate(handle);
    }

    private bool IsOverlayWindow(IntPtr handle) => handle != IntPtr.Zero
        && new Window?[] { this, _bar, _previousChevronWindow, _nextChevronWindow }
            .Any(window => window is not null && handle == new System.Windows.Interop.WindowInteropHelper(window).Handle);

    // Click-through makes the whole map window transparent to input, so the chevrons move into small owned windows
    // laid over their in-map spots while it is on.
    private void PlaceChevronWindows()
    {
        if (!IsLoaded) return;
        if (!_clickThrough)
        {
            _previousChevronWindow?.Hide();
            _nextChevronWindow?.Hide();
            PreviousChevronImage.Opacity = NextChevronImage.Opacity = 1;
            return;
        }
        _previousChevronWindow ??= new RouteMapChevronWindow(this, PreviousChevronImage.Source, "Previous map on this route", () => ShowPage(-1));
        _nextChevronWindow ??= new RouteMapChevronWindow(this, NextChevronImage.Source, "Next map on this route", () => ShowPage(1));
        PreviousChevronImage.Opacity = NextChevronImage.Opacity = 0;
        _previousChevronWindow.Mirror(PreviousChevron, Left, Top);
        _nextChevronWindow.Mirror(NextChevron, Left, Top);
    }

    // DragMove moves one window and lets the other chase it through Left/Top, which WPF applies as two separate
    // moves and makes the pair jitter. Instead, both windows move together in device pixels from the cursor delta.
    internal void BeginDrag(UIElement handle, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || _dragging) return;
        var mapHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var barHandle = new System.Windows.Interop.WindowInteropHelper(_bar).Handle;
        if (mapHandle == IntPtr.Zero || barHandle == IntPtr.Zero
            || !NativeDrag.GetCursorPos(out var cursorStart)
            || !NativeDrag.GetWindowRect(mapHandle, out var mapStart)
            || !NativeDrag.GetWindowRect(barHandle, out var barStart)) return;
        if (!handle.CaptureMouse()) return;
        _dragging = true;
        e.Handled = true;

        void Move(object? sender, MouseEventArgs args)
        {
            if (!NativeDrag.GetCursorPos(out var cursor)) return;
            int dx = cursor.X - cursorStart.X, dy = cursor.Y - cursorStart.Y;
            NativeDrag.MoveTogether(barHandle, barStart.Left + dx, barStart.Top + dy, mapHandle, mapStart.Left + dx, mapStart.Top + dy);
        }
        void End(object? sender, EventArgs args)
        {
            handle.MouseMove -= Move;
            handle.LostMouseCapture -= End;
            handle.MouseLeftButtonUp -= End;
            if (handle.IsMouseCaptured) handle.ReleaseMouseCapture();
            _dragging = false;
            PlaceBar();
            SaveBounds();
        }
        handle.MouseMove += Move;
        handle.LostMouseCapture += End;
        handle.MouseLeftButtonUp += End;
    }

    // Keeps the shown page when the new route crosses the same maps, so live price refreshes do not reset paging.
    public void SetPlan(RouteMapPlan plan)
    {
        var sameRegions = plan.Pages.Select(page => page.Region.Id).SequenceEqual(_plan.Pages.Select(page => page.Region.Id));
        _plan = plan;
        if (!sameRegions) _pageIndex = 0;
        _pageIndex = Math.Clamp(_pageIndex, 0, Math.Max(0, plan.Pages.Count - 1));
        Render();
    }

    public void ShowPage(int step)
    {
        var next = Math.Clamp(_pageIndex + step, 0, Math.Max(0, _plan.Pages.Count - 1));
        if (next == _pageIndex) return;
        _pageIndex = next;
        Render();
    }

    public void SetLinesMode(bool linesMode)
    {
        _settings.LinesMode = linesMode;
        _saveSettings();
        Render();
    }

    public void SetOptions(bool showOthers, bool clickThrough)
    {
        _settings.ShowOtherRoutes = showOthers;
        _saveSettings();
        if (clickThrough != _clickThrough)
        {
            _clickThrough = clickThrough;
            // Layered + transparent windows pass every click to the window beneath and never take focus.
            if (clickThrough) NativeWindowStyles.Add(this, NativeWindowStyles.Transparent | NativeWindowStyles.Layered | NativeWindowStyles.NoActivate);
            else NativeWindowStyles.Remove(this, NativeWindowStyles.Transparent | NativeWindowStyles.NoActivate);
            Root.Background = clickThrough ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        }
        Render();
        PlaceChevronWindows();
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_clickThrough) BeginDrag(Root, e);
    }

    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e) => Render();

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Render();
    }

    // The game draws its map at fixed pixel sizes, so over a calibrated region lines mode sizes the window to that
    // map's pixels (not resizable) and gives the user's own size back otherwise. The game keeps its map window's
    // top-right corner fixed between maps, so these size changes keep the overlay's right edge in place too.
    private void ApplyGameLayoutSize(Size? view, DpiScale dpi)
    {
        if (view is not Size layout)
        {
            ResizeMode = ResizeMode.CanResize;
            if (_freeSize is not Size free) return;
            _freeSize = null;
            SetWidthKeepingRight(free.Width);
            Height = free.Height;
            return;
        }
        ResizeMode = ResizeMode.NoResize;
        _freeSize ??= new Size(Width, Height);
        SetWidthKeepingRight(Math.Max(MinWidth, layout.Width / dpi.DpiScaleX + 2 * SideMargin));
        var height = Math.Max(MinHeight, layout.Height / dpi.DpiScaleY + 2 * EdgeMargin);
        if (Math.Abs(Height - height) > 0.1) Height = height;
    }

    private void SetWidthKeepingRight(double width)
    {
        var current = double.IsNaN(Width) ? ActualWidth : Width;
        if (Math.Abs(current - width) <= 0.1) return;
        Left += current - width;
        Width = width;
    }

    private void PreviousChevron_Click(object sender, RoutedEventArgs e) => ShowPage(-1);

    private void NextChevron_Click(object sender, RoutedEventArgs e) => ShowPage(1);

    private void Render()
    {
        if (!IsLoaded) return;
        Surface.Children.Clear();
        var page = _plan.Pages.Count > 0 ? _plan.Pages[_pageIndex] : null;
        if (page is not null && !page.Lines.Any(line => line.Count > 0)) page = null;
        _bar.ShowState(_settings.LinesMode, _settings.ShowOtherRoutes, _clickThrough,
            page is null ? "No route map" : string.IsNullOrEmpty(_plan.Title) ? $"{page.StartName} → {page.EndName}" : _plan.Title,
            _pageIndex, _plan.Pages.Count);
        NotesText.Text = page is null
            ? (_plan.Notes.Count > 0 ? string.Join(Environment.NewLine, _plan.Notes) : "Choose a start town and a Load Profit destination.")
            : string.Empty;
        NotesText.Visibility = page is null ? Visibility.Visible : Visibility.Collapsed;
        PreviousChevron.Visibility = NextChevron.Visibility = page is null ? Visibility.Collapsed : Visibility.Visible;
        var dpi = VisualTreeHelper.GetDpi(this);
        var layout = page is not null && _settings.LinesMode ? _data.GameLayout(page.Region.Id) : null;
        // The window keeps the largest calibrated map's size for the whole route, so nothing moves while paging.
        var fixedView = page is not null && _settings.LinesMode ? LargestGameView() : null;
        ApplyGameLayoutSize(fixedView, dpi);
        Surface.Clip = null;
        if (page is null)
        {
            PlaceChevronWindows();
            return;
        }

        var area = fixedView is Size view
            ? new Rect(Width - SideMargin - view.Width / dpi.DpiScaleX, EdgeMargin, view.Width / dpi.DpiScaleX, view.Height / dpi.DpiScaleY)
            : new Rect(SideMargin, EdgeMargin,
                Math.Max(1, Surface.ActualWidth - 2 * SideMargin), Math.Max(1, Surface.ActualHeight - 2 * EdgeMargin));
        var region = page.Region;
        double regionWidth = Math.Max(1, region.Width), regionHeight = Math.Max(1, region.Height);
        Rect source;
        if (layout is not null)
        {
            source = new Rect(0, 0, regionWidth, regionHeight);
        }
        else if (_settings.LinesMode)
        {
            var points = page.Lines.SelectMany(line => line).ToList();
            double minX = points.Min(point => point.X) * regionWidth, maxX = points.Max(point => point.X) * regionWidth;
            double minY = points.Min(point => point.Y) * regionHeight, maxY = points.Max(point => point.Y) * regionHeight;
            var padding = Math.Max(Math.Max(maxX - minX, maxY - minY) * 0.06, 8);
            source = new Rect(minX - padding, minY - padding, maxX - minX + 2 * padding, maxY - minY + 2 * padding);
        }
        else
        {
            source = new Rect(0, 0, regionWidth, regionHeight);
        }
        var scale = Math.Min(area.Width / source.Width, area.Height / source.Height);
        var target = new Rect(area.Left + (area.Width - source.Width * scale) / 2, area.Top + (area.Height - source.Height * scale) / 2,
            source.Width * scale, source.Height * scale);
        if (layout is not null)
        {
            // Pin the in-game map's visible area to the window's top-right, matching how the game anchors its map.
            var viewWidth = layout.ViewWidth / dpi.DpiScaleX;
            target = new Rect(Width - SideMargin - viewWidth, EdgeMargin, viewWidth, layout.ViewHeight / dpi.DpiScaleY);
            Surface.Clip = new RectangleGeometry(target);
        }
        Point ToScreen(RouteMapPoint point) => layout is null
            ? new(target.Left + (point.X * regionWidth - source.Left) * scale, target.Top + (point.Y * regionHeight - source.Top) * scale)
            : new(target.Left + (layout.X + point.X * layout.Width) / dpi.DpiScaleX, target.Top + (layout.Y + point.Y * layout.Height) / dpi.DpiScaleY);

        if (!_settings.LinesMode && LoadRegionImage(region) is BitmapImage image)
        {
            var mapImage = new Image { Source = image, Width = target.Width, Height = target.Height, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(mapImage, BitmapScalingMode.HighQuality);
            Canvas.SetLeft(mapImage, target.Left);
            Canvas.SetTop(mapImage, target.Top);
            Surface.Children.Add(mapImage);
        }

        if (_settings.ShowOtherRoutes)
            foreach (var leg in _data.LegsInRegion(region.Id))
                Surface.Children.Add(Line(leg.Points.Select(ToScreen), OtherLineBrush, 2));
        foreach (var line in page.Lines)
            Surface.Children.Add(Line(line.Select(ToScreen), HaloBrush, 7));
        foreach (var line in page.Lines)
            Surface.Children.Add(Line(line.Select(ToScreen), SelectedLineBrush, 3.5));

        var lines = page.Lines.Where(line => line.Count > 0).ToList();
        var start = ToScreen(lines[0][0]);
        var end = ToScreen(lines[^1][^1]);
        AddWaypointFlags(lines.Select(line => line.Select(ToScreen).ToList()));
        AddMarker(start, StartBrush, page.StartName, labelBelow: true);
        AddMarker(end, null, page.EndName, labelBelow: false);

        var caption = string.Join("  ·  ", new[] { page.ArrivalNote }.Concat(_plan.Notes).Where(text => !string.IsNullOrEmpty(text)));
        if (caption.Length > 0)
        {
            var captionText = Label(caption, 11);
            captionText.Width = target.Width;
            captionText.TextAlignment = TextAlignment.Center;
            captionText.TextWrapping = TextWrapping.Wrap;
            Canvas.SetLeft(captionText, target.Left);
            Canvas.SetTop(captionText, target.Top + 2);
            Surface.Children.Add(captionText);
        }

        // Chevrons stay put while paging: just outside the largest map of the route (or the window edges for lines).
        var chevronBounds = fixedView is not null ? area
            : _settings.LinesMode ? new Rect(SideMargin, 0, area.Width, Surface.ActualHeight) : LargestMapRect(area);
        var chevronTop = Math.Clamp(chevronBounds.Top + chevronBounds.Height / 2 - 28, 0, Math.Max(0, Surface.ActualHeight - 56));
        PreviousChevron.Margin = new Thickness(Math.Max(0, chevronBounds.Left - 28), chevronTop, 0, 0);
        NextChevron.Margin = new Thickness(Math.Min(Surface.ActualWidth - 24, chevronBounds.Right + 4), chevronTop, 0, 0);
        PreviousChevron.IsEnabled = _pageIndex > 0;
        NextChevron.IsEnabled = _pageIndex < _plan.Pages.Count - 1;
        PlaceChevronWindows();
    }

    // Largest in-game view (device pixels) among the route's calibrated maps, or null when none are calibrated.
    private Size? LargestGameView()
    {
        var layouts = _plan.Pages.Select(item => _data.GameLayout(item.Region.Id)).OfType<RouteMapGameLayout>().ToList();
        return layouts.Count == 0 ? null : new Size(layouts.Max(item => item.ViewWidth), layouts.Max(item => item.ViewHeight));
    }

    // Every page's map is fitted and centered in the same area, so their union is the largest map's footprint.
    private Rect LargestMapRect(Rect area)
    {
        double width = 0, height = 0;
        foreach (var region in _plan.Pages.Select(item => item.Region))
        {
            double regionWidth = Math.Max(1, region.Width), regionHeight = Math.Max(1, region.Height);
            var scale = Math.Min(area.Width / regionWidth, area.Height / regionHeight);
            width = Math.Max(width, regionWidth * scale);
            height = Math.Max(height, regionHeight * scale);
        }
        return new Rect(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
    }

    private static Polyline Line(IEnumerable<Point> points, Brush brush, double thickness) => new()
    {
        Points = new PointCollection(points),
        Stroke = brush,
        StrokeThickness = thickness,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        IsHitTestVisible = false
    };

    private static OutlinedTextBlock Label(string text, double size) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White,
        OutlineBrush = HaloBrush,
        OutlineThickness = 2,
        IsHitTestVisible = false
    };

    // Start labels sit below their marker and end labels above, so nearby endpoints do not overprint each other.
    // A null fill adds only the label (the end point is marked by its waypoint flag).
    private void AddMarker(Point point, Brush? fill, string name, bool labelBelow)
    {
        const double size = 11;
        if (fill is not null)
        {
            var marker = new Ellipse { Width = size, Height = size, Fill = fill, Stroke = Brushes.Black, StrokeThickness = 2, IsHitTestVisible = false };
            Canvas.SetLeft(marker, point.X - size / 2);
            Canvas.SetTop(marker, point.Y - size / 2);
            Surface.Children.Add(marker);
        }
        if (string.IsNullOrEmpty(name)) return;

        var label = Label(name, 11);
        // Measure inside the window so the monitor DPI is used; off-tree measuring is narrower and trims the text.
        Surface.Children.Add(label);
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Ceiling(label.DesiredSize.Width) + 2;
        var height = label.DesiredSize.Height;
        label.Width = width;
        var left = point.X + 8 + width <= Surface.ActualWidth ? point.X + 8 : point.X - 8 - width;
        Canvas.SetLeft(label, Math.Clamp(left, 0, Math.Max(0, Surface.ActualWidth - width)));
        Canvas.SetTop(label, Math.Clamp(labelBelow ? point.Y + 4 : point.Y - height - 4, 0, Math.Max(0, Surface.ActualHeight - height)));
    }

    // Flags mark every turn and the forward end of each line; the start point gets none.
    private void AddWaypointFlags(IEnumerable<List<Point>> lines)
    {
        if (WaypointFlag is not BitmapSource flag) return;
        var placed = new List<Point>();
        Point? start = null;
        void Place(Point point)
        {
            if (placed.Any(other => (other - point).Length < MinimumFlagSpacing)) return;
            // A corner sitting on the start dot would read as a start flag.
            if (start is Point origin && (origin - point).Length < StartClearance) return;
            placed.Add(point);
            var image = new Image { Source = flag, Width = FlagWidth, Height = FlagHeight, Stretch = Stretch.Fill, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            Canvas.SetLeft(image, Math.Round(point.X - FlagAnchorX));
            Canvas.SetTop(image, Math.Round(point.Y - FlagAnchorY));
            Surface.Children.Add(image);
        }

        var cleaned = lines.Select(raw =>
        {
            var line = new List<Point>();
            foreach (var point in raw)
                if (line.Count == 0 || (point - line[^1]).Length >= 1) line.Add(point);
            return line;
        }).Where(line => line.Count >= 2).ToList();
        if (cleaned.Count > 0) start = cleaned[0][0];
        // Ends go first so a tight cluster of turns never hides the forward end's flag.
        foreach (var line in cleaned) Place(line[^1]);
        // Simplifying to the visible shape keeps shallow and gradual bends but drops collinear jitter.
        foreach (var line in cleaned)
        {
            var corners = Simplify(line, TurnTolerancePixels);
            for (var index = 1; index < corners.Count - 1; index++) Place(corners[index]);
        }
    }

    // Douglas-Peucker: keeps the points that deviate more than the tolerance from the straight span.
    private static List<Point> Simplify(List<Point> line, double tolerance)
    {
        var keep = new bool[line.Count];
        keep[0] = keep[^1] = true;
        var spans = new Stack<(int First, int Last)>();
        spans.Push((0, line.Count - 1));
        while (spans.Count > 0)
        {
            var (first, last) = spans.Pop();
            if (last - first < 2) continue;
            var chord = line[last] - line[first];
            double farthest = -1;
            var farthestIndex = -1;
            for (var index = first + 1; index < last; index++)
            {
                var offset = line[index] - line[first];
                var distance = chord.Length < 1e-6 ? offset.Length : Math.Abs(Vector.CrossProduct(chord, offset)) / chord.Length;
                if (distance > farthest) { farthest = distance; farthestIndex = index; }
            }
            if (farthest <= tolerance) continue;
            keep[farthestIndex] = true;
            spans.Push((first, farthestIndex));
            spans.Push((farthestIndex, last));
        }
        return line.Where((_, index) => keep[index]).ToList();
    }

    private const double FlagScale = 2, FlagWidth = 10 * FlagScale, FlagHeight = 13 * FlagScale;
    // The pole meets the ring at about (4.5, 10.5) in the 10 x 13 source image.
    private const double FlagAnchorX = 4.5 * FlagScale, FlagAnchorY = 10.5 * FlagScale;
    private const double TurnTolerancePixels = 3, MinimumFlagSpacing = 14, StartClearance = 6;
    private static readonly BitmapSource? WaypointFlag = LoadUiImage("WaypointFlag.png");

    private static BitmapSource? LoadUiImage(string name)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "CommerceUI", name);
        if (!File.Exists(path)) return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private BitmapImage? LoadRegionImage(RouteMapRegion region)
    {
        if (_images.TryGetValue(region.Image, out var cached)) return cached;
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "RouteMaps", region.Image);
        if (!File.Exists(path)) return null;
        // Minimaps are large; keep only a few decoded pages so paging back and forth stays quick.
        if (_images.Count >= 4) _images.Clear();
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        _images[region.Image] = image;
        return image;
    }
}

// One chevron button in its own small owned window, so it stays clickable while the map window is click-through.
internal sealed class RouteMapChevronWindow : Window
{
    private readonly Button _button;

    public RouteMapChevronWindow(RouteMapWindow map, ImageSource? art, string toolTip, Action click)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Title = toolTip;
        Owner = map;
        TopmostGuard.Watch(this);
        var image = new Image { Source = art, Width = 24, Height = 56 };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        _button = new Button { Style = (Style)map.FindResource("ChevronButton"), Content = image, ToolTip = toolTip };
        System.Windows.Automation.AutomationProperties.SetName(_button, toolTip);
        _button.Click += (_, _) => click();
        Content = _button;
        SourceInitialized += (_, _) => NativeWindowStyles.Add(this, NativeWindowStyles.ToolWindow);
        // Same as the grab bar: become foreground on hover so the click does not also reach a foreground game.
        MouseEnter += (_, _) => map.HoverEnter(this);
    }

    public void Mirror(FrameworkElement source, double ownerLeft, double ownerTop)
    {
        _button.IsEnabled = source.IsEnabled;
        if (source.Visibility != Visibility.Visible)
        {
            Hide();
            return;
        }
        Left = ownerLeft + source.Margin.Left;
        Top = ownerTop + source.Margin.Top;
        if (!IsVisible) Show();
    }
}
