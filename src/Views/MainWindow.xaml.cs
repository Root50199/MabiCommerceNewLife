using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MabiCommerceNewLife;

public partial class MainWindow : Window
{
    private const int SmugglerDestinationId = -1;
    private readonly ObservableCollection<CommercePost> _posts;
    private readonly ObservableCollection<GoodsEntry> _products;
    // Barter goods share product IDs with trade goods in the client tables, so they are keyed with an offset.
    private const int BarterProductIdOffset = 1_000_000;
    // Display-only tag so this barter offer isn't confused with Tara's trade Rocking Chair.
    private const int KaruRockingChairOfferId = 21006;
    private const int KaruOasisRouteId = 201202;
    private const int CalidaPeraRouteId = 203204;
    private static readonly (int RouteId, int FirstPostId, int SecondPostId, string Name)[] BarterPairRoutes =
    [
        (KaruOasisRouteId, PostIds.KaruForest, PostIds.Oasis, "Karu + Oasis"),
        (CalidaPeraRouteId, PostIds.Calida, PostIds.Pera, "Calida + Pera")
    ];
    private readonly List<BarterMaterialCatalogEntry> _barterMaterialCatalog;
    private readonly List<MerchantRatingLevel> _merchantRatingLevels;
    private readonly List<CommerceTransport> _transports;
    private readonly Dictionary<int, List<DestinationQuote>> _quotesByProduct = [];
    private readonly Dictionary<int, CargoLine> _tradeCargoByProduct = [];
    private readonly Dictionary<int, CargoLine> _groupCargoByProduct = [];
    private readonly Dictionary<int, CargoLine> _barterCargoByProduct = [];
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<PurchasePlan, StrongBox<int>> _planSourceIds = new();
    // The letter Auto assigned to each route, and the Gold per sale Ducat its barter search used (for purchase validation).
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<PurchasePlan, StrongBox<GuaranteeLetterKind>> _planLetters = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<PurchasePlan, StrongBox<decimal>> _planSearchK = new();
    private BarterMaterialsWindow? _barterMaterialsWindow;
    private InventoryWindow? _inventoryWindow;
    private ShoppingListWindow? _shoppingListWindow;
    private TradeHistoryWindow? _tradeHistoryWindow;
    private TradeHistory? _tradeHistory;
    private readonly HashSet<int> _availableTransportIds = [1];
    private readonly IReadOnlyDictionary<(int SourceId, int DestinationId), decimal> _regionalRouteHandcartMinutes =
        RegionalRouteEstimates.LoadHandcartMinutes(Path.Combine(AppContext.BaseDirectory, "Data", "regional-route-distances.json"));
    private readonly IReadOnlyDictionary<(int SourceId, int DestinationId), int> _regionalRoutePortalCounts =
        RegionalRouteEstimates.LoadPortalCounts(Path.Combine(AppContext.BaseDirectory, "Data", "regional-route-distances.json"));
    private readonly ObservableCollection<DestinationTotal> _destinationTotals = [];
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _shipTimeRefreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _weeklyResetTimer = new();
    // Stock is not saved between launches, so every launch starts with refreshed (full) stock.
    // When the saved Group/Barter stock was last known current; a weekly reset after this triggers a restock.
    private DateTime _lastGroupStockRefreshUtc = DateTime.UtcNow;
    private readonly ShipScheduleEstimator _shipScheduleEstimator =
        ShipScheduleEstimator.Load(Path.Combine(AppContext.BaseDirectory, "Data", "ship-schedules.json"));
    private CancellationTokenSource? _autoCalculationCancellation;
    private AutoPlanRow? _selectedAutoPlan;
    private AutoExpandedRoutesWindow? _autoExpandedRoutesWindow;
    private readonly RouteMapData? _routeMapData = LoadRouteMapData();
    private RouteMapWindow? _routeMapWindow;
    private IReadOnlyList<AutoExpandedRouteRow> _autoExpandedProfitRoutes = [];
    private IReadOnlyList<AutoExpandedRouteRow> _autoExpandedPerMinuteRoutes = [];
    private bool _updatingAutoPlanSelection;
    private bool _initializing = true;
    private GoodsMode _goodsMode = GoodsMode.Trade;
    private bool _bestOverallAutoMode;
    private bool _partnerEnabled;
    private bool _williamEnabled;
    private bool _alpacaEnabled;
    private PlannerPreferences _plannerPreferences = new();
    private System.Drawing.Rectangle? _lastSuccessfulScanRegion;
    private int _displaySourceId = -1;
    private GoodsMode? _displayGoodsMode;
    private int? _selectedSaleDestinationId;
    // Destination of a loaded Auto plan per goods mode; manually added goods drop it and use the route map picker.
    private readonly Dictionary<GoodsMode, int> _plannedRouteDestinationByMode = [];
    private bool _updatingRouteMapPicker;
    private readonly Dictionary<(int From, int To), decimal> _postPairWeights = [];

    public MainWindow()
    {
        InitializeComponent();
        var autoPlaceholderRows = Enumerable.Range(0, 3).ToArray();
        AutoProfitPlaceholder.ItemsSource = autoPlaceholderRows;
        AutoTimePlaceholder.ItemsSource = autoPlaceholderRows;
        AutoCustomPlaceholder.ItemsSource = autoPlaceholderRows.Take(1).ToArray();
        _plannerPreferences = LoadPlannerPreferences();
        ScreenReaderModeCheckBox.IsChecked = _plannerPreferences.ScreenReaderModeEnabled;
        DetailedRewardTooltipsCheckBox.IsChecked = _plannerPreferences.DetailedRewardTooltips;
        ScanDebugModeCheckBox.IsChecked = _plannerPreferences.ScanDebugMode;
        TradeProfitInGoldCheckBox.IsChecked = _plannerPreferences.ShowTradeProfitInGold;
        ReadPriceListButton.Visibility = _plannerPreferences.ScreenReaderModeEnabled
            ? Visibility.Visible : Visibility.Collapsed;
        GroupReadPriceListButton.Visibility = Visibility.Collapsed;
        DucatsInput.Text = FormatDucats(_plannerPreferences.CurrentDucats);
        Topmost = _plannerPreferences.AlwaysOnTop;
        TopmostGuard.Watch(this);
        AlwaysOnTopCheckBox.IsChecked = _plannerPreferences.AlwaysOnTop;
        AutoRefreshGroupStockCheckBox.IsChecked = _plannerPreferences.AutoRefreshGroupStock;
        SaveLastScanRegionCheckBox.IsChecked = _plannerPreferences.SaveLastScanRegion;
        if (_plannerPreferences.FerryBufferSeconds is < 0 or > ShipScheduleEstimator.MaxMissedDepartureBufferSeconds)
            _plannerPreferences.FerryBufferSeconds = ShipScheduleEstimator.DefaultMissedDepartureBufferSeconds;
        FerryBufferCheckBox.IsChecked = _plannerPreferences.FerryBufferEnabled;
        FerryBufferSecondsInput.Text = _plannerPreferences.FerryBufferSeconds.ToString(CultureInfo.InvariantCulture);
        FerryBufferSecondsInput.IsEnabled = _plannerPreferences.FerryBufferEnabled;
        if (_plannerPreferences.PortalLoadBufferSeconds is < 0 or > RegionalRouteEstimates.MaxPortalLoadBufferSeconds)
            _plannerPreferences.PortalLoadBufferSeconds = RegionalRouteEstimates.DefaultPortalLoadBufferSeconds;
        PortalLoadBufferSecondsInput.Text = _plannerPreferences.PortalLoadBufferSeconds.ToString(CultureInfo.InvariantCulture);
        if (_plannerPreferences.SaveLastScanRegion)
            _lastSuccessfulScanRegion = _plannerPreferences.LastScanRegion?.ToRectangle();
        Resources["ResetIconArt"] = LoadImage("Data/CommerceUI/ResetIcon.png");
        Resources["GoldCurrencyArt"] = LoadImage("Data/CommerceUI/Icon_Currency_Gold.png");
        Resources["DucatCurrencyArt"] = LoadImage("Data/CommerceUI/Icon_Currency_Ducat.png");
        Resources["TransportViewToggleArt"] = LoadImage("Data/CommerceUI/TransportViewToggle.png");
        ApplyTransportView();
        FontFamily = new System.Windows.Media.FontFamily(
            new Uri(Path.Combine(AppContext.BaseDirectory, "Data", "Fonts") + Path.DirectorySeparatorChar, UriKind.Absolute),
            "./#NanumGothic");
        TradingPostFrameImage.Source = LoadImage("Data/CommerceUI/TradingPostFrameCustom.png");
        SettingsButtonImage.Source = LoadImage("Data/CommerceUI/Settings.png");
        Resources["SettingsMenuBackgroundArt"] = BuildSettingsMenuBackground(11);
        Resources["BracketTopLeftArt"] = LoadImage("Data/CommerceUI/BracketTopLeft.png");
        Resources["BracketTopRightArt"] = LoadImage("Data/CommerceUI/BracketTopRight.png");
        Resources["BracketBottomLeftArt"] = LoadImage("Data/CommerceUI/BracketBottomLeft.png");
        Resources["BracketBottomRightArt"] = LoadImage("Data/CommerceUI/BracketBottomRight.png");
        Resources["PaperTopBrush"] = CreatePaperTileBrush("Data/CommerceUI/PageTopBorder.png");
        Resources["PaperFillBrush"] = CreatePaperTileBrush("Data/CommerceUI/Page.png");
        Resources["PaperBottomBrush"] = CreatePaperTileBrush("Data/CommerceUI/PageBottomBorder.png");
        Resources["CommerceMasteryArt"] = LoadImage("Data/CommerceUI/CommerceMasterySkill.png");
        RefreshActiveLetterPickers();
        Resources["PurchaseButtonArt"] = LoadImage("Data/CommerceUI/PurchaseButton.png");
        CloseButtonImage.Source = LoadImage("Data/CommerceUI/CloseButton.png");
        MinimizeButtonImage.Source = LoadImage("Data/CommerceUI/MinimizeButton.png");
        Resources["SettingsCloseIcon"] = CloseButtonImage.Source;
        ProductListGuardTopImage.Source = LoadImage("Data/CommerceUI/ProductListGuardTop.png");
        ProductListGuardBottomImage.Source = LoadImage("Data/CommerceUI/ProductListGuardBottom.png");
        Resources["DetailsIconArt"] = TintImage(LoadImage("Data/CommerceUI/DetailsIcon.png"), 0x3B, 0x2A, 0x12);
        GoodsToCenterArrow.Source = LoadImage("Data/CommerceUI/CenterArrowRight.png");
        TransportToCenterArrow.Source = LoadImage("Data/CommerceUI/CenterArrowLeft.png");
        UpdateMiddleTabChevron();
        TransportHeaderImage.Source = LoadImage("Data/CommerceUI/TransportHeader.png");
        WeightGaugeBackImage.Source = LoadImage("Data/CommerceUI/WeightGaugeBack.png");
        InventoryListGuardTopImage.Source = LoadImage("Data/CommerceUI/InventoryListGuardTop.png");
        InventoryListGuardBottomImage.Source = LoadImage("Data/CommerceUI/InventoryListGuardBottom.png");
        DucatPlateImage.Source = LoadImage("Data/CommerceUI/DucatPlate.png");
        BarterMaterialsButton.Tag = LoadImage("Data/CommerceUI/AuctionHouseIcon.png");
        InventoryButton.Tag = TintImage(LoadImage("Data/CommerceUI/GreyscaleBagIcon.png"), 0xD0, 0xA6, 0x4D);
        Resources["InventorySlotFrame"] = LoadImage("Data/CommerceUI/InventorySlot.png");
        var idleProductRow = LoadImage("Data/CommerceUI/ProductRowIdle.png");
        var lockedProductRow = LoadImage("Data/CommerceUI/ProductRowLocked.png");
        var selectedProductRow = LoadImage("Data/CommerceUI/ProductRowSelected.png");

        var catalogPath = Path.Combine(AppContext.BaseDirectory, "Data", "commerce-catalog.json");
        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException("The imported commerce catalog was not found beside the application.", catalogPath);
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var catalog = JsonSerializer.Deserialize<CommerceCatalog>(File.ReadAllText(catalogPath), options)
            ?? throw new InvalidDataException("The imported commerce catalog is empty.");

        // Scathach Beach is listed after every other post.
        _posts = new ObservableCollection<CommercePost>(catalog.Posts.OrderBy(post => post.Id == PostIds.ScathachBeach).ThenBy(post => post.Id).Select(post =>
        {
            post.Icon = LoadImage(post.IconPath);
            return post;
        }));
        _products = new ObservableCollection<GoodsEntry>(catalog.Products.Select(product =>
        {
            if (_plannerPreferences.ManualBuyPricesByProductId.TryGetValue(product.Id, out var savedBuyPrice) && savedBuyPrice > 0)
            {
                product.CurrentBuyPrice = savedBuyPrice;
                product.CurrentBuyPriceText = FormatPrice(savedBuyPrice);
                product.CurrentBuyPriceIsEstimated = false;
            }
            else
            {
                product.CurrentBuyPriceText = FormatPriceMidpoint(product.MinPrice, product.MaxPrice);
                product.CurrentBuyPriceIsEstimated = true;
            }
            product.ActiveIcon = LoadImage(product.IconPath);
            product.LockedIcon = LoadImage(product.LockedIconPath);
            product.Icon = product.ActiveIcon;
            product.IdleRowBackground = idleProductRow;
            product.LockedRowBackground = lockedProductRow;
            product.SelectedRowOverlay = selectedProductRow;
            return product;
        }));
        _barterMaterialCatalog = catalog.BarterMaterials;
        _merchantRatingLevels = catalog.CreditLevels;
        foreach (var pair in catalog.PostWeights)
        {
            _postPairWeights[(pair.Post1Id, pair.Post2Id)] = pair.Weight;
            _postPairWeights[(pair.Post2Id, pair.Post1Id)] = pair.Weight;
        }
        foreach (var offer in catalog.BarterProducts)
        {
            var barterGood = new GoodsEntry
            {
                Id = BarterProductIdOffset + offer.Id,
                CatalogId = offer.Id,
                PostId = offer.PostId,
                Name = offer.Id == KaruRockingChairOfferId ? offer.Name + " (Karu)" : offer.Name,
                GameName = offer.Name,
                RequiredCreditLevel = offer.RequiredCreditLevel,
                ResetType = offer.ResetType,
                SourceCount = offer.SourceCount,
                Weight = offer.Weight,
                MaxBundle = offer.MaxBundle,
                MaxStock = offer.MaxStock,
                MinPrice = offer.MinPrice,
                MaxPrice = offer.MaxPrice,
                IconPath = offer.IconPath,
                LockedIconPath = offer.IconPath,
                IsBarter = true,
                IsSeasonal = offer.IsSeasonal,
                Recipe = offer.Recipe
            };
            barterGood.ActiveIcon = LoadImage(offer.IconPath);
            barterGood.LockedIcon = barterGood.ActiveIcon;
            barterGood.Icon = barterGood.ActiveIcon;
            barterGood.IdleRowBackground = idleProductRow;
            barterGood.LockedRowBackground = lockedProductRow;
            barterGood.SelectedRowOverlay = selectedProductRow;
            _products.Add(barterGood);
        }
        // Must run after the barter goods are added: their recipes list the materials to seed.
        SeedBarterMaterialDefaults();
        // Rotating grade-1 offers: the client has no schedule, so one alternative per post is active,
        // chosen manually from the name dropdown or by scanning its price list.
        foreach (var group in _products.Where(product => product.IsBarter && product.IsSeasonal).GroupBy(product => product.PostId))
        {
            var alternatives = group.OrderBy(product => product.Id).ToList();
            var defaultId = catalog.BarterProducts.FirstOrDefault(offer =>
                offer.PostId == group.Key && offer.IsSeasonal && offer.IsDefaultRotation)?.Id ?? alternatives[0].CatalogId;
            var activeId = _plannerPreferences.ActiveBarterRotationByPostId.TryGetValue(group.Key, out var savedId) &&
                alternatives.Any(product => product.CatalogId == savedId) ? savedId : defaultId;
            foreach (var alternative in alternatives)
            {
                alternative.RotationAlternatives = alternatives;
                alternative.IsInactiveRotation = alternative.CatalogId != activeId;
            }
        }
        foreach (var postId in _products.Where(product => product.HasRotationAlternatives).Select(product => product.PostId).Distinct())
            RefreshRotationHistory(postId);
        RecalculateBarterCosts();

        var groupSourcePostIds = _products.Where(product => product.CommerceParty)
            .Select(product => product.PostId).ToHashSet();
        foreach (var product in _products)
        {
            _quotesByProduct[product.Id] = _posts
                .Where(post => post.CanSell && post.Id != product.PostId &&
                    (!product.CommerceParty || groupSourcePostIds.Contains(post.Id)))
                .Select(post =>
                {
                    var quote = new DestinationQuote { ProductId = product.Id, PostId = post.Id, PostName = post.Name };
                    quote.PropertyChanged += Quote_PropertyChanged;
                    return quote;
                })
                .ToList();
            if (!product.CommerceParty && !product.IsBarter)
            {
                var smugglerQuote = new DestinationQuote { ProductId = product.Id, PostId = SmugglerDestinationId, PostName = "Smuggler" };
                smugglerQuote.PropertyChanged += Quote_PropertyChanged;
                _quotesByProduct[product.Id].Add(smugglerQuote);
            }
        }
        LoadCurrentMarketPriceOverrides(Path.Combine(AppContext.BaseDirectory, "Data", "current-market-prices.json"));
        LoadSavedDestinationPriceOverrides();

        PostPicker.ItemsSource = _posts.Where(post => post.CanBuy && _products.Any(product => product.PostId == post.Id && ModeOf(product) == GoodsMode.Trade)).ToList();
        MigrateLegacyMerchantRating();
        _transports = catalog.Transports
            .OrderBy(transport => transport.Id)
            .Select(transport =>
            {
                transport.Icon = LoadImage(transport.IconPath);
                if (transport.Name.StartsWith("[Partner]", StringComparison.Ordinal))
                {
                    var baseMatch = Regex.Match(transport.Condition, @"pet\([^,]+,\s*(\d+)\)");
                    if (baseMatch.Success)
                        transport.PartnerBaseTransportId = int.Parse(baseMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                }
                if (transport.Name.StartsWith("[Alpaca]", StringComparison.Ordinal))
                {
                    var baseMatch = Regex.Match(transport.Condition, @"pet\(/pet_lama/,\s*(\d+)\)");
                    if (baseMatch.Success)
                        transport.AlpacaBaseTransportId = int.Parse(baseMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                }
                return transport;
            })
            .ToList();
        _availableTransportIds.UnionWith(LoadAvailableTransportIds());
        _availableTransportIds.RemoveWhere(id => _transports.Any(transport => transport.Id == id && (transport.IsPartner || transport.IsAlpaca)));
        _partnerEnabled = LoadPartnerEnabled();
        _williamEnabled = LoadWilliamEnabled();
        _alpacaEnabled = LoadAlpacaEnabled();
        foreach (var transport in _transports)
            transport.IsOwned = !transport.IsPartner && !transport.IsAlpaca && _availableTransportIds.Contains(transport.Id);
        UpdateDependentTransportOwnership();
        TransportPicker.ItemsSource = EligibleTransports(false).ToList();
        foreach (var post in _posts.Where(post => post.CanSell).OrderBy(post => post.Id))
            _destinationTotals.Add(new DestinationTotal { PostId = post.Id, PostName = post.Name });
        _destinationTotals.Add(new DestinationTotal { PostId = SmugglerDestinationId, PostName = "Smuggler" });
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusPanel.Visibility = Visibility.Collapsed;
        };
        PostPicker.SelectedValue = 2;
        LoadMerchantRatingForSelectedPost();
        RefreshAutoSourceOptions();
        TransportPicker.SelectedValue = 1;
        LoadSavedWeeklyStock();
        _initializing = false;
        RefreshProducts();
        RefreshTransportLoad();
        _lastTimeRefreshMinute = CurrentMinute();
        _weeklyResetTimer.Tick += (_, _) => AutoRefreshGroupStockIfDue();
        Microsoft.Win32.SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
        Closed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            _routeMapWindow?.Close();
            SaveWeeklyStock();
        };
        AutoRefreshGroupStockIfDue();
        _shipTimeRefreshTimer.Tick += (_, _) =>
        {
            // Ferry-dependent trip times move with the clock; refresh them once per minute so tooltips stay steady.
            var minute = CurrentMinute();
            if (minute == _lastTimeRefreshMinute) return;
            _lastTimeRefreshMinute = minute;
            if (TradeMiddleTabs.SelectedIndex == 0) RefreshManualQuoteMetrics();
            RefreshDestinationTotals();
        };
        _shipTimeRefreshTimer.Start();
    }

    private long _lastTimeRefreshMinute;

    private static long CurrentMinute() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;

