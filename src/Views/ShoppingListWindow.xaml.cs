using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MabiCommerceNewLife;

public partial class ShoppingListWindow : Window
{
    private const int WidthSegments = 4;
    private const int MinHeightSegments = 5;
    private const double SummaryTop = 64;
    private const double RowHeight = 28;
    // Paper borders, inner margins and the column header row.
    private const double PaperChrome = 2 + 2 + 8 + 24;
    private const double TotalsHeight = 20;
    private const double BottomFrame = 22;
    private int _heightSegments;
    internal static readonly Brush DoneBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0x22));
    internal static readonly Brush ShortBrush = new SolidColorBrush(Color.FromRgb(0xA3, 0x3A, 0x22));

    private readonly MainWindow _owner;
    private List<ShoppingRow> _rows = [];

    public ShoppingListWindow(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;
        Owner = owner;
        PaperTop.Background = CreateTileBrush("PageTopBorder.png");
        PaperFill.Background = CreateTileBrush("Page.png");
        PaperBottom.Background = CreateTileBrush("PageBottomBorder.png");
        CloseIcon.Source = owner.TryFindResource("SettingsCloseIcon") as ImageSource;
        Refresh();
    }

    public void Refresh()
    {
        var (summary, lines) = _owner.GetBarterShoppingList();
        CartSummary.Text = lines.Count == 0 ? "No barter goods in the cart." : "For " + summary;
        _rows = lines.Select(line => new ShoppingRow(line)).ToList();
        ShoppingItems.ItemsSource = _rows;
        UpdateTotals();
        FitToRows();
    }

    // Grows the window in whole frame segments so every material fits without scrolling,
    // unless that would exceed the screen work area.
    private void FitToRows()
    {
        CartSummary.Measure(new Size(CartSummary.Width, double.PositiveInfinity));
        var paperTop = SummaryTop + Math.Max(16, CartSummary.DesiredSize.Height) + 10;
        var wanted = paperTop + PaperChrome + Math.Max(1, _rows.Count) * RowHeight + 4 + 6 + TotalsHeight + BottomFrame;
        var maxSegments = Math.Max(MinHeightSegments, (int)((SystemParameters.WorkArea.Height - 90) / 32));
        var segments = Math.Clamp((int)Math.Ceiling((wanted - 90) / 32), MinHeightSegments, maxSegments);
        var height = 90 + 32 * segments;
        if (segments != _heightSegments)
        {
            _heightSegments = segments;
            BackgroundArt.Source = MainWindow.BuildSettingsMenuBackground(segments, WidthSegments);
        }
        Width = Root.Width = BackgroundArt.Width = 270 + 46 * WidthSegments;
        Height = Root.Height = BackgroundArt.Height = height;
        var totalsTop = height - BottomFrame - TotalsHeight;
        Canvas.SetTop(PaperPanel, paperTop);
        PaperPanel.Height = Math.Max(RowHeight, totalsTop - 6 - paperTop);
        Canvas.SetTop(TotalsText, totalsTop);
        if (IsLoaded)
        {
            var bottom = SystemParameters.WorkArea.Bottom;
            if (Top + height > bottom) Top = Math.Max(SystemParameters.WorkArea.Top, bottom - height);
        }
    }

    private void UpdateTotals()
    {
        var remaining = _rows.Count(row => row.StillNeed > 0);
        TotalsText.Text = _rows.Count == 0 ? string.Empty
            : remaining == 0 ? "You have everything for this cart."
            : $"{remaining} of {_rows.Count} materials still needed.";
    }

    private void Have_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: ShoppingRow row } box) return;
        if (!BarterShoppingList.TryParseHave(box.Text, out var have))
        {
            box.BorderBrush = Brushes.Red;
            box.ToolTip = $"Enter a whole number from 0 to {BarterShoppingList.MaxHave:N0}";
            return;
        }
        box.ClearValue(Border.BorderBrushProperty);
        box.ToolTip = "Whole number you already have";
        if (row.Have == have) return;
        row.SetHave(have);
        _owner.SetBarterMaterialOnHand(row.ItemId, have);
        UpdateTotals();
    }

    // A malformed entry is never saved; leaving the box shows the saved count again.
    private void Have_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: ShoppingRow row } box) return;
        if (BarterShoppingList.TryParseHave(box.Text, out var have) && have == row.Have) return;
        row.HaveText = row.Have == 0 ? string.Empty : row.Have.ToString(CultureInfo.CurrentCulture);
        box.Text = row.HaveText;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static Brush CreateTileBrush(string fileName)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(Path.Combine(AppContext.BaseDirectory, "Data", "CommerceUI", fileName));
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            var brush = new ImageBrush(image)
            {
                TileMode = TileMode.Tile,
                Stretch = Stretch.None,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, image.PixelWidth, image.PixelHeight),
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
            brush.Freeze();
            return brush;
        }
        catch (Exception)
        {
            return new SolidColorBrush(Color.FromRgb(0xF4, 0xE8, 0xD2));
        }
    }
}

public sealed class ShoppingRow : INotifyPropertyChanged
{
    public ShoppingRow(BarterShoppingLine line)
    {
        ItemId = line.ItemId;
        Name = line.Name;
        Needed = line.Needed;
        Have = (int)Math.Min(line.Have, BarterShoppingList.MaxHave);
        HaveText = Have == 0 ? string.Empty : Have.ToString(CultureInfo.CurrentCulture);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int ItemId { get; }
    public string Name { get; }
    public long Needed { get; }
    public int Have { get; private set; }
    public string HaveText { get; set; }
    public long StillNeed => Math.Max(0, Needed - Have);
    public string NeededText => Needed.ToString("N0", CultureInfo.CurrentCulture);
    public string StillNeedText => StillNeed == 0 ? "✓" : StillNeed.ToString("N0", CultureInfo.CurrentCulture);
    public Brush StillNeedBrush => StillNeed == 0 ? ShoppingListWindow.DoneBrush : ShoppingListWindow.ShortBrush;
    public string Tooltip => $"{Name}\nNeeded: {NeededText}\nHave: {Have:N0}\nStill need: {StillNeed:N0}";

    public void SetHave(int have)
    {
        Have = have;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StillNeedText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StillNeedBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tooltip)));
    }
}
