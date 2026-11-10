using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MabiCommerceNewLife;

internal sealed class TutorialHost
{
    private readonly Window _window;
    private readonly UIElement _content;
    private readonly Grid _wrapper;
    public TutorialOverlay Overlay { get; } = new();

    public TutorialHost(Window window)
    {
        _window = window;
        _content = window.Content as UIElement
            ?? throw new InvalidOperationException("Tutorial requires visual window content.");
        _wrapper = new Grid();
        window.Content = null;
        _wrapper.Children.Add(_content);
        _wrapper.Children.Add(Overlay);
        window.Content = _wrapper;
        window.PreviewKeyDown += KeepFocusInTour;
    }

    private void KeepFocusInTour(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Overlay.Exit();
            e.Handled = true;
        }
        else if (!Overlay.IsKeyboardFocusWithin || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            e.Handled = true;
    }

    public void Detach()
    {
        Overlay.Stop();
        _window.PreviewKeyDown -= KeepFocusInTour;
        _window.Content = null;
        _wrapper.Children.Clear();
        _window.Content = _content;
    }
}
