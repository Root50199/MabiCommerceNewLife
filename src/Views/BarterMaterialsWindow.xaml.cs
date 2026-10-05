using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MabiCommerceNewLife;

public partial class BarterMaterialsWindow : Window
{
    private const int HeightSegments = 17;
    private const int WidthSegments = 13;
    private static readonly Brush GainBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0x22));
    private static readonly Brush LossBrush = new SolidColorBrush(Color.FromRgb(0xA3, 0x3A, 0x22));
    private static readonly Brush NeutralBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x40, 0x28));

    private readonly MainWindow _owner;
    private readonly List<MaterialValueRow> _materials;
    private readonly List<OwnedMaterialRow> _ownedMaterials;
    private readonly DispatcherTimer _applyTimer;
    private bool _loading = true;
    // Window-only letter choice; remembered while the app runs, never saved or applied to the main planner.
    private static GuaranteeLetterKind s_letter = GuaranteeLetterKind.None;

    public BarterMaterialsWindow(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;
        Owner = owner;

        BackgroundArt.Source = MainWindow.BuildSettingsMenuBackground(HeightSegments, WidthSegments);
        PaperTop.Background = CreateTileBrush("PageTopBorder.png");
        PaperFill.Background = CreateTileBrush("Page.png");
        PaperBottom.Background = CreateTileBrush("PageBottomBorder.png");
        CloseIcon.Source = owner.TryFindResource("SettingsCloseIcon") as ImageSource;
        Resources["CopyIconArt"] = LoadIcon("DetailsIcon.png");
        foreach (var (image, key) in new[]
        {
            (Header1TopLeft, "BracketTopLeftArt"), (Header1TopRight, "BracketTopRightArt"),
            (Header1BottomLeft, "BracketBottomLeftArt"), (Header1BottomRight, "BracketBottomRightArt"),
            (Header2TopLeft, "BracketTopLeftArt"), (Header2TopRight, "BracketTopRightArt"),
            (Header2BottomLeft, "BracketBottomLeftArt"), (Header2BottomRight, "BracketBottomRightArt")
        })
            image.Source = owner.TryFindResource(key) as ImageSource;

        // Materials cover every rotating alternative so values can be entered before an offer rotates in.
        var usedIds = owner.AllBarterGoods.SelectMany(good => good.Recipe).Select(part => part.ItemId).ToHashSet();
        var usedMaterials = owner.BarterMaterialCatalog
            .Where(material => usedIds.Contains(material.Id))
            .OrderBy(material => material.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        string UsedBy(int itemId) => string.Join(", ", owner.AllBarterGoods
            .Where(good => good.Recipe.Any(part => part.ItemId == itemId)).Select(good => good.Name).Distinct());
        _materials = usedMaterials.Where(material => !material.IsUntradable)
            .Select(material => new MaterialValueRow(material, owner.GetSavedBarterMaterialValue(material.Id), UsedBy(material.Id)))
            .ToList();
        _ownedMaterials = usedMaterials.Where(material => material.IsUntradable)
            .Select(material => new OwnedMaterialRow(material, owner.GetBarterMaterialOnHand(material.Id), UsedBy(material.Id)))
            .ToList();
        MaterialList.ItemsSource = _materials;
        OwnedMaterialList.ItemsSource = _ownedMaterials;
        GoldPerDucatInput.Text = owner.GoldPerDucat.ToString("0.####", CultureInfo.InvariantCulture);
        var letterOptions = new[] { new LetterOption(GuaranteeLetterKind.None, "No letter") }
            .Concat(GuaranteeLetters.All.Select(letter => new LetterOption(letter.Kind,
                $"{letter.Name.Replace(" Letter of Guarantee", string.Empty)} · +{letter.BarterPercent}%")))
            .ToList();
        LetterPicker.ItemsSource = letterOptions;
        LetterPicker.SelectedItem = letterOptions.FirstOrDefault(option => option.Kind == s_letter) ?? letterOptions[0];

        _applyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _applyTimer.Tick += (_, _) =>
        {
            _applyTimer.Stop();
            ApplyValues();
        };
        _loading = false;
        RefreshVerdicts();
    }

    public void RefreshVerdicts()
    {
        var rate = _owner.GoldPerDucat;
        var rows = new List<BarterVerdictRow>();
        // Every rotating alternative is listed: the rotation schedule is unknown, so values must be checkable before an offer rotates in.
        foreach (var good in _owner.AllBarterGoods)
        {
            var materialGold = good.Recipe.Sum(part => part.Quantity * _owner.GetBarterMaterialValue(part.ItemId));
            var estimated = good.Recipe.Any(part => !_owner.HasSavedBarterMaterialValue(part.ItemId));
            var sale = _owner.BestBarterSalePrice(good, out var destination) ?? 0m;
            // No Ducats are spent on a barter good, so its whole sale price is profit: the sale pays that many
            // Ducats (converted at the user's rate) plus the same amount again as raw Gold, both raised by
            // Commerce Mastery and the selected Letter of Guarantee (Commerce/Bartering wiki).
            var reward = CommerceRewardModel.Calculate(
                [new RewardLine(1, materialGold, sale, good.Weight)], isBarter: true,
                _owner.CurrentRewardModifiers() with { Letter = s_letter, LetterSupply = -1, LetterMarketValue = 0 },
                perLoadLetterEffects: false);
            var rawGold = reward.RawGold;
            var saleGold = reward.RawGold + reward.DucatGold;
            var gain = reward.TotalGold;
            var postName = _owner.Posts.FirstOrDefault(post => post.Id == good.PostId)?.Name ?? "Barter";
            var untradableRecipe = good.Recipe.Count > 0 && good.Recipe.All(part => _owner.IsUntradableBarterMaterial(part.ItemId));
            var ownedUnits = untradableRecipe
                ? good.Recipe.Where(part => part.Quantity > 0).Select(part => _owner.GetBarterMaterialOnHand(part.ItemId) / part.Quantity).DefaultIfEmpty(0).Min()
                : 0;
            var recipeText = string.Join(" + ", good.Recipe.Select(part => $"{part.Quantity:N0} {part.Name}"));
            var components = good.Recipe.Select(part =>
            {
                var unit = _owner.GetBarterMaterialValue(part.ItemId);
                if (_owner.IsUntradableBarterMaterial(part.ItemId))
                    return new BarterComponentLine($"{part.Quantity:N0} × {part.Name}", "untradable");
                var mark = _owner.HasSavedBarterMaterialValue(part.ItemId) ? string.Empty : "*";
                return new BarterComponentLine($"{part.Quantity:N0} × {part.Name}",
                    (part.Quantity * unit).ToString("N0", CultureInfo.CurrentCulture) + mark + " G");
            }).ToList();
            var verdict = gain > 0 ? "Run it" : "Sell mats";
            rows.Add(new BarterVerdictRow
            {
                Name = good.Name + (!good.IsSeasonal ? string.Empty : good.IsInactiveRotation ? " (rotating · not active)" : " (rotating · active)"),
                Icon = good.Icon,
                IsScathach = good.PostId == PostIds.ScathachBeach,
                PostName = untradableRecipe
                    ? $"{postName} · untradable materials · owned for {ownedUnits.ToString("N0", CultureInfo.CurrentCulture)}"
                    : $"{postName} · materials {materialGold.ToString("N0", CultureInfo.CurrentCulture)} G",
                Components = components,
                SaleLabel = sale > 0 ? $"Best sale: {destination}" : "Best sale: no price yet",
                SaleValueText = sale > 0
                    ? $"{reward.DucatGain.ToString("N0", CultureInfo.CurrentCulture)} Ducats + {rawGold.ToString("N0", CultureInfo.CurrentCulture)} raw G = {saleGold.ToString("N0", CultureInfo.CurrentCulture)} G"
                    : "—",
                Gain = gain,
                GainText = (gain >= 0 ? "+" : string.Empty) + gain.ToString("N0", CultureInfo.CurrentCulture) + (estimated ? "*" : string.Empty),
                GainBrush = gain > 0 ? GainBrush : gain < 0 ? LossBrush : NeutralBrush,
                Verdict = verdict,
                Tooltip = $"{good.Name} at {postName}\nCost: {recipeText}\n" +
                    (untradableRecipe
                        ? $"Materials are untradable, so they have no Gold value. Owned materials cover {ownedUnits:N0} unit(s).\n"
                        : $"Material value: {materialGold:N0} Gold{(estimated ? " (* some materials have no value entered and count as 0)" : string.Empty)}\n") +
                    $"Best sale at {destination}: {sale:N0} Ducats per unit\n" +
                    _owner.RewardTooltip(reward) + "\n" +
                    $"Gain per unit: {gain:N0} Gold before transport costs and travel time.\n" +
                    (untradableRecipe ? "These materials cannot be sold, so bartering is their only use."
                        : gain > 0 ? "Bartering beats selling these materials on the player market." : "Selling these materials as-is is worth more than bartering them.")
            });
        }
        VerdictList.ItemsSource = rows.OrderBy(row => row.IsScathach).ThenByDescending(row => row.Gain).ToList();
    }

    private void ApplyValues()
    {
        var rate = TryParse(GoldPerDucatInput.Text) is { } parsedRate and > 0 ? parsedRate : 1m;
        var values = _materials.ToDictionary(row => row.Id, row => TryParse(row.ValueText));
        _owner.ApplyBarterMaterialValues(values, rate);
        RefreshVerdicts();
    }

    private static ImageSource? LoadIcon(string fileName)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(Path.Combine(AppContext.BaseDirectory, "Data", "CommerceUI", fileName));
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void CopyMaterialName_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } button || string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Clipboard.SetText(name);
            button.ToolTip = $"Copied \"{name}\"";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            button.ToolTip = "Clipboard is busy; try again";
        }
    }

    // Paper strips are 7 x 2 tiles cropped from the in-game paper edge and fill.
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

    private static decimal? TryParse(string? text)
    {
        var cleaned = (text ?? string.Empty).Replace(",", string.Empty).Trim();
        return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
    }

    private void ScheduleApply()
    {
        if (_loading) return;
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    private void MaterialValue_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box) FormatWithThousandsSeparators(box);
        ScheduleApply();
    }

    // A click into an unfocused box or any double-click selects the whole value; the default word selection stops at commas.
    private void MaterialValue_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box || (box.IsKeyboardFocusWithin && e.ClickCount < 2)) return;
        box.Focus();
        box.SelectAll();
        e.Handled = true;
    }

    private void MaterialValue_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) box.SelectAll();
    }

    // Re-inserts commas as the user types, keeping the caret after the same digit.
    private static void FormatWithThousandsSeparators(TextBox box)
    {
        var text = box.Text;
        var raw = text.Replace(",", string.Empty);
        var dot = raw.IndexOf('.');
        var whole = dot < 0 ? raw : raw[..dot];
        if (whole.Length == 0 || !whole.All(char.IsAsciiDigit) || !decimal.TryParse(whole, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return;
        var formatted = number.ToString("#,0", CultureInfo.InvariantCulture) + (dot < 0 ? string.Empty : raw[dot..]);
        if (formatted == text) return;
        var digitsBeforeCaret = text[..Math.Min(box.CaretIndex, text.Length)].Count(ch => ch != ',');
        var caret = 0;
        for (var seen = 0; caret < formatted.Length && seen < digitsBeforeCaret; caret++)
            if (formatted[caret] != ',') seen++;
        box.Text = formatted;
        box.CaretIndex = caret;
    }

    private void OwnedMaterial_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || sender is not TextBox { DataContext: OwnedMaterialRow row } box) return;
        if (BarterShoppingList.TryParseHave(box.Text, out var have))
        {
            box.ClearValue(Control.BorderBrushProperty);
            _owner.SetBarterMaterialOnHand(row.Id, have);
            ScheduleApply();
        }
        else
        {
            box.BorderBrush = LossBrush;
        }
    }

    private void GoldPerDucatInput_TextChanged(object sender, TextChangedEventArgs e) => ScheduleApply();

    private void LetterPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LetterPicker.SelectedItem is not LetterOption option) return;
        s_letter = option.Kind;
        if (!_loading) RefreshVerdicts();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (_applyTimer.IsEnabled)
        {
            _applyTimer.Stop();
            ApplyValues();
        }
        base.OnClosed(e);
    }
}

public sealed record LetterOption(GuaranteeLetterKind Kind, string Label)
{
    // The themed ComboBox template shows the selection box via ToString, not DisplayMemberPath.
    public override string ToString() => Label;
}

public sealed class MaterialValueRow
{
    public MaterialValueRow(BarterMaterialCatalogEntry material, decimal? savedValue, string usedBy)
    {
        Id = material.Id;
        Name = material.Name;
        ValueText = savedValue is { } value ? value.ToString("#,0.##", CultureInfo.InvariantCulture) : string.Empty;
        Tooltip = $"{material.Name}\nUsed by: {usedBy}" +
            (string.IsNullOrWhiteSpace(material.Description) ? string.Empty : "\n" + material.Description);
    }

    public int Id { get; }
    public string Name { get; }
    public string ValueText { get; set; }
    public string Tooltip { get; }
}

public sealed class OwnedMaterialRow
{
    public OwnedMaterialRow(BarterMaterialCatalogEntry material, int owned, string usedBy)
    {
        Id = material.Id;
        Name = material.Name;
        OwnedText = owned > 0 ? owned.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
        Tooltip = $"{material.Name} (untradable)\nUsed by: {usedBy}" +
            (string.IsNullOrWhiteSpace(material.Description) ? string.Empty : "\n" + material.Description);
    }

    public int Id { get; }
    public string Name { get; }
    public string OwnedText { get; set; }
    public string Tooltip { get; }
}

public sealed class BarterVerdictRow
{
    public bool IsScathach { get; init; }
    public string Name { get; init; } = string.Empty;
    public BitmapImage? Icon { get; init; }
    public string PostName { get; init; } = string.Empty;
    public IReadOnlyList<BarterComponentLine> Components { get; init; } = [];
    public string SaleLabel { get; init; } = string.Empty;
    public string SaleValueText { get; init; } = string.Empty;
    public decimal Gain { get; init; }
    public string GainText { get; init; } = string.Empty;
    public Brush GainBrush { get; init; } = Brushes.White;
    public string Verdict { get; init; } = string.Empty;
    public string Tooltip { get; init; } = string.Empty;
}

public sealed record BarterComponentLine(string Label, string ValueText);
