using System.Windows;

namespace MabiCommerceNewLife;

public enum CommunityPendingChoice { Cancel, Upload, Discard }

public partial class CommunityPendingWindow : Window
{
    public CommunityPendingChoice Choice { get; private set; }

    public CommunityPendingWindow(MainWindow owner, int count)
    {
        InitializeComponent();
        Owner = owner;
        FontFamily = owner.FontFamily;
        Resources["CommerceActionButton"] = owner.FindResource("CommerceActionButton");
        Resources["PurchaseButtonArt"] = owner.FindResource("PurchaseButtonArt");
        BackgroundArt.Source = MainWindow.BuildSettingsMenuBackground(7, 4);
        PendingText.Text = $"{count} goods are waiting to be shared.\n\nUpload before closing, or discard pending uploads? Your applied local prices are kept either way.";
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void Upload_Click(object sender, RoutedEventArgs e) { Choice = CommunityPendingChoice.Upload; DialogResult = true; }
    private void Discard_Click(object sender, RoutedEventArgs e) { Choice = CommunityPendingChoice.Discard; DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