    // Only a price edit changes profits; time and profit text on the quote are outputs written by the refreshes themselves.
    private void Quote_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DestinationQuote.DestinationPrice)) RefreshDestinationTotals();
    }

    private sealed class PlannerPreferences
    {
        public decimal CurrentDucats { get; set; } = 10000m;
        public bool AlwaysOnTop { get; set; }
        public bool ScreenReaderModeEnabled { get; set; }
        public bool AutoRefreshGroupStock { get; set; }
        public bool SaveLastScanRegion { get; set; }
        public bool FerryBufferEnabled { get; set; } = true;
        public int FerryBufferSeconds { get; set; } = ShipScheduleEstimator.DefaultMissedDepartureBufferSeconds;
        public int PortalLoadBufferSeconds { get; set; } = RegionalRouteEstimates.DefaultPortalLoadBufferSeconds;
        public bool TransportListView { get; set; }
        public RouteMapOverlaySettings RouteMapOverlay { get; set; } = new();
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ScanRegion? LastScanRegion { get; set; }
        public string CommerceMasteryRank { get; set; } = "F";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MerchantRating { get; set; }
        public Dictionary<int, int> MerchantRatingsByPostId { get; set; } = [];
        public Dictionary<int, decimal> ManualBuyPricesByProductId { get; set; } = [];
        public Dictionary<int, Dictionary<int, decimal>> ManualDestinationPricesByProductId { get; set; } = [];
        public Dictionary<int, decimal> BarterMaterialValuesById { get; set; } = [];
        // Set once the starter market values have been written, so later user edits or blanks are never overwritten.
        public bool BarterMaterialStarterValuesApplied { get; set; }
        public Dictionary<int, int> BarterMaterialsOnHandById { get; set; } = [];
        public Dictionary<int, int> ActiveBarterRotationByPostId { get; set; } = [];
        // Scan sightings only; the earlier BarterRotationSightings key may hold manual picks and is ignored.
        public List<BarterRotationSighting> BarterRotationScanSightings { get; set; } = [];
        public decimal GoldPerDucat { get; set; } = 1m;
        // Keyed by GoodsMode name (Trade, Group, Barter); a missing entry means no letter.
        public Dictionary<string, string> ActiveGuaranteeLetterByMode { get; set; } = [];
        // Missing letters are untracked; an entered count of 0 stops the letter from being applied.
        public Dictionary<string, int> GuaranteeLetterSupply { get; set; } = [];
        public Dictionary<string, int> GuaranteeLetterMarketValue { get; set; } = [];
        public Dictionary<string, int> CommerceModifierCounts { get; set; } = [];
        // Keyed by AccessoryEnchants.SlotKey, e.g. "1-prefix".
        public Dictionary<string, string> AccessoryEnchantIds { get; set; } = [];
        public Dictionary<string, int> AccessoryEnchantRolls { get; set; } = [];
        public bool DetailedRewardTooltips { get; set; }
        // Saves auto-accept-worthy scan captures and their OCR text to ScanDebugLog.FolderPath.
        public bool ScanDebugMode { get; set; }
        public bool ShowTradeProfitInGold { get; set; }
        // Remaining weekly stock for Group and Barter goods, keyed by GoodsEntry.Id.
        public Dictionary<int, int> WeeklyStockByProductId { get; set; } = [];
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? WeeklyStockSavedUtc { get; set; }
    }

    private sealed class ScanRegion
    {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public static ScanRegion From(System.Drawing.Rectangle region) =>
            new() { Left = region.Left, Top = region.Top, Width = region.Width, Height = region.Height };

        public System.Drawing.Rectangle? ToRectangle() => Width > 0 && Height > 0
            ? new System.Drawing.Rectangle(Left, Top, Width, Height) : null;
    }

    private GoodsEntry? SelectedProduct => ProductList.SelectedItem as GoodsEntry;
    private Dictionary<int, CargoLine> ActiveCargo => CargoFor(_goodsMode);
    private CommerceTransport? SelectedTransport => TransportPicker.SelectedItem as CommerceTransport;

    private void TradeMiddleTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateMiddleTabChevron();
        if (TradeMiddleTabs.SelectedIndex != 2 || DestinationTotalsList.Items.Count == 0) return;

        if (_selectedSaleDestinationId is int selectedId &&
            DestinationTotalsList.Items.OfType<DestinationTotal>().Any(total => total.PostId == selectedId))
            DestinationTotalsList.SelectedValue = selectedId;
        else
            DestinationTotalsList.SelectedIndex = 0;
        DestinationTotalsList.UpdateLayout();
    }

    private void AutoSourcePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        SyncPostPickerToAutoSource();
        _autoCalculationCancellation?.Cancel();
        _autoExpandedRoutesWindow?.Close();
        AutoProfitResultsList.ItemsSource = null;
        AutoTimeResultsList.ItemsSource = null;
        AutoProfitPlaceholder.Visibility = Visibility.Visible;
        AutoTimePlaceholder.Visibility = Visibility.Visible;
        _autoExpandedProfitRoutes = [];
        _autoExpandedPerMinuteRoutes = [];
        SetAutoExpandedRoutesButtonState();
        ClearAutoCustomSelection();
        _selectedAutoPlan = null;
        RefreshPurchaseButtonState();
        AutoStatusText.Text = "Start changed. Calculate to refresh route suggestions.";
    }

    private void AutoSelectedStartModeButton_Click(object sender, RoutedEventArgs e) => SetAutoMode(bestOverall: false);

    private void AutoBestOverallModeButton_Click(object sender, RoutedEventArgs e) => SetAutoMode(bestOverall: true);

    private void SetAutoMode(bool bestOverall)
    {
        if (_bestOverallAutoMode == bestOverall) return;
        _bestOverallAutoMode = bestOverall;
        _autoCalculationCancellation?.Cancel();
        _autoExpandedRoutesWindow?.Close();
        AutoSourcePicker.IsEnabled = !bestOverall;
        AutoSourcePicker.Visibility = bestOverall ? Visibility.Collapsed : Visibility.Visible;
        AutoAllSourcesLabel.Visibility = bestOverall ? Visibility.Visible : Visibility.Collapsed;
        if (!bestOverall) SyncAutoSourceToPostPicker();

        var selectedBackground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA9, 0x82, 0x3C));
        var selectedForeground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x27, 0x1D, 0x10));
        var inactiveBackground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x62, 0x59, 0x46));
        var inactiveForeground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC4, 0xBD, 0xAF));
        AutoSelectedStartModeButton.Background = bestOverall ? inactiveBackground : selectedBackground;
        AutoSelectedStartModeButton.Foreground = bestOverall ? inactiveForeground : selectedForeground;
        AutoBestOverallModeButton.Background = bestOverall ? selectedBackground : inactiveBackground;
        AutoBestOverallModeButton.Foreground = bestOverall ? selectedForeground : inactiveForeground;

        AutoProfitResultsList.ItemsSource = null;
        AutoTimeResultsList.ItemsSource = null;
        AutoProfitPlaceholder.Visibility = Visibility.Visible;
        AutoTimePlaceholder.Visibility = Visibility.Visible;
        _autoExpandedProfitRoutes = [];
        _autoExpandedPerMinuteRoutes = [];
        SetAutoExpandedRoutesButtonState();
        ClearAutoCustomSelection();
        _selectedAutoPlan = null;
        RefreshPurchaseButtonState();
        AutoStatusText.Text = bestOverall
            ? "Calculate to rank routes across all cached destination prices."
            : "Choose a start town, then calculate.";
    }

    private async void AutoCalculate_Click(object sender, RoutedEventArgs e)
    {
        _autoExpandedRoutesWindow?.Close();
        AutoProfitResultsList.ItemsSource = null;
        AutoTimeResultsList.ItemsSource = null;
        AutoProfitPlaceholder.Visibility = Visibility.Visible;
        AutoTimePlaceholder.Visibility = Visibility.Visible;
        _autoExpandedProfitRoutes = [];
        _autoExpandedPerMinuteRoutes = [];
        SetAutoExpandedRoutesButtonState();
        ClearAutoCustomSelection();
        _selectedAutoPlan = null;
        RefreshPurchaseButtonState();
        var selectedSource = AutoSourcePicker.SelectedItem as CommercePost;
        if (!_bestOverallAutoMode && selectedSource is null)
        {
            AutoStatusText.Text = "Choose a start town first.";
            return;
        }
        if (!TryRead(DucatsInput, out var funds) || funds < 0)
        {
            AutoStatusText.Text = "Enter a valid Ducat budget.";
            return;
        }

        var existingCargo = ActiveCargo;
        if (existingCargo.Count > 0)
        {
            AutoStatusText.Text = "Clear the current load first; Auto plans a fresh load.";
            return;
        }

        // Barter goods are paid for with materials, not Ducats, so the Ducat balance does not limit them.
        if (IsBarterMode) funds = 1_000_000_000_000m;
        var sourcePosts = _bestOverallAutoMode
            ? _posts.Where(post => post.CanBuy && _products.Any(product =>
                product.PostId == post.Id && MatchesMode(product) &&
                _quotesByProduct[product.Id].Any(quote => quote.IsManualPrice && quote.DestinationPrice > 0))).ToList()
            : new List<CommercePost> { selectedSource! };
        if (sourcePosts.Count == 0)
        {
            AutoStatusText.Text = "No eligible source towns have goods in this category.";
            return;
        }

        var transports = EligibleTransports(IsGroupMode)
            .Where(transport => transport.IsOwned)
            .Select(transport => new PurchaseTransport(transport.Id, CapacitySlots(transport), CapacityWeight(transport),
                IsAvailable: true, IsFlight: IsFlight(transport)))
            .ToList();
        if (transports.Count == 0)
        {
            AutoStatusText.Text = "Mark at least one eligible transport as owned.";
            return;
        }

        var routeMinutes = new Dictionary<(int SourcePostId, int DestinationId, int TransportId), decimal>();
        // Barter sale values depend on the letter, so barter searches once per letter tier; each resulting route is then
        // assigned its own best letter (or none) after the letter's market cost, and the routes are pooled.
        var letterCandidates = AutoLetterCandidates();
        var requestSets = IsBarterMode
            ? letterCandidates.Select(letter =>
            {
                var goldPerSaleDucat = BarterGoldPerSaleDucat(letter);
                return (SearchK: goldPerSaleDucat, Requests: BuildSourceRequests(goldPerSaleDucat));
            }).ToList()
            : [(SearchK: 1m, Requests: BuildSourceRequests(1m))];
        var sourceRequests = requestSets[0].Requests;

        List<(CommercePost Source, PurchaseRequest Request)> BuildSourceRequests(decimal barterGoldPerSaleDucat)
        {
        var requests = new List<(CommercePost Source, PurchaseRequest Request)>();
        var totalSearchNodeBudget = _bestOverallAutoMode ? 4_000_000 : 500_000;
        var sourceSearchNodeBudget = Math.Max(20_000, totalSearchNodeBudget / sourcePosts.Count);
        foreach (var source in sourcePosts)
        {
            var sourcePostIds = SourcePostIds(source.Id);
            var goods = new List<PurchaseGood>();
            foreach (var product in _products.Where(product => sourcePostIds.Contains(product.PostId) && MatchesMode(product)))
            {
                var buyPrice = GetCurrentBuyPrice(product);
                var stock = product.StockInitialized
                    ? Math.Max(0, product.CurrentStock)
                    : product.IsWeeklyLimited
                        ? Math.Min(product.MaxStock, product.SourceCount)
                        : product.MaxStock;
                var sellPrices = new Dictionary<int, decimal>();
                var estimatedDestinations = new HashSet<int>();
                foreach (var quote in _quotesByProduct[product.Id])
                {
                    if (_bestOverallAutoMode && !quote.IsManualPrice) continue;
                    if (quote.PostId == SmugglerDestinationId && (!quote.IsManualPrice || quote.DestinationPrice <= 0))
                        continue;
                    var salePrice = EffectiveSalePrice(product, quote.DestinationPrice > 0
                        ? quote.DestinationPrice
                        : quote.PostId == SmugglerDestinationId ? 0m : DefaultSalePrice(product, buyPrice));
                    if (salePrice <= 0) continue;
                    sellPrices[quote.PostId] = AutoSearchSalePrice(product, salePrice, barterGoldPerSaleDucat);
                    if (!quote.IsManualPrice) estimatedDestinations.Add(quote.PostId);
                }

                goods.Add(new PurchaseGood(product.Id, source.Id, stock, buyPrice, product.Weight,
                    product.MaxBundle, sellPrices, product.CommerceParty,
                    IsRatingLocked(product, GetMerchantRatingForPost(product.PostId)),
                    product.IsWeeklyLimited ? Math.Min(stock, product.SourceCount) : null,
                    product.CurrentBuyPriceIsEstimated, estimatedDestinations));
            }

            var destinations = _posts.Where(post => post.CanSell && !sourcePostIds.Contains(post.Id))
                .Select(post => new PurchaseDestination(post.Id)).ToList();
            if (goods.Count == 0 || destinations.Count == 0) continue;

            var sourceTransportMinutes = new Dictionary<(int DestinationId, int TransportId), decimal>();
            foreach (var destination in destinations)
            foreach (var transport in transports)
            {
                var minutes = GetRouteMinutes(source.Id, destination.Id, transport.Id, out _);
                if (minutes is not > 0) continue;
                sourceTransportMinutes[(destination.Id, transport.Id)] = minutes.Value;
                routeMinutes[(source.Id, destination.Id, transport.Id)] = minutes.Value;
            }

            requests.Add((source, new PurchaseRequest(source.Id, funds, goods, transports, destinations,
                Objective: PurchaseObjective.NetProfit, GroupMode: IsGroupMode,
                MaxSearchNodesPerPair: 20_000, MaxTotalSearchNodes: sourceSearchNodeBudget,
                TransportMinutes: sourceTransportMinutes)));
        }
        return requests;
        }

        if (sourceRequests.Count == 0)
        {
            AutoStatusText.Text = _bestOverallAutoMode
                ? "No cached or manually entered destination prices are available for these goods."
                : "No goods or destinations are available from this start.";
            return;
        }

        _autoCalculationCancellation?.Cancel();
        _autoCalculationCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _autoCalculationCancellation = cancellation;
        AutoCalculateButton.IsEnabled = false;
        var scopeName = _bestOverallAutoMode ? "all eligible source towns" : selectedSource!.Name;
        AutoStatusText.Text = _bestOverallAutoMode
            ? "Calculating routes across cached destination prices..."
            : $"Calculating from {scopeName}... Plans assume a fresh empty load.";

        try
        {
            var searchSets = await Task.Run(() => requestSets.Select(set => (set.SearchK, Result: RunSearches(set.Requests))).ToList(), cancellation.Token);

            (PurchaseSearchResult Raw, PurchaseSearchResult PerMinute) RunSearches(List<(CommercePost Source, PurchaseRequest Request)> sourceRequests)
            {
                var rawPlans = new List<PurchasePlan>();
                var perMinutePlans = new List<PurchasePlan>();
                var rawIsExhaustive = true;
                var perMinuteIsExhaustive = true;
                long rawVisitedNodes = 0;
                long perMinuteVisitedNodes = 0;
                foreach (var sourceRequest in sourceRequests)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var raw = PurchaseOptimizer.Search(sourceRequest.Request, cancellation.Token);
                    var perMinute = PurchaseOptimizer.Search(sourceRequest.Request with
                    {
                        Objective = PurchaseObjective.ProfitPerMinute
                    }, cancellation.Token);
                    var sourceId = sourceRequest.Request.SourcePostId;
                    var rawSourcePlans = raw.Plans.Where(plan => IsUsefulSourcePlan(sourceId, plan)).ToList();
                    var perMinuteSourcePlans = perMinute.Plans.Where(plan => IsUsefulSourcePlan(sourceId, plan)).ToList();
                    foreach (var plan in rawSourcePlans.Concat(perMinuteSourcePlans))
                        _planSourceIds.AddOrUpdate(plan, new StrongBox<int>(sourceId));
                    rawPlans.AddRange(rawSourcePlans);
                    perMinutePlans.AddRange(perMinuteSourcePlans);
                    rawIsExhaustive &= raw.IsExhaustive;
                    perMinuteIsExhaustive &= perMinute.IsExhaustive;
                    rawVisitedNodes += raw.VisitedNodes;
                    perMinuteVisitedNodes += perMinute.VisitedNodes;
                }
                return (
                    Raw: new PurchaseSearchResult(rawPlans, rawIsExhaustive, rawVisitedNodes),
                    PerMinute: new PurchaseSearchResult(perMinutePlans, perMinuteIsExhaustive, perMinuteVisitedNodes));
            }

            if (cancellation.IsCancellationRequested) return;
            var searches = AssignAutoLetters(searchSets, letterCandidates);
            _updatingAutoPlanSelection = true;
            AutoProfitResultsList.SelectedItem = null;
            AutoTimeResultsList.SelectedItem = null;
            _selectedAutoPlan = null;
            RefreshPurchaseButtonState();
            AutoProfitResultsList.ItemsSource = BuildAutoPlanRows(searches.Raw, perMinute: false, routeMinutes);
            AutoTimeResultsList.ItemsSource = BuildAutoPlanRows(searches.PerMinute, perMinute: true, routeMinutes);
            _autoExpandedProfitRoutes = BuildExpandedAutoRouteRows(searches.Raw, perMinute: false, routeMinutes);
            _autoExpandedPerMinuteRoutes = BuildExpandedAutoRouteRows(searches.PerMinute, perMinute: true, routeMinutes);
            AutoProfitPlaceholder.Visibility = AutoProfitResultsList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AutoTimePlaceholder.Visibility = AutoTimeResultsList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetAutoExpandedRoutesButtonState();
            _updatingAutoPlanSelection = false;
            if (_autoExpandedRoutesWindow is { IsVisible: true })
                UpdateExpandedAutoRoutesWindow(scopeName);
            if (AutoProfitResultsList.Items.Count == 0 && AutoTimeResultsList.Items.Count == 0)
                AutoStatusText.Text = searchSets.Any(set => set.Result.Raw.Plans.Count > 0)
                    ? "No route makes a profit, with or without a letter after its market value (check stock)."
                    : _bestOverallAutoMode
                    ? "No profitable routes from cached destination prices, stock, budget, and owned transports."
                    : "No profitable routes with these prices, stock, budget, and owned transports.";
            else
            {
                var incomplete = !searches.Raw.IsExhaustive || !searches.PerMinute.IsExhaustive;
                var resultScope = _bestOverallAutoMode ? "All sources" : scopeName;
                AutoStatusText.Text = incomplete
                    ? $"{resultScope} · best found within search budget · buy prices may be estimated"
                    : $"{resultScope} · ranked route suggestions · buy prices may be estimated";
                AutoStatusText.Text += letterCandidates.Count > 1
                    ? " · letter picked per route (hover a route; selecting it sets the letter)"
                    : " · set letter Market Gold values in Inventory to compare letters";
            }
        }
        catch (OperationCanceledException)
        {
            _updatingAutoPlanSelection = false;
            AutoStatusText.Text = "Calculation cancelled.";
        }
        catch (Exception exception)
        {
            _updatingAutoPlanSelection = false;
            AutoStatusText.Text = $"Auto calculation failed: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_autoCalculationCancellation, cancellation))
            {
                _autoCalculationCancellation = null;
                AutoCalculateButton.IsEnabled = true;
                cancellation.Dispose();
            }
        }
    }

    private int GetPlanSourcePostId(PurchasePlan plan)
    {
        if (_planSourceIds.TryGetValue(plan, out var sourceId)) return sourceId.Value;
        return plan.Lines.Count == 0
            ? 0
            : _products.FirstOrDefault(product => product.Id == plan.Lines[0].GoodId)?.PostId ?? 0;
    }

    // A two-stop barter route is only worth listing when it actually buys at both stops.
    private bool IsUsefulSourcePlan(int sourceId, PurchasePlan plan)
    {
        if (!IsBarterPairRoute(sourceId)) return true;
        var productPosts = plan.Lines
            .Select(line => _products.FirstOrDefault(product => product.Id == line.GoodId)?.PostId ?? 0)
            .Distinct()
            .Count();
        return productPosts > 1;
    }

    private decimal GetCurrentBuyPrice(GoodsEntry product) =>
        product.IsBarter
            ? product.CurrentBuyPrice
            : decimal.TryParse(product.CurrentBuyPriceText, NumberStyles.Number, CultureInfo.CurrentCulture, out var entered) && entered > 0
            ? entered
            : product.CurrentBuyPrice > 0
                ? product.CurrentBuyPrice
                : ((decimal)product.MinPrice + product.MaxPrice) / 2m;

    // Auto values a barter sale in Gold: each sale Ducat pays raw Gold plus Ducats × Gold per Ducat (with Mastery and
    // letter bonuses), so sale Ducats × that multiplier minus material Gold is the load's Gold profit.
    private decimal AutoSearchSalePrice(GoodsEntry product, decimal salePrice, decimal barterGoldPerSaleDucat) =>
        product.IsBarter ? salePrice * barterGoldPerSaleDucat : salePrice;

    private decimal BarterGoldPerSaleDucat(GuaranteeLetterKind? letter = null)
    {
        var modifiers = RewardModifiersFor(letter ?? SelectedGuaranteeLetter, assumeHeld: true);
        decimal GoldFor(decimal sale)
        {
            var reward = CommerceRewardModel.Calculate([new RewardLine(1, 0m, sale, 0m)], true, modifiers, perLoadLetterEffects: false);
            return reward.RawGold + reward.DucatGold;
        }
        var perDucat = (GoldFor(2_000_000m) - GoldFor(1_000_000m)) / 1_000_000m;
        return perDucat > 0 ? perDucat : 1m + GoldPerDucat;
    }

    private bool AutoProfitIsGold(PurchasePlan plan) =>
        _plannerPreferences.ShowTradeProfitInGold ||
        plan.Lines.Any(line => _products.FirstOrDefault(product => product.Id == line.GoodId)?.IsBarter == true);

    // The value the Auto lists show and rank by: the reward model's Gold total for barter (and for trade when
    // Settings > Trade profit in Gold is on), otherwise the full Ducats earned after Mastery, letter and extra bonuses.
    private decimal AutoProfitValue(PurchasePlan plan)
    {
        var reward = CalculatePlanReward(plan);
        return AutoProfitIsGold(plan) ? reward.TotalGold : reward.DucatGain;
    }

    public decimal GoldPerDucat => _plannerPreferences.GoldPerDucat > 0 ? _plannerPreferences.GoldPerDucat : 1m;

    // Each trade type (normal, group, barter) keeps its own active letter.
    public GuaranteeLetterKind SelectedGuaranteeLetter =>
        _plannerPreferences.ActiveGuaranteeLetterByMode.TryGetValue(_goodsMode.ToString(), out var name)
        && Enum.TryParse<GuaranteeLetterKind>(name, out var kind) ? kind : GuaranteeLetterKind.None;

    private bool _syncingLetterPickers;

    private void RefreshActiveLetterPickers()
    {
        var barter = IsBarterMode;
        string Label(GuaranteeLetterInfo letter) =>
            $"{letter.Name.Replace(" Letter of Guarantee", string.Empty)} · +{(barter ? letter.BarterPercent : letter.NormalPercent)}%";
        var options = new[] { new LetterOption(GuaranteeLetterKind.None, "No letter") }
            .Concat(GuaranteeLetters.All.Select(letter => new LetterOption(letter.Kind, Label(letter))))
            .ToList();
        var loadProfitOptions = new[] { new LetterOption(GuaranteeLetterKind.None, "No letter") }
            .Concat(GuaranteeLetters.All.Select(letter => new LetterOption(letter.Kind, Label(letter) + LoadProfitLetterGainText(letter.Kind))))
            .ToList();
        var tooltip = ActiveLetterTooltip(SelectedGuaranteeLetter);
        if (_selectedSaleDestinationId is int destinationId && _loadProfitRewardLines.ContainsKey(destinationId))
            tooltip += $"\n\nLoad Profit lists each letter's Gold gain or loss for the current load sold at {_posts.FirstOrDefault(post => post.Id == destinationId)?.Name ?? "the selected town"}, after its Market Gold value and Ducats on use.";
        _syncingLetterPickers = true;
        try
        {
            foreach (var picker in new[] { ManualLetterPicker, LoadProfitLetterPicker })
            {
                var pickerOptions = picker == LoadProfitLetterPicker ? loadProfitOptions : options;
                picker.ItemsSource = pickerOptions;
                picker.SelectedItem = pickerOptions.FirstOrDefault(option => option.Kind == SelectedGuaranteeLetter) ?? pickerOptions[0];
                picker.ToolTip = tooltip;
            }
        }
        finally
        {
            _syncingLetterPickers = false;
        }
        var gain = LoadProfitLetterGain(SelectedGuaranteeLetter);
        LoadProfitLetterWarning.Text = SelectedGuaranteeLetter != GuaranteeLetterKind.None && gain < 0
            ? $"This letter loses {Math.Abs(gain.Value):N0} G on this load versus no letter. It is still applied."
            : string.Empty;
        LoadProfitLetterWarning.Visibility = LoadProfitLetterWarning.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Reward lines for the current load at each destination with a quote for every loaded good.
    private readonly Dictionary<int, List<RewardLine>> _loadProfitRewardLines = [];

    // Gold gain or loss from using this letter on the current load at the selected sale destination, versus no letter.
    private decimal? LoadProfitLetterGain(GuaranteeLetterKind kind)
    {
        if (_selectedSaleDestinationId is not int destinationId ||
            !_loadProfitRewardLines.TryGetValue(destinationId, out var lines)) return null;
        var withLetter = CommerceRewardModel.Calculate(lines, IsBarterMode, RewardModifiersFor(kind, assumeHeld: true)).TotalGold;
        var withoutLetter = CommerceRewardModel.Calculate(lines, IsBarterMode, RewardModifiersFor(GuaranteeLetterKind.None)).TotalGold;
        return withLetter - withoutLetter;
    }

    private string LoadProfitLetterGainText(GuaranteeLetterKind kind) => LoadProfitLetterGain(kind) is not { } gain
        ? string.Empty
        : $" · {(gain >= 0 ? "+" : "−")}{Math.Abs(gain):N0} G" + (GetGuaranteeLetterSupply(kind) == 0 ? " (buy)" : string.Empty);

    private string ActiveLetterTooltip(GuaranteeLetterKind kind)
    {
        var mode = _goodsMode switch { GoodsMode.Group => "group", GoodsMode.Barter => "barter", _ => "normal" };
        var text = $"Active Letter of Guarantee for {mode} trade. Normal, group and barter each remember their own choice; it applies to Manual, Auto and Load Profit estimates.";
        if (GuaranteeLetters.Get(kind) is not { } letter) return text;
        text += $"\n{letter.Name}: +{(IsBarterMode ? letter.BarterPercent : letter.NormalPercent)}% of profit";
        text += letter.BoostsGold ? " as extra Ducats and Gold" : " as extra Ducats";
        text += $", plus {letter.DucatsGranted:N0} Ducats on use.";
        var market = GetGuaranteeLetterMarketValue(kind);
        text += GuaranteeLetters.IsAvailable(market)
            ? $" Its Market Gold value ({market:N0}) is subtracted once per load."
            : " No Market Gold value is set in Inventory, so the letter is treated as free.";
        if (GetGuaranteeLetterSupply(kind) == 0)
            text += " Inventory shows 0 held; estimates still apply it, so buy one before selling.";
        return text;
    }

    private void ActiveLetterPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingLetterPickers || sender is not ComboBox { SelectedItem: LetterOption option }) return;
        SetActiveGuaranteeLetter(option.Kind);
        RefreshRewardDisplays();
    }

    private void SetActiveGuaranteeLetter(GuaranteeLetterKind kind)
    {
        var key = _goodsMode.ToString();
        if (kind == GuaranteeLetterKind.None) _plannerPreferences.ActiveGuaranteeLetterByMode.Remove(key);
        else _plannerPreferences.ActiveGuaranteeLetterByMode[key] = kind.ToString();
        SavePlannerPreferences();
        RefreshActiveLetterPickers();
    }

    // Auto considers no letter plus every letter tier with a Market Gold value, held or not (an unheld winner is
    // flagged to buy); a blank market value keeps a letter out of automated selection.
    private List<GuaranteeLetterKind> AutoLetterCandidates() =>
        [GuaranteeLetterKind.None, .. GuaranteeLetters.All
            .Where(letter => GuaranteeLetters.IsAvailable(GetGuaranteeLetterMarketValue(letter.Kind)))
            .Select(letter => letter.Kind)];

    // Each route gets its own most efficient letter: the candidate (no letter first, so ties keep no letter) giving that
    // load the highest Gold total after the letter's market cost and Ducats on use. A load's goods are valid with any
    // letter, so every route is scored against every candidate. Routes from all per-letter searches are pooled, duplicate
    // loads are merged, and routes that lose Gold even with their best letter are dropped.
    private (PurchaseSearchResult Raw, PurchaseSearchResult PerMinute) AssignAutoLetters(
        List<(decimal SearchK, (PurchaseSearchResult Raw, PurchaseSearchResult PerMinute) Result)> searchSets,
        List<GuaranteeLetterKind> letterCandidates)
    {
        var assigned = new Dictionary<string, PurchasePlan?>();
        PurchasePlan? Assign(PurchasePlan plan, decimal searchK)
        {
            var key = $"{GetPlanSourcePostId(plan)}|{plan.DestinationId}|{plan.TransportId}|" +
                string.Join(",", plan.Lines.OrderBy(line => line.GoodId).Select(line => $"{line.GoodId}x{line.Quantity}"));
            if (assigned.TryGetValue(key, out var existing)) return existing;
            var bestLetter = letterCandidates[0];
            var bestGold = CalculatePlanReward(plan, bestLetter).TotalGold;
            foreach (var letter in letterCandidates.Skip(1))
            {
                var gold = CalculatePlanReward(plan, letter).TotalGold;
                if (gold <= bestGold) continue;
                bestGold = gold;
                bestLetter = letter;
            }
            _planLetters.AddOrUpdate(plan, new StrongBox<GuaranteeLetterKind>(bestLetter));
            _planSearchK.AddOrUpdate(plan, new StrongBox<decimal>(searchK));
            var keep = !AutoProfitIsGold(plan) || bestGold > 0;
            assigned[key] = keep ? plan : null;
            return keep ? plan : null;
        }

        var raw = new List<PurchasePlan>();
        var perMinute = new List<PurchasePlan>();
        var rawExhaustive = true;
        var perMinuteExhaustive = true;
        long rawNodes = 0, perMinuteNodes = 0;
        foreach (var set in searchSets)
        {
            foreach (var plan in set.Result.Raw.Plans)
                if (Assign(plan, set.SearchK) is { } kept && !raw.Contains(kept)) raw.Add(kept);
            foreach (var plan in set.Result.PerMinute.Plans)
                if (Assign(plan, set.SearchK) is { } kept && !perMinute.Contains(kept)) perMinute.Add(kept);
            rawExhaustive &= set.Result.Raw.IsExhaustive;
            perMinuteExhaustive &= set.Result.PerMinute.IsExhaustive;
            rawNodes += set.Result.Raw.VisitedNodes;
            perMinuteNodes += set.Result.PerMinute.VisitedNodes;
        }
        return (new PurchaseSearchResult(raw, rawExhaustive, rawNodes),
            new PurchaseSearchResult(perMinute, perMinuteExhaustive, perMinuteNodes));
    }

    private GuaranteeLetterKind? PlanLetter(PurchasePlan plan) =>
        _planLetters.TryGetValue(plan, out var letter) ? letter.Value : null;

    private static string ShortLetterName(GuaranteeLetterKind kind) =>
        GuaranteeLetters.Get(kind)?.Name.Replace(" Letter of Guarantee", string.Empty) ?? "No letter";

    public int? GetGuaranteeLetterSupply(GuaranteeLetterKind kind) =>
        _plannerPreferences.GuaranteeLetterSupply.TryGetValue(kind.ToString(), out var count) ? count : null;

    public int? GetGuaranteeLetterMarketValue(GuaranteeLetterKind kind) =>
        _plannerPreferences.GuaranteeLetterMarketValue.TryGetValue(kind.ToString(), out var value) ? value : null;

    public static readonly IReadOnlyList<string> CommerceMasteryRanks = ["F", "E", "D", "C", "B", "A", "9", "8", "7", "6", "5", "4", "3", "2", "1"];

    public string CommerceMasteryRank => _plannerPreferences.CommerceMasteryRank;

    public void SetCommerceMasteryRank(string rank)
    {
        if (_plannerPreferences.CommerceMasteryRank == rank) return;
        _plannerPreferences.CommerceMasteryRank = rank;
        SavePlannerPreferences();
        RefreshRewardDisplays();
    }

    // Outposts that sell trade or group goods; barter outposts have no Merchant Rating.
    public IReadOnlyList<CommercePost> MerchantRatingPosts() =>
        _posts.Where(post => post.CanBuy && _products.Any(product => product.PostId == post.Id && ModeOf(product) != GoodsMode.Barter)).ToList();

    public int GetMerchantRating(int postId) => GetMerchantRatingForPost(postId);

    public void SetMerchantRating(int postId, int rating)
    {
        rating = Math.Max(1, rating);
        if (PostPicker.SelectedItem is CommercePost selected && selected.Id == postId)
        {
            // RatingInput_TextChanged saves the value and refreshes the products.
            RatingInput.Text = rating.ToString(CultureInfo.CurrentCulture);
            return;
        }
        if (GetMerchantRatingForPost(postId) == rating && _plannerPreferences.MerchantRatingsByPostId.ContainsKey(postId)) return;
        _plannerPreferences.MerchantRatingsByPostId[postId] = rating;
        SavePlannerPreferences();
        RefreshProducts();
    }

    // The chosen letter always applies, even with none held or at a loss: the user decides, and the UI warns.
    public RewardModifiers CurrentRewardModifiers() => RewardModifiersFor(SelectedGuaranteeLetter, assumeHeld: true);

    // Auto may pick a letter the user does not hold yet; its plans are then shown as if one were bought.


    // assumeHeld prices a letter as if one were in Inventory, so letters marked 0 held can still be compared.
    public RewardModifiers RewardModifiersFor(GuaranteeLetterKind letter, bool assumeHeld = false)
    {
        var totals = ModifierTotals(IsGroupMode);
        return new RewardModifiers(_plannerPreferences.CommerceMasteryRank, letter, GoldPerDucat,
            assumeHeld ? -1 : GetGuaranteeLetterSupply(letter) ?? -1, GetGuaranteeLetterMarketValue(letter) ?? 0,
            totals.DucatPercent, totals.ProfitPercent, totals.MerchantRatingPercent);
    }

    public string RewardTooltip(CommerceRewardBreakdown reward) => _plannerPreferences.DetailedRewardTooltips
        ? CommerceRewardModel.DetailedTooltip(reward)
        : CommerceRewardModel.BasicTooltip(reward);

    // The Merchant Rating discount comes from the rating level entered for the buying outpost.
    public decimal MerchantRatingDiscountPercentFor(int postId) =>
        MerchantRatingTable.DiscountPercent(GetMerchantRatingForPost(postId), _merchantRatingLevels);

    public decimal PurchaseDiscountPercentFor(int postId) =>
        MerchantRatingDiscountPercentFor(postId) + EquipmentPurchaseDiscountPercent;

    public decimal EquipmentPurchaseDiscountPercent => ModifierTotals(false).PurchaseDiscountPercent;

    public CommerceModifierTotals ModifierTotals(bool groupGoods)
    {
        var totals = CommerceModifiers.Totals(_plannerPreferences.CommerceModifierCounts, groupGoods);
        var enchants = AccessoryEnchants.Totals(_plannerPreferences.AccessoryEnchantIds, _plannerPreferences.AccessoryEnchantRolls);
        return totals with
        {
            PurchaseDiscountPercent = totals.PurchaseDiscountPercent + enchants.PurchaseDiscountPercent,
            MerchantRatingPercent = totals.MerchantRatingPercent + enchants.MerchantRatingPercent
        };
    }

    public string? GetAccessoryEnchant(string slotKey) =>
        _plannerPreferences.AccessoryEnchantIds.TryGetValue(slotKey, out var id) ? id : null;

    public int? GetAccessoryEnchantRoll(string slotKey) =>
        _plannerPreferences.AccessoryEnchantRolls.TryGetValue(slotKey, out var roll) ? roll : null;

    public void SetAccessoryEnchant(string slotKey, string? enchantId, int? roll)
    {
        if (string.IsNullOrEmpty(enchantId)) _plannerPreferences.AccessoryEnchantIds.Remove(slotKey);
        else _plannerPreferences.AccessoryEnchantIds[slotKey] = enchantId;
        if (roll is > 0) _plannerPreferences.AccessoryEnchantRolls[slotKey] = roll.Value;
        else _plannerPreferences.AccessoryEnchantRolls.Remove(slotKey);
        SavePlannerPreferences();
    }

    public int GetModifierCount(string id) =>
        _plannerPreferences.CommerceModifierCounts.TryGetValue(id, out var count) ? count : 0;

    public void SetModifierCount(string id, int count)
    {
        if (GetModifierCount(id) == count) return;
        if (count > 0) _plannerPreferences.CommerceModifierCounts[id] = count;
        else _plannerPreferences.CommerceModifierCounts.Remove(id);
        SavePlannerPreferences();
        RefreshTransportLoad();
        RefreshRewardDisplays();
    }

    // Partner and talent capacity bonuses are added to every transport.
    private int CapacitySlots(CommerceTransport transport) => transport.Slots + ModifierTotals(false).ExtraSlots;

    private int CapacityWeight(CommerceTransport transport) => transport.Weight + ModifierTotals(false).ExtraWeight;

    public void SetGuaranteeLetterInventory(GuaranteeLetterKind kind, int? supply, int? marketValue)
    {
        var key = kind.ToString();
        if (supply is >= 0) _plannerPreferences.GuaranteeLetterSupply[key] = supply.Value;
        else _plannerPreferences.GuaranteeLetterSupply.Remove(key);
        if (marketValue is >= 0) _plannerPreferences.GuaranteeLetterMarketValue[key] = marketValue.Value;
        else _plannerPreferences.GuaranteeLetterMarketValue.Remove(key);
        SavePlannerPreferences();
        RefreshActiveLetterPickers();
        RefreshRewardDisplays();
    }

    private void RefreshRewardDisplays()
    {
        RefreshDestinationTotals();
        RefreshAutoRewardText();
    }

    // Auto plans hold barter sales as sale Ducats × the Gold multiplier, so barter rewards read the destination's sale Ducats.
    // Without an explicit letter, a route uses the letter Auto assigned to it (assumed bought if none are held).
    private CommerceRewardBreakdown CalculatePlanReward(PurchasePlan plan, GuaranteeLetterKind? letter = null)
    {
        letter ??= PlanLetter(plan);
        return CommerceRewardModel.Calculate(
        plan.Lines.Select(line =>
        {
            var product = _products.FirstOrDefault(item => item.Id == line.GoodId);
            var unitSale = product?.IsBarter == true
                ? AutoQuoteSalePrice(product, plan.DestinationId) ?? 0m
                : line.Quantity > 0 ? line.Sale / line.Quantity : 0m;
            return new RewardLine(line.Quantity,
                line.Quantity > 0 ? line.Cost / line.Quantity : 0m,
                unitSale,
                product?.Weight ?? 0,
                product is { IsBarter: false } ? PurchaseDiscountPercentFor(product.PostId) : 0m);
        }),
        isBarter: plan.Lines.Any(line => _products.FirstOrDefault(product => product.Id == line.GoodId)?.IsBarter == true),
        RewardModifiersFor(letter ?? SelectedGuaranteeLetter, assumeHeld: true));
    }

    // The per-unit sale price Auto, Load Profit and Sell share: the entered quote, else the default estimate.
    private decimal? AutoQuoteSalePrice(GoodsEntry product, int destinationId)
    {
        if (!_quotesByProduct.TryGetValue(product.Id, out var quotes)) return null;
        var quote = quotes.FirstOrDefault(item => item.PostId == destinationId);
        if (quote is null) return null;
        if (quote.PostId == SmugglerDestinationId && quote.DestinationPrice <= 0) return null;
        var sale = EffectiveSalePrice(product, quote.DestinationPrice > 0
            ? quote.DestinationPrice
            : DefaultSalePrice(product, GetCurrentBuyPrice(product)));
        return sale > 0 ? sale : null;
    }

    public IReadOnlyList<BarterMaterialCatalogEntry> BarterMaterialCatalog => _barterMaterialCatalog;

    public IEnumerable<GoodsEntry> BarterGoods => _products.Where(product => product.IsBarter && !product.IsInactiveRotation);

    public IEnumerable<GoodsEntry> AllBarterGoods => _products.Where(product => product.IsBarter);

    public IReadOnlyList<CommercePost> Posts => _posts;

    public bool IsUntradableBarterMaterial(int itemId) =>
        _barterMaterialCatalog.FirstOrDefault(material => material.Id == itemId)?.IsUntradable == true;

    // Untradable materials have a known value of 0, so they never mark a result as incomplete.
    public bool HasSavedBarterMaterialValue(int itemId) =>
        IsUntradableBarterMaterial(itemId) ||
        _plannerPreferences.BarterMaterialValuesById.TryGetValue(itemId, out var value) && value > 0;

    // Only user-saved market values count; a blank material adds nothing and marks the result as incomplete.
    public decimal GetBarterMaterialValue(int itemId) =>
        !IsUntradableBarterMaterial(itemId) &&
        _plannerPreferences.BarterMaterialValuesById.TryGetValue(itemId, out var value) && value > 0 ? value : 0m;

    public int GetBarterMaterialOnHand(int itemId) =>
        _plannerPreferences.BarterMaterialsOnHandById.TryGetValue(itemId, out var have) ? have : 0;

    public const decimal StarterBarterMaterialValue = 10_000m;

    // One-time starter values saved as if the user had entered them; afterwards they are ordinary saved entries.
    private void SeedBarterMaterialDefaults()
    {
        if (_plannerPreferences.BarterMaterialStarterValuesApplied) return;
        var materialIds = AllBarterGoods.SelectMany(good => good.Recipe).Select(part => part.ItemId).Distinct().ToList();
        if (materialIds.Count == 0) return;
        foreach (var itemId in materialIds)
        {
            if (!IsUntradableBarterMaterial(itemId) && !HasSavedBarterMaterialValue(itemId))
                _plannerPreferences.BarterMaterialValuesById[itemId] = StarterBarterMaterialValue;
        }
        _plannerPreferences.BarterMaterialStarterValuesApplied = true;
        SavePlannerPreferences();
    }

    public decimal? GetSavedBarterMaterialValue(int itemId) =>
        !IsUntradableBarterMaterial(itemId) && _plannerPreferences.BarterMaterialValuesById.TryGetValue(itemId, out var value) && value > 0 ? value : null;

    public (string Summary, IReadOnlyList<BarterShoppingLine> Lines) GetBarterShoppingList()
    {
        var cargo = CargoFor(GoodsMode.Barter).Values.Where(line => line.Quantity > 0).OrderBy(line => line.Product.Name).ToList();
        var summary = string.Join(", ", cargo.Select(line => $"{line.Quantity:N0} × {line.Product.Name}"));
        var needs = cargo.SelectMany(line => line.Product.Recipe.Select(part =>
            new BarterRecipeNeed(line.Quantity, part.ItemId, part.Name, part.Quantity)));
        return (summary, BarterShoppingList.Build(needs, itemId =>
            _plannerPreferences.BarterMaterialsOnHandById.TryGetValue(itemId, out var have) ? have : 0));
    }

    public void SetBarterMaterialOnHand(int itemId, int have)
    {
        if (have > 0) _plannerPreferences.BarterMaterialsOnHandById[itemId] = have;
        else _plannerPreferences.BarterMaterialsOnHandById.Remove(itemId);
        SavePlannerPreferences();
    }

    // Barter goods are bought with materials. CurrentBuyPrice holds the materials' Gold value directly (never divided
    // into Ducats); the reward model subtracts it from raw Gold + Ducats × Gold per Ducat.
    private void RecalculateBarterCosts()
    {
        foreach (var product in _products.Where(product => product.IsBarter))
        {
            var goldCost = product.Recipe.Sum(part => part.Quantity * GetBarterMaterialValue(part.ItemId));
            product.CurrentBuyPrice = goldCost;
            product.CurrentBuyPriceText = FormatPrice(goldCost);
            product.CurrentBuyPriceIsEstimated = product.Recipe.Any(part => !HasSavedBarterMaterialValue(part.ItemId));
            product.RecipeTooltip = BuildRecipeTooltip(product, goldCost);
        }
    }

    private string BuildRecipeTooltip(GoodsEntry product, decimal goldCost)
    {
        var text = new System.Text.StringBuilder($"{product.Name} — exchange materials (each){Environment.NewLine}");
        foreach (var part in product.Recipe)
        {
            var value = GetBarterMaterialValue(part.ItemId);
            var valueText = value > 0
                ? $"{FormatPrice(part.Quantity * value)} Gold{(HasSavedBarterMaterialValue(part.ItemId) ? string.Empty : " (reference)")}"
                : "no Gold value";
            text.AppendLine($"{part.Quantity:N0} × {part.Name}   {valueText}");
        }
        text.Append($"Material cost: {FormatPrice(goldCost)} Gold");
        if (product.CurrentBuyPriceIsEstimated)
            text.Append($"{Environment.NewLine}(reference) = client reference value; set market values in the barter materials panel.");
        return text.ToString();
    }

    public void ApplyBarterMaterialValues(IReadOnlyDictionary<int, decimal?> values, decimal goldPerDucat)
    {
        foreach (var (itemId, value) in values)
        {
            if (value is > 0) _plannerPreferences.BarterMaterialValuesById[itemId] = value.Value;
            else _plannerPreferences.BarterMaterialValuesById.Remove(itemId);
        }
        _plannerPreferences.GoldPerDucat = goldPerDucat > 0 ? goldPerDucat : 1m;
        SavePlannerPreferences();
        RecalculateBarterCosts();
        foreach (var line in _barterCargoByProduct.Values)
            line.UnitBuyPrice = line.Product.CurrentBuyPrice;
        if (SelectedProduct is { IsBarter: true } selected)
        {
            BuyPriceInput.Text = selected.CurrentBuyPriceText;
            RefreshQuotes();
        }
        RefreshDestinationTotals();
    }

    public decimal? BestBarterSalePrice(GoodsEntry product, out string destinationName)
    {
        destinationName = string.Empty;
        DestinationQuote? best = null;
        foreach (var quote in _quotesByProduct[product.Id])
            if (quote.DestinationPrice > 0 && (best is null || quote.DestinationPrice > best.DestinationPrice))
                best = quote;
        if (best is null)
        {
            destinationName = "client reference midpoint";
            return ((decimal)product.MinPrice + product.MaxPrice) / 2m;
        }
        destinationName = best.PostName + (best.IsManualPrice ? string.Empty : " (estimate)");
        return best.DestinationPrice;
    }

    private List<AutoPlanRow> BuildAutoPlanRows(PurchaseSearchResult result, bool perMinute,
        IReadOnlyDictionary<(int SourcePostId, int DestinationId, int TransportId), decimal> routeMinutes)
    {
        var ranked = RankAutoPlans(result, perMinute, routeMinutes);
        return ranked
            .GroupBy(item => (SourcePostId: GetPlanSourcePostId(item.Plan), item.Plan.DestinationId))
            .Select(group => group.First())
            .Take(3)
            .Select(item =>
            {
                var plan = item.Plan;
                var sourcePostId = GetPlanSourcePostId(plan);
                var source = SourceName(sourcePostId);
                var destination = _posts.FirstOrDefault(post => post.Id == plan.DestinationId)?.Name ?? "Unknown town";
                var routeName = _bestOverallAutoMode
                    ? $"{ShortenAutoSourceName(sourcePostId)}>{ShortenAutoTownName(destination)}"
                    : destination;
                var transport = ShortenAutoTransportName(
                    _transports.FirstOrDefault(entry => entry.Id == plan.TransportId)?.Name ?? $"Transport {plan.TransportId}");
                var hasMinutes = item.Minutes is > 0;
                var minutes = item.Minutes ?? 0m;
                var estimateTag = plan.HasEstimatedPrices ? "EST" : string.Empty;
                var travelText = hasMinutes ? $"{minutes:N1}m" : "--";
                var profitPerMinute = hasMinutes ? (item.Value / minutes).ToString("N0", CultureInfo.CurrentCulture) : "--";
                var profitLabel = AutoProfitIsGold(plan) ? "Gold profit (reward total below)" : "Ducats earned (all bonuses)";
                var rowTooltip = $"{source} → {destination} · {transport}{Environment.NewLine}{profitLabel} {item.Value:N0} · travel time {(hasMinutes ? $"{minutes:N1} minutes" : "unavailable")} · profit per minute {profitPerMinute}{(estimateTag.Length > 0 ? " · estimated prices" : string.Empty)}" +
                    AutoLetterTooltipLine(plan);
                var autoRow = new AutoPlanRow(routeName,
                    item.Value.ToString("N0", CultureInfo.CurrentCulture),
                    transport,
                    travelText,
                    profitPerMinute,
                    rowTooltip,
                        plan, sourcePostId, _goodsMode, perMinute, AutoProfitIsGold(plan));
                ApplyAutoReward(autoRow);
                return autoRow;
            }).ToList();
    }

    // Ranks by the displayed value (Gold total or Ducat profit), or that value per minute; equal results prefer the
    // shorter trip, then the cheaper load, so a slow transport never wins a tie.
    private List<(PurchasePlan Plan, decimal Value, decimal? Minutes)> RankAutoPlans(PurchaseSearchResult result, bool perMinute,
        IReadOnlyDictionary<(int SourcePostId, int DestinationId, int TransportId), decimal> routeMinutes)
    {
        return result.Plans
            .Select(plan =>
            {
                decimal? minutes = routeMinutes.TryGetValue((GetPlanSourcePostId(plan), plan.DestinationId, plan.TransportId), out var found) && found > 0
                    ? found
                    : null;
                return (Plan: plan, Value: AutoProfitValue(plan), Minutes: minutes);
            })
            .OrderByDescending(item => perMinute ? item.Minutes is { } m ? item.Value / m : decimal.MinValue : item.Value)
            .ThenBy(item => item.Minutes ?? decimal.MaxValue)
            .ThenBy(item => item.Plan.Profit.PurchaseCost)
            .ThenBy(item => GetPlanSourcePostId(item.Plan))
            .ThenBy(item => item.Plan.DestinationId)
            .ThenBy(item => item.Plan.TransportId)
            .ToList();
    }

    private string AutoLetterTooltipLine(PurchasePlan plan) => GroupBonusTooltipLine(plan) + (PlanLetter(plan) is not { } letter
        ? string.Empty
        : Environment.NewLine + (letter == GuaranteeLetterKind.None
            ? "Letter: none (no letter pays for itself on this route)"
            : $"Letter: {ShortLetterName(letter)}" + (GetGuaranteeLetterSupply(letter) == 0 ? " (none held: buy one)" : string.Empty)));

    private string GroupBonusTooltipLine(PurchasePlan plan) =>
        plan.Lines.Any(line => _products.FirstOrDefault(product => product.Id == line.GoodId) is { } good && ModeOf(good) == GoodsMode.Group)
            ? Environment.NewLine + $"Sale prices include the group destination bonus (+{GroupDestinationBonus.Percent:0}%, rounded down)."
            : string.Empty;

    // Selecting or loading a route makes its assigned letter the active one for this trade type.
    private void ApplyPlanLetter(PurchasePlan plan)
    {
        if (PlanLetter(plan) is not { } letter) return;
        if (letter != SelectedGuaranteeLetter) SetActiveGuaranteeLetter(letter);
        else RefreshActiveLetterPickers();
    }

    private void ApplyAutoReward(AutoPlanRow row) => row.SetRewardText(RewardTooltip(CalculatePlanReward(row.Plan)));

    private void ApplyAutoReward(AutoExpandedRouteRow row)
    {
        var reward = CalculatePlanReward(row.PlannerRoute.Plan);
        row.SetReward($"{reward.DucatGain:N0}", PerMinuteText(reward.DucatGain, row.Minutes),
            $"{reward.TotalGold:N0}", PerMinuteText(reward.TotalGold, row.Minutes),
            RewardTooltip(reward) + AutoLetterTooltipLine(row.PlannerRoute.Plan));
        ApplyAutoReward(row.PlannerRoute);
    }

    private void RefreshAutoRewardText()
    {
        foreach (var row in new[] { AutoProfitResultsList, AutoTimeResultsList }
            .SelectMany(list => list.Items.OfType<AutoPlanRow>()))
            ApplyAutoReward(row);
        foreach (var row in _autoExpandedProfitRoutes.Concat(_autoExpandedPerMinuteRoutes))
            ApplyAutoReward(row);
    }

    private List<AutoExpandedRouteRow> BuildExpandedAutoRouteRows(PurchaseSearchResult result, bool perMinute,
                IReadOnlyDictionary<(int SourcePostId, int DestinationId, int TransportId), decimal> routeMinutes)
    {
        return RankAutoPlans(result, perMinute, routeMinutes)
            .Take(100)
            .Select(item =>
            {
                var plan = item.Plan;
                var sourcePostId = GetPlanSourcePostId(plan);
                var source = SourceName(sourcePostId);
                var destination = _posts.FirstOrDefault(post => post.Id == plan.DestinationId)?.Name ?? "Unknown town";
                var routeName = _bestOverallAutoMode ? $"{source} → {destination}" : destination;
                var transport = _transports.FirstOrDefault(entry => entry.Id == plan.TransportId);
                var hasMinutes = item.Minutes is > 0;
                var minutes = item.Minutes ?? 0m;
                var isGold = AutoProfitIsGold(plan);
                var profitPerMinute = hasMinutes
                    ? (item.Value / minutes).ToString("N1", CultureInfo.CurrentCulture)
                    : "--";
                var reward = CalculatePlanReward(plan);
                var isBarterPlan = plan.Lines.Any(line => _products.FirstOrDefault(product => product.Id == line.GoodId)?.IsBarter == true);
                var costText = isBarterPlan
                    ? $"{reward.MaterialGold:N0}"
                    : $"{plan.Profit.PurchaseCost:N0}";
                var transportName = ShortenAutoTransportName(transport?.Name ?? $"Transport {plan.TransportId}");
                var profitText = item.Value.ToString("N0", CultureInfo.CurrentCulture);
                var goodsToBuy = string.Join(Environment.NewLine, plan.Lines.Select(line =>
                {
                    var goodName = _products.FirstOrDefault(product => product.Id == line.GoodId)?.Name ?? $"Good {line.GoodId}";
                    return $"{goodName} x {line.Quantity:N0}";
                }));
                var profitLabel = isGold ? "Gold profit" : "Ducats earned (all bonuses)";
                var plannerRoute = new AutoPlanRow(routeName, profitText, transportName,
                    hasMinutes ? $"{minutes:N1}m" : "--", profitPerMinute,
                    $"{source} → {destination} · {transportName}{Environment.NewLine}{profitLabel} {profitText} · travel time {(hasMinutes ? $"{minutes:N1} minutes" : "unavailable")} · profit per minute {profitPerMinute}" +
                        AutoLetterTooltipLine(plan),
                    plan, sourcePostId, _goodsMode, perMinute, isGold);
                var expandedRow = new AutoExpandedRouteRow(
                    routeName,
                    transportName,
                    costText,
                    isBarterPlan,
                    $"{reward.DucatGain:N0}",
                    PerMinuteText(reward.DucatGain, item.Minutes),
                    hasMinutes ? $"{minutes:N1} min" : "--",
                    $"{reward.TotalGold:N0}",
                    PerMinuteText(reward.TotalGold, item.Minutes),
                    reward.SeasonalScore.ToString("N0", CultureInfo.CurrentCulture),
                    goodsToBuy,
                    item.Minutes,
                    plannerRoute);
                ApplyAutoReward(expandedRow);
                return expandedRow;
            }).ToList();
    }

    private static string PerMinuteText(decimal value, decimal? minutes) => minutes is > 0
        ? (value / minutes.Value).ToString("N1", CultureInfo.CurrentCulture)
        : "--";

    private static string ShortenAutoTransportName(string transportName)
    {
        if (transportName.StartsWith("[Partner]", StringComparison.Ordinal))
            return $"[P]{transportName["[Partner]".Length..]}";
        if (transportName.StartsWith("[Alpaca]", StringComparison.Ordinal))
            return $"[A]{transportName["[Alpaca]".Length..]}";
        return transportName;
    }

    private static string ShortenAutoTownName(string townName) => townName.Length <= 4
        ? townName
        : townName[..4].TrimEnd();

    private string ShortenAutoSourceName(int sourceId)
    {
        var route = BarterPairRoutes.FirstOrDefault(item => item.RouteId == sourceId);
        if (route.RouteId == 0) return ShortenAutoTownName(SourceName(sourceId));
        return $"{ShortenAutoTownName(SourceName(route.FirstPostId))}+{ShortenAutoTownName(SourceName(route.SecondPostId))}";
    }

    private void ExpandedAutoRoutesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoExpandedProfitRoutes.Count == 0 && _autoExpandedPerMinuteRoutes.Count == 0)
        {
            AutoStatusText.Text = "Calculate routes before opening the expanded view.";
            return;
        }

        if (_autoExpandedRoutesWindow is not { IsVisible: true })
        {
            _autoExpandedRoutesWindow = new AutoExpandedRoutesWindow { Owner = this };
            _autoExpandedRoutesWindow.RouteAddedToCustomSelection += AddExpandedRouteToCustomSelection;
            _autoExpandedRoutesWindow.Closed += (_, _) => _autoExpandedRoutesWindow = null;
        }
        var sourceLabel = _bestOverallAutoMode
            ? "all sources with cached prices"
            : (AutoSourcePicker.SelectedItem as CommercePost)?.Name ?? "Selected start";
        UpdateExpandedAutoRoutesWindow(sourceLabel);
        _autoExpandedRoutesWindow.Show();
        _autoExpandedRoutesWindow.Activate();
    }

    // Barter outposts have no Merchant Rating, so barter goods are never rating-locked.
    private static bool IsRatingLocked(GoodsEntry product, int rating) =>
        !product.IsBarter && product.RequiredCreditLevel > rating;

    // Auto checks each good against its own outpost's rating; the status names it only for a single start.
    private void UpdateExpandedAutoRoutesWindow(string sourceName) =>
        _autoExpandedRoutesWindow?.SetRoutes(sourceName,
            IsBarterMode || _bestOverallAutoMode || AutoSourcePicker.SelectedItem is not CommercePost source ? null : GetMerchantRatingForPost(source.Id),
            _autoExpandedProfitRoutes, _autoExpandedPerMinuteRoutes);

    private void AddExpandedRouteToCustomSelection(AutoPlanRow selectedRoute)
    {
        AutoCustomPlaceholder.Visibility = Visibility.Collapsed;
        AutoCustomSelectionList.ItemsSource = new[] { selectedRoute };
        AutoCustomSelectionList.Visibility = Visibility.Visible;
        _selectedAutoPlan = selectedRoute;
        ApplyPlanLetter(selectedRoute.Plan);
        RefreshPurchaseButtonState();
        AutoStatusText.Text = $"{selectedRoute.DestinationName} added to Custom Selection. Purchase loads it into planner cargo.";
    }

    private void ClearAutoCustomSelection()
    {
        AutoCustomSelectionList.ItemsSource = null;
        AutoCustomSelectionList.Visibility = Visibility.Collapsed;
        AutoCustomPlaceholder.Visibility = Visibility.Visible;
    }

    private void SetAutoExpandedRoutesButtonState()
    {
        ExpandedAutoRoutesButton.IsEnabled = true;
        ExpandedAutoRoutesButtonText.Opacity = _autoExpandedProfitRoutes.Count > 0 || _autoExpandedPerMinuteRoutes.Count > 0
            ? 1
            : 0.4;
    }

    private void AutoPlanResult_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAutoPlanSelection || sender is not ListBox list) return;
        var selected = list.SelectedItem as AutoPlanRow;
        _updatingAutoPlanSelection = true;
        if (ReferenceEquals(list, AutoProfitResultsList)) AutoTimeResultsList.SelectedItem = null;
        else AutoProfitResultsList.SelectedItem = null;
        _updatingAutoPlanSelection = false;
        _selectedAutoPlan = selected;
        RefreshPurchaseButtonState();
        if (selected is not null)
        {
            ApplyPlanLetter(selected.Plan);
            AutoStatusText.Text = $"Selected {selected.DestinationName} · {(PlanLetter(selected.Plan) is { } letter ? ShortLetterName(letter) : "current letter")} · review plan details, then load.";
        }
    }

    private void LoadSelectedAutoPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAutoPlan is not { } selected)
        {
            AutoStatusText.Text = "Select a route result first.";
            return;
        }
        var targetCargo = CargoFor(selected.Mode);
        var isBarterPlan = selected.Mode == GoodsMode.Barter;
        if (targetCargo.Count > 0)
        {
            AutoStatusText.Text = "Clear the existing cargo in this market before loading an Auto plan.";
            return;
        }
        if (!TryRead(DucatsInput, out var funds) || funds < 0)
        {
            AutoStatusText.Text = "Enter a valid, non-negative Ducat balance before loading an Auto plan.";
            return;
        }

        var transport = _transports.FirstOrDefault(item => item.Id == selected.Plan.TransportId);
        if (transport is null || !EligibleTransports(selected.GroupMode)
                .Any(item => item.Id == selected.Plan.TransportId && item.IsOwned))
        {
            AutoStatusText.Text = "The plan's transport is no longer available. Recalculate Auto.";
            return;
        }

        var planSourcePostIds = SourcePostIds(selected.SourcePostId);
        var barterGoldPerSaleDucat = _planSearchK.TryGetValue(selected.Plan, out var searchK) ? searchK.Value : BarterGoldPerSaleDucat();
        var linesToLoad = new List<(GoodsEntry Product, int Quantity, decimal BuyPrice)>();
        foreach (var line in selected.Plan.Lines)
        {
            var product = _products.FirstOrDefault(item => item.Id == line.GoodId);
            if (product is null || !planSourcePostIds.Contains(product.PostId) || ModeOf(product) != selected.Mode ||
                IsRatingLocked(product, GetMerchantRatingForPost(product.PostId)) || product.MaxBundle <= 0)
            {
                AutoStatusText.Text = "A planned good is no longer available at this start or rating. Recalculate Auto.";
                return;
            }
            var buyPrice = GetCurrentBuyPrice(product);
            if (buyPrice <= 0 || buyPrice * line.Quantity != line.Cost)
            {
                AutoStatusText.Text = $"{product.Name}'s {(isBarterPlan ? "material cost" : "buy price")} changed. Recalculate Auto before loading.";
                return;
            }

            var quote = _quotesByProduct[product.Id].FirstOrDefault(item => item.PostId == selected.Plan.DestinationId);
            if (quote is null || (quote.IsManualPrice && quote.DestinationPrice <= 0))
            {
                AutoStatusText.Text = $"{product.Name}'s destination quote is no longer available. Recalculate Auto.";
                return;
            }
            var salePrice = EffectiveSalePrice(product, quote.DestinationPrice > 0 ? quote.DestinationPrice : DefaultSalePrice(product, buyPrice));
            if (AutoSearchSalePrice(product, salePrice, barterGoldPerSaleDucat) * line.Quantity != line.Sale)
            {
                AutoStatusText.Text = $"{product.Name}'s destination quote changed. Recalculate Auto before loading.";
                return;
            }

            var stock = product.StockInitialized
                ? product.CurrentStock
                : product.IsWeeklyLimited ? Math.Min(product.MaxStock, product.SourceCount) : product.MaxStock;
            if (line.Quantity > stock || line.Quantity <= 0)
            {
                AutoStatusText.Text = $"{product.Name}'s available stock changed. Recalculate Auto.";
                return;
            }
            if (product.IsWeeklyLimited && line.Quantity > product.SourceCount)
            {
                AutoStatusText.Text = $"{product.Name}'s weekly limit changed. Recalculate Auto.";
                return;
            }
            linesToLoad.Add((product, line.Quantity, buyPrice));
        }

        var totalWeight = linesToLoad.Sum(line => line.Product.Weight * line.Quantity);
        var totalSlots = linesToLoad.Sum(line => decimal.ToInt32(decimal.Ceiling((decimal)line.Quantity / line.Product.MaxBundle)));
        if (totalWeight > CapacityWeight(transport) || totalSlots > CapacitySlots(transport) ||
            totalWeight != selected.Plan.UsedWeight || totalSlots != selected.Plan.UsedSlots)
        {
            AutoStatusText.Text = "Transport capacity changed. Recalculate Auto before loading.";
            return;
        }
        if (!isBarterPlan && funds < selected.Plan.Profit.PurchaseCost && !HasOtherModeCargo(selected.Mode))
        {
            AutoStatusText.Text = "The current Ducat balance is below this plan's purchase cost.";
            return;
        }

        if (_goodsMode != selected.Mode)
            SetGoodsMode(selected.Mode);
        var pickerSourceId = isBarterPlan ? planSourcePostIds[0] : selected.SourcePostId;
        if (PostPicker.SelectedValue is not int currentSourceId || currentSourceId != pickerSourceId)
            PostPicker.SelectedValue = pickerSourceId;
        if (PostPicker.SelectedValue is not int selectedSourceId || selectedSourceId != pickerSourceId)
        {
            AutoStatusText.Text = "Could not switch to the selected start town.";
            return;
        }
        if (!TransportPicker.Items.Cast<CommerceTransport>().Any(item => item.Id == transport.Id && item.IsOwned))
            RefreshTransportOptions();
        TransportPicker.SelectedValue = transport.Id;
        if (SelectedTransport?.Id != transport.Id)
        {
            AutoStatusText.Text = "Could not select the plan's transport.";
            return;
        }
        if (!ResolveOtherModeCargo(selected.Mode)) return;
        if (!TryRead(DucatsInput, out funds) || !isBarterPlan && funds < selected.Plan.Profit.PurchaseCost)
        {
            AutoStatusText.Text = "The current Ducat balance is below this plan's purchase cost.";
            return;
        }

        foreach (var line in linesToLoad)
        {
            var product = line.Product;
            if (!product.StockInitialized)
            {
                product.CurrentStock = product.IsWeeklyLimited
                    ? Math.Min(product.MaxStock, product.SourceCount)
                    : product.MaxStock;
                product.StockInitialized = true;
            }
            product.CurrentStock -= line.Quantity;
            ActiveCargo[product.Id] = new CargoLine
            {
                Product = product,
                Quantity = line.Quantity,
                UnitBuyPrice = line.BuyPrice
            };
        }
        SaveWeeklyStock();
        _plannedRouteDestinationByMode[selected.Mode] = selected.Plan.DestinationId;
        ApplyPlanLetter(selected.Plan);
        RefreshTransportLoad();
        RefreshDestinationTotals();
        ProductList.SelectedItem = linesToLoad.FirstOrDefault().Product;
        if (SelectedProduct is { } selectedProduct)
            StockInput.Text = selectedProduct.CurrentStock.ToString(CultureInfo.CurrentCulture);
        if (!isBarterPlan)
            DucatsInput.Text = FormatDucats(funds - selected.Plan.Profit.PurchaseCost);
        TradeMiddleTabs.SelectedIndex = 2;
        DestinationTotalsList.SelectedValue = selected.Plan.DestinationId;
        DestinationTotalsList.UpdateLayout();
        if (DestinationTotalsList.SelectedItem is DestinationTotal selectedDestination)
            (DestinationTotalsList.ItemContainerGenerator.ContainerFromItem(selectedDestination) as FrameworkElement)?.BringIntoView();
        AutoStatusText.Text = $"Loaded plan for { _posts.FirstOrDefault(post => post.Id == selected.Plan.DestinationId)?.Name ?? "destination" }.";
        SetStatus($"Loaded {linesToLoad.Count} goods for the selected route plan.");
    }

    private void UpdateMiddleTabChevron()
    {
        if (GoodsToCenterArrow is null || TransportToCenterArrow is null)
            return;

        GoodsToCenterArrow.Visibility = TradeMiddleTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        TransportToCenterArrow.Visibility = TradeMiddleTabs.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        PurchaseInputsPanel.Visibility = TradeMiddleTabs.SelectedIndex == 1 ? Visibility.Collapsed : Visibility.Visible;
        var selectedTab = TradeMiddleTabs.SelectedIndex;
        PurchaseInputsPanel.Visibility = Visibility.Visible;
        ManualPurchaseInputs.Visibility = selectedTab == 0 ? Visibility.Visible : Visibility.Collapsed;
        AutoPurchaseInputs.Visibility = selectedTab == 1 ? Visibility.Visible : Visibility.Collapsed;
        LoadProfitInputs.Visibility = selectedTab == 2 ? Visibility.Visible : Visibility.Collapsed;
        PurchaseActionButton.Visibility = selectedTab is 0 or 1 ? Visibility.Visible : Visibility.Collapsed;
        RefreshPurchaseButtonState();
        PurchaseActionButton.ToolTip = selectedTab == 1
            ? "Load the selected Auto route into planner cargo; this does not buy anything in game."
            : "Add selected goods to the transport";
        SellActionButton.Visibility = TradeMiddleTabs.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Manual Purchase is always available; Auto Purchase needs a selected route. Auto-only resets must not disable Manual.
    private void RefreshPurchaseButtonState() =>
        PurchaseActionButton.IsEnabled = TradeMiddleTabs.SelectedIndex == 0 || TradeMiddleTabs.SelectedIndex == 1 && _selectedAutoPlan is not null;

    private void ShowTradePage_Click(object sender, RoutedEventArgs e) => SetGoodsMode(GoodsMode.Trade);

    private void ShowGroupPage_Click(object sender, RoutedEventArgs e) => SetGoodsMode(GoodsMode.Group);

    private void ShowBarterPage_Click(object sender, RoutedEventArgs e) => SetGoodsMode(GoodsMode.Barter);

    private void SetGoodsMode(GoodsMode mode)
    {
        _goodsMode = mode;
        UpdateTopTabs(mode switch
        {
            GoodsMode.Group => GroupPageTab,
            GoodsMode.Barter => BarterPageTab,
            _ => TradePageTab
        });
        Resources["BarterMaterialsIdleOpacity"] = mode == GoodsMode.Barter ? 1.0 : 0.4;
        BuyPriceInput.IsReadOnly = mode == GoodsMode.Barter;
        BuyPriceCaption.Text = mode == GoodsMode.Barter ? "MATERIAL COST" : "BUY PRICE";
        BuyPriceGoldIcon.Source = (System.Windows.Media.ImageSource)FindResource(mode == GoodsMode.Barter ? "GoldCurrencyArt" : "DucatCurrencyArt");
        PostRatingText.Visibility = RatingInput.Visibility = mode == GoodsMode.Barter ? Visibility.Collapsed : Visibility.Visible;
        BuyPriceInput.ToolTip = mode == GoodsMode.Barter
            ? "Gold value of the exchange materials for one item (set in the barter materials panel)."
            : "Observed buy price at this post. Defaults to the client reference midpoint until entered or scanned.";
        // Barter has only a handful of possible runs, so Auto always plans from the chosen outpost or outpost pair.
        if (mode == GoodsMode.Barter) SetAutoMode(bestOverall: false);
        AutoBestOverallModeButton.Visibility = mode == GoodsMode.Barter ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumnSpan(AutoSelectedStartModeButton, mode == GoodsMode.Barter ? 2 : 1);
        RefreshPostOptions();
        RefreshProducts();
        RefreshTransportOptions();
        UpdateShoppingListButton();
        RefreshActiveLetterPickers();
        RefreshRewardDisplays();
    }

    private bool IsGroupMode => _goodsMode == GoodsMode.Group;

    private bool IsBarterMode => _goodsMode == GoodsMode.Barter;

    private static GoodsMode ModeOf(GoodsEntry product) =>
        product.IsBarter ? GoodsMode.Barter : product.CommerceParty ? GoodsMode.Group : GoodsMode.Trade;

    private bool MatchesMode(GoodsEntry product) => ModeOf(product) == _goodsMode && !product.IsInactiveRotation;

    // Entered and scanned group quotes are base prices; the party's chosen destination always adds its sale bonus.
    private static decimal EffectiveSalePrice(GoodsEntry product, decimal enteredPrice) =>
        ModeOf(product) == GoodsMode.Group ? GroupDestinationBonus.Apply(enteredPrice) : enteredPrice;

    // Makes one rotating barter alternative the active offer for its post and remembers the choice.
    private bool ActivateRotation(GoodsEntry product)
    {
        if (!product.HasRotationAlternatives || !product.IsInactiveRotation) return false;
        foreach (var alternative in product.RotationAlternatives)
            alternative.IsInactiveRotation = !ReferenceEquals(alternative, product);
        _plannerPreferences.ActiveBarterRotationByPostId[product.PostId] = product.CatalogId;
        SavePlannerPreferences();
        _barterMaterialsWindow?.RefreshVerdicts();
        return true;
    }

    // Logs the time a price-list scan saw this rotating offer at its post, so weekly and monthly rotation can be
    // told apart. Manual dropdown picks are deliberately not logged.
    private void RecordRotationSighting(GoodsEntry product, BarterSightingSource source)
    {
        if (!product.HasRotationAlternatives) return;
        BarterRotationLog.Record(_plannerPreferences.BarterRotationScanSightings, product.PostId, product.CatalogId, DateTime.UtcNow, source);
        SavePlannerPreferences();
        RefreshRotationHistory(product.PostId);
    }

    private void RefreshRotationHistory(int postId)
    {
        var alternatives = _products.FirstOrDefault(product => product.PostId == postId && product.HasRotationAlternatives)?.RotationAlternatives;
        if (alternatives is null) return;
        var history = BarterRotationLog.History(_plannerPreferences.BarterRotationScanSightings, postId);
        string NameOf(int offerId) => alternatives.FirstOrDefault(item => item.CatalogId == offerId)?.Name ?? $"Offer {offerId}";
        var now = DateTime.UtcNow;
        var confirmed = BarterRotationLog.IsConfirmedSinceReset(history, now);
        var pacific = GroupStockResetSchedule.PacificTimeZone;
        string When(DateTime utc) => $"{TimeZoneInfo.ConvertTimeFromUtc(utc, pacific):MM-dd HH:mm}";
        var lines = history.TakeLast(12).Select(item => item.FirstSeenUtc == item.LastSeenUtc
            ? $"{When(item.FirstSeenUtc)}: {NameOf(item.OfferId)}"
            : $"{When(item.FirstSeenUtc)} to {When(item.LastSeenUtc)}: {NameOf(item.OfferId)}");
        var text = "Pick the offer this outpost has now. Scan its price list to log a sighting.\n" +
            (confirmed ? "Scanned since the weekly reset." : "Not scanned since the weekly reset.") +
            "\n\nScanned (Pacific time):\n" +
            (history.Count == 0 ? "None yet" : string.Join("\n", lines)) +
            "\n\n" + BarterRotationLog.Analyze(history, alternatives.Select(item => item.CatalogId).ToList(), NameOf, now);
        foreach (var alternative in alternatives)
        {
            alternative.RotationHistoryText = text;
            alternative.IsRotationConfirmedThisWeek = confirmed && history[^1].OfferId == alternative.CatalogId;
        }
    }

    private void RotationPicker_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // The dropdown toggle consumes the click, so select the row it belongs to explicitly.
        if (sender is FrameworkElement { DataContext: GoodsEntry product } && !ReferenceEquals(ProductList.SelectedItem, product))
            ProductList.SelectedItem = product;
    }

    // A focused closed ComboBox changes its selection on mouse wheel, which silently switched the active rotating
    // offer while scrolling the goods list. Forward the wheel to the list instead.
    private void RotationPicker_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not ComboBox { IsDropDownOpen: false } comboBox) return;
        e.Handled = true;
        if (System.Windows.Media.VisualTreeHelper.GetParent(comboBox) is not UIElement parent) return;
        parent.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = comboBox
        });
    }

    private void RotationPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || sender is not ComboBox { DataContext: GoodsEntry current } ||
            e.AddedItems.Count == 0 || e.AddedItems[0] is not GoodsEntry chosen || ReferenceEquals(chosen, current)) return;
        if (!ActivateRotation(chosen)) return;
        RefreshRotationHistory(chosen.PostId);
        // Defer the list rebuild until the ComboBox inside the old row has finished its selection change.
        Dispatcher.BeginInvoke(() =>
        {
            RefreshProducts();
            ProductList.SelectedItem = chosen;
            ProductList.ScrollIntoView(chosen);
            SetStatus($"{chosen.Name} is now the active rotating offer.");
        });
    }

    private Dictionary<int, CargoLine> CargoFor(GoodsMode mode) => mode switch
    {
        GoodsMode.Group => _groupCargoByProduct,
        GoodsMode.Barter => _barterCargoByProduct,
        _ => _tradeCargoByProduct
    };

    private static string ModeName(GoodsMode mode) => mode switch
    {
        GoodsMode.Group => "Group",
        GoodsMode.Barter => "Barter",
        _ => "Trade"
    };

    private static decimal DefaultSalePrice(GoodsEntry product, decimal buyPrice) => product.IsBarter
        ? ((decimal)product.MinPrice + product.MaxPrice) / 2m
        : buyPrice + 10m;

    private static bool IsBarterPairRoute(int sourceId) => BarterPairRoutes.Any(route => route.RouteId == sourceId);

    private string SourceName(int sourceId)
    {
        foreach (var route in BarterPairRoutes)
            if (route.RouteId == sourceId) return route.Name;
        return _posts.FirstOrDefault(post => post.Id == sourceId)?.Name ?? "Unknown source";
    }

    private static IReadOnlyList<int> SourcePostIds(int sourceId)
    {
        foreach (var route in BarterPairRoutes)
            if (route.RouteId == sourceId) return [route.FirstPostId, route.SecondPostId];
        return [sourceId];
    }

    // Barter cargo may combine the two outposts of one pair route; the trip visits both, then sells at one destination.
    private static int? BarterPairRouteFor(IEnumerable<int> postIds)
    {
        var ids = postIds.Distinct().ToList();
        if (ids.Count <= 1) return ids.Count == 1 ? ids[0] : null;
        foreach (var route in BarterPairRoutes)
            if (ids.All(id => id == route.FirstPostId || id == route.SecondPostId)) return route.RouteId;
        return null;
    }

    private decimal? GetRouteMinutes(int sourceId, int destinationId, int transportId, out string source)
    {
        var pair = BarterPairRoutes.FirstOrDefault(route => route.RouteId == sourceId);
        if (pair.RouteId == 0) return GetManualQuoteMinutes(sourceId, destinationId, transportId, out source);

        decimal? best = null;
        source = "No route time for this trip.";
        foreach (var (first, second) in new[] { (pair.FirstPostId, pair.SecondPostId), (pair.SecondPostId, pair.FirstPostId) })
        {
            var firstLeg = GetManualQuoteMinutes(first, second, transportId, out _);
            var secondLeg = GetManualQuoteMinutes(second, destinationId, transportId, out var legSource);
            if (firstLeg is not > 0 || secondLeg is not > 0) continue;
            var total = firstLeg.Value + secondLeg.Value;
            if (best is not null && total >= best) continue;
            best = total;
            var firstName = _posts.FirstOrDefault(post => post.Id == first)?.Name ?? "first outpost";
            var secondName = _posts.FirstOrDefault(post => post.Id == second)?.Name ?? "second outpost";
            source = $"{firstName} → {secondName}: {firstLeg.Value:N1} min{Environment.NewLine}{secondName} → destination: {secondLeg.Value:N1} min{Environment.NewLine}{legSource}";
        }
        return best;
    }

    private void UpdateTopTabs(Button activeTab)
    {
        var inactive = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(91, 69, 36));
        var active = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(197, 158, 75));
        TradePageTab.Background = ReferenceEquals(activeTab, TradePageTab) ? active : inactive;
        GroupPageTab.Background = ReferenceEquals(activeTab, GroupPageTab) ? active : inactive;
        BarterPageTab.Background = ReferenceEquals(activeTab, BarterPageTab) ? active : inactive;
    }

    private void RefreshPostOptions()
    {
        var selectedPostId = (PostPicker.SelectedItem as CommercePost)?.Id;
        var postsWithGoods = _posts.Where(post => (post.CanBuy || IsBarterMode) && _products.Any(product =>
            product.PostId == post.Id && MatchesMode(product))).ToList();
        PostPicker.ItemsSource = postsWithGoods;
        if (postsWithGoods.Any(post => post.Id == selectedPostId))
            PostPicker.SelectedValue = selectedPostId;
        else if (postsWithGoods.Count > 0)
            PostPicker.SelectedIndex = 0;
        RefreshAutoSourceOptions();
    }

    private void RefreshAutoSourceOptions()
    {
        if (AutoSourcePicker is null) return;
        var postId = (PostPicker.SelectedItem as CommercePost)?.Id;
        var autoId = (AutoSourcePicker.SelectedItem as CommercePost)?.Id;
        var selectedId = autoId is int current && postId is int post && !SourcePostIds(current).Contains(post) ? postId : autoId ?? postId;
        var sources = _posts.Where(post => (post.CanBuy || IsBarterMode) && _products.Any(product =>
            product.PostId == post.Id && MatchesMode(product))).ToList();
        if (IsBarterMode)
            sources.AddRange(BarterPairRoutes.Select(route => new CommercePost
            {
                Id = route.RouteId,
                Name = route.Name,
                Icon = _posts.FirstOrDefault(post => post.Id == route.FirstPostId)?.Icon
            }));
        sources = sources.OrderBy(post => post.Id == PostIds.ScathachBeach).ToList();
        var wasSyncing = _syncingStartPickers;
        _syncingStartPickers = true;
        try
        {
            AutoSourcePicker.ItemsSource = sources;
            if (sources.Any(post => post.Id == selectedId))
                AutoSourcePicker.SelectedValue = selectedId;
            else if (sources.Count > 0)
                AutoSourcePicker.SelectedIndex = 0;
        }
        finally { _syncingStartPickers = wasSyncing; }
        _autoCalculationCancellation?.Cancel();
        _autoExpandedRoutesWindow?.Close();
        ClearAutoCustomSelection();
        AutoProfitResultsList.ItemsSource = null;
        AutoTimeResultsList.ItemsSource = null;
        AutoProfitPlaceholder.Visibility = Visibility.Visible;
        AutoTimePlaceholder.Visibility = Visibility.Visible;
        _autoExpandedProfitRoutes = [];
        _autoExpandedPerMinuteRoutes = [];
        SetAutoExpandedRoutesButtonState();
        if (!_initializing)
        {
            _selectedAutoPlan = null;
            RefreshPurchaseButtonState();
        }
        if (!_initializing) AutoStatusText.Text = "Start list changed. Calculate to refresh route suggestions.";
    }

    private void RefreshTransportOptions()
    {
        var transports = EligibleTransports(IsGroupMode).ToList();
        var selectedTransportId = (TransportPicker.SelectedItem as CommerceTransport)?.Id;
        TransportPicker.ItemsSource = transports;
        if (transports.Any(transport => transport.Id == selectedTransportId && transport.IsOwned))
            TransportPicker.SelectedValue = selectedTransportId;
        else
            TransportPicker.SelectedItem = transports.FirstOrDefault(transport => transport.IsOwned);
        RefreshTransportLoad();
    }


    private static string AvailableTransportsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MabiCommerceNewLife", "available-transports.json");

    private static string PartnerEnabledPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MabiCommerceNewLife", "partner-enabled.json");

    private static string AlpacaEnabledPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MabiCommerceNewLife", "alpaca-enabled.json");

    private static string WilliamEnabledPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MabiCommerceNewLife", "william-enabled.json");

    private static string PlannerPreferencesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MabiCommerceNewLife", "planner-preferences.json");

    private static PlannerPreferences LoadPlannerPreferences()
    {
        try
        {
            if (File.Exists(PlannerPreferencesPath))
                return JsonSerializer.Deserialize<PlannerPreferences>(File.ReadAllText(PlannerPreferencesPath)) ?? new PlannerPreferences();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new PlannerPreferences();
    }

    private void SavePlannerPreferences()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PlannerPreferencesPath)!);
            File.WriteAllText(PlannerPreferencesPath, JsonSerializer.Serialize(_plannerPreferences));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Planner settings could not be saved: {exception.Message}", true);
        }
    }

    private static bool LoadPartnerEnabled()
    {
        try
        {
            return File.Exists(PartnerEnabledPath) && JsonSerializer.Deserialize<bool>(File.ReadAllText(PartnerEnabledPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool LoadAlpacaEnabled()
    {
        try
        {
            return File.Exists(AlpacaEnabledPath) && JsonSerializer.Deserialize<bool>(File.ReadAllText(AlpacaEnabledPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool LoadWilliamEnabled()
    {
        try
        {
            return File.Exists(WilliamEnabledPath) && JsonSerializer.Deserialize<bool>(File.ReadAllText(WilliamEnabledPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private void PartnerEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _transports is null) return;
        var enabled = (sender as CheckBox)?.IsChecked == true;
        if (_partnerEnabled == enabled) return;
        _partnerEnabled = enabled;
        UpdateDependentTransportOwnership();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PartnerEnabledPath)!);
            File.WriteAllText(PartnerEnabledPath, JsonSerializer.Serialize(_partnerEnabled));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Partner setting is active for this session but could not be saved: {exception.Message}", "Partner transport");
        }
        RefreshTransportOptions();
    }

    private void WilliamEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _transports is null) return;
        var enabled = (sender as CheckBox)?.IsChecked == true;
        if (_williamEnabled == enabled) return;
        _williamEnabled = enabled;
        UpdateDependentTransportOwnership();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WilliamEnabledPath)!);
            File.WriteAllText(WilliamEnabledPath, JsonSerializer.Serialize(_williamEnabled));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"William setting is active for this session but could not be saved: {exception.Message}", "William transport");
        }
        RefreshTransportOptions();
    }

    private void AlpacaEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _transports is null) return;
        var enabled = (sender as CheckBox)?.IsChecked == true;
        if (_alpacaEnabled == enabled) return;
        _alpacaEnabled = enabled;
        UpdateDependentTransportOwnership();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AlpacaEnabledPath)!);
            File.WriteAllText(AlpacaEnabledPath, JsonSerializer.Serialize(_alpacaEnabled));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Alpaca setting is active for this session but could not be saved: {exception.Message}", "Alpaca transport");
        }
        RefreshTransportOptions();
    }

    private void UpdateDependentTransportOwnership()
    {
        foreach (var transport in _transports.Where(transport => transport.IsPartner))
            transport.IsOwned = (transport.IsWilliamPartner ? _williamEnabled : _partnerEnabled) &&
                _availableTransportIds.Contains(transport.PartnerBaseTransportId);
        foreach (var transport in _transports.Where(transport => transport.IsAlpaca))
            transport.IsOwned = _alpacaEnabled && _availableTransportIds.Contains(transport.AlpacaBaseTransportId);
    }

    private static IEnumerable<int> LoadAvailableTransportIds()
    {
        try
        {
            return File.Exists(AvailableTransportsPath)
                ? JsonSerializer.Deserialize<int[]>(File.ReadAllText(AvailableTransportsPath)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void TransportOwnership_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is CheckBox { DataContext: CommerceTransport transport } && transport.IsOwnershipEditable)
            ToggleTransportOwnership(transport);
        e.Handled = true;
    }

    private void TransportOwnership_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Space) return;
        if (sender is CheckBox { DataContext: CommerceTransport transport } && transport.IsOwnershipEditable)
            ToggleTransportOwnership(transport);
        e.Handled = true;
    }

    private void ToggleTransportOwnership(CommerceTransport transport)
    {
        transport.IsOwned = !transport.IsOwned;
        if (transport.IsOwned) _availableTransportIds.Add(transport.Id);
        else _availableTransportIds.Remove(transport.Id);
        UpdateDependentTransportOwnership();
        SaveAvailableTransportIds();
    }

    private void SaveAvailableTransportIds()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AvailableTransportsPath)!);
            File.WriteAllText(AvailableTransportsPath, JsonSerializer.Serialize(_availableTransportIds.Order()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Transport choices are active for this session but could not be saved: {exception.Message}", "Transport availability");
        }
    }

    private void TransportPicker_DropDownClosed(object sender, EventArgs e)
    {
        if (SelectedTransport is { IsOwned: false }) RefreshTransportOptions();
    }

    private void TransportPicker_DropDownOpened(object sender, EventArgs e)
    {
        if (sender is not ComboBox picker) return;
        picker.ApplyTemplate();
        SetTransportFilterCheckBox(picker, "PartnerFilterCheckBox", _partnerEnabled);
        SetTransportFilterCheckBox(picker, "WilliamFilterCheckBox", _williamEnabled);
        SetTransportFilterCheckBox(picker, "AlpacaFilterCheckBox", _alpacaEnabled);
    }

    private static void SetTransportFilterCheckBox(ComboBox picker, string name, bool isChecked)
    {
        if (picker.Template.FindName(name, picker) is CheckBox checkBox)
            checkBox.IsChecked = isChecked;
    }

    private static bool IsFlight(CommerceTransport transport) =>
        string.Equals(transport.Type, "flight", StringComparison.OrdinalIgnoreCase);

    private static bool IsAvailableTransport(CommerceTransport transport) =>
        !transport.IsSkin && !transport.IsEvent && !transport.IsRental;

    private IEnumerable<CommerceTransport> EligibleTransports(bool group) =>
        _transports.Where(transport => IsAvailableTransport(transport) && (group || !IsFlight(transport)) &&
            (!transport.IsPartner || (transport.IsWilliamPartner ? _williamEnabled : _partnerEnabled)) &&
            (!transport.IsAlpaca || _alpacaEnabled));

    public int NormalizeDetectedTransportId(int detectedTransportId) =>
        _transports.FirstOrDefault(transport => transport.Id == detectedTransportId)?.BaseTransportId ?? detectedTransportId;

    public bool SelectDetectedTransport(int detectedTransportId)
    {
        var baseTransportId = NormalizeDetectedTransportId(detectedTransportId);
        if (!TransportPicker.Items.Cast<CommerceTransport>().Any(transport => transport.Id == baseTransportId && transport.IsOwned)) return false;
        TransportPicker.SelectedValue = baseTransportId;
        return true;
    }

    private void PostPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        _keepAutoTabOnProductRefresh = TradeMiddleTabs.SelectedIndex == 1;
        try
        {
            LoadMerchantRatingForSelectedPost();
            RefreshProducts();
        }
        finally { _keepAutoTabOnProductRefresh = false; }
        RefreshQuotes();
        SyncAutoSourceToPostPicker();
    }

    private bool _syncingStartPickers;
    private bool _keepAutoTabOnProductRefresh;

    // The Auto start picker mirrors the main town picker. A barter pair route counts as matching either of its outposts.
    private void SyncAutoSourceToPostPicker()
    {
        if (_syncingStartPickers || _bestOverallAutoMode || AutoSourcePicker is null || PostPicker.SelectedValue is not int postId) return;
        if (AutoSourcePicker.SelectedValue is int current && SourcePostIds(current).Contains(postId)) return;
        if (!AutoSourcePicker.Items.Cast<CommercePost>().Any(post => post.Id == postId)) return;
        _syncingStartPickers = true;
        try { AutoSourcePicker.SelectedValue = postId; }
        finally { _syncingStartPickers = false; }
    }

    private void SyncPostPickerToAutoSource()
    {
        if (_syncingStartPickers || AutoSourcePicker.SelectedValue is not int sourceId) return;
        var posts = SourcePostIds(sourceId);
        if (PostPicker.SelectedValue is int current && posts.Contains(current)) return;
        if (!PostPicker.Items.Cast<CommercePost>().Any(post => post.Id == posts[0])) return;
        _syncingStartPickers = true;
        try { PostPicker.SelectedValue = posts[0]; }
        finally { _syncingStartPickers = false; }
    }

    private void RatingInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing || ProductList is null) return;
        if (PostPicker.SelectedItem is CommercePost post &&
            int.TryParse(RatingInput.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var rating) && rating >= 1)
        {
            _plannerPreferences.MerchantRatingsByPostId[post.Id] = rating;
            SavePlannerPreferences();
            _inventoryWindow?.SyncMerchantRating(post.Id, rating);
        }
        RefreshProducts();
    }

    private void ScreenReaderMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _plannerPreferences.ScreenReaderModeEnabled = ScreenReaderModeCheckBox.IsChecked == true;
        ReadPriceListButton.Visibility = _plannerPreferences.ScreenReaderModeEnabled && !IsGroupMode
            ? Visibility.Visible : Visibility.Collapsed;
        GroupReadPriceListButton.Visibility = _plannerPreferences.ScreenReaderModeEnabled && IsGroupMode
            ? Visibility.Visible : Visibility.Collapsed;
        SavePlannerPreferences();
    }

    private void AlwaysOnTop_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var enabled = AlwaysOnTopCheckBox.IsChecked == true;
        Topmost = enabled;
        foreach (var child in new Window?[] { _shoppingListWindow, _barterMaterialsWindow, _inventoryWindow, _tradeHistoryWindow })
            if (child is not null) child.Topmost = enabled;
        if (_plannerPreferences.AlwaysOnTop == enabled) return;
        _plannerPreferences.AlwaysOnTop = enabled;
        SavePlannerPreferences();
    }

    private void AutoRefreshGroupStock_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var enabled = AutoRefreshGroupStockCheckBox.IsChecked == true;
        if (_plannerPreferences.AutoRefreshGroupStock == enabled) return;
        _plannerPreferences.AutoRefreshGroupStock = enabled;
        SavePlannerPreferences();
        AutoRefreshGroupStockIfDue();
    }

    private void ResetGroupStock_Click(object sender, RoutedEventArgs e)
    {
        var mode = IsBarterMode ? GoodsMode.Barter : GoodsMode.Group;
        RestockWeeklyGoods(mode);
        SetStatus(mode == GoodsMode.Barter
            ? "Barter goods stock reset to the weekly maximum."
            : "Group goods stock reset to the weekly maximum.");
    }

    // Checked at startup (against the saved last-session time), when the option is turned on, after the PC resumes from sleep, and by a one-shot timer set for the next reset.
    private void AutoRefreshGroupStockIfDue()
    {
        _weeklyResetTimer.Stop();
        if (!_plannerPreferences.AutoRefreshGroupStock) return;
        var now = DateTime.UtcNow;
        if (GroupStockResetSchedule.IsRefreshDue(_lastGroupStockRefreshUtc, now))
        {
            _lastGroupStockRefreshUtc = now;
            RestockWeeklyGoods(GoodsMode.Group, GoodsMode.Barter);
            SetStatus("Weekly reset passed (Thursday 7 AM Pacific); Group and Barter goods were restocked.");
        }
        // A second of slack guards against the timer firing a hair early.
        var untilNext = GroupStockResetSchedule.GetNextResetUtc(now) - now + TimeSpan.FromSeconds(1);
        _weeklyResetTimer.Interval = untilNext > TimeSpan.FromSeconds(1) ? untilNext : TimeSpan.FromSeconds(1);
        _weeklyResetTimer.Start();
    }

    private void SystemEvents_PowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            Dispatcher.BeginInvoke(AutoRefreshGroupStockIfDue);
    }

    private void RestockWeeklyGoods(params GoodsMode[] modes)
    {
        foreach (var product in _products.Where(product => modes.Contains(ModeOf(product))))
        {
            product.CurrentStock = product.IsWeeklyLimited ? Math.Min(product.MaxStock, product.SourceCount) : product.MaxStock;
            product.StockInitialized = true;
        }
        if (SelectedProduct is { } selected && modes.Contains(ModeOf(selected)))
            StockInput.Text = selected.CurrentStock.ToString(CultureInfo.CurrentCulture);
        SaveWeeklyStock();
    }

    private void LoadSavedWeeklyStock()
    {
        foreach (var (productId, stock) in _plannerPreferences.WeeklyStockByProductId)
        {
            var product = _products.FirstOrDefault(entry => entry.Id == productId);
            if (product is null || ModeOf(product) == GoodsMode.Trade) continue;
            var limit = product.IsWeeklyLimited ? Math.Min(product.MaxStock, product.SourceCount) : product.MaxStock;
            product.CurrentStock = Math.Clamp(stock, 0, limit);
            product.StockInitialized = true;
        }
        if (_plannerPreferences.WeeklyStockSavedUtc is DateTime savedUtc)
            _lastGroupStockRefreshUtc = DateTime.SpecifyKind(savedUtc, DateTimeKind.Utc);
        if (SelectedProduct is { } selected && ModeOf(selected) != GoodsMode.Trade)
            StockInput.Text = selected.CurrentStock.ToString(CultureInfo.CurrentCulture);
    }

    // Persists Group/Barter stock with the session time, so the next startup restocks only if a weekly reset has passed since.
    // Transport loads are not saved, so goods still in a transport (not sold) are saved as returned to stock; a close or
    // crash before Sell therefore refunds them.
    private void SaveWeeklyStock()
    {
        if (_initializing) return;
        var saved = _plannerPreferences.WeeklyStockByProductId;
        saved.Clear();
        var unsold = Enum.GetValues<GoodsMode>().SelectMany(mode => CargoFor(mode).Values)
            .GroupBy(line => line.Product.Id)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));
        foreach (var product in _products.Where(product => product.StockInitialized && ModeOf(product) != GoodsMode.Trade))
        {
            var limit = product.IsWeeklyLimited ? Math.Min(product.MaxStock, product.SourceCount) : product.MaxStock;
            saved[product.Id] = Math.Min(limit, product.CurrentStock + unsold.GetValueOrDefault(product.Id));
        }
        var now = DateTime.UtcNow;
        // While auto-restock is on and a reset is still pending (e.g. just after waking from sleep), keep the older time so the restock is not skipped.
        if (!_plannerPreferences.AutoRefreshGroupStock || !GroupStockResetSchedule.IsRefreshDue(_lastGroupStockRefreshUtc, now))
            _lastGroupStockRefreshUtc = now;
        _plannerPreferences.WeeklyStockSavedUtc = _lastGroupStockRefreshUtc;
        SavePlannerPreferences();
    }

    private int GetMerchantRatingForPost(int postId) =>
        _plannerPreferences.MerchantRatingsByPostId.TryGetValue(postId, out var rating) && rating >= 1 ? rating : 1;

    private void LoadMerchantRatingForSelectedPost()
    {
        if (PostPicker.SelectedItem is CommercePost post)
            RatingInput.Text = GetMerchantRatingForPost(post.Id).ToString(CultureInfo.CurrentCulture);
    }

    private void MigrateLegacyMerchantRating()
    {
        if (_plannerPreferences.MerchantRating is not int legacyRating) return;
        foreach (var post in _posts)
            _plannerPreferences.MerchantRatingsByPostId.TryAdd(post.Id, Math.Max(1, legacyRating));
        _plannerPreferences.MerchantRating = null;
        SavePlannerPreferences();
    }

    private static decimal RoundDucats(decimal ducats) => decimal.Round(ducats, 0, MidpointRounding.AwayFromZero);

    // Ducat balances are whole numbers; decimal scale (e.g. "745889.0") must never reach the text box.
    private static string FormatDucats(decimal ducats) => RoundDucats(ducats).ToString("N0", CultureInfo.CurrentCulture);

    private void DucatsInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing || !decimal.TryParse(DucatsInput.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var ducats) || ducats < 0)
            return;
        _plannerPreferences.CurrentDucats = RoundDucats(ducats);
        SavePlannerPreferences();
    }

    // Separators are applied once editing ends so they don't move the caret while typing.
    private void DucatsInput_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (TryRead(DucatsInput, out var ducats) && ducats >= 0)
            DucatsInput.Text = FormatDucats(ducats);
    }

    private void ProductList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Rows' rotation dropdowns raise SelectionChanged too; it bubbles here when their templates load.
        if (!ReferenceEquals(e.OriginalSource, ProductList)) return;
        if (SelectedProduct is not { } product) return;

        SelectedProductName.Text = product.Name;
        SelectedProductMeta.Text = $"Weight {product.Weight} / {product.MaxBundle} per slot";
        CatalogPriceRange.Text = $"Client reference range\n{product.MinPrice:N0}–{product.MaxPrice:N0} Ducats";
        if (!product.StockInitialized)
        {
            product.CurrentStock = product.IsWeeklyLimited ? Math.Min(product.MaxStock, product.SourceCount) : product.MaxStock;
            product.StockInitialized = true;
        }
        StockInput.Text = product.CurrentStock.ToString(CultureInfo.CurrentCulture);
        BuyPriceInput.Text = product.CurrentBuyPriceText;
        if (!_keepAutoTabOnProductRefresh) TradeMiddleTabs.SelectedIndex = 0;
        RefreshQuotes();
    }

    private void BuyPriceInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!BuyPriceInput.IsKeyboardFocusWithin || SelectedProduct is not { IsBarter: false } product ||
            !decimal.TryParse(BuyPriceInput.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var buyPrice) || buyPrice <= 0)
            return;
        product.CurrentBuyPriceIsEstimated = false;
        product.CurrentBuyPrice = buyPrice;
        product.CurrentBuyPriceText = BuyPriceInput.Text;
        _plannerPreferences.ManualBuyPricesByProductId[product.Id] = buyPrice;
        SavePlannerPreferences();
        if (_quotesByProduct.TryGetValue(product.Id, out var quotes))
            foreach (var quote in quotes)
                if (quote.PostId != SmugglerDestinationId) quote.SetDefaultDestinationPrice(buyPrice + 10m);
        RefreshManualQuoteMetrics(product, buyPrice);
        RefreshDestinationTotals();
    }

    private void StockInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SelectedProduct is { } product && int.TryParse(StockInput.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var stock))
        {
            product.CurrentStock = stock;
            product.StockInitialized = true;
            if (ModeOf(product) != GoodsMode.Trade) SaveWeeklyStock();
        }
    }

    public void SetDetectedStock(int productId, int stock)
    {
        var product = _products.FirstOrDefault(entry => entry.Id == productId);
        if (product is null || stock < 0) return;
        product.CurrentStock = stock;
        product.StockInitialized = true;
        if (SelectedProduct?.Id == productId)
            StockInput.Text = stock.ToString(CultureInfo.CurrentCulture);
        if (ModeOf(product) != GoodsMode.Trade) SaveWeeklyStock();
    }

    public void SetDetectedBuyPrice(int productId, decimal price)
    {
        var product = _products.FirstOrDefault(entry => entry.Id == productId);
        if (product is null || price <= 0) return;
        product.CurrentBuyPriceIsEstimated = false;
        product.CurrentBuyPrice = price;
        product.CurrentBuyPriceText = FormatPrice(price);
        if (SelectedProduct?.Id == productId)
            BuyPriceInput.Text = product.CurrentBuyPriceText;
    }

    private void TransportPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && SelectedTransport is { IsOwned: false, IsPartner: false, IsAlpaca: false } transport)
        {
            transport.IsOwned = true;
            _availableTransportIds.Add(transport.Id);
            UpdateDependentTransportOwnership();
            SaveAvailableTransportIds();
        }
        RefreshTransportLoad();
        RefreshManualQuoteMetrics();
        RefreshDestinationTotals();
    }

    private void RefreshProducts()
    {
        if (PostPicker.SelectedItem is not CommercePost post) return;

        ResetGroupStockButton.Visibility = IsGroupMode || IsBarterMode ? Visibility.Visible : Visibility.Collapsed;
        ResetGroupStockButton.ToolTip = IsBarterMode
            ? "Reset Barter goods stock to the weekly maximum"
            : "Reset Group goods stock to the weekly maximum";
        var rating = int.TryParse(RatingInput.Text, out var parsedRating) ? Math.Max(1, parsedRating) : 1;
        SelectedTownIcon.Source = post.Icon;
        var products = _products.Where(product => product.PostId == post.Id && MatchesMode(product))
            .OrderBy(product => product.Id).ToList();
        foreach (var product in products)
        {
            product.IsLocked = IsRatingLocked(product, rating);
            product.Icon = product.IsLocked ? product.LockedIcon : product.ActiveIcon;
            product.RowBackground = product.IsLocked ? product.LockedRowBackground : product.IdleRowBackground;
            product.LockText = product.IsLocked ? $"{product.RequiredCreditLevel} or higher" : string.Empty;
            product.ReferenceRangeText = $"{product.MinPrice:N0}–{product.MaxPrice:N0}";
        }

        ProductList.ItemsSource = products;
        if (products.Count > 0)
        {
            ProductList.SelectedItem = products.FirstOrDefault(product => !product.IsLocked) ?? products[0];
        }
        RefreshDestinationTotals();
    }

    private void RefreshQuotes()
    {
        if (SelectedProduct is not { } product || QuotesList is null || GroupQuotesList is null || PostPicker.SelectedItem is not CommercePost post) return;
        var buyPrice = GetCurrentBuyPrice(product);
        foreach (var quote in _quotesByProduct[product.Id])
            if (quote.PostId != SmugglerDestinationId) quote.SetDefaultDestinationPrice(DefaultSalePrice(product, buyPrice));
        RefreshManualQuoteMetrics(product, buyPrice);
        var visibleQuotes = _quotesByProduct[product.Id]
            .Where(quote => quote.PostId != post.Id).ToList();
        var isGroupGood = product.CommerceParty;
        SelectedProductHeader.Margin = isGroupGood
            ? new Thickness(12, 0, 12, 0)
            : new Thickness(12, -18, 12, 0);
        SelectedProductHeader.VerticalAlignment = isGroupGood
            ? VerticalAlignment.Center
            : VerticalAlignment.Top;
        SelectedProductHeader.RenderTransform = isGroupGood
            ? new System.Windows.Media.TranslateTransform(0, -75)
            : System.Windows.Media.Transform.Identity;
        GroupQuoteRowsGrid.RenderTransform = isGroupGood
            ? new System.Windows.Media.TranslateTransform(0, -65)
            : System.Windows.Media.Transform.Identity;
        TradeMarketPriceCaption.Visibility = isGroupGood ? Visibility.Collapsed : Visibility.Visible;
        MarketPriceCaption.Visibility = isGroupGood ? Visibility.Visible : Visibility.Collapsed;
        ReadPriceListButton.Visibility = _plannerPreferences.ScreenReaderModeEnabled && !isGroupGood
            ? Visibility.Visible : Visibility.Collapsed;
        GroupReadPriceListButton.Visibility = _plannerPreferences.ScreenReaderModeEnabled && isGroupGood
            ? Visibility.Visible : Visibility.Collapsed;
        TradeQuoteRowsGrid.Visibility = isGroupGood ? Visibility.Collapsed : Visibility.Visible;
        GroupQuoteRowsGrid.Visibility = isGroupGood ? Visibility.Visible : Visibility.Collapsed;
        QuotesList.ItemsSource = product.CommerceParty ? null : visibleQuotes;
        GroupQuotesList.ItemsSource = product.CommerceParty ? visibleQuotes : null;
    }

    private void RefreshManualQuoteMetrics(GoodsEntry? product = null, decimal? currentBuyPrice = null)
    {
        product ??= SelectedProduct;
        if (product is null || PostPicker.SelectedItem is not CommercePost sourcePost ||
            !_quotesByProduct.TryGetValue(product.Id, out var quotes)) return;
        if (currentBuyPrice is null && product.IsBarter && product.CurrentBuyPrice > 0)
            currentBuyPrice = product.CurrentBuyPrice;
        if (currentBuyPrice is null && decimal.TryParse(product.CurrentBuyPriceText, NumberStyles.Number,
                CultureInfo.CurrentCulture, out var parsedBuyPrice) && parsedBuyPrice > 0)
            currentBuyPrice = parsedBuyPrice;

        foreach (var quote in quotes)
        {
            var perItemProfitLoss = currentBuyPrice > 0 && quote.DestinationPrice > 0
                ? EffectiveSalePrice(product, quote.DestinationPrice) - currentBuyPrice.Value
                : (decimal?)null;
            // Barter goods cost Gold-valued materials, so their per-item result is the full sale reward in Gold:
            // a Ducat sale pays equal raw Gold and Ducats, plus Mastery and other active bonuses, minus material Gold.
            // Trade and group goods can optionally show the same converted Gold reward instead of Ducat profit.
            var showGold = product.IsBarter || _plannerPreferences.ShowTradeProfitInGold;
            quote.PerItemProfitIsGold = showGold && perItemProfitLoss is not null;
            quote.PerItemProfitIsDucat = !showGold && perItemProfitLoss is not null;
            if (showGold && perItemProfitLoss is not null)
            {
                var reward = CommerceRewardModel.Calculate(
                    [new RewardLine(1, currentBuyPrice!.Value, EffectiveSalePrice(product, quote.DestinationPrice), product.Weight,
                        product.IsBarter ? 0m : PurchaseDiscountPercentFor(product.PostId))],
                    product.IsBarter, CurrentRewardModifiers(), perLoadLetterEffects: false);
                perItemProfitLoss = reward.TotalGold;
                quote.PerItemProfitLossToolTip = "Per item: " + RewardTooltip(reward) +
                    (ModeOf(product) == GoodsMode.Group
                        ? $"\nGroup destination bonus: listed {quote.DestinationPrice:N0} + {GroupDestinationBonus.Percent:0}% = sale {EffectiveSalePrice(product, quote.DestinationPrice):N0} (rounded down)."
                        : string.Empty) +
                    (product.IsBarter && product.CurrentBuyPriceIsEstimated ? "\nSome material values are not entered yet and are estimated." : string.Empty);
            }
            else
            {
                quote.PerItemProfitLossToolTip = product.IsBarter
                    ? "Per-item Gold: sale as raw Gold + Ducats × Gold per Ducat with bonuses, minus material Gold. Enter a sale price."
                    : showGold
                        ? "Per-item Gold: profit as raw Gold + Ducats × Gold per Ducat with bonuses. Enter a buy and sale price."
                        : "Sale price minus buy price, per item";
            }
            quote.PerItemProfitLossText = perItemProfitLoss is null
                ? "--"
                : perItemProfitLoss.Value >= 0
                    ? $"+{perItemProfitLoss.Value:N0}"
                    : perItemProfitLoss.Value.ToString("N0", CultureInfo.CurrentCulture);
            quote.PerItemProfitLossBrush = perItemProfitLoss switch
            {
                > 0 => System.Windows.Media.Brushes.Green,
                < 0 => System.Windows.Media.Brushes.Red,
                _ => System.Windows.Media.Brushes.Black
            };

            var minutes = GetManualQuoteMinutes(sourcePost.Id, quote.PostId, SelectedTransport?.Id ?? 0,
                out var timeSource);
            quote.EstimatedTimeText = minutes?.ToString("N1", CultureInfo.CurrentCulture) ?? "--";
            quote.EstimatedTimeToolTip = timeSource;
        }
    }

    private decimal? GetManualQuoteMinutes(int sourcePostId, int destinationPostId, int transportId, out string source)
    {
        source = "No route time for this trip.";
        if (destinationPostId == SmugglerDestinationId || transportId <= 0) return null;

        string PostName(int id) => _posts.FirstOrDefault(post => post.Id == id)?.Name ?? "Unknown post";
        var speedPercent = ModifierTotals(IsGroupMode).TransportSpeedPercent;
        var loadBufferSeconds = _plannerPreferences.PortalLoadBufferSeconds;
        string Leg(decimal minutes, int portals = 0) => $"{PostName(sourcePostId)} → {PostName(destinationPostId)}: {minutes:N1} min" +
            (speedPercent > 0 ? $" (+{speedPercent:0.#}% transport speed)" : string.Empty) +
            (portals > 0 && loadBufferSeconds > 0
                ? $"{Environment.NewLine}Includes {portals} portal{(portals == 1 ? "" : "s")} × {loadBufferSeconds} s load time"
                : string.Empty);
        var shipEstimate = _shipScheduleEstimator.Estimate(sourcePostId, destinationPostId, DateTimeOffset.UtcNow, transportId, PostName,
            _plannerPreferences.FerryBufferEnabled ? _plannerPreferences.FerryBufferSeconds : 0, speedPercent, loadBufferSeconds);
        if (shipEstimate is not null)
        {
            source = shipEstimate.Breakdown;
            return shipEstimate.Minutes;
        }

        // Client-map distances are the only land route source; pairs without one show no estimate.
        if (_regionalRouteHandcartMinutes.TryGetValue((sourcePostId, destinationPostId), out var handcartMinutes))
        {
            var sourceRegionId = _posts.FirstOrDefault(post => post.Id == sourcePostId)?.RegionId;
            var destinationRegionId = _posts.FirstOrDefault(post => post.Id == destinationPostId)?.RegionId;
            var routeRegionId = sourceRegionId == destinationRegionId ? sourceRegionId : null;
            var regionalMinutes = ApplySpeed(LegacyRouteEstimates.EstimatedMinutesFromHandcart(handcartMinutes, transportId, routeRegionId));
            if (regionalMinutes is not null)
            {
                // Load screens are not shortened by transport speed, so the buffer is added after the speed adjustment.
                var portals = _regionalRoutePortalCounts.GetValueOrDefault((sourcePostId, destinationPostId));
                regionalMinutes += RegionalRouteEstimates.PortalLoadMinutes(portals, loadBufferSeconds);
                source = Leg(regionalMinutes.Value, portals);
                return regionalMinutes;
            }
        }


        source = "No route time for this trip.";
        return null;

        decimal? ApplySpeed(decimal? minutes) => minutes is null ? null : TransportSpeed.ApplyToMinutes(minutes.Value, speedPercent);
    }

    private void DestinationPrice_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { IsKeyboardFocusWithin: true } priceBox && priceBox.DataContext is DestinationQuote quote)
            PersistDestinationPriceEdit(quote, priceBox.Text);
        RefreshManualQuoteMetrics();
        RefreshDestinationTotals();
    }

    private void DestinationPrice_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox priceBox) priceBox.Tag = priceBox.Text;
    }

    private void DestinationPrice_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { DataContext: DestinationQuote quote, Tag: string originalText } priceBox) return;
        priceBox.Tag = null;
        if (!string.Equals(originalText, priceBox.Text, StringComparison.Ordinal))
            PersistDestinationPriceEdit(quote, priceBox.Text);
    }

    private void PersistDestinationPriceEdit(DestinationQuote quote, string text)
    {
        quote.IsManualPrice = true;
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var price) && price > 0)
            SaveManualDestinationPrice(quote.ProductId, quote.PostId, price);
        else
            RemoveManualDestinationPrice(quote.ProductId, quote.PostId);
        SavePlannerPreferences();
    }

    private sealed record PriceListScanRead(GoodsEntry Product, CommercePost SourcePost,
        IReadOnlyList<DestinationQuote> ProductQuotes, OcrSourcePrice? SourcePrice,
        IReadOnlyList<PriceListOcrCandidate> Candidates, float MeanConfidence, bool CanAutoAccept)
    {
        public string DebugDetails { get; init; } = string.Empty;
    }

    private string _lastScanFailureDetails = string.Empty;

    private async void ReadPriceListButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_plannerPreferences.ScreenReaderModeEnabled)
        {
            SetStatus("Enable Screen reader mode first.", true);
            return;
        }

        SettingsPopup.IsOpen = false;
        var region = _plannerPreferences.SaveLastScanRegion ? _lastSuccessfulScanRegion : null;
        while (true)
        {
            var usingSavedRegion = region is not null;
            var (screenImage, capturedRegion, captureError) = await CapturePriceListImageAsync(region);
            if (screenImage is null || capturedRegion is null)
            {
                if (usingSavedRegion)
                {
                    region = null;
                    continue;
                }
                SetStatus(captureError ?? "Price-list scan cancelled.", captureError is not null);
                return;
            }

            SetStatus(usingSavedRegion ? "Reading the saved scan region..." : "Reading the selected price list...");
            _lastScanFailureDetails = string.Empty;
            var (read, readError) = await ReadPriceListImageAsync(screenImage);
            if (read is null)
            {
                if (_plannerPreferences.ScanDebugMode)
                    ScanDebugLog.Save(screenImage, "FAILED", $"{readError}{Environment.NewLine}{_lastScanFailureDetails}", "Failed");
                if (usingSavedRegion)
                {
                    region = null;
                    continue;
                }
                SetStatus(readError ?? "Price-list OCR failed.", true);
                return;
            }

            RememberSuccessfulScanRegion(capturedRegion.Value);
            if (_plannerPreferences.ScanDebugMode && read.CanAutoAccept)
                ScanDebugLog.Save(screenImage, read.Product.Name, read.DebugDetails);
            ApplyPriceListScan(read);
            return;
        }
    }

    private async Task<(byte[]? Image, System.Drawing.Rectangle? Region, string? Error)> CapturePriceListImageAsync(
        System.Drawing.Rectangle? savedRegion)
    {
        var wasVisible = IsVisible;
        try
        {
            Hide();
            System.Drawing.Rectangle region;
            if (savedRegion is { } saved)
            {
                await Task.Delay(200);
                region = saved;
            }
            else
            {
                var selector = new ScreenRegionSelectorWindow();
                if (selector.ShowDialog() != true || selector.SelectedRegion is not { } selected)
                    return (null, null, null);
                region = selected;
            }
            return (CaptureScreenRegion(region), region, null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or ExternalException)
        {
            return (null, null, $"Could not capture that screen region: {exception.Message}");
        }
        finally
        {
            if (wasVisible)
            {
                Show();
                Activate();
            }
        }
    }

    private async Task<(PriceListScanRead? Read, string? Error)> ReadPriceListImageAsync(byte[] screenImage)
    {
        try
        {
            var tessdataPath = Path.Combine(AppContext.BaseDirectory, "Data", "Ocr", "tessdata_best");
            var anchorPath = Path.Combine(AppContext.BaseDirectory, "Data", "Ocr", "PriceListAnchor.png");
            var allDestinations = _posts.Where(item => item.CanSell)
                .Select(item => (item.Id, item.Name))
                .Append((SmugglerDestinationId, "Smuggler"))
                .ToArray();
            var result = await Task.Run(() => PriceListOcrService.RecognizeAnchoredPriceList(
                screenImage, File.ReadAllBytes(anchorPath), tessdataPath, allDestinations));
            // Barter goods share names with trade goods, so the detected table layout selects the goods
            // family and the active page only breaks ties between Trade and Group within it.
            var layoutGoods = _products.Where(item => item.IsBarter == result.IsSaleOnlyTable).ToArray();
            var visibleIds = result.TownRows.Select(row => row.PostId).ToHashSet();
            var recognizedName = $"{result.ProductNameText}\n{result.ProductMetadataText}";
            OcrProductIdentity? FindIn(IEnumerable<GoodsEntry> items) => PriceListOcrParser.FindUniqueProduct(recognizedName,
                items.Select(item => new OcrProductIdentity(item.Id, item.PostId, item.GameName ?? item.Name)), visibleIds);
            // Inactive rotating barter alternatives stay searchable so a scan can switch the active offer.
            var productIdentity = FindIn(layoutGoods.Where(item => ModeOf(item) == _goodsMode)) ?? FindIn(layoutGoods);
            if (productIdentity is null)
            {
                if (_plannerPreferences.ScanDebugMode)
                    _lastScanFailureDetails = ScanDebugLog.Describe(result);
                return (null, "Could not uniquely identify the good. Include its full bracketed name in the region.");
            }

            var product = _products.First(item => item.Id == productIdentity.Id);
            var sourcePost = _posts.FirstOrDefault(item => item.Id == product.PostId);
            if (sourcePost is null || !_quotesByProduct.TryGetValue(product.Id, out var productQuotes))
                return (null, "The identified good has no matching source post or quote list.");

            var townRows = PriceListOcrParser.FilterTownRowsForDestinations(result.TownRows,
                productQuotes.Select(quote => quote.PostId), sourcePost.Id);
            if (product.IsBarter)
            {
                // Barter lists have no profit column and the cost comes from material values, so only sale prices are ingested.
                var barterCandidates = PriceListOcrParser.ParseSaleOnlyCells(townRows, result.PriceCells);
                if (barterCandidates.Count == 0)
                    return (null, "No town prices were found in the Ducat column. Include the bracket corner and full price table.");
                var destinationWeights = townRows
                    .Where(row => _postPairWeights.ContainsKey((sourcePost.Id, row.PostId)))
                    .ToDictionary(row => row.PostId, row => _postPairWeights[(sourcePost.Id, row.PostId)]);
                barterCandidates = PriceListOcrParser.ApplyBarterValueCheck(barterCandidates, destinationWeights,
                    product.MinPrice, product.MaxPrice);
                return (new PriceListScanRead(product, sourcePost, productQuotes, null, barterCandidates,
                    result.MeanConfidence,
                    PriceListOcrParser.CanAutoAccept(barterCandidates, townRows.Count, false))
                    { DebugDetails = DescribeForDebug(result, product, sourcePost, null, barterCandidates) }, null);
            }

            var sourcePrice = PriceListOcrParser.ParseSourcePrice(
                result.ProductMetadataText, result.MeanConfidence);
            // Purchase discounts can put a real buy price below the catalog minimum, so the lower bound is widened by them.
            var sourcePriceSuspect = false;
            if (sourcePrice is not null && !PurchasePriceRange.IsPlausibleBuyPrice(sourcePrice.Price,
                    product.MinPrice, product.MaxPrice, PurchaseDiscountPercentFor(sourcePost.Id)))
            {
                sourcePriceSuspect = true;
                var lowest = PurchasePriceRange.LowestPlausible(product.MinPrice, PurchaseDiscountPercentFor(sourcePost.Id));
                sourcePrice = sourcePrice with
                {
                    SourceText = $"{sourcePrice.SourceText} (outside the expected {lowest:N0}-{product.MaxPrice:N0} after discounts; check it)"
                };
            }

            var hasCurrentBuyPrice = decimal.TryParse(product.CurrentBuyPriceText, NumberStyles.Number,
                CultureInfo.CurrentCulture, out var buyPrice) && buyPrice > 0 && !product.CurrentBuyPriceIsEstimated;
            if (sourcePrice is null && !hasCurrentBuyPrice)
                buyPrice = ((decimal)product.MinPrice + product.MaxPrice) / 2m;
            else if (sourcePrice is not null)
                buyPrice = sourcePrice.Price;
            var buyPriceIsEstimated = sourcePrice is null && !hasCurrentBuyPrice;
            var candidates = PriceListOcrParser.ParsePriceCells(townRows, result.PriceCells,
                buyPrice, buyPriceIsEstimated);
            if (candidates.Count == 0)
                return (null, "No town prices were found in the Ducat column. Include the bracketed name and full price table.");

            return (new PriceListScanRead(product, sourcePost, productQuotes, sourcePrice, candidates,
                result.MeanConfidence,
                !sourcePriceSuspect && PriceListOcrParser.CanAutoAccept(candidates, townRows.Count, buyPriceIsEstimated))
                { DebugDetails = DescribeForDebug(result, product, sourcePost, sourcePrice, candidates) }, null);
        }
        catch (Exception exception)
        {
            return (null, $"Price-list OCR failed: {exception.Message}");
        }
    }

    private string DescribeForDebug(AnchoredPriceListOcrResult result, GoodsEntry product, CommercePost sourcePost,
        OcrSourcePrice? sourcePrice, IReadOnlyList<PriceListOcrCandidate> candidates) =>
        _plannerPreferences.ScanDebugMode
            ? ScanDebugLog.DescribeCandidates(product.Name, sourcePost.Name, sourcePrice, candidates)
                + Environment.NewLine + ScanDebugLog.Describe(result)
            : string.Empty;

    private void ApplyPriceListScan(PriceListScanRead read)
    {
        try
        {
            var (product, sourcePost, productQuotes) = (read.Product, read.SourcePost, read.ProductQuotes);
            decimal? acceptedSourcePrice;
            IReadOnlyList<PriceListOcrCandidate> acceptedCandidates;
            if (read.CanAutoAccept)
            {
                acceptedSourcePrice = read.SourcePrice?.Price;
                acceptedCandidates = read.Candidates;
            }
            else
            {
                var review = new OcrPriceReviewWindow(product.Name, sourcePost.Name, read.Candidates,
                    read.MeanConfidence, read.SourcePrice, showSourcePrice: !product.IsBarter) { Owner = this };
                if (review.ShowDialog() != true) return;
                acceptedSourcePrice = review.AcceptedSourcePrice;
                acceptedCandidates = review.AcceptedCandidates;
            }

            var rotationChanged = ActivateRotation(product);
            RecordRotationSighting(product, BarterSightingSource.Scan);
            if (_goodsMode != ModeOf(product))
                SetGoodsMode(ModeOf(product));
            if (PostPicker.SelectedValue is not int selectedPostId || selectedPostId != sourcePost.Id)
                PostPicker.SelectedValue = sourcePost.Id;
            else if (rotationChanged)
                RefreshProducts();
            ProductList.SelectedItem = product;

            if (!product.IsBarter && acceptedSourcePrice is { } sourcePrice)
            {
                SetDetectedBuyPrice(product.Id, sourcePrice);
                _plannerPreferences.ManualBuyPricesByProductId[product.Id] = sourcePrice;
                foreach (var quote in productQuotes)
                    if (quote.PostId != SmugglerDestinationId)
                        quote.SetDefaultDestinationPrice(sourcePrice + 10m);
            }

            var applied = 0;
            foreach (var candidate in acceptedCandidates)
            {
                var quote = productQuotes.FirstOrDefault(item => item.PostId == candidate.PostId);
                if (quote is null || candidate.Price <= 0) continue;
                quote.DestinationPrice = candidate.Price;
                SaveManualDestinationPrice(product.Id, candidate.PostId, candidate.Price);
                applied++;
            }

            RefreshQuotes();
            RefreshManualQuoteMetrics(product);
            RefreshDestinationTotals();
            SavePlannerPreferences();
            var sourcePriceStatus = acceptedSourcePrice is null || product.IsBarter ? string.Empty : " and source price";
            SetStatus(read.CanAutoAccept
                ? $"Auto-applied {applied} cross-checked town prices{sourcePriceStatus} to {product.Name}."
                : $"Applied {applied} confirmed town prices{sourcePriceStatus} to {product.Name}.");
        }
        catch (Exception exception)
        {
            SetStatus($"Price-list OCR failed: {exception.Message}", true);
        }
    }

    private void RememberSuccessfulScanRegion(System.Drawing.Rectangle region)
    {
        _lastSuccessfulScanRegion = region;
        if (!_plannerPreferences.SaveLastScanRegion) return;
        _plannerPreferences.LastScanRegion = ScanRegion.From(region);
        SavePlannerPreferences();
    }

    private void SaveLastScanRegion_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var enabled = SaveLastScanRegionCheckBox.IsChecked == true;
        if (_plannerPreferences.SaveLastScanRegion == enabled) return;
        _plannerPreferences.SaveLastScanRegion = enabled;
        _plannerPreferences.LastScanRegion = enabled && _lastSuccessfulScanRegion is { } region
            ? ScanRegion.From(region) : null;
        if (!enabled) _lastSuccessfulScanRegion = null;
        SavePlannerPreferences();
    }

    private void FerryBuffer_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var enabled = FerryBufferCheckBox.IsChecked == true;
        FerryBufferSecondsInput.IsEnabled = enabled;
        if (!enabled) RestoreFerryBufferText();
        if (_plannerPreferences.FerryBufferEnabled == enabled) return;
        _plannerPreferences.FerryBufferEnabled = enabled;
        SavePlannerPreferences();
        RefreshManualQuoteMetrics();
    }

    // Only a valid whole number is saved; a malformed entry is flagged and leaves the saved buffer unchanged.
    private void FerryBufferSeconds_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        if (!ShipScheduleEstimator.TryParseBufferSeconds(FerryBufferSecondsInput.Text, out var seconds))
        {
            FerryBufferSecondsPaper.BorderBrush = System.Windows.Media.Brushes.Red;
            FerryBufferSecondsInput.ToolTip = "Enter whole seconds from 0 to 300";
            return;
        }
        FerryBufferSecondsPaper.ClearValue(Border.BorderBrushProperty);
        FerryBufferSecondsInput.ToolTip = "Whole seconds, 0-300";
        if (_plannerPreferences.FerryBufferSeconds == seconds) return;
        _plannerPreferences.FerryBufferSeconds = seconds;
        SavePlannerPreferences();
        RefreshManualQuoteMetrics();
    }

    private void PortalLoadBufferSeconds_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing) return;
        if (!ShipScheduleEstimator.TryParseBufferSeconds(PortalLoadBufferSecondsInput.Text, out var seconds))
        {
            PortalLoadBufferSecondsPaper.BorderBrush = System.Windows.Media.Brushes.Red;
            PortalLoadBufferSecondsInput.ToolTip = "Enter whole seconds from 0 to 300";
            return;
        }
        PortalLoadBufferSecondsPaper.ClearValue(Border.BorderBrushProperty);
        PortalLoadBufferSecondsInput.ToolTip = "Whole seconds per portal, 0-300";
        if (_plannerPreferences.PortalLoadBufferSeconds == seconds) return;
        _plannerPreferences.PortalLoadBufferSeconds = seconds;
        SavePlannerPreferences();
        RefreshManualQuoteMetrics();
    }

    private void PortalLoadBufferSeconds_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (ShipScheduleEstimator.TryParseBufferSeconds(PortalLoadBufferSecondsInput.Text, out var seconds) &&
            seconds == _plannerPreferences.PortalLoadBufferSeconds) return;
        PortalLoadBufferSecondsInput.Text = _plannerPreferences.PortalLoadBufferSeconds.ToString(CultureInfo.InvariantCulture);
    }

    // A click into an unfocused box or any double-click selects the whole value.
    private void SettingsSeconds_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not TextBox box || (box.IsKeyboardFocusWithin && e.ClickCount < 2)) return;
        box.Focus();
        box.SelectAll();
        e.Handled = true;
    }

    private void SettingsSeconds_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) box.SelectAll();
    }

    private void FerryBufferSeconds_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) =>
        RestoreFerryBufferText();

    private void FerryBufferSecondsInput_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        FerryBufferSecondsPaper.Opacity = FerryBufferSecondsInput.IsEnabled ? 1 : 0.45;

    private static System.Windows.Media.Brush CreatePaperTileBrush(string relativePath)
    {
        var image = LoadImage(relativePath);
        if (image is null) return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xE8, 0xD2));
        var brush = new System.Windows.Media.ImageBrush(image)
        {
            TileMode = System.Windows.Media.TileMode.Tile,
            Stretch = System.Windows.Media.Stretch.None,
            ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, image.PixelWidth, image.PixelHeight),
            AlignmentX = System.Windows.Media.AlignmentX.Left,
            AlignmentY = System.Windows.Media.AlignmentY.Top
        };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(brush, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        return brush;
    }

    private void RestoreFerryBufferText()
    {
        if (ShipScheduleEstimator.TryParseBufferSeconds(FerryBufferSecondsInput.Text, out var seconds) &&
            seconds == _plannerPreferences.FerryBufferSeconds) return;
        FerryBufferSecondsInput.Text = _plannerPreferences.FerryBufferSeconds.ToString(CultureInfo.InvariantCulture);
    }
    private static byte[] CaptureScreenRegion(System.Drawing.Rectangle region)
    {
        if (region.Width <= 0 || region.Height <= 0)
            throw new ArgumentException("The selected screen region is empty.", nameof(region));
        using var bitmap = new System.Drawing.Bitmap(region.Width, region.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(region.Left, region.Top, 0, 0, region.Size,
                System.Drawing.CopyPixelOperation.SourceCopy);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    private void SaveManualDestinationPrice(int productId, int postId, decimal price)
    {
        if (!_plannerPreferences.ManualDestinationPricesByProductId.TryGetValue(productId, out var prices))
        {
            prices = [];
            _plannerPreferences.ManualDestinationPricesByProductId[productId] = prices;
        }
        prices[postId] = price;
    }

    private void RemoveManualDestinationPrice(int productId, int postId)
    {
        if (!_plannerPreferences.ManualDestinationPricesByProductId.TryGetValue(productId, out var prices)) return;
        prices.Remove(postId);
        if (prices.Count == 0) _plannerPreferences.ManualDestinationPricesByProductId.Remove(productId);
    }

    private void AddToLoad_Click(object sender, RoutedEventArgs e)
    {
        if (TradeMiddleTabs.SelectedIndex == 1)
        {
            LoadSelectedAutoPlan_Click(sender, e);
            return;
        }

        if (SelectedProduct is not { } product || PostPicker.SelectedItem is not CommercePost sourcePost)
        {
            SetStatus("Select a good before adding it to the transport.");
            return;
        }

        if (product.IsLocked)
        {
            SetStatus($"{product.Name} requires Merchant Rating {product.RequiredCreditLevel} or higher.");
            return;
        }

        var currentBuyPrice = GetCurrentBuyPrice(product);

        if (!int.TryParse(StockInput.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var currentStock) || currentStock <= 0)
        {
            SetStatus($"{product.Name}: enter a current stock quantity greater than zero.", true);
            StockInput.Focus();
            StockInput.SelectAll();
            return;
        }

        if (!TryRead(DucatsInput, out var ducats) || ducats < 0)
        {
            SetStatus("Enter a valid, non-negative Ducat balance before adding goods.", true);
            DucatsInput.Focus();
            DucatsInput.SelectAll();
            return;
        }

        if (SelectedTransport is not { } transport)
        {
            SetStatus("Select a transport before adding goods.", true);
            return;
        }

        if (product.Weight <= 0 || product.MaxBundle <= 0)
        {
            SetStatus("This good has invalid weight or bundle data and cannot be loaded.", true);
            return;
        }

        if (!ResolveOtherModeCargo(_goodsMode)) return;
        if (!TryRead(DucatsInput, out ducats) || ducats < 0)
        {
            SetStatus("Enter a valid, non-negative Ducat balance before adding goods.", true);
            DucatsInput.Focus();
            DucatsInput.SelectAll();
            return;
        }

        var cargo = ActiveCargo;
        if (product.IsBarter && BarterPairRouteFor(cargo.Values.Select(line => line.Product.PostId).Append(product.PostId)) is null)
        {
            SetStatus("Barter cargo can only combine goods from one outpost or one pair route (Karu + Oasis, Calida + Pera). Sell or clear the current load first.", true);
            return;
        }
        var alreadyLoaded = cargo.TryGetValue(product.Id, out var cargoLine) ? cargoLine.Quantity : 0;
        var remainingStock = Math.Max(0, currentStock);
        if (product.IsWeeklyLimited)
            remainingStock = Math.Min(remainingStock, Math.Max(0, product.SourceCount - alreadyLoaded));
        // Barter goods are paid with exchange materials, so the Ducat balance does not cap them.
        var maximumAdd = product.IsBarter
            ? remainingStock
            : decimal.ToInt32(Math.Min(remainingStock, decimal.Floor(ducats / currentBuyPrice)));
        var requestedQuantity = int.TryParse(QuantityInput.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var requested) ? Math.Max(0, requested) : 0;
        if (requestedQuantity > 0) maximumAdd = Math.Min(maximumAdd, requestedQuantity);

        var quantityToAdd = maximumAdd;
        while (quantityToAdd > 0 && !FitsTransport(transport, product, quantityToAdd)) quantityToAdd--;
        if (quantityToAdd == 0)
        {
            SetStatus("No units fit. Check remaining stock, Ducats, transport weight, and free slots.", true);
            return;
        }

        product.CurrentBuyPrice = currentBuyPrice;
        product.CurrentStock = currentStock - quantityToAdd;
        if (cargoLine is null)
        {
            cargoLine = new CargoLine { Product = product, Quantity = 0, UnitBuyPrice = currentBuyPrice };
            cargo[product.Id] = cargoLine;
        }
        cargoLine.UnitBuyPrice = (cargoLine.Quantity * cargoLine.UnitBuyPrice + quantityToAdd * currentBuyPrice) /
            (cargoLine.Quantity + quantityToAdd);
        cargoLine.Quantity += quantityToAdd;
        _plannedRouteDestinationByMode.Remove(_goodsMode);
        if (ModeOf(product) != GoodsMode.Trade) SaveWeeklyStock();
        if (!product.IsBarter)
            DucatsInput.Text = FormatDucats(ducats - currentBuyPrice * quantityToAdd);
        if (SelectedProduct?.Id == product.Id)
            StockInput.Text = product.CurrentStock.ToString(CultureInfo.CurrentCulture);
        RefreshTransportLoad();
        RefreshDestinationTotals();
        SetStatus($"Added {quantityToAdd:N0} {product.Name} to the transport.");
    }

    private void TransportSlot_PreviewMouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(TransportSlots, source) is not FrameworkElement { DataContext: TransportSlotDisplay slot } ||
            slot.ProductId <= 0 || slot.Quantity <= 0)
            return;

        RemoveTransportGoods(slot.ProductId, slot.Quantity);
        e.Handled = true;
    }

    private void TransportListItem_PreviewMouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(TransportListItems, source) is not FrameworkElement { DataContext: TransportListDisplay item } ||
            !ActiveCargo.TryGetValue(item.ProductId, out var line) || line.Quantity <= 0)
            return;

        var partialStack = line.Quantity % line.Product.MaxBundle;
        RemoveTransportGoods(item.ProductId, partialStack > 0 ? partialStack : line.Product.MaxBundle);
        e.Handled = true;
    }

    private void TransportViewToggle_Click(object sender, RoutedEventArgs e)
    {
        _plannerPreferences.TransportListView = !_plannerPreferences.TransportListView;
        ApplyTransportView();
        SavePlannerPreferences();
    }

    private void ApplyTransportView()
    {
        var listView = _plannerPreferences.TransportListView;
        TransportSlots.Visibility = listView ? Visibility.Collapsed : Visibility.Visible;
        TransportListView.Visibility = listView ? Visibility.Visible : Visibility.Collapsed;
        TransportViewToggleButton.Tag = listView ? "List" : "Pocket";
        TransportViewToggleButton.ToolTip = listView ? "Switch held goods to pocket view" : "Switch held goods to list view";
    }

    private void RemoveTransportGoods(int productId, int quantity)
    {
        var cargo = ActiveCargo;
        if (!cargo.TryGetValue(productId, out var line)) return;
        var removedQuantity = Math.Min(line.Quantity, quantity);
        line.Quantity -= removedQuantity;
        if (!line.Product.IsBarter) RefundDucats(removedQuantity * line.UnitBuyPrice);
        line.Product.CurrentStock = Math.Min(line.Product.IsWeeklyLimited ? Math.Min(line.Product.MaxStock, line.Product.SourceCount) : line.Product.MaxStock,
            line.Product.CurrentStock + removedQuantity);
        if (SelectedProduct?.Id == productId)
            StockInput.Text = line.Product.CurrentStock.ToString(CultureInfo.CurrentCulture);
        if (line.Quantity == 0) cargo.Remove(productId);
        if (ModeOf(line.Product) != GoodsMode.Trade) SaveWeeklyStock();
        RefreshTransportLoad();
        RefreshDestinationTotals();
        SetStatus($"Removed {removedQuantity:N0} {line.Product.Name} from the transport.");
    }

    private void SellLoad_Click(object sender, RoutedEventArgs e)
    {
        var cargo = ActiveCargo;
        if (cargo.Count == 0)
        {
            SetStatus("There are no goods in this transport to sell.", true);
            return;
        }

        if (_selectedSaleDestinationId is not int destinationId)
        {
            SetStatus("Select a destination town before selling the load.", true);
            return;
        }

        if (!TrySellCargo(cargo, destinationId, out var saleProceeds, out var soldGoods, out var destinationName, out var error))
        {
            SetStatus(error, true);
            return;
        }
        RefreshTransportLoad();
        RefreshDestinationTotals();
        SetStatus($"Sold {soldGoods:N0} goods at {destinationName}; gained {saleProceeds:N0} Ducats. Source stock was not restored.");
    }

    private IEnumerable<GoodsMode> OtherModes(GoodsMode targetMode) =>
        Enum.GetValues<GoodsMode>().Where(mode => mode != targetMode);

    private bool HasOtherModeCargo(GoodsMode targetMode) =>
        OtherModes(targetMode).Any(mode => CargoFor(mode).Count > 0);

    private bool ResolveOtherModeCargo(GoodsMode targetMode)
    {
        foreach (var mode in OtherModes(targetMode))
            if (!ResolveModeCargo(mode)) return false;
        return true;
    }

    private bool ResolveModeCargo(GoodsMode otherMode)
    {
        var otherCargo = CargoFor(otherMode);
        if (otherCargo.Count == 0) return true;
        if (!TryRead(DucatsInput, out var currentDucats) || currentDucats < 0)
        {
            SetStatus("Enter a valid Ducat balance before resolving the other market's cargo.", true);
            return false;
        }

        var destinations = new List<CargoDispositionDestination>();
        foreach (var post in _posts.Where(post => post.CanSell))
        {
            var priceLines = new List<CargoDispositionPriceLine>();
            var canSellAll = true;
            foreach (var line in otherCargo.Values)
            {
                var quote = _quotesByProduct[line.Product.Id]
                    .FirstOrDefault(item => item.PostId == post.Id);
                if (quote is null || AutoQuoteSalePrice(line.Product, post.Id) is not decimal salePrice)
                {
                    canSellAll = false;
                    break;
                }

                priceLines.Add(new CargoDispositionPriceLine(
                    line.Product.Name,
                    line.Quantity,
                    salePrice,
                    quote.IsManualPrice));
            }
            if (canSellAll)
                destinations.Add(new CargoDispositionDestination(post.Id, post.Name, priceLines));
        }

        var modeName = ModeName(otherMode);
        var loadedQuantity = otherCargo.Values.Sum(line => line.Quantity);
        var dialog = new CargoDispositionWindow(modeName, destinations, otherCargo.Count, loadedQuantity)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result)
        {
            SetStatus($"New load cancelled; {modeName} cargo was left unchanged.");
            return false;
        }

        if (result.Action == CargoDispositionAction.Clear)
        {
            ClearCargoContents(otherCargo);
            SetStatus($"Cleared {modeName} cargo and restored its source stock and purchase cost.");
        }
        else
        {
            if (result.DestinationId is not int destinationId)
            {
                SetStatus("Select a destination before selling cargo.", true);
                return false;
            }
            if (!TrySellCargo(otherCargo, destinationId, out var saleProceeds, out var soldGoods, out var destinationName, out var error))
            {
                SetStatus(error, true);
                return false;
            }
            SetStatus($"Sold {soldGoods:N0} {modeName} goods at {destinationName} for {saleProceeds:N0} Ducats; source stock was not restored.");
        }

        RefreshTransportLoad();
        RefreshDestinationTotals();
        return true;
    }

    private bool TrySellCargo(
        Dictionary<int, CargoLine> cargo,
        int destinationId,
        out decimal saleProceeds,
        out int soldGoods,
        out string destinationName,
        out string error)
    {
        saleProceeds = 0;
        soldGoods = 0;
        destinationName = _posts.FirstOrDefault(post => post.Id == destinationId)?.Name ?? "destination";
        error = string.Empty;
        var saleLines = new List<ProfitLine>();
        var salePrices = new Dictionary<int, decimal>();
        foreach (var line in cargo.Values)
        {
            if (AutoQuoteSalePrice(line.Product, destinationId) is not decimal salePrice)
            {
                error = $"Enter a positive sale price for {line.Product.Name} at {destinationName} before selling.";
                return false;
            }
            salePrices[line.Product.Id] = salePrice;
            saleLines.Add(new ProfitLine(line.Quantity, line.UnitBuyPrice, salePrice));
        }

        if (!TryRead(DucatsInput, out var currentDucats) || currentDucats < 0)
        {
            error = "Enter a valid Ducat balance before selling the load.";
            return false;
        }

        saleProceeds = ProfitCalculator.Calculate(saleLines).GrossSale;
        soldGoods = cargo.Values.Sum(line => line.Quantity);
        RecordTradeHistory(cargo, salePrices, destinationName, saleProceeds);
        var weeklyStockSold = cargo.Values.Any(line => ModeOf(line.Product) != GoodsMode.Trade);
        cargo.Clear();
        if (weeklyStockSold) SaveWeeklyStock();
        DucatsInput.Text = FormatDucats(currentDucats + saleProceeds);
        return true;
    }

    private static string TradeHistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MabiCommerceNewLife", "trade-history.json");

    public TradeHistory TradeHistory => _tradeHistory ??= TradeHistory.Load(TradeHistoryPath);

    // Rewards use the sold cargo's own mode, so a market-switch sale still logs its Group or Barter bonuses and letter.
    private void RecordTradeHistory(Dictionary<int, CargoLine> cargo, IReadOnlyDictionary<int, decimal> salePrices,
        string destinationName, decimal saleProceeds)
    {
        if (cargo.Count == 0) return;
        var mode = ModeOf(cargo.Values.First().Product);
        var isBarter = mode == GoodsMode.Barter;
        var rewardLines = cargo.Values.Select(line => new RewardLine(line.Quantity, line.UnitBuyPrice, salePrices[line.Product.Id],
            line.Product.Weight, line.Product.IsBarter ? 0m : PurchaseDiscountPercentFor(line.Product.PostId))).ToList();
        var letter = _plannerPreferences.ActiveGuaranteeLetterByMode.TryGetValue(mode.ToString(), out var letterName)
            && Enum.TryParse<GuaranteeLetterKind>(letterName, out var kind) ? kind : GuaranteeLetterKind.None;
        var totals = ModifierTotals(mode == GoodsMode.Group);
        var modifiers = new RewardModifiers(_plannerPreferences.CommerceMasteryRank, letter, GoldPerDucat, -1,
            GetGuaranteeLetterMarketValue(letter) ?? 0, totals.DucatPercent, totals.ProfitPercent, totals.MerchantRatingPercent);
        var reward = CommerceRewardModel.Calculate(rewardLines, isBarter, modifiers);
        var goods = cargo.Values.Select(line => new TradeHistoryGood(line.Product.Name, line.Quantity)).ToList();
        TradeHistory.Add(new TradeHistoryEntry(DateTime.Now, ModeName(mode), destinationName, goods, saleProceeds,
            reward.RawGold, reward.DucatGain, reward.TotalGold, reward.GoldPerDucat,
            reward.LetterApplied ? reward.Letter?.Name : null));
        SaveTradeHistory();
        _tradeHistoryWindow?.Refresh();
    }

    public void SaveTradeHistory()
    {
        try
        {
            TradeHistory.Save(TradeHistoryPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Trade history could not be saved: {exception.Message}", true);
        }
    }

    private void TradeHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tradeHistoryWindow is { IsLoaded: true } existing)
        {
            existing.Refresh();
            existing.Activate();
            return;
        }
        var window = new TradeHistoryWindow(this);
        var workArea = SystemParameters.WorkArea;
        var left = Left + ActualWidth + 4;
        if (left + window.Width > workArea.Right) left = Left - window.Width - 4;
        window.Left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - window.Width));
        window.Top = Math.Max(workArea.Top, Math.Min(Top, workArea.Bottom - window.Height));
        window.Topmost = Topmost;
        TopmostGuard.Watch(window);
        window.Closed += (_, _) => _tradeHistoryWindow = null;
        _tradeHistoryWindow = window;
        window.Show();
    }

    public void ApplyDetectedSale(int productId, int soldQuantity)
    {
        if (soldQuantity <= 0 || !ActiveCargo.TryGetValue(productId, out var line)) return;

        line.Quantity -= Math.Min(line.Quantity, soldQuantity);
        if (line.Quantity <= 0) ActiveCargo.Remove(productId);
        if (ModeOf(line.Product) != GoodsMode.Trade) SaveWeeklyStock();
        RefreshTransportLoad();
        RefreshDestinationTotals();
        SetStatus($"Detected sale of {soldQuantity:N0} {line.Product.Name}.");
    }

    private void ClearLoad_Click(object sender, RoutedEventArgs e)
    {
        var selectedProduct = SelectedProduct;
        ClearCargoContents(ActiveCargo);
        if (selectedProduct is not null)
            StockInput.Text = selectedProduct.CurrentStock.ToString(CultureInfo.CurrentCulture);
        RefreshTransportLoad();
        RefreshDestinationTotals();
        SetStatus("Transport load cleared.");
    }

    private void ClearCargoContents(Dictionary<int, CargoLine> cargo)
    {
        var cashToRefund = cargo.Values.Where(line => !line.Product.IsBarter).Sum(line => line.Quantity * line.UnitBuyPrice);
        foreach (var line in cargo.Values)
        {
            var stockLimit = line.Product.IsWeeklyLimited
                ? Math.Min(line.Product.MaxStock, line.Product.SourceCount)
                : line.Product.MaxStock;
            line.Product.CurrentStock = Math.Min(stockLimit, line.Product.CurrentStock + line.Quantity);
        }
        var weeklyStockChanged = cargo.Values.Any(line => ModeOf(line.Product) != GoodsMode.Trade);
        cargo.Clear();
        if (weeklyStockChanged) SaveWeeklyStock();
        RefundDucats(cashToRefund);
    }

    private void RefundDucats(decimal amount)
    {
        if (amount <= 0) return;
        var currentBalance = TryRead(DucatsInput, out var balance) && balance >= 0
            ? balance
            : _plannerPreferences.CurrentDucats;
        DucatsInput.Text = FormatDucats(currentBalance + amount);
    }

    private bool FitsTransport(CommerceTransport transport, GoodsEntry product, int quantityToAdd)
    {
        var cargo = ActiveCargo;
        var totalWeight = cargo.Values.Sum(line => line.Quantity * line.Product.Weight) + quantityToAdd * product.Weight;
        if (totalWeight > CapacityWeight(transport)) return false;

        var requiredSlots = cargo.Values.Sum(line => decimal.ToInt32(decimal.Ceiling((decimal)line.Quantity / line.Product.MaxBundle)));
        var currentQuantity = cargo.TryGetValue(product.Id, out var line) ? line.Quantity : 0;
        var priorSlots = cargo.ContainsKey(product.Id)
            ? decimal.ToInt32(decimal.Ceiling((decimal)currentQuantity / product.MaxBundle))
            : 0;
        var updatedProductSlots = decimal.ToInt32(decimal.Ceiling((decimal)(currentQuantity + quantityToAdd) / product.MaxBundle));
        return requiredSlots - priorSlots + updatedProductSlots <= CapacitySlots(transport);
    }

    private void UpdateShoppingListButton()
    {
        if (ShoppingListButton is null) return;
        var barter = IsBarterMode;
        ShoppingListButton.Visibility = barter ? Visibility.Visible : Visibility.Collapsed;
        var hasCargo = CargoFor(GoodsMode.Barter).Values.Any(line => line.Quantity > 0);
        ShoppingListButton.IsEnabled = hasCargo;
        ShoppingListButton.ToolTip = hasCargo
            ? "Shopping list: materials needed for the barter goods in the cart"
            : "Add barter goods to the cart to see the materials you need";
    }

    private void RefreshTransportLoad()
    {
        UpdateShoppingListButton();
        _shoppingListWindow?.Refresh();
        if (SelectedTransport is not { } transport || TransportSlots is null) return;

        var cargo = ActiveCargo;
        var weight = cargo.Values.Sum(line => line.Quantity * line.Product.Weight);
        var slots = cargo.Values.Sum(line => decimal.ToInt32(decimal.Ceiling((decimal)line.Quantity / line.Product.MaxBundle)));
        var slotDisplay = new List<TransportSlotDisplay>();
        foreach (var line in cargo.Values.OrderBy(line => line.Product.Name))
        {
            var remaining = line.Quantity;
            while (remaining > 0)
            {
                var stack = Math.Min(remaining, line.Product.MaxBundle);
                slotDisplay.Add(new TransportSlotDisplay
                {
                    Icon = line.Product.Icon,
                    ProductId = line.Product.Id,
                    Quantity = stack,
                    Tooltip = $"{line.Product.Name} x{stack:N0}"
                });
                remaining -= stack;
            }
        }
        var capacitySlots = CapacitySlots(transport);
        var capacityWeight = CapacityWeight(transport);
        while (slotDisplay.Count < capacitySlots) slotDisplay.Add(new TransportSlotDisplay());

        TransportSlots.ItemsSource = slotDisplay;
        var listDisplay = cargo.Values.Where(line => line.Quantity > 0).OrderBy(line => line.Product.Name)
            .Select(line =>
            {
                var lineSlots = decimal.ToInt32(decimal.Ceiling((decimal)line.Quantity / line.Product.MaxBundle));
                return new TransportListDisplay
                {
                    Icon = line.Product.Icon,
                    ProductId = line.Product.Id,
                    Name = line.Product.Name,
                    QuantityText = $"x{line.Quantity:N0}",
                    Details = $"{lineSlots:N0} slot{(lineSlots == 1 ? string.Empty : "s")} · {line.Quantity * line.Product.Weight:N0} weight · {(line.Product.IsBarter ? $"{line.Quantity * line.UnitBuyPrice:N0} Gold in materials" : $"{FormatDucats(line.Quantity * line.UnitBuyPrice)} paid")}",
                    Tooltip = $"{line.Product.Name} x{line.Quantity:N0}. Right-click to remove one stack."
                };
            }).ToList();
        TransportListItems.ItemsSource = listDisplay;
        TransportListEmptyText.Visibility = listDisplay.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SlotText.Text = $"{slots:N0} / {capacitySlots}";
        WeightText.Text = $"{weight:N0} / {capacityWeight:N0}";
        WeightGauge.Value = capacityWeight > 0 ? Math.Min(100, (double)(weight / capacityWeight * 100)) : 0;
    }

    private void RefreshDestinationTotals()
    {
        if (DestinationTotalsList is null) return;
        _barterMaterialsWindow?.RefreshVerdicts();
        if (SelectedProduct is { IsBarter: true } && TradeMiddleTabs.SelectedIndex == 0) RefreshManualQuoteMetrics();
        var sourceId = (PostPicker.SelectedItem as CommercePost)?.Id ?? 0;
        var groupSourcePostIds = _products.Where(product => product.CommerceParty)
            .Select(product => product.PostId)
            .ToHashSet();
        var destinations = _destinationTotals
            .Where(total => total.PostId != sourceId &&
                (_goodsMode == GoodsMode.Trade || total.PostId != SmugglerDestinationId) &&
                (!IsGroupMode || groupSourcePostIds.Contains(total.PostId)))
            .ToList();
        if (_displaySourceId != sourceId || _displayGoodsMode != _goodsMode)
        {
            var priorDestinationId = _selectedSaleDestinationId;
            _displaySourceId = sourceId;
            _displayGoodsMode = _goodsMode;
            DestinationTotalsList.ItemsSource = destinations;
            // One spare pixel keeps DPI rounding from making the last row overflow and scroll the top town out of view.
            DestinationTotalsList.Height = Math.Max(15, destinations.Count * 15) + 1;
            if (priorDestinationId is int priorId && destinations.Any(total => total.PostId == priorId))
                DestinationTotalsList.SelectedValue = priorId;
            else if (destinations.Count > 0)
                DestinationTotalsList.SelectedIndex = 0;
        }
        var routeSourceId = IsBarterMode && ActiveCargo.Count > 0
            ? BarterPairRouteFor(ActiveCargo.Values.Select(line => line.Product.PostId)) ?? sourceId
            : sourceId;
        _loadProfitRewardLines.Clear();
        foreach (var total in destinations)
        {
            var profitLines = new List<ProfitLine>();
            var rewardLines = new List<RewardLine>();
            var missingQuote = false;
            foreach (var line in ActiveCargo.Values)
            {
                if (AutoQuoteSalePrice(line.Product, total.PostId) is not decimal salePrice)
                {
                    missingQuote = true;
                    break;
                }
                profitLines.Add(new ProfitLine(line.Quantity, line.UnitBuyPrice, salePrice));
                rewardLines.Add(new RewardLine(line.Quantity, line.UnitBuyPrice, salePrice, line.Product.Weight,
                    line.Product.IsBarter ? 0m : PurchaseDiscountPercentFor(line.Product.PostId)));
            }
            var result = ProfitCalculator.Calculate(profitLines);
            if (!missingQuote && rewardLines.Count > 0) _loadProfitRewardLines[total.PostId] = rewardLines;
            var reward = CommerceRewardModel.Calculate(rewardLines, IsBarterMode, CurrentRewardModifiers());
            var travelMinutes = GetRouteMinutes(routeSourceId, total.PostId, SelectedTransport?.Id ?? 0, out var timeSource);
            total.EstimatedTimeText = travelMinutes is > 0
                ? $"{travelMinutes.Value:N1}"
                : "--";
            var perMinuteIsGold = IsBarterMode || _plannerPreferences.ShowTradeProfitInGold;
            total.PerMinuteIsGold = perMinuteIsGold;
            total.ProfitPerMinuteText = missingQuote
                ? "--"
                : perMinuteIsGold
                    ? travelMinutes is > 0 ? CommerceRewardModel.Compact(reward.TotalGold / travelMinutes.Value) : "--"
                    : result.PerMinute(travelMinutes) is decimal perMinute ? CommerceRewardModel.Compact(perMinute) : "--";
            // The row shows only the Gold total; the Gold + Ducats × rate breakdown is in the row tooltip.
            total.DisplayValue = missingQuote || ActiveCargo.Count == 0 ? "--" : CommerceRewardModel.Compact(reward.TotalGold);
            total.ProfitDetails = missingQuote
                ? total.PostId == SmugglerDestinationId
                    ? "Enter a positive Smuggler sale price for every loaded good in Good Prices. No live smuggler quote is available."
                    : "Not every loaded good has a sale quote at this town."
                : CommerceRewardModel.BasicTooltip(reward) + Environment.NewLine + Environment.NewLine +
                    CommerceRewardModel.DetailedTooltip(reward) + Environment.NewLine +
                    (IsBarterMode ? "Material value uses your barter material prices." : "Default quotes are not live prices.") +
                    (IsGroupMode ? $" Group sale prices include the party destination bonus (+{GroupDestinationBonus.Percent:0}%)." : string.Empty) +
                    $" P/M is {(perMinuteIsGold ? "total Gold" : "net Ducat profit")} per minute. {timeSource}";
        }
        RefreshActiveLetterPickers();
        RefreshRouteMapControls();
    }

    private void DestinationTotalsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedSaleDestinationId = (DestinationTotalsList.SelectedItem as DestinationTotal)?.PostId;
        // The list is sized to show every town with no scrollbar; never let a selection scroll the top row away.
        if (System.Windows.Media.VisualTreeHelper.GetChildrenCount(DestinationTotalsList) > 0 &&
            System.Windows.Media.VisualTreeHelper.GetChild(DestinationTotalsList, 0) is Border { Child: ScrollViewer scroller })
            scroller.ScrollToTop();
        RefreshActiveLetterPickers();
        // Manual loads: picking a Load Profit town also points the route map there when that town buys every held good.
        if (RouteMapDestinationPicker.Visibility == Visibility.Visible && _selectedSaleDestinationId is int saleId &&
            RouteMapDestinationPicker.Items.Cast<CommercePost>().Any(post => post.Id == saleId))
            RouteMapDestinationPicker.SelectedValue = saleId;
        else if (!_updatingRouteMapPicker)
            RefreshRouteMapControls();
    }

    private static RouteMapData? LoadRouteMapData()
    {
        try
        {
            return RouteMapData.Load(Path.Combine(AppContext.BaseDirectory, "Data", "RouteMaps", "route-maps.json"));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void RouteMapButton_Click(object sender, RoutedEventArgs e)
    {
        if (_routeMapData is null)
        {
            SetStatus("Route map data is missing from Data/RouteMaps.", isError: true);
            return;
        }
        if (_routeMapWindow is { IsLoaded: true })
        {
            _routeMapWindow.Activate();
            return;
        }
        _plannerPreferences.RouteMapOverlay ??= new RouteMapOverlaySettings();
        _routeMapWindow = new RouteMapWindow(_routeMapData, _plannerPreferences.RouteMapOverlay, SavePlannerPreferences);
        _routeMapWindow.Closed += (_, _) => _routeMapWindow = null;
        _routeMapWindow.Show();
        RefreshRouteMap();
    }

    // The map button is live only while goods are held. A loaded Auto plan maps its own destination; manually added
    // goods get a picker of towns that buy every held good. Either way the start comes from where the goods were bought.
    private void RefreshRouteMapControls()
    {
        if (RouteMapButton is null) return;
        if (ActiveCargo.Count == 0) _plannedRouteDestinationByMode.Remove(_goodsMode);
        var planned = _plannedRouteDestinationByMode.TryGetValue(_goodsMode, out var plannedId) ? plannedId : (int?)null;
        var manual = ActiveCargo.Count > 0 && planned is null;
        if (manual)
        {
            var starts = ActiveCargo.Values.Select(line => line.Product.PostId).ToHashSet();
            var valid = _posts.Where(post => _loadProfitRewardLines.ContainsKey(post.Id) &&
                post.Id != SmugglerDestinationId && !starts.Contains(post.Id)).ToList();
            var current = RouteMapDestinationPicker.Items.Cast<CommercePost>().Select(post => post.Id);
            if (!current.SequenceEqual(valid.Select(post => post.Id)))
            {
                var prior = RouteMapDestinationPicker.SelectedValue as int?;
                _updatingRouteMapPicker = true;
                RouteMapDestinationPicker.ItemsSource = valid;
                RouteMapDestinationPicker.SelectedValue = valid.Any(post => post.Id == prior) ? prior
                    : valid.Any(post => post.Id == _selectedSaleDestinationId) ? _selectedSaleDestinationId
                    : valid.FirstOrDefault()?.Id;
                _updatingRouteMapPicker = false;
            }
        }
        RouteMapDestinationPicker.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        RouteMapCaption.Visibility = manual ? Visibility.Collapsed : Visibility.Visible;
        var stops = RouteMapStops();
        RouteMapButton.IsEnabled = stops is not null;
        RouteMapRouteText.Text = ActiveCargo.Count == 0 ? "Load goods to map their route."
            : stops is null ? manual && RouteMapDestinationPicker.Items.Count == 0
                ? "No town buys every held good."
                : "Choose a destination for the route map."
            : (planned is not null && stops[^1] == planned ? "Planned route: " : string.Empty) + string.Join(" → ", stops.Select(PostName));
        RefreshRouteMap();
    }

    private void RouteMapDestinationPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingRouteMapPicker) return;
        if (RouteMapDestinationPicker.SelectedValue is int id && DestinationTotalsList.SelectedValue as int? != id &&
            DestinationTotalsList.Items.Cast<DestinationTotal>().Any(total => total.PostId == id))
            DestinationTotalsList.SelectedValue = id;
        RefreshRouteMapControls();
    }

    private string PostName(int id) => _posts.FirstOrDefault(post => post.Id == id)?.Name ?? "Unknown post";

    // The posts the held goods were bought at, in visiting order, then the destination; null when no route is active.
    // An Auto plan supplies the destination until another Load Profit town that buys the whole load is selected.
    private List<int>? RouteMapStops()
    {
        if (ActiveCargo.Count == 0) return null;
        var destination = _plannedRouteDestinationByMode.TryGetValue(_goodsMode, out var planned)
            ? _selectedSaleDestinationId is int selected && _loadProfitRewardLines.ContainsKey(selected) ? selected : planned
            : RouteMapDestinationPicker.SelectedValue as int?;
        if (destination is not int destinationId) return null;
        var starts = ActiveCargo.Values.Select(line => line.Product.PostId).Distinct().Where(id => id != destinationId).ToList();
        if (starts.Count == 0) return null;
        // A barter pair load visits both outposts in whichever order is faster, as its Load Profit time assumes.
        if (starts.Count == 2 && BarterPairRoutes.Any(route => starts.Contains(route.FirstPostId) && starts.Contains(route.SecondPostId)))
        {
            var transportId = SelectedTransport?.Id ?? 0;
            decimal Minutes(int first, int second) =>
                (GetManualQuoteMinutes(first, second, transportId, out _) ?? 1e6m) + (GetManualQuoteMinutes(second, destinationId, transportId, out _) ?? 1e6m);
            if (Minutes(starts[1], starts[0]) < Minutes(starts[0], starts[1])) starts.Reverse();
        }
        return [.. starts, destinationId];
    }

    // Ferry legs use the next sailing's ports.
    private void RefreshRouteMap()
    {
        if (_routeMapWindow is null || _routeMapData is null) return;
        var stops = RouteMapStops();
        if (stops is null || stops[^1] == SmugglerDestinationId)
        {
            _routeMapWindow.SetPlan(new RouteMapPlan([], [stops is not null
                ? "The Smuggler moves around, so it has no route map."
                : ActiveCargo.Count == 0 ? "Load goods to map their route." : "Choose a destination for the route map."]));
            return;
        }
        RouteMapPlan Leg(int from, int to)
        {
            var ship = _shipScheduleEstimator.Estimate(from, to, DateTimeOffset.UtcNow, SelectedTransport?.Id ?? 1, PostName,
                _plannerPreferences.FerryBufferEnabled ? _plannerPreferences.FerryBufferSeconds : 0,
                ModifierTotals(IsGroupMode).TransportSpeedPercent, _plannerPreferences.PortalLoadBufferSeconds);
            return _routeMapData!.Plan(from, to, ship?.Ports, PostName);
        }
        _routeMapWindow.SetPlan(RouteMapPlan.Join(stops.Zip(stops.Skip(1), Leg), string.Join(" → ", stops.Select(PostName))));
    }
    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(92, 36, 24))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 44, 25));
        StatusPanel.Background = isError
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(218, 184, 158))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(197, 175, 127));
        StatusPanel.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void WindowHeader_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.PlacementTarget = SettingsButton;
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }

    private void SettingsPopupCloseButton_Click(object sender, RoutedEventArgs e) => SettingsPopup.IsOpen = false;

    private void ShoppingListButton_Click(object sender, RoutedEventArgs e)
    {
        if (_shoppingListWindow is { IsLoaded: true } existing)
        {
            existing.Refresh();
            existing.Activate();
            return;
        }
        var window = new ShoppingListWindow(this);
        var workArea = SystemParameters.WorkArea;
        var left = Left + ActualWidth + 4;
        if (left + window.Width > workArea.Right) left = Left - window.Width - 4;
        window.Left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - window.Width));
        window.Top = Math.Max(workArea.Top, Math.Min(Top, workArea.Bottom - window.Height));
        window.Topmost = Topmost;
        TopmostGuard.Watch(window);
        window.Closed += (_, _) => _shoppingListWindow = null;
        _shoppingListWindow = window;
        window.Show();
    }

    private void BarterMaterialsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_barterMaterialsWindow is { IsLoaded: true } existing)
        {
            existing.RefreshVerdicts();
            existing.Activate();
            return;
        }
        var window = new BarterMaterialsWindow(this);
        var anchor = BarterMaterialsButton.PointToScreen(new Point(0, BarterMaterialsButton.ActualHeight + 4));
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is { } target)
            anchor = target.TransformFromDevice.Transform(anchor);
        var workArea = SystemParameters.WorkArea;
        window.Left = Math.Max(workArea.Left, Math.Min(anchor.X, workArea.Right - window.Width));
        window.Top = Math.Max(workArea.Top, Math.Min(anchor.Y, workArea.Bottom - window.Height));
        window.Topmost = Topmost;
        TopmostGuard.Watch(window);
        window.Closed += (_, _) => _barterMaterialsWindow = null;
        _barterMaterialsWindow = window;
        window.Show();
    }

    private void InventoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_inventoryWindow is { IsLoaded: true } existing)
        {
            existing.Activate();
            return;
        }
        var window = new InventoryWindow(this);
        // Center over the main window, kept inside the work area.
        var workArea = SystemParameters.WorkArea;
        var left = Left + (ActualWidth - window.Width) / 2;
        var top = Top + (ActualHeight - window.Height) / 2;
        window.Left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - window.Width));
        window.Top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - window.Height));
        window.Topmost = Topmost;
        TopmostGuard.Watch(window);
        window.Closed += (_, _) => _inventoryWindow = null;
        _inventoryWindow = window;
        window.Show();
    }

    private void DetailedRewardTooltips_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _plannerPreferences.DetailedRewardTooltips = DetailedRewardTooltipsCheckBox.IsChecked == true;
        SavePlannerPreferences();
        RefreshRewardDisplays();
        _barterMaterialsWindow?.RefreshVerdicts();
    }

    private void TradeProfitInGold_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _plannerPreferences.ShowTradeProfitInGold = TradeProfitInGoldCheckBox.IsChecked == true;
        SavePlannerPreferences();
        RefreshManualQuoteMetrics();
        RefreshRewardDisplays();
    }

    private void ScanDebugMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _plannerPreferences.ScanDebugMode = ScanDebugModeCheckBox.IsChecked == true;
        SavePlannerPreferences();
        if (_plannerPreferences.ScanDebugMode)
            SetStatus(ScanDebugLog.FolderPath is { } folder
                ? $"Scan debug mode on (dev only): auto-accept-worthy scans are saved to {folder}."
                : "Scan debug mode is dev-only: no source workspace was found, so scans will not be saved.",
                ScanDebugLog.FolderPath is null);
    }

    private void SettingsPopup_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        SettingsPopup.HorizontalOffset += e.HorizontalChange;
        SettingsPopup.VerticalOffset += e.VerticalChange;
    }

    private const double DefaultSettingsHorizontalOffset = -246;
    private const double DefaultSettingsVerticalOffset = 2;

    // Failsafe for windows dragged or saved off screen: recenter the planner on the primary work area, then bring the
    // Settings popup, the route map overlay and any open helper windows back with it.
    private void ResetWindowPositions_Click(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        Left = area.Left + Math.Max(0, (area.Width - ActualWidth) / 2);
        Top = area.Top + Math.Max(0, (area.Height - ActualHeight) / 2);
        SettingsPopup.HorizontalOffset = DefaultSettingsHorizontalOffset;
        SettingsPopup.VerticalOffset = DefaultSettingsVerticalOffset;
        if (SettingsPopup.IsOpen)
        {
            // A popup does not follow its owner window, so reopen it at the new anchor.
            SettingsPopup.IsOpen = false;
            SettingsPopup.IsOpen = true;
        }

        _plannerPreferences.RouteMapOverlay ??= new RouteMapOverlaySettings();
        if (_routeMapWindow is { IsLoaded: true } map)
        {
            map.ResetPosition();
            map.Activate();
        }
        else
        {
            var overlay = _plannerPreferences.RouteMapOverlay;
            overlay.Left = overlay.Top = overlay.Width = overlay.Height = null;
            SavePlannerPreferences();
        }

        foreach (var window in new Window?[] { _shoppingListWindow, _barterMaterialsWindow, _inventoryWindow, _tradeHistoryWindow })
        {
            if (window is not { IsLoaded: true }) continue;
            window.WindowState = WindowState.Normal;
            window.Left = Math.Max(area.Left, Math.Min(Left + (ActualWidth - window.ActualWidth) / 2, area.Right - window.ActualWidth));
            window.Top = Math.Max(area.Top, Math.Min(Top + (ActualHeight - window.ActualHeight) / 2, area.Bottom - window.ActualHeight));
        }
        SetStatus("Window positions reset to the main screen.");
    }

    private void LoadCurrentMarketPriceOverrides(string path)
    {
        if (!File.Exists(path)) return;
        var overrides = JsonSerializer.Deserialize<CurrentMarketPriceOverrides>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The current market price file is empty.");

        foreach (var source in overrides.Sources)
        {
            if (!_posts.Any(post => post.Id == source.SourcePostId && post.Name == source.SourcePostName))
                throw new InvalidDataException($"Current market prices reference an unknown source post: {source.SourcePostId}.");

            foreach (var productPrices in source.Products)
            {
                var product = _products.FirstOrDefault(entry => entry.Id == productPrices.ProductId);
                if (product is null || product.PostId != source.SourcePostId || product.Name != productPrices.Name)
                    throw new InvalidDataException($"Current market prices reference an unknown product: {productPrices.ProductId}.");

                var seenPostIds = new HashSet<int>();
                foreach (var entry in productPrices.Prices)
                {
                    if (entry.Price <= 0 || !seenPostIds.Add(entry.PostId))
                        throw new InvalidDataException($"Current market price for product {productPrices.ProductId} is invalid or duplicated.");
                    var quote = _quotesByProduct[product.Id].FirstOrDefault(item => item.PostId == entry.PostId);
                    if (quote is null)
                        throw new InvalidDataException($"Destination post {entry.PostId} is not a valid quote destination for product {product.Id}.");
                    quote.DestinationPrice = entry.Price;
                }
            }
        }
    }

    private void LoadSavedDestinationPriceOverrides()
    {
        foreach (var (productId, prices) in _plannerPreferences.ManualDestinationPricesByProductId)
        {
            if (!_quotesByProduct.TryGetValue(productId, out var quotes)) continue;
            foreach (var (postId, price) in prices)
            {
                if (price <= 0) continue;
                var quote = quotes.FirstOrDefault(item => item.PostId == postId);
                if (quote is not null) quote.DestinationPrice = price;
            }
        }
    }

    // Multiplies each pixel by the tint so a mostly white icon takes the theme color but keeps its shading.
    private static BitmapSource? TintImage(BitmapSource? source, byte red, byte green, byte blue)
    {
        if (source is null) return null;
        var converted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(pixels[i] * blue / 255);
            pixels[i + 1] = (byte)(pixels[i + 1] * green / 255);
            pixels[i + 2] = (byte)(pixels[i + 2] * red / 255);
        }
        var tinted = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, source.DpiX, source.DpiY,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        tinted.Freeze();
        return tinted;
    }

    private static BitmapImage? LoadImage(string relativePath)
    {
        var imagePath = Path.Combine(AppContext.BaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(imagePath)) return null;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(imagePath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    // Stacks the menu slices into one bitmap; alternate middle slices are mirrored vertically so
    // adjoining rows match, and each join gets a light vertical blur to hide the remaining step.
    // Width = 270 + 46 * widthSegments: the *Middle center pieces are inserted at each slice's horizontal center.
    internal static BitmapSource? BuildSettingsMenuBackground(int middleCount, int widthSegments = 0)
    {
        var slices = new[] { "SettingsMenuTop", "SettingsMenuMiddle", "SettingsMenuBottom" }
            .Select(name => LoadImage($"Data/CommerceUI/{name}.png"))
            .ToArray();
        if (slices.Any(slice => slice is null)) return null;
        var centers = widthSegments > 0
            ? new[] { "SettingsMenuTopMiddle", "SettingsMenuMiddleMiddle", "SettingsMenuBottomMiddle" }
                .Select(name => LoadImage($"Data/CommerceUI/{name}.png")).ToArray()
            : [];
        if (centers.Any(center => center is null)) widthSegments = 0;

        static (byte[] Pixels, int Width, int Height) ReadPixels(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
            var pixels = new byte[converted.PixelWidth * 4 * converted.PixelHeight];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            return (pixels, converted.PixelWidth, converted.PixelHeight);
        }

        var centerJoins = new List<int>();
        (byte[] Pixels, int Width, int Height) Widen(int index)
        {
            var (basePixels, baseWidth, baseHeight) = ReadPixels(slices[index]!);
            if (widthSegments <= 0) return (basePixels, baseWidth, baseHeight);
            var (centerPixels, centerWidth, centerHeight) = ReadPixels(centers[index]!);
            var rows = Math.Min(baseHeight, centerHeight);
            var half = baseWidth / 2;
            var newWidth = baseWidth + centerWidth * widthSegments;
            var result = new byte[newWidth * 4 * baseHeight];
            for (var row = 0; row < baseHeight; row++)
            {
                var sourceRow = row * baseWidth * 4;
                var targetRow = row * newWidth * 4;
                Buffer.BlockCopy(basePixels, sourceRow, result, targetRow, half * 4);
                for (var segment = 0; segment < widthSegments; segment++)
                    Buffer.BlockCopy(centerPixels, Math.Min(row, rows - 1) * centerWidth * 4, result,
                        targetRow + (half + segment * centerWidth) * 4, centerWidth * 4);
                Buffer.BlockCopy(basePixels, sourceRow + half * 4, result,
                    targetRow + (half + widthSegments * centerWidth) * 4, (baseWidth - half) * 4);
            }
            if (index == 0)
                for (var segment = 0; segment <= widthSegments; segment++)
                    centerJoins.Add(half + segment * centerWidth);
            return (result, newWidth, baseHeight);
        }

        var (top, width, topHeight) = Widen(0);
        var (middle, middleWidth, middleHeight) = Widen(1);
        var (bottom, bottomWidth, bottomHeight) = Widen(2);
        if (middleWidth != width || bottomWidth != width) return null;
        var stride = width * 4;
        var height = topHeight + middleHeight * middleCount + bottomHeight;
        var pixels = new byte[stride * height];
        Buffer.BlockCopy(top, 0, pixels, 0, top.Length);
        var joins = new List<int> { topHeight };
        for (var index = 0; index < middleCount; index++)
        {
            var offset = topHeight + index * middleHeight;
            for (var row = 0; row < middleHeight; row++)
            {
                var sourceRow = index % 2 == 1 ? middleHeight - 1 - row : row;
                Buffer.BlockCopy(middle, sourceRow * stride, pixels, (offset + row) * stride, stride);
            }
            joins.Add(offset + middleHeight);
        }
        Buffer.BlockCopy(bottom, 0, pixels, (height - bottomHeight) * stride, bottom.Length);

        var original = (byte[])pixels.Clone();
        int[] weights = [1, 2, 3, 2, 1];
        foreach (var join in joins)
        {
            for (var row = Math.Max(2, join - 2); row < Math.Min(height - 2, join + 2); row++)
            {
                for (var column = 0; column < stride; column++)
                {
                    var sum = 0;
                    for (var tap = 0; tap < weights.Length; tap++)
                        sum += weights[tap] * original[(row - 2 + tap) * stride + column];
                    pixels[row * stride + column] = (byte)((sum + 4) / 9);
                }
            }
        }

        var verticalBlurred = (byte[])pixels.Clone();
        foreach (var join in centerJoins)
        {
            for (var row = 0; row < height; row++)
            {
                for (var x = Math.Max(2, join - 2); x < Math.Min(width - 2, join + 2); x++)
                {
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var sum = 0;
                        for (var tap = 0; tap < weights.Length; tap++)
                            sum += weights[tap] * verticalBlurred[row * stride + (x - 2 + tap) * 4 + channel];
                        pixels[row * stride + x * 4 + channel] = (byte)((sum + 4) / 9);
                    }
                }
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static bool TryRead(TextBox textBox, out decimal value) =>
        decimal.TryParse(textBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out value);

    private static string FormatPriceMidpoint(int minimum, int maximum) =>
        FormatPrice(((decimal)minimum + maximum) / 2m);

    private static string FormatPrice(decimal value) =>
        value % 1m == 0
            ? value.ToString("N0", CultureInfo.CurrentCulture)
            : value.ToString("N1", CultureInfo.CurrentCulture);
}

public sealed class CommerceCatalog
{
    [JsonPropertyName("posts")]
    public List<CommercePost> Posts { get; set; } = [];
    [JsonPropertyName("products")]
    public List<GoodsEntry> Products { get; set; } = [];
    [JsonPropertyName("transports")]
    public List<CommerceTransport> Transports { get; set; } = [];
    [JsonPropertyName("barterProducts")]
    public List<BarterOffer> BarterProducts { get; set; } = [];
    [JsonPropertyName("barterMaterials")]
    public List<BarterMaterialCatalogEntry> BarterMaterials { get; set; } = [];
    [JsonPropertyName("guarantees")]
    public List<BarterGuarantee> Guarantees { get; set; } = [];
    [JsonPropertyName("creditLevels")]
    public List<MerchantRatingLevel> CreditLevels { get; set; } = [];
    [JsonPropertyName("postWeights")]
    public List<PostPairWeight> PostWeights { get; set; } = [];
}

public sealed class PostPairWeight
{
    public int Post1Id { get; set; }
    public int Post2Id { get; set; }
    public decimal Weight { get; set; }
}

public sealed class CurrentMarketPriceOverrides
{
    public string ObservedOn { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public List<CurrentMarketPriceSource> Sources { get; set; } = [];
}

public sealed class CurrentMarketPriceSource
{
    public int SourcePostId { get; set; }
    public string SourcePostName { get; set; } = string.Empty;
    public List<CurrentProductMarketPrices> Products { get; set; } = [];
}

public sealed class CurrentProductMarketPrices
{
    public int ProductId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<CurrentDestinationMarketPrice> Prices { get; set; } = [];
}

public sealed class CurrentDestinationMarketPrice
{
    public int PostId { get; set; }
    public decimal Price { get; set; }
}

public sealed class CommercePost
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int RegionId { get; set; }
    public bool CanBuy { get; set; }
    public bool CanSell { get; set; }
    public string IconPath { get; set; } = string.Empty;
    [JsonIgnore]
    public BitmapImage? Icon { get; set; }
}

public sealed class GoodsEntry : INotifyPropertyChanged
{
    private string _currentBuyPriceText = string.Empty;
    private string _rotationHistoryText = string.Empty;
    private bool _isRotationConfirmedThisWeek;

    public int Id { get; set; }
    public int PostId { get; set; }
    public string Name { get; set; } = string.Empty;
    // The in-game name used for OCR matching when Name carries a display-only tag.
    public string? GameName { get; set; }
    public int RequiredCreditLevel { get; set; }
    public bool CommerceParty { get; set; }
    public string ResetType { get; set; } = string.Empty;
    public int SourceCount { get; set; }
    [JsonIgnore]
    public bool IsWeeklyLimited => (CommerceParty || IsBarter) && ResetType.Equals("weekly", StringComparison.OrdinalIgnoreCase) && SourceCount > 0;
    [JsonIgnore]
    public bool IsBarter { get; set; }
    [JsonIgnore]
    public int CatalogId { get; set; }
    [JsonIgnore]
    public bool IsSeasonal { get; set; }
    [JsonIgnore]
    public List<GoodsEntry> RotationAlternatives { get; set; } = [];
    [JsonIgnore]
    public bool HasRotationAlternatives => RotationAlternatives.Count > 1;
    [JsonIgnore]
    public bool IsInactiveRotation { get; set; }
    [JsonIgnore]
    public string RotationHistoryText
    {
        get => _rotationHistoryText;
        set
        {
            if (_rotationHistoryText == value) return;
            _rotationHistoryText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RotationHistoryText)));
        }
    }
    [JsonIgnore]
    public bool IsRotationConfirmedThisWeek
    {
        get => _isRotationConfirmedThisWeek;
        set
        {
            if (_isRotationConfirmedThisWeek == value) return;
            _isRotationConfirmedThisWeek = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRotationConfirmedThisWeek)));
        }
    }
    [JsonIgnore]
    public List<BarterRecipeComponent> Recipe { get; set; } = [];
    private string _recipeTooltip = string.Empty;
    [JsonIgnore]
    public string RecipeTooltip
    {
        get => _recipeTooltip;
        set
        {
            if (_recipeTooltip == value) return;
            _recipeTooltip = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecipeTooltip)));
        }
    }
    [JsonIgnore]
    public string WeeklyLimitText => $"{SourceCount:N0} per week";
    public int Weight { get; set; }
    public int MaxBundle { get; set; }
    public int MaxStock { get; set; }
    public int MinPrice { get; set; }
    public int MaxPrice { get; set; }
    public string IconPath { get; set; } = string.Empty;
    public string LockedIconPath { get; set; } = string.Empty;
    [JsonIgnore]
    public BitmapImage? Icon { get; set; }
    [JsonIgnore]
    public BitmapImage? ActiveIcon { get; set; }
    [JsonIgnore]
    public BitmapImage? LockedIcon { get; set; }
    [JsonIgnore]
    public BitmapImage? IdleRowBackground { get; set; }
    [JsonIgnore]
    public BitmapImage? LockedRowBackground { get; set; }
    [JsonIgnore]
    public BitmapImage? RowBackground { get; set; }
    [JsonIgnore]
    public BitmapImage? SelectedRowOverlay { get; set; }
    [JsonIgnore]
    public string CurrentBuyPriceText
    {
        get => _currentBuyPriceText;
        set
        {
            if (_currentBuyPriceText == value) return;
            _currentBuyPriceText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentBuyPriceText)));
        }
    }
    [JsonIgnore]
    public bool CurrentBuyPriceIsEstimated { get; set; } = true;
    [JsonIgnore]
    public decimal CurrentBuyPrice { get; set; }
    [JsonIgnore]
    public int CurrentStock { get; set; }
    [JsonIgnore]
    public bool StockInitialized { get; set; }
    [JsonIgnore]
    public bool IsLocked { get; set; }
    [JsonIgnore]
    public string LockText { get; set; } = string.Empty;
    [JsonIgnore]
    public string ReferenceRangeText { get; set; } = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class CommerceTransport : INotifyPropertyChanged
{
    private bool _isOwned;

    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Slots { get; set; }
    public int Weight { get; set; }
    public string Condition { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int BaseTransportId { get; set; }
    public bool IsSkin { get; set; }
    public bool IsEvent { get; set; }
    public bool IsRental { get; set; }
    [JsonIgnore]
    public int PartnerBaseTransportId { get; set; }
    [JsonIgnore]
    public bool IsPartner => PartnerBaseTransportId > 0;
    [JsonIgnore]
    public bool IsWilliamPartner => IsPartner && Condition.Contains("pet(/commerce_upward/", StringComparison.Ordinal);
    [JsonIgnore]
    public int AlpacaBaseTransportId { get; set; }
    [JsonIgnore]
    public bool IsAlpaca => AlpacaBaseTransportId > 0;
    [JsonIgnore]
    public bool IsOwnershipEditable => Id != TransportIds.Backpack && !IsPartner && !IsAlpaca;
    [JsonIgnore]
    public bool IsVisibleInList => (!IsPartner && !IsAlpaca) || IsOwned;
    [JsonIgnore]
    public bool IsOwned
    {
        get => _isOwned;
        set
        {
            if (_isOwned == value) return;
            _isOwned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOwned)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisibleInList)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string IconPath { get; set; } = string.Empty;
    [JsonIgnore]
    public BitmapImage? Icon { get; set; }
}

public sealed class DestinationQuote : INotifyPropertyChanged
{
    private decimal _destinationPrice;
    private string _perItemProfitLossText = "--";
    private bool _perItemProfitIsGold;
    private bool _perItemProfitIsDucat;
    private string _perItemProfitLossToolTip = "Sale price minus buy price, per item";
    private System.Windows.Media.Brush _perItemProfitLossBrush = System.Windows.Media.Brushes.DimGray;
    private string _estimatedTimeText = "--";
    private string _estimatedTimeToolTip = "No route time for this trip.";

    public int ProductId { get; set; }
    public int PostId { get; set; }
    public string PostName { get; set; } = string.Empty;
    public decimal DestinationPrice
    {
        get => _destinationPrice;
        set
        {
            IsManualPrice = true;
            if (_destinationPrice == value) return;
            _destinationPrice = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DestinationPrice)));
        }
    }

    public bool IsManualPrice { get; set; }

    public string PerItemProfitLossText
    {
        get => _perItemProfitLossText;
        set
        {
            if (_perItemProfitLossText == value) return;
            _perItemProfitLossText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PerItemProfitLossText)));
        }
    }

    public bool PerItemProfitIsGold
    {
        get => _perItemProfitIsGold;
        set
        {
            if (_perItemProfitIsGold == value) return;
            _perItemProfitIsGold = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PerItemProfitIsGold)));
        }
    }

    public bool PerItemProfitIsDucat
    {
        get => _perItemProfitIsDucat;
        set
        {
            if (_perItemProfitIsDucat == value) return;
            _perItemProfitIsDucat = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PerItemProfitIsDucat)));
        }
    }

    public string PerItemProfitLossToolTip
    {
        get => _perItemProfitLossToolTip;
        set
        {
            if (_perItemProfitLossToolTip == value) return;
            _perItemProfitLossToolTip = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PerItemProfitLossToolTip)));
        }
    }

    public System.Windows.Media.Brush PerItemProfitLossBrush
    {
        get => _perItemProfitLossBrush;
        set
        {
            if (_perItemProfitLossBrush == value) return;
            _perItemProfitLossBrush = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PerItemProfitLossBrush)));
        }
    }

    public string EstimatedTimeText
    {
        get => _estimatedTimeText;
        set
        {
            if (_estimatedTimeText == value) return;
            _estimatedTimeText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EstimatedTimeText)));
        }
    }

    public string EstimatedTimeToolTip
    {
        get => _estimatedTimeToolTip;
        set
        {
            if (_estimatedTimeToolTip == value) return;
            _estimatedTimeToolTip = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EstimatedTimeToolTip)));
        }
    }

    public void SetDefaultDestinationPrice(decimal value)
    {
        if (IsManualPrice || _destinationPrice == value) return;
        _destinationPrice = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DestinationPrice)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class DestinationTotal : INotifyPropertyChanged
{
    private string _displayValue = "0";
    private string _profitDetails = string.Empty;
    private string _estimatedTimeText = "--";
    private string _profitPerMinuteText = "--";

    public int PostId { get; set; }
    public string PostName { get; set; } = string.Empty;
    public string DisplayValue
    {
        get => _displayValue;
        set
        {
            if (_displayValue == value) return;
            _displayValue = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayValue)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowGoldIcon)));
        }
    }

    public bool ShowGoldIcon => _displayValue != "--";

    private bool _perMinuteIsGold;
    public bool PerMinuteIsGold
    {
        get => _perMinuteIsGold;
        set
        {
            if (_perMinuteIsGold == value) return;
            _perMinuteIsGold = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPerMinuteGoldIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPerMinuteDucatIcon)));
        }
    }

    public bool ShowPerMinuteGoldIcon => _perMinuteIsGold && _profitPerMinuteText != "--";
    public bool ShowPerMinuteDucatIcon => !_perMinuteIsGold && _profitPerMinuteText != "--";

    public string EstimatedTimeText
    {
        get => _estimatedTimeText;
        set
        {
            if (_estimatedTimeText == value) return;
            _estimatedTimeText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EstimatedTimeText)));
        }
    }

    public string ProfitPerMinuteText
    {
        get => _profitPerMinuteText;
        set
        {
            if (_profitPerMinuteText == value) return;
            _profitPerMinuteText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProfitPerMinuteText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPerMinuteGoldIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPerMinuteDucatIcon)));
        }
    }

    public string ProfitDetails
    {
        get => _profitDetails;
        set
        {
            if (_profitDetails == value) return;
            _profitDetails = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProfitDetails)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class AutoPlanRow : INotifyPropertyChanged
{
    private string _tooltip;

    public AutoPlanRow(string destinationName, string scoreText, string detailText, string timeText,
        string profitPerMinuteText, string tooltip,
        PurchasePlan plan, int sourcePostId, GoodsMode mode, bool rankByProfitPerMinute, bool profitIsGold = false)
    {
        ProfitIsGold = profitIsGold;
        DestinationName = destinationName;
        ScoreText = scoreText;
        DetailText = detailText;
        TimeText = timeText;
        ProfitPerMinuteText = profitPerMinuteText;
        BaseTooltip = tooltip;
        _tooltip = tooltip;
        Plan = plan;
        SourcePostId = sourcePostId;
        Mode = mode;
        RankByProfitPerMinute = rankByProfitPerMinute;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DestinationName { get; }
    public string ScoreText { get; }
    public bool ProfitIsGold { get; }
    public bool ProfitIsDucat => !ProfitIsGold;
    public bool HasProfitPerMinute => ProfitPerMinuteText != "--";
    public string DetailText { get; }
    public string TimeText { get; }
    public string ProfitPerMinuteText { get; }
    public string BaseTooltip { get; }
    public string Tooltip => _tooltip;

    public void SetRewardText(string rewardText)
    {
        _tooltip = string.IsNullOrEmpty(rewardText) ? BaseTooltip : $"{BaseTooltip}{Environment.NewLine}{rewardText}";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tooltip)));
    }
    public PurchasePlan Plan { get; }
    public int SourcePostId { get; }
    public GoodsMode Mode { get; }
    public bool GroupMode => Mode == GoodsMode.Group;
    public bool RankByProfitPerMinute { get; }
    public FontWeight ProfitFontWeight => RankByProfitPerMinute ? FontWeights.Normal : FontWeights.Bold;
    public FontWeight ProfitPerMinuteFontWeight => RankByProfitPerMinute ? FontWeights.Bold : FontWeights.Normal;
}

