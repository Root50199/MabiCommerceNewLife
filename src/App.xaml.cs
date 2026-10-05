using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MabiCommerceNewLife;

public partial class App : Application
{
    static App()
    {
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler(DropTextBoxFocusOnOutsideClick));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        new MainWindow().Show();
    }

    // Clicking anywhere outside the focused text box ends editing, so its LostKeyboardFocus commit or revert runs.
    private static void DropTextBoxFocusOnOutsideClick(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.FocusedElement is not TextBox box || e.OriginalSource is not DependencyObject source) return;
        for (var node = source; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is TextBox) return;
        }
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(box), null);
        Keyboard.ClearFocus();
    }
}