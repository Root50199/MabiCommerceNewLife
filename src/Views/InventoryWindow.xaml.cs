using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MabiCommerceNewLife;

public partial class InventoryWindow : Window
{
    private const int WidthSegments = 4;
    private const int HeightSegments = 23;
    internal const int MaxLetterSupply = 9_999;
    internal const int MaxLetterMarketValue = 100_000_000;

    private readonly MainWindow _owner;
    private readonly List<LetterRow> _letters;
    private readonly List<ModifierRow> _modifiers;
    private readonly List<EnchantSlot> _enchantSlots;
    private readonly List<RatingRow> _ratings;

    public InventoryWindow(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;
        Owner = owner;
        BackgroundArt.Source = MainWindow.BuildSettingsMenuBackground(HeightSegments, WidthSegments);
        Width = Root.Width = BackgroundArt.Width = 270 + 46 * WidthSegments;
        Height = Root.Height = BackgroundArt.Height = 90 + 32 * HeightSegments;
        foreach (var (top, fill, bottom) in new[] { (LettersPaperTop, LettersPaperFill, LettersPaperBottom), (EnchantPaperTop, EnchantPaperFill, EnchantPaperBottom), (MasteryPaperTop, MasteryPaperFill, MasteryPaperBottom), (ModifierPaperTop, ModifierPaperFill, ModifierPaperBottom) })
        {
            top.Background = CreateTileBrush("PageTopBorder.png");
            fill.Background = CreateTileBrush("Page.png");
            bottom.Background = CreateTileBrush("PageBottomBorder.png");
        }
        CloseIcon.Source = owner.TryFindResource("SettingsCloseIcon") as ImageSource;

        _letters = [.. GuaranteeLetters.All.Select(info =>
            new LetterRow(info, owner.GetGuaranteeLetterSupply(info.Kind), owner.GetGuaranteeLetterMarketValue(info.Kind)))];
        LetterItems.ItemsSource = _letters;

        _modifiers = [];
        foreach (var group in CommerceModifiers.All.GroupBy(modifier => modifier.Group))
        {
            _modifiers.Add(ModifierRow.ForHeader(CommerceModifiers.GroupName(group.Key)));
            _modifiers.AddRange(group.Select(modifier => new ModifierRow(modifier, owner.GetModifierCount(modifier.Id))));
        }
        ModifierItems.ItemsSource = _modifiers;

        _enchantSlots =
        [
            new(1, EnchantPosition.Prefix, Slot1Prefix, Slot1PrefixRoll),
            new(1, EnchantPosition.Suffix, Slot1Suffix, Slot1SuffixRoll),
            new(2, EnchantPosition.Prefix, Slot2Prefix, Slot2PrefixRoll),
            new(2, EnchantPosition.Suffix, Slot2Suffix, Slot2SuffixRoll)
        ];
        foreach (var slot in _enchantSlots) InitializeEnchantSlot(slot);

        MasteryIcon.Source = owner.TryFindResource("CommerceMasteryArt") as ImageSource;
        MasteryRankPicker.ItemsSource = MainWindow.CommerceMasteryRanks;
        MasteryRankPicker.SelectedItem = owner.CommerceMasteryRank;
        UpdateMasteryTooltip();

        _ratings = [.. owner.MerchantRatingPosts().Select(post => new RatingRow(post, owner.GetMerchantRating(post.Id)))];
        RatingItems.ItemsSource = _ratings;
        UpdateFooter();
    }

    private void InitializeEnchantSlot(EnchantSlot slot)
    {
        var options = new List<EnchantOption> { new(null) };
        options.AddRange(AccessoryEnchants.All.Where(enchant => enchant.Position == slot.Position).Select(enchant => new EnchantOption(enchant)));
        slot.Combo.ItemsSource = options;
        var saved = AccessoryEnchants.Find(_owner.GetAccessoryEnchant(slot.Key), slot.Position);
        slot.Combo.SelectedItem = options.First(option => option.Enchant == saved);
        if (saved is { HasRoll: true })
            slot.Roll.Text = Math.Clamp(_owner.GetAccessoryEnchantRoll(slot.Key) ?? saved.MinRoll, saved.MinRoll, saved.MaxRoll).ToString(CultureInfo.CurrentCulture);
        UpdateRollBox(slot);
        slot.Combo.SelectionChanged += (_, _) => EnchantSelectionChanged(slot);
        slot.Roll.TextChanged += (_, _) => EnchantRollChanged(slot);
        slot.Roll.LostKeyboardFocus += (_, _) =>
        {
            slot.Roll.Text = _owner.GetAccessoryEnchantRoll(slot.Key)?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            slot.Roll.ClearValue(Border.BorderBrushProperty);
        };
    }

