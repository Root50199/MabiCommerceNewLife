using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MabiCommerceNewLife;

public partial class AutoExpandedRoutesWindow : Window
{
    private AutoPlanRow? _selectedPlannerRoute;

    public event Action<AutoPlanRow>? RouteAddedToCustomSelection;

    public AutoExpandedRoutesWindow()
    {
        InitializeComponent();
        var framePath = Path.Combine(AppContext.BaseDirectory, "Data", "CommerceUI", "TradingPostFrameExpanded.png");
        if (File.Exists(framePath))
        {
            var frame = new BitmapImage();
            frame.BeginInit();
            frame.CacheOption = BitmapCacheOption.OnLoad;
            frame.UriSource = new Uri(framePath, UriKind.Absolute);
            frame.EndInit();
            frame.Freeze();
            ExpandedFrameImage.Source = frame;
        }
        var closePath = Path.Combine(AppContext.BaseDirectory, "Data", "CommerceUI", "CloseButton.png");
        if (File.Exists(closePath))
        {
            var closeImage = new BitmapImage();
            closeImage.BeginInit();
            closeImage.CacheOption = BitmapCacheOption.OnLoad;
            closeImage.UriSource = new Uri(closePath, UriKind.Absolute);
            closeImage.EndInit();
            closeImage.Freeze();
            ExpandedCloseButtonImage.Source = closeImage;
        }
        foreach (var (key, file) in new[] { ("GoldCurrencyArt", "Icon_Currency_Gold.png"), ("DucatCurrencyArt", "Icon_Currency_Ducat.png") })
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Data", "CommerceUI", file);
            if (!File.Exists(iconPath)) continue;
            var icon = new BitmapImage();
            icon.BeginInit();
            icon.CacheOption = BitmapCacheOption.OnLoad;
            icon.UriSource = new Uri(iconPath, UriKind.Absolute);
            icon.EndInit();
            icon.Freeze();
            Resources[key] = icon;
        }
    }

    public void SetRoutes(string sourceName, int? merchantRating,
        IReadOnlyList<AutoExpandedRouteRow> totalProfitRoutes,
        IReadOnlyList<AutoExpandedRouteRow> profitPerMinuteRoutes)
    {
        TotalProfitRoutesGrid.ItemsSource = totalProfitRoutes;
        ProfitPerMinuteRoutesGrid.ItemsSource = profitPerMinuteRoutes;
        TotalProfitTabButton.Content = $"TOTAL PROFIT ({totalProfitRoutes.Count})";
        ProfitPerMinuteTabButton.Content = $"PROFIT / MIN ({profitPerMinuteRoutes.Count})";
        var rating = merchantRating is int value ? $" · merchant rating {value}" : string.Empty;
        ExpandedStatusText.Text = $"From {sourceName}{rating} · Raw Gold excludes bonuses; Seasonal Points are base estimates.";
        SetSelectedRanking(totalProfit: true);
    }

    private void TotalProfitTab_Click(object sender, RoutedEventArgs e) => SetSelectedRanking(totalProfit: true);

    private void ProfitPerMinuteTab_Click(object sender, RoutedEventArgs e) => SetSelectedRanking(totalProfit: false);

    private void SetSelectedRanking(bool totalProfit)
    {
        TotalProfitRoutesGrid.Visibility = totalProfit ? Visibility.Visible : Visibility.Collapsed;
        ProfitPerMinuteRoutesGrid.Visibility = totalProfit ? Visibility.Collapsed : Visibility.Visible;
        TotalProfitTabButton.Background = totalProfit ? new SolidColorBrush(Color.FromRgb(0xA9, 0x82, 0x3C)) : new SolidColorBrush(Color.FromRgb(0x62, 0x59, 0x46));
        TotalProfitTabButton.Foreground = totalProfit ? new SolidColorBrush(Color.FromRgb(0x27, 0x1D, 0x10)) : new SolidColorBrush(Color.FromRgb(0xF4, 0xEC, 0xD9));
        ProfitPerMinuteTabButton.Background = totalProfit ? new SolidColorBrush(Color.FromRgb(0x62, 0x59, 0x46)) : new SolidColorBrush(Color.FromRgb(0xA9, 0x82, 0x3C));
        ProfitPerMinuteTabButton.Foreground = totalProfit ? new SolidColorBrush(Color.FromRgb(0xF4, 0xEC, 0xD9)) : new SolidColorBrush(Color.FromRgb(0x27, 0x1D, 0x10));
        TotalProfitRoutesGrid.SelectedItem = null;
        ProfitPerMinuteRoutesGrid.SelectedItem = null;
        _selectedPlannerRoute = null;
        ApplySelectedRouteButtonText.Opacity = 0.4;
    }

    private void RouteGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid) return;
        if (grid.SelectedItem is AutoExpandedRouteRow selected)
        {
            if (ReferenceEquals(grid, TotalProfitRoutesGrid)) ProfitPerMinuteRoutesGrid.SelectedItem = null;
            else TotalProfitRoutesGrid.SelectedItem = null;
            _selectedPlannerRoute = selected.PlannerRoute;
        }
        else
            _selectedPlannerRoute = null;
        ApplySelectedRouteButtonText.Opacity = _selectedPlannerRoute is not null ? 1 : 0.4;
    }

    private void ApplySelectedRouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlannerRoute is not { } selectedRoute)
        {
            ExpandedStatusText.Text = "Select a route row to add it to Custom Selection.";
            return;
        }
        RouteAddedToCustomSelection?.Invoke(selectedRoute);
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}