using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MabiCommerceNewLife;

public partial class TradeHistoryWindow : Window
{
    private const int WidthSegments = 4;
    private const int HeightSegments = 12;
    private static readonly Brush LossBrush = new SolidColorBrush(Color.FromRgb(0xA3, 0x3A, 0x22));
    private static readonly Brush GainBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x24, 0x14));
    private static readonly Brush TotalBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xD7, 0x8A));
    private static readonly Brush HeaderLossBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0x7A, 0x5A));

    private readonly MainWindow _owner;

    public TradeHistoryWindow(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;
        Owner = owner;
        BackgroundArt.Source = MainWindow.BuildSettingsMenuBackground(HeightSegments, WidthSegments);
        PaperTop.Background = ShoppingListWindow.CreateTileBrush("PageTopBorder.png");
        PaperFill.Background = ShoppingListWindow.CreateTileBrush("Page.png");
        PaperBottom.Background = ShoppingListWindow.CreateTileBrush("PageBottomBorder.png");
        CloseIcon.Source = owner.TryFindResource("SettingsCloseIcon") as ImageSource;
        RawGoldIcon.Source = TotalGoldIcon.Source = owner.TryFindResource("GoldCurrencyArt") as ImageSource;
        DucatIcon.Source = owner.TryFindResource("DucatCurrencyArt") as ImageSource;
        Refresh();
    }

    public void Refresh()
    {
        var history = _owner.TradeHistory;
        var totals = history.Totals;
        RawGoldTotal.Text = Format(totals.RawGold);
        DucatsTotal.Text = Format(totals.Ducats);
        TotalGoldTotal.Text = Format(totals.TotalGold);
        TotalGoldTotal.Foreground = totals.TotalGold < 0 ? HeaderLossBrush : TotalBrush;
        HistoryItems.ItemsSource = history.Entries.Reverse().Select(entry => new TradeHistoryRow(entry)).ToList();
        EmptyText.Visibility = totals.Trades == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = totals.Trades == 1 ? "1 sale logged" : $"{totals.Trades:N0} sales logged";
        ClearButton.IsEnabled = totals.Trades > 0;
    }

    internal static string Format(decimal value) => value.ToString("N0", CultureInfo.CurrentCulture);

    internal static Brush TotalBrushFor(decimal value) => value < 0 ? LossBrush : GainBrush;

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "Delete every logged sale and reset the running totals?", "Clear trade history",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _owner.TradeHistory.Clear();
        _owner.SaveTradeHistory();
        Refresh();
    }

    private void RemoveEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TradeHistoryRow row }) return;
        var answer = MessageBox.Show(this, $"Delete this sale from the history?\n\n{row.Title}\n{row.GoodsText}", "Delete sale",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes || !_owner.TradeHistory.Remove(row.Entry)) return;
        _owner.SaveTradeHistory();
        Refresh();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed class TradeHistoryRow
{
    public TradeHistoryRow(TradeHistoryEntry entry)
    {
        Entry = entry;
        Title = $"{entry.Time.ToString("g", CultureInfo.CurrentCulture)} · {entry.Mode} → {entry.Destination}";
        GoodsText = string.Join(", ", entry.Goods.Select(good => $"{good.Quantity:N0} {good.Name}"));
        DucatsText = TradeHistoryWindow.Format(entry.Ducats);
        RawGoldText = TradeHistoryWindow.Format(entry.RawGold);
        TotalGoldText = TradeHistoryWindow.Format(entry.TotalGold);
        TotalBrush = TradeHistoryWindow.TotalBrushFor(entry.TotalGold);
        var tooltip = new StringBuilder()
            .AppendLine($"{entry.Mode} sale at {entry.Destination}")
            .AppendLine(entry.Time.ToString("F", CultureInfo.CurrentCulture))
            .AppendLine();
        foreach (var good in entry.Goods) tooltip.AppendLine($"{good.Quantity:N0} × {good.Name}");
        tooltip.AppendLine()
            .AppendLine($"Sale proceeds: {entry.SaleProceeds:N0} Ducats")
            .AppendLine($"Raw Gold: {entry.RawGold:N0}")
            .AppendLine($"Ducats earned: {entry.Ducats:N0} (× {entry.GoldPerDucat:0.##} Gold per Ducat)")
            .Append($"Total Gold: {entry.TotalGold:N0}");
        if (entry.Letter is not null) tooltip.AppendLine().Append($"Letter: {entry.Letter}");
        Tooltip = tooltip.ToString();
    }

    public TradeHistoryEntry Entry { get; }
    public string Title { get; }
    public string GoodsText { get; }
    public string DucatsText { get; }
    public string RawGoldText { get; }
    public string TotalGoldText { get; }
    public Brush TotalBrush { get; }
    public string Tooltip { get; }
}