    private static void UpdateRollBox(EnchantSlot slot)
    {
        var enchant = (slot.Combo.SelectedItem as EnchantOption)?.Enchant;
        slot.Roll.Visibility = enchant is { HasRoll: true } ? Visibility.Visible : Visibility.Collapsed;
        slot.Roll.ToolTip = enchant is { HasRoll: true }
            ? $"Rolled {(enchant.RollIsDiscount ? "purchase discount" : "Merchant Rating gain")} %: whole number from {enchant.MinRoll} to {enchant.MaxRoll}"
            : null;
        slot.Combo.ToolTip = enchant is null ? "No commerce enchant" : $"{enchant.Name}: {enchant.Effect}";
    }

    private void EnchantSelectionChanged(EnchantSlot slot)
    {
        var enchant = (slot.Combo.SelectedItem as EnchantOption)?.Enchant;
        int? roll = enchant is { HasRoll: true } ? enchant.MinRoll : null;
        _owner.SetAccessoryEnchant(slot.Key, enchant?.Id, roll);
        slot.Roll.Text = roll?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        slot.Roll.ClearValue(Border.BorderBrushProperty);
        UpdateRollBox(slot);
        UpdateFooter();
    }

    private void EnchantRollChanged(EnchantSlot slot)
    {
        if ((slot.Combo.SelectedItem as EnchantOption)?.Enchant is not { HasRoll: true } enchant) return;
        if (!TryParseWhole(slot.Roll.Text, enchant.MaxRoll, out var roll) || roll is null || roll < enchant.MinRoll)
        {
            slot.Roll.BorderBrush = Brushes.Red;
            return;
        }
        slot.Roll.ClearValue(Border.BorderBrushProperty);
        if (_owner.GetAccessoryEnchantRoll(slot.Key) == roll) return;
        _owner.SetAccessoryEnchant(slot.Key, enchant.Id, roll);
        UpdateFooter();
    }

    private void UpdateFooter()
    {
        var totals = _owner.ModifierTotals(false);
        var parts = new List<string>();
        if (totals.DucatPercent > 0) parts.Add($"+{totals.DucatPercent:0.##}% Ducats");
        if (totals.ExtraSlots > 0 || totals.ExtraWeight > 0) parts.Add($"+{totals.ExtraSlots} slot, +{totals.ExtraWeight:N0} weight");
        if (totals.TransportSpeedPercent > 0) parts.Add($"+{totals.TransportSpeedPercent:0.##}% speed");
        if (totals.PurchaseDiscountPercent > 0) parts.Add($"{totals.PurchaseDiscountPercent:0.##}% enchant discount");
        if (totals.MerchantRatingPercent > 0) parts.Add($"+{totals.MerchantRatingPercent:0.##}% rating gain");
        FooterText.Text = parts.Count == 0 ? "No modifiers selected." : "Active: " + string.Join(" · ", parts) + ".";
        FooterText.ToolTip = "Speed applies to land travel legs, not ferry waits or sailing. Scan checks allow the buying outpost's Merchant Rating discount plus the enchant discount. Merchant Rating gain is tracked only. The active letter is chosen per trade type on the main window (Manual, Auto and Load Profit tabs).";
    }

    internal static bool TryParseWhole(string text, int max, out int? value)
    {
        value = null;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return true;
        if (!int.TryParse(trimmed, NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var parsed) ||
            parsed < 0 || parsed > max) return false;
        value = parsed;
        return true;
    }

    internal static bool TryParseSupply(string text, out int? supply) => TryParseWhole(text, MaxLetterSupply, out supply);

    internal static bool TryParseMarketValue(string text, out int? value) => TryParseWhole(text, MaxLetterMarketValue, out value);

