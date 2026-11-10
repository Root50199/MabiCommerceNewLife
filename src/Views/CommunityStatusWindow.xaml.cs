using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace MabiCommerceNewLife;

public partial class CommunityStatusWindow : Window
{
    private readonly MainWindow _owner;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public CommunityStatusWindow(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;
        Owner = owner;
        FontFamily = owner.FontFamily;
        Resources["CommerceActionButton"] = owner.FindResource("CommerceActionButton");
        Resources["PurchaseButtonArt"] = owner.FindResource("PurchaseButtonArt");
        BackgroundArt.Source = MainWindow.BuildSettingsMenuBackground(14, 4);
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); CloseButton.Focus(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        var issue = _owner.CommunityIssueDetails();
        IssueDetails.Text = issue.Details;
        WaitText.Text = issue.Wait;
        RetryButton.IsEnabled = issue.CanRetry;
        RetryButton.Opacity = issue.CanRetry ? 1 : 0.45;
        RetryButton.ToolTip = issue.CanRetry ? "Retry price sharing. Previous scans are not replayed." : issue.Wait;
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
