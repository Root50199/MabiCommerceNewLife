using System.Windows;
using System.Windows.Input;

namespace MabiCommerceNewLife;

public partial class SharingConsentWindow : Window
{
    public SharingConsentWindow(MainWindow owner)
    {
        InitializeComponent();
        Owner = owner;
        FontFamily = owner.FontFamily;
        Resources["CommerceActionButton"] = owner.FindResource("CommerceActionButton");
        Resources["PurchaseButtonArt"] = owner.FindResource("PurchaseButtonArt");
        BackgroundArt.Source = MainWindow.BuildSettingsMenuBackground(10, 4);
        PrivacyDetailsText.Text =
            $"Service: {CommunityClient.DefaultEndpoint} (Cloudflare).\n\n" +
            "Automatically checked scans and complete reviews upload; edited values are marked manual. " +
            "Refresh receives and applies prices (one-minute cooldown). No background polling or offline upload queue.\n\n" +
            "Connection IP logs and new observations' random installation links expire after 30 days, " +
            "with deletion at the next successful cleanup. Pre-policy links are exempt. " +
            "Unused expired credentials are deleted. Price/stock history remains indefinitely; " +
            "turning sharing off does not erase it. Cloudflare platform retention/backups are separate.\n\n" +
            "No character/account data, screenshots, raw OCR or personal weekly allowance is sent. " +
            "A random installation ID and authentication token are saved unencrypted beside the app in community-credentials.json " +
            "and reused across launches. No hardware fingerprint or game identity is used. Keep this file private; copying it shares the installation identity. " +
            "The backend stores only a hash of the token. This choice is saved for this service/policy and reconnects on launch. " +
            "Turning sharing off stops transfers and keeps credentials and newest applied values locally. " +
            "Saved credentials alone never enable sharing.";
        Loaded += (_, _) => DeclineSharingButton.Focus();
    }

    private void Enable_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void PrivacyDetails_Changed(object sender, RoutedEventArgs e) =>
        Height = PrivacyDetails.IsExpanded ? 390 : 310;
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