    private void SaveLetter(LetterRow row)
    {
        _owner.SetGuaranteeLetterInventory(row.Info.Kind, row.Supply, row.MarketValue);
        UpdateFooter();
    }

    private void LetterHave_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: LetterRow row } box) return;
        if (!TryParseSupply(box.Text, out var supply))
        {
            box.BorderBrush = Brushes.Red;
            return;
        }
        box.ClearValue(Border.BorderBrushProperty);
        if (row.Supply == supply) return;
        row.Supply = supply;
        SaveLetter(row);
    }

    // A malformed entry is never saved; leaving the box shows the saved value again.
    private void LetterHave_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: LetterRow row } box) return;
        if (TryParseSupply(box.Text, out var supply) && supply == row.Supply) return;
        box.Text = row.HaveText;
        box.ClearValue(Border.BorderBrushProperty);
    }

    private void LetterValue_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: LetterRow row } box) return;
        if (!TryParseMarketValue(box.Text, out var value))
        {
            box.BorderBrush = Brushes.Red;
            return;
        }
        box.ClearValue(Border.BorderBrushProperty);
        if (row.MarketValue == value) return;
        row.MarketValue = value;
        SaveLetter(row);
    }

    private void LetterValue_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: LetterRow row } box) return;
        box.Text = row.ValueText;
        box.ClearValue(Border.BorderBrushProperty);
    }

    private void SetModifier(ModifierRow row, int count)
    {
        if (row.Modifier is not { } modifier) return;
        if (count > 0 && modifier.ExclusiveGroup is { } exclusive)
        {
            foreach (var other in _modifiers.Where(other => other != row && other.Modifier?.ExclusiveGroup == exclusive && other.Count > 0))
            {
                other.Count = 0;
                _owner.SetModifierCount(other.Modifier!.Id, 0);
            }
        }
        row.Count = count;
        _owner.SetModifierCount(modifier.Id, count);
        UpdateFooter();
    }

    private void ModifierCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: ModifierRow row } box) SetModifier(row, box.IsChecked == true ? 1 : 0);
    }

    private void ModifierCount_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: ModifierRow { Modifier: { } modifier } row } box) return;
        if (!TryParseWhole(box.Text, modifier.MaxCount, out var count))
        {
            box.BorderBrush = Brushes.Red;
            return;
        }
        box.ClearValue(Border.BorderBrushProperty);
        if ((count ?? 0) != row.Count) SetModifier(row, count ?? 0);
    }

    // Clear a 0 so typing replaces it instead of producing an over-long or invalid value.
    private void ModifierCount_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (box.Text.Trim() == "0") box.Clear();
        else box.SelectAll();
    }

    private void ModifierCount_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: ModifierRow row } box) return;
        box.Text = row.CountText;
        box.ClearValue(Border.BorderBrushProperty);
    }
    private void MasteryRank_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MasteryRankPicker.SelectedItem is not string rank) return;
        _owner.SetCommerceMasteryRank(rank);
        UpdateMasteryTooltip();
    }

    private void UpdateMasteryTooltip()
    {
        var rank = _owner.CommerceMasteryRank;
        var tooltip = $"Commerce Mastery rank {rank} (character-wide): +{CommerceRewardModel.MasteryPercent(rank)}% of profit as extra Ducats, Gold and EXP on every profitable sale.";
        MasteryIcon.ToolTip = MasteryLabel.ToolTip = MasteryRankPicker.ToolTip = tooltip;
    }

    private void RatingsToggle_Changed(object sender, RoutedEventArgs e) =>
        RatingItems.Visibility = RatingsToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void Rating_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: RatingRow row } box) return;
        if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var rating) || rating < 1)
        {
            box.BorderBrush = Brushes.Red;
            return;
        }
        box.ClearValue(Border.BorderBrushProperty);
        if (rating == row.Rating) return;
        row.Rating = rating;
        _owner.SetMerchantRating(row.Post.Id, rating);
    }

    private void Rating_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: RatingRow row } box) return;
        box.Text = row.RatingText;
        box.ClearValue(Border.BorderBrushProperty);
    }

    // Called when the main window's rating box changes so both views stay in step.
    internal void SyncMerchantRating(int postId, int rating)
    {
        if (_ratings.FirstOrDefault(row => row.Post.Id == postId) is { } row && row.Rating != rating)
            row.SetRating(rating);
    }

    // A click into an unfocused box or any double-click selects the whole value; the default word selection stops at commas.
    private void Entry_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box || (box.IsKeyboardFocusWithin && e.ClickCount < 2)) return;
        box.Focus();
        box.SelectAll();
        e.Handled = true;
    }

    private void Entry_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) box.SelectAll();
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

    private sealed class LetterRow(GuaranteeLetterInfo info, int? supply, int? marketValue)
    {
        public GuaranteeLetterInfo Info { get; } = info;
        public int? Supply { get; set; } = supply;
        public int? MarketValue { get; set; } = marketValue;
        public string ShortName => Info.Name.Replace(" Letter of Guarantee", string.Empty, StringComparison.Ordinal);
        public string NormalText => $"+{Info.NormalPercent}%";
        public string BarterText => $"+{Info.BarterPercent}%";
        public string HaveText => Supply?.ToString("N0", CultureInfo.CurrentCulture) ?? string.Empty;
        public string ValueText => MarketValue?.ToString("N0", CultureInfo.CurrentCulture) ?? string.Empty;
        public string HaveLabel => $"{Info.Name} owned";
        public string ValueLabel => $"{Info.Name} market value in Gold";
        public string Tooltip =>
            $"{Info.Name}\nTrade and group goods: +{Info.NormalPercent}% of profit as Ducats{(Info.BoostsGold ? ", Gold" : string.Empty)} and EXP\n" +
            $"Barter goods: +{Info.BarterPercent}% of the selling price\nUsing it also grants {Info.DucatsGranted:N0} Ducats" +
            (Info.BoostsGold ? string.Empty : "\nNo Gold bonus");
    }

    private sealed class RatingRow(CommercePost post, int rating) : INotifyPropertyChanged
    {
        public CommercePost Post { get; } = post;
        public int Rating { get; set; } = rating;
        public string Name => Post.Name;
        public string RatingText => Rating.ToString(CultureInfo.CurrentCulture);
        public string Label => $"{Post.Name} Merchant Rating level";
        public string Tooltip => $"Your Merchant Rating level at {Post.Name}. Mirrors the rating box on the main window when this outpost is selected.";
        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetRating(int value)
        {
            Rating = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RatingText)));
        }
    }

    private sealed record EnchantSlot(int Number, EnchantPosition Position, ComboBox Combo, TextBox Roll)
    {
        public string Key => AccessoryEnchants.SlotKey(Number, Position);
    }

    private sealed class EnchantOption(AccessoryEnchant? enchant)
    {
        public AccessoryEnchant? Enchant { get; } = enchant;
        public string Name => Enchant?.Name ?? "(none)";
        public string Tooltip => Enchant is null ? "No commerce enchant" : $"{Enchant.Name}: {Enchant.Effect}";
        public override string ToString() => Name;
    }

    private sealed class ModifierRow : INotifyPropertyChanged
    {
        private int _count;

        public ModifierRow(CommerceModifier modifier, int count)
        {
            Modifier = modifier;
            _count = Math.Clamp(count, 0, modifier.MaxCount);
        }

        private ModifierRow(string header) => Header = header;

        public static ModifierRow ForHeader(string header) => new(header);

        public event PropertyChangedEventHandler? PropertyChanged;

        public CommerceModifier? Modifier { get; }
        public string Header { get; } = string.Empty;

        public string Name => Modifier?.Name ?? string.Empty;
        public string Effect => Modifier?.Effect ?? string.Empty;
        public string? Tooltip => Modifier is null ? null : $"{Modifier.Name}: {Modifier.Effect}\n{Modifier.Detail}";
        public Visibility HeaderVisibility => Modifier is null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RowVisibility => Modifier is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility CheckVisibility => Modifier is { MaxCount: 1 } ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CountVisibility => Modifier is { MaxCount: > 1 } ? Visibility.Visible : Visibility.Collapsed;
        public string CountTooltip => $"Whole number from 0 to {Modifier?.MaxCount ?? 0}";
        public string CountText => _count.ToString(CultureInfo.CurrentCulture);
        public bool IsChecked => _count > 0;

        public int Count
        {
            get => _count;
            set
            {
                if (_count == value) return;
                _count = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }
    }
}