using System.Windows;
using System.Windows.Input;

namespace MabiCommerceNewLife;

public partial class AppUpdateWindow : Window
{
    public AppUpdateWindow(MainWindow owner, AppRelease release)
    {
        InitializeComponent();
        Owner = owner;
        FontFamily = owner.FontFamily;
        Resources["CommerceActionButton"] = owner.FindResource("CommerceActionButton");
        Resources["PurchaseButtonArt"] = owner.FindResource("PurchaseButtonArt");
        BackgroundArt.Source = MainWindow.BuildSettingsMenuBackground(4, 2);
        ReleaseSummary.Text = $"{release.Tag} ({release.Size / (1024m * 1024m):N1} MB)";
        Loaded += (_, _) => DeclineUpdateButton.Focus();
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
