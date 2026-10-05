using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DrawingRectangle = System.Drawing.Rectangle;
using ShapeRectangle = System.Windows.Shapes.Rectangle;

namespace MabiCommerceNewLife;

public sealed class ScreenRegionSelectorWindow : Window
{
    private readonly Canvas _surface = new();
    private readonly ShapeRectangle _selection = new()
    {
        Fill = Brushes.Transparent,
        Stroke = new SolidColorBrush(Color.FromRgb(0xF0, 0xD7, 0x8A)),
        StrokeThickness = 2,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };
    private Point _dragStart;
    private bool _dragging;

    public DrawingRectangle? SelectedRegion { get; private set; }

    public ScreenRegionSelectorWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(76, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Content = _surface;

        _surface.Background = Brushes.Transparent;
        var instruction = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 35, 29, 20)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xA8, 0x8A, 0x46)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5, 10, 5),
            Child = new TextBlock
            {
                Text = "Drag from the left bracket to the right bracket around the good name and full price list · Esc cancels",
                Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xEC, 0xD9)),
                FontSize = 12
            }
        };
        _surface.Children.Add(instruction);
        Canvas.SetLeft(instruction, 24);
        Canvas.SetTop(instruction, 24);
        _surface.Children.Add(_selection);

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        KeyDown += OnKeyDown;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(_surface);
        _dragging = true;
        _selection.Visibility = Visibility.Visible;
        CaptureMouse();
        UpdateSelection(e.GetPosition(_surface));
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging) UpdateSelection(e.GetPosition(_surface));
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        var left = Canvas.GetLeft(_selection);
        var top = Canvas.GetTop(_selection);
        var width = _selection.Width;
        var height = _selection.Height;
        if (width < 24 || height < 16) return;

        var screenStart = PointToScreen(new Point(left, top));
        var screenEnd = PointToScreen(new Point(left + width, top + height));
        SelectedRegion = new DrawingRectangle(
            (int)Math.Round(screenStart.X),
            (int)Math.Round(screenStart.Y),
            (int)Math.Round(screenEnd.X - screenStart.X),
            (int)Math.Round(screenEnd.Y - screenStart.Y));
        DialogResult = true;
        e.Handled = true;
    }

    private void UpdateSelection(Point current)
    {
        var left = Math.Min(_dragStart.X, current.X);
        var top = Math.Min(_dragStart.Y, current.Y);
        Canvas.SetLeft(_selection, left);
        Canvas.SetTop(_selection, top);
        _selection.Width = Math.Abs(current.X - _dragStart.X);
        _selection.Height = Math.Abs(current.Y - _dragStart.Y);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DialogResult = false;
        e.Handled = true;
    }
}