using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MabiCommerceNewLife;

public class HoverOverlaySettings
{
    public bool Enabled { get; set; }
    public bool RouteOnly { get; set; }
    public bool OnlyWithRoute { get; set; }
    public double BackgroundTransparency { get; set; } = 7;
    public double? Left { get; set; }
    public double? Top { get; set; }
}

internal sealed class HoverOverlayChrome
{
    private readonly Window _window;
    private readonly Border _background;
    private readonly HoverOverlaySettings _settings;
    private readonly Action _save;
    private readonly Action<bool>? _setClickThrough;
    private readonly HoverOverlayToolbar _bar;
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _syncing;
    private bool _dragging;
    private bool _clickThrough;
    private bool _minimized;

    public HoverOverlayChrome(Window window, Border background, HoverOverlaySettings settings, Action save,
        string title, bool clickThrough = true, Action<bool>? setClickThrough = null)
    {
        _window = window;
        _background = background;
        _settings = settings;
        _save = save;
        _clickThrough = clickThrough;
        _setClickThrough = setClickThrough;
        if (!double.IsFinite(settings.BackgroundTransparency) || settings.BackgroundTransparency is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(settings), "Overlay background transparency must be 0-100.");
        _bar = new HoverOverlayToolbar(this, title, settings.BackgroundTransparency, clickThrough, setClickThrough is not null)
            { FontFamily = window.FontFamily, Opacity = 0, Width = window.Width };
        ApplyBackgroundTransparency();
        if (settings.Left is double left && double.IsFinite(left) && settings.Top is double top && double.IsFinite(top))
        {
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            window.Left = Math.Clamp(left, screen.Left, Math.Max(screen.Left, screen.Right - window.Width));
            window.Top = Math.Clamp(top, screen.Top + _bar.Height, screen.Bottom - 40);
        }
        else ResetPosition();
        window.SourceInitialized += (_, _) =>
        {
            NativeWindowStyles.Add(window, NativeWindowStyles.ToolWindow);
            ApplyClickThrough();
        };
        window.Loaded += (_, _) =>
        {
            _bar.Owner = window;
            _bar.FontFamily = window.FontFamily;
            PlaceBar();
            _bar.Show();
            SetBarVisible(false);
            TopmostGuard.Watch(window);
            TopmostGuard.Watch(_bar);
            _hoverTimer.Start();
        };
        _hoverTimer.Tick += (_, _) => RefreshHover();
        window.LocationChanged += (_, _) => PlaceBar();
        _bar.LocationChanged += (_, _) =>
        {
            if (_syncing || _dragging) return;
            _syncing = true;
            window.Left = _bar.Left;
            window.Top = _bar.Top + _bar.Height;
            _syncing = false;
        };
        window.IsVisibleChanged += (_, _) =>
        {
            if (!window.IsLoaded) return;
            if (window.IsVisible)
            {
                PlaceBar();
                _bar.Show();
                _hoverTimer.Start();
                RefreshHover();
            }
            else
            {
                _hoverTimer.Stop();
                _bar.Hide();
            }
        };
        window.MouseEnter += (_, _) =>
        {
            if (!_clickThrough) NativeDrag.Activate(new WindowInteropHelper(window).Handle);
        };
        window.Closing += (_, _) => SavePosition();
        window.Closed += (_, _) => _hoverTimer.Stop();
    }

    public void Close() => _window.Close();

