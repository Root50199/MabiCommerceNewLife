using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MabiCommerceNewLife;

public partial class HoverOverlayToolbar : Window
{
    private readonly HoverOverlayChrome _overlay;
    private bool _initializing = true;

    internal HoverOverlayToolbar(HoverOverlayChrome overlay, string title, double transparency, bool clickThrough, bool canToggleClickThrough)
    {
        InitializeComponent();
        _overlay = overlay;
        Title = title + " Controls";
        DragHandle.Text = title;
        ClickThroughCheckBox.Visibility = canToggleClickThrough ? Visibility.Visible : Visibility.Collapsed;
        ClickThroughCheckBox.IsChecked = clickThrough;
        BackgroundTransparencySlider.Value = transparency;
        _initializing = false;
        SourceInitialized += (_, _) => NativeWindowStyles.Add(this, NativeWindowStyles.ToolWindow);
        MouseEnter += (_, _) => NativeDrag.Activate(new WindowInteropHelper(this).Handle);
    }

    private void BackgroundTransparency_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initializing) _overlay.SetBackgroundTransparency(e.NewValue);
    }

    private void ClickThrough_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initializing) _overlay.SetClickThrough(ClickThroughCheckBox.IsChecked == true);
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _overlay.BeginDrag(DragHandle, e);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => _overlay.Close();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => _overlay.ToggleMinimize();

    internal void SetMinimized(bool minimized)
    {
        MinimizeButton.Content = minimized ? "+" : "-";
        var label = minimized ? "Restore overlay" : "Minimize overlay";
        MinimizeButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(MinimizeButton, label);
    }

    private void Toolbar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != this;
             element = VisualTreeHelper.GetParent(element))
            if (element is ButtonBase or Slider) return;
        _overlay.ToggleMinimize();
        e.Handled = true;
    }
}