public sealed class CargoLine
{
    public required GoodsEntry Product { get; init; }
    public int Quantity { get; set; }
    public decimal UnitBuyPrice { get; set; }
}

public sealed class TransportSlotDisplay
{
    public BitmapImage? Icon { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public bool HasContents => ProductId > 0 && Quantity > 0;
    public string Tooltip { get; set; } = string.Empty;
}

public sealed class TransportListDisplay
{
    public BitmapImage? Icon { get; set; }
    public int ProductId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string QuantityText { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string Tooltip { get; set; } = string.Empty;
}

public enum GoodsMode
{
    Trade,
    Group,
    Barter
}

public sealed class BarterOffer
{
    public int Id { get; set; }
    public int PostId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    [JsonIgnore]
    public string SourceLimitText => $"{SourceCount:N0} {(ResetType.Equals("weekly", StringComparison.OrdinalIgnoreCase) ? "per week" : ResetType)}{(IsSeasonal ? " · Seasonal" : string.Empty)}";
    [JsonIgnore]
    public string TooltipText => $"{Name}\n{Description}\n\nBasic info\nMax quantity per slot: {MaxBundle}\nPer unit weight: {Weight}\n{(IsSeasonal ? "Rotating offer; pick the active one from the name dropdown or scan its price list." + Environment.NewLine : string.Empty)}\nExchange recipe\n{string.Join(Environment.NewLine, Recipe.Select(part => $"{part.Quantity} {part.Name}"))}";
    public bool IsSeasonal { get; set; }
    public bool IsDefaultRotation { get; set; } = true;
    public int Grade { get; set; }
    public int RequiredCreditLevel { get; set; }
    public int Weight { get; set; }
    public int MaxBundle { get; set; }
    public int MaxStock { get; set; }
    public int MinPrice { get; set; }
    public int MaxPrice { get; set; }
    public string ResetType { get; set; } = string.Empty;
    public int SourceCount { get; set; }
    public int CountTypeId { get; set; }
    public string IconPath { get; set; } = string.Empty;
    public List<BarterRecipeComponent> Recipe { get; set; } = [];
    [JsonIgnore]
    public BitmapImage? Icon { get; set; }
}

public sealed class BarterRecipeComponent
{
    public int ItemId { get; set; }
    public int Quantity { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ReferenceBuyPrice { get; set; }
    public int BundleSize { get; set; }
}

public sealed class BarterMaterialCatalogEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int ReferenceBuyPrice { get; set; }
    public int BundleSize { get; set; }
    public string AuctionSearchFlag { get; set; } = string.Empty;

    // Items with no Auction House search flag (the Scathach Beach recipe materials) cannot be traded, so they have no Gold value.
    public bool IsUntradable => string.IsNullOrWhiteSpace(AuctionSearchFlag);
}

public sealed class BarterGuarantee
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ReferenceBuyPrice { get; set; }
    public int BarterSaleBonusPercent { get; set; }
    public int DucatsGranted { get; set; }
    public bool IsTradable { get; set; }
}
