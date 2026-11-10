using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace MabiCommerceNewLife;

public partial class UserManualWindow : Window
{
    public event EventHandler? TourRequested;

    public UserManualWindow(MainWindow owner, bool offerTour = false)
    {
        InitializeComponent();
        Owner = owner;
        FontFamily = owner.FontFamily;
        Topmost = owner.Topmost;
        TopmostGuard.Watch(this);
        ContentsList.ItemsSource = UserGuide.Topics;
        ContentsList.SelectedIndex = 0;
        if (offerTour)
        {
            MinHeight = 260;
            Height = 310;
            WelcomePanel.Visibility = Visibility.Visible;
            ManualPages.Visibility = Visibility.Collapsed;
            StartTourButton.Visibility = Visibility.Collapsed;
            Loaded += (_, _) => WelcomeStartButton.Focus();
        }
    }

    private void Topic_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContentsList.SelectedItem is not GuideTopic topic) return;
        TopicTitle.Text = topic.Title;
        TopicInstructions.Text = topic.Instructions;
        var screenshotPath = Path.Combine(AppContext.BaseDirectory, "Data", "UserGuide", topic.ScreenshotFileName);
        TopicScreenshot.Source = null;
        if (File.Exists(screenshotPath))
        {
            try
            {
                var screenshot = new BitmapImage();
                screenshot.BeginInit();
                screenshot.CacheOption = BitmapCacheOption.OnLoad;
                screenshot.UriSource = new Uri(screenshotPath);
                screenshot.EndInit();
                screenshot.Freeze();
                TopicScreenshot.Source = screenshot;
                ScreenshotNotice.Text = "Example app view. Your values and available controls may differ.";
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or FileFormatException)
            {
                ScreenshotNotice.Text = $"Illustration could not load: {exception.Message}";
            }
        }
        else
            ScreenshotNotice.Text = $"Illustration missing: Data/UserGuide/{topic.ScreenshotFileName}. Keep Data beside the app.";
        PageScroll.ScrollToTop();
    }

    private void ReadManual_Click(object sender, RoutedEventArgs e)
    {
        MinHeight = 460;
        Height = 640;
        WelcomePanel.Visibility = Visibility.Collapsed;
        ManualPages.Visibility = Visibility.Visible;
        StartTourButton.Visibility = Visibility.Visible;
        ContentsList.Focus();
    }

    private void StartTour_Click(object sender, RoutedEventArgs e)
    {
        Close();
        TourRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