    internal Window CreateTutorialPreview()
    {
        // Reuse the real visuals without showing native click-through windows or starting hover timers.
        var toolbar = (FrameworkElement)_bar.Content;
        _bar.Content = null;
        _window.Content = null;
        toolbar.Height = _bar.Height;
        var example = new StackPanel { Width = _window.Width, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Top };
        example.Children.Add(toolbar);
        example.Children.Add(_background);
        var content = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x49, 0x3A, 0x26)) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_window.Width + 24) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        content.Children.Add(example);
        var preview = new Window
        {
            Title = _window.Title + " - Tutorial", Width = _window.Width + 404,
            SizeToContent = SizeToContent.Height, MinHeight = 460,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Content = content, FontFamily = _window.FontFamily
        };
        NameScope.SetNameScope(preview, new NameScope());
        preview.RegisterName(_background.Name, _background);
        preview.RegisterName(toolbar.Name, toolbar);
        preview.Closed += (_, _) =>
        {
            _window.Close();
            _bar.Close();
        };
        return preview;
    }

    public void ToggleMinimize()
    {
        _minimized = !_minimized;
        _background.Visibility = _minimized ? Visibility.Collapsed : Visibility.Visible;
        _bar.SetMinimized(_minimized);
        RefreshHover();
    }

    public void SetClickThrough(bool value)
    {
        _clickThrough = value;
        ApplyClickThrough();
        _setClickThrough?.Invoke(value);
        _save();
    }

    private void ApplyClickThrough()
    {
        if (_clickThrough)
            NativeWindowStyles.Add(_window, NativeWindowStyles.Layered | NativeWindowStyles.Transparent | NativeWindowStyles.NoActivate);
        else NativeWindowStyles.Remove(_window, NativeWindowStyles.Transparent | NativeWindowStyles.NoActivate);
    }

    public void SetBackgroundTransparency(double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(value));
        _settings.BackgroundTransparency = value;
        ApplyBackgroundTransparency();
        _save();
    }

    private void ApplyBackgroundTransparency() =>
        _background.Background = new SolidColorBrush(Color.FromRgb(0x21, 0x1A, 0x11))
            { Opacity = 1 - _settings.BackgroundTransparency / 100 };

    private void PlaceBar()
    {
        if (_syncing || _dragging) return;
        _syncing = true;
        _bar.Left = _window.Left;
        _bar.Top = _window.Top - _bar.Height;
        _syncing = false;
    }

    // Click-through windows get no mouse events. Check only our window rectangles, without hooks or screen capture.
    private void RefreshHover()
    {
        if (!_window.IsVisible || !NativeDrag.GetCursorPos(out var cursor)) return;
        bool Contains(Window window) => NativeDrag.GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds) &&
            cursor.X >= bounds.Left && cursor.X < bounds.Right && cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom;
        SetBarVisible(_minimized || _dragging || _bar.IsMouseCaptureWithin || Contains(_window) || Contains(_bar));
    }

    private void SetBarVisible(bool visible)
    {
        _bar.Opacity = visible ? 1 : 0;
        if (visible) NativeWindowStyles.Remove(_bar, NativeWindowStyles.Transparent | NativeWindowStyles.NoActivate);
        else NativeWindowStyles.Add(_bar, NativeWindowStyles.Transparent | NativeWindowStyles.NoActivate);
    }

    public void ResetPosition()
    {
        var area = SystemParameters.WorkArea;
        _window.Left = Math.Max(area.Left, area.Right - _window.Width - 20);
        _window.Top = area.Top + Math.Max(40, _bar.Height);
    }

    private void SavePosition()
    {
        _settings.Left = _window.Left;
        _settings.Top = _window.Top;
        _save();
    }

    public void BeginDrag(UIElement handle, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || _dragging) return;
        var bodyHandle = new WindowInteropHelper(_window).Handle;
        var barHandle = new WindowInteropHelper(_bar).Handle;
        if (!NativeDrag.GetCursorPos(out var cursorStart) ||
            !NativeDrag.GetWindowRect(bodyHandle, out var bodyStart) ||
            !NativeDrag.GetWindowRect(barHandle, out var barStart) || !handle.CaptureMouse()) return;
        _dragging = true;
        e.Handled = true;
        void Move(object? sender, MouseEventArgs args)
        {
            if (!NativeDrag.GetCursorPos(out var cursor)) return;
            var dx = cursor.X - cursorStart.X;
            var dy = cursor.Y - cursorStart.Y;
            NativeDrag.MoveTogether(barHandle, barStart.Left + dx, barStart.Top + dy,
                bodyHandle, bodyStart.Left + dx, bodyStart.Top + dy);
        }
        void End(object? sender, EventArgs args)
        {
            handle.MouseMove -= Move;
            handle.LostMouseCapture -= End;
            handle.MouseLeftButtonUp -= End;
            if (handle.IsMouseCaptured) handle.ReleaseMouseCapture();
            _dragging = false;
            PlaceBar();
            SavePosition();
            RefreshHover();
        }
        handle.MouseMove += Move;
        handle.LostMouseCapture += End;
        handle.MouseLeftButtonUp += End;
    }
}
