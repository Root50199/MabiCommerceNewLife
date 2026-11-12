using System.Windows;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace MabiCommerceNewLife;

public partial class MainWindow
{
    private CommunityClient? _communityClient;
    private CancellationTokenSource? _communitySharing;
    private readonly CommunityRefreshGate _communityRefreshGate = new();
    private readonly DispatcherTimer _communityRefreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _communityConnecting;
    private bool CommunitySharingEnabled => _communitySharing is not null && _communityClient?.IsConnected == true;
    private readonly Dictionary<(int Product, int Post), CommunityMarketValue> _communityValues = [];
    private readonly Dictionary<(int Product, int Post), bool> _communityOriginalFlags = [];
    private readonly HashSet<int> _communityOriginallyUncapped = [];
    private bool _applyingCommunity;
    private bool _applyingLocalScan;
    private bool _communityApplicationsEnabled;
    private CommunitySnapshot? _pendingCommunitySnapshot;
    private string? _communityEndpoint;
    private const int CommunityBuyKey = int.MinValue;
    private const int CommunityStockKey = int.MinValue + 1;

    private CommunityMarketValue CommunityValue(int productId, int postId, decimal currentValue)
    {
        var key = (productId, postId);
        if (!_communityValues.TryGetValue(key, out var value))
        {
            var product = _products.Single(p => p.Id == productId);
            var loaded = CargoFor(ModeOf(product)).GetValueOrDefault(productId)?.Quantity ?? 0;
            _communityValues[key] = value = new CommunityMarketValue(currentValue, postId == CommunityStockKey, loaded);
            if (postId == CommunityBuyKey) _communityOriginalFlags[key] = product.CurrentBuyPriceIsEstimated;
            else if (postId == CommunityStockKey)
            {
                if (product.IsWeeklyLimited && product.ScannedMarketStock is null)
                    _communityOriginallyUncapped.Add(productId);
            }
            else _communityOriginalFlags[key] = _quotesByProduct[productId].Single(q => q.PostId == postId).IsManualPrice;
            if (_plannerPreferences.MarketLocalTimes.TryGetValue($"{productId}:{postId}", out var time))
                value.SetLocal(currentValue, time, "Local");
        }
        return value;
    }

    private void RecordLocalCommunityValue(int productId, int postId, decimal value, string origin, long? time = null)
    {
        if (_applyingCommunity || _initializing) return;
        var timestamp = time is > 0 ? time.Value : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        CommunityValue(productId, postId, value).SetLocal(value, timestamp, origin);
        _plannerPreferences.MarketLocalTimes[$"{productId}:{postId}"] =
            _communityValues[(productId, postId)].LocalTime;
        RefreshCommunityProvenance();
    }

    private void InitializeCommunitySharing()
    {
        CommunitySharingInfo.Message += $"\n\nCurrent backend: {CommunityClient.DefaultEndpoint}";
        _communityRefreshTimer.Tick += async (_, _) =>
        {
            RefreshCommunityButtonState();
            await FlushCommunityBatchesAsync();
        };
        _communityRefreshTimer.Start();
        RefreshCommunityButtonState();
        Loaded += RestoreCommunitySharing;
    }

    private async void RestoreCommunitySharing(object sender, RoutedEventArgs e)
    {
        Loaded -= RestoreCommunitySharing;
        if (!_plannerPreferences.CommunitySharingEnabled || ScreenReaderModeCheckBox.IsChecked != true || _helpClosing)
            return;
        if (!CommunitySharingConsent.IsCurrent(_plannerPreferences.CommunitySharingConsentEndpoint,
                _plannerPreferences.CommunitySharingConsentVersion))
        {
            _plannerPreferences.CommunitySharingEnabled = false;
            SavePlannerPreferences();
            SetStatus("Price sharing is OFF: the service or sharing policy changed. Enable sharing in Settings to review and accept it.", true);
            return;
        }
        _initializing = true;
        CommunitySharingCheckBox.IsChecked = true;
        _initializing = false;
        await ConnectCommunitySharingAsync(startup: true);
    }

    private async void CommunitySharing_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (CommunitySharingCheckBox.IsChecked != true)
        {
            _plannerPreferences.CommunitySharingEnabled = false;
            SavePlannerPreferences();
            StopCommunitySharing();
            SetStatus("Price sharing OFF. Newest applied values saved locally; consumed supply stays consumed.");
            return;
        }
        if (_communityConnecting) return;
        if (ScreenReaderModeCheckBox.IsChecked != true)
        {
            CommunitySharingCheckBox.IsChecked = false;
            SetStatus("Enable Screen reader mode before enabling scanned price sharing.", true);
            return;
        }
        if (!CommunitySharingConsent.IsCurrent(_plannerPreferences.CommunitySharingConsentEndpoint,
                _plannerPreferences.CommunitySharingConsentVersion) &&
            new SharingConsentWindow(this).ShowDialog() != true)
        {
            CommunitySharingCheckBox.IsChecked = false;
            return;
        }
        _plannerPreferences.CommunitySharingEnabled = true;
        _plannerPreferences.CommunitySharingConsentEndpoint = CommunityClient.DefaultEndpoint;
        _plannerPreferences.CommunitySharingConsentVersion = CommunitySharingConsent.CurrentVersion;
        SavePlannerPreferences();
        await ConnectCommunitySharingAsync();
    }

    private async Task ConnectCommunitySharingAsync(bool startup = false)
    {
        if (_communityConnecting || _helpClosing || CommunitySharingCheckBox.IsChecked != true) return;
        _communityConnecting = true;
        SetCommunityHealth(CommunityHealth.Waiting, "Connecting price sharing...");
        RefreshCommunityButtonState();
        var sharing = new CancellationTokenSource();
        _communitySharing = sharing;
        var cancellationToken = sharing.Token;
        try
        {
            _communityClient ??= new CommunityClient(CommunityClient.DefaultEndpoint,
                credentialPath: Path.Combine(AppContext.BaseDirectory, CommunityCredentialStore.FileName));
            SetCommunityEndpoint(_communityClient.Endpoint.AbsoluteUri);
            var policy = await CommunityRetry.RunAsync(
                () => _communityClient.GetPrivacyAsync(cancellationToken), startup, true,
                CommunityRetryWaiting, cancellationToken);
            await CommunityRetry.RunAsync(async () =>
            {
                await _communityClient.ConnectAsync(policy, cancellationToken);
                return true;
            }, startup, false, CommunityRetryWaiting, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_helpClosing)
            {
                CommunityHealthy();
                SetStatus("Price sharing ON. SCAN sends checked observations; REFRESH receives and applies prices.");
            }
        }
        catch (Exception exception) when (IsCommunityError(exception))
        {
            if (!ReferenceEquals(_communitySharing, sharing)) return;
            StopCommunitySharing();
            if (!_helpClosing)
            {
                CommunityFailed(exception);
                SetStatus("Price sharing could not connect; ON preference saved. Click the bottom-left status dot for details and retry.", true);
            }
        }
        finally
        {
            if (ReferenceEquals(_communitySharing, sharing) || _communitySharing is null)
            {
                _communityConnecting = false;
                _communityAutoRetry = false;
                if (!_helpClosing) RefreshCommunityButtonState();
            }
        }
    }

    private void StopCommunitySharing()
    {
        var discarded = _communityScanQueue.Count;
        _communityScanQueue.Clear();
        var sharing = _communitySharing;
        _communitySharing = null;
        _communityConnecting = false;
        sharing?.Cancel();
        sharing?.Dispose();
        _communityClient?.Disconnect();
        SetCommunityHealth(CommunityHealth.Off, "Price sharing OFF.");
        RetainCommunityValues();
        if (!_helpClosing)
        {
            RefreshCommunityButtonState();
            if (discarded > 0) SetStatus($"Discarded {discarded} pending shared goods. Applied local prices were kept.");
        }
    }

    private void ShutdownCommunitySharing()
    {
        _communityRefreshTimer.Stop();
        StopCommunitySharing();
        _communityClient?.Dispose();
    }

    private void RefreshCommunityButtonState()
    {
        RefreshCommunityHealth();
        CommunityRefreshButton.IsEnabled = CommunitySharingEnabled && _communityRefreshGate.CanRefresh &&
            CommunityWaitRemaining == TimeSpan.Zero && !_communityAutoRetry && !_communityChecking;
        var remaining = (int)Math.Ceiling(_communityRefreshGate.Remaining.TotalSeconds);
        CommunityRefreshInfo.Message = !CommunitySharingEnabled
            ? _communityConnecting
                ? "Connecting scanned price sharing. Refresh will be enabled after the connection succeeds."
                : _plannerPreferences.CommunitySharingEnabled
                    ? "Scanned price sharing is saved ON but is not connected. Click the bottom-left status dot for details and retry. Refresh is limited to once per minute."
                    : "Receives scanned prices, Inventory supply, and rotating barter offers; newest values apply. Enabled when Screen reader mode and Scanned price sharing are turned on in Settings > Calculator Behavior. Refresh is limited to once per minute."
            : remaining > 0
                ? $"Receives and applies newest shared prices. Next refresh in {remaining} seconds. The one-minute limit applies across all goods and tabs, including failed attempts."
                : "Receives scanned prices, Inventory supply, and rotating barter offers; newest values apply using your gear and source-post merchant level. Newer local picks/scans are kept. Refresh at most once per minute; no automatic polling.";
    }

    private async void CommunityRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (!CommunitySharingEnabled || _communitySharing is not { } sharing || _communityClient is not { } client ||
            CommunityWaitRemaining > TimeSpan.Zero || _communityAutoRetry || _communityChecking ||
            !_communityRefreshGate.TryBegin()) return;
        var cancellationToken = sharing.Token;
        RefreshCommunityButtonState();
        SetStatus("Receiving shared scanned prices...");
        try
        {
            var snapshot = await CommunityRetry.RunAsync(() =>
            {
                _communityRefreshGate.RecordRetry();
                return client.SyncAsync(cancellationToken);
            }, false, false, (exception, delay) => CommunityRetryWaiting(exception, delay, refresh: true), cancellationToken,
                minimumWait: () => _communityRefreshGate.Remaining);
            cancellationToken.ThrowIfCancellationRequested();
            if (_helpClosing || !ReferenceEquals(_communitySharing, sharing)) return;
            ApplyCommunitySnapshot(snapshot);
            CommunityHealthy();
            SetStatus(_scanCancellation is null
                ? $"Refreshed shared prices for {snapshot.Observations.Count} goods. Newest values applied."
                : "Shared prices received; applying after the current scan ends.");
        }
        catch (Exception exception) when (IsCommunityError(exception))
        {
            if (!_helpClosing && ReferenceEquals(_communitySharing, sharing))
            {
                CommunityFailed(exception, refresh: true);
                SetStatus($"Price refresh failed; current values kept. {CommunityNetworkError(exception)}", true);
            }
        }
        finally
        {
            _communityRefreshGate.Complete();
            if (ReferenceEquals(_communitySharing, sharing))
            {
                _communityAutoRetry = false;
                if (!_helpClosing) RefreshCommunityButtonState();
            }
        }
    }

    private Guid? BeginCommunityScan()
    {
        if (!CommunitySharingEnabled || _communityClient is not { } client) return null;
        if (SelectedProduct is { } product)
            _communityScanQueue.ScanStarted(new CommunityOutpost(ModeOf(product).ToString().ToLowerInvariant(), product.PostId));
        return client.ConnectedSession;
    }

    private async Task SharePriceListScanAsync(PriceListScanRead read, AcceptedPriceListScan accepted,
        Guid? session, CancellationToken cancellationToken)
    {
        if (session is null || !CommunitySharingEnabled || _communitySharing is not { } sharing ||
            _communityClient?.ConnectedSession != session) return;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mode = ModeOf(read.Product).ToString().ToLowerInvariant();
            var expected = read.ProductQuotes.Where(q => q.PostId != SmugglerDestinationId).Select(q => q.PostId).ToArray();
            var scanId = Guid.NewGuid().ToString();
            var scan = accepted.Review is { } review
                ? CommunityScanBuilder.BuildReviewed(scanId, mode,
                    read.Product.IsBarter ? read.Product.CatalogId : read.Product.Id, read.SourcePost.Id,
                    accepted.SourcePrice, accepted.Stock, accepted.Candidates, expected, read.PurchaseDiscountPercent, review)
                : CommunityScanBuilder.Build(scanId, mode,
                read.Product.IsBarter ? read.Product.CatalogId : read.Product.Id, read.SourcePost.Id,
                read.CanAutoAccept, read.SourcePrice, read.Stock, read.Candidates,
                expected,
                read.PurchaseDiscountPercent);
            var products = _products.Where(product => !product.IsInactiveRotation && product.PostId == read.SourcePost.Id &&
                ModeOf(product) == ModeOf(read.Product))
                .Select(product => product.IsBarter ? product.CatalogId : product.Id).Distinct().ToArray();
            _communityScanQueue.Enqueue(scan, products);
            RefreshCommunityHealth();
            SetStatus($"Scan saved locally; {_communityScanQueue.Count} goods pending sharing. " +
                "Uploads after 60s idle, at 4 minutes, or when this outpost is complete.");
            await FlushCommunityBatchesAsync();
        }
        catch (Exception exception) when (IsCommunityError(exception))
        {
            if (!cancellationToken.IsCancellationRequested && !_helpClosing && ReferenceEquals(_communitySharing, sharing))
            {
                CommunityFailed(exception);
                SetStatus($"Scan remains local; not shared: {CommunityNetworkError(exception)}", true);
            }
        }
    }

    private void ApplyCommunitySnapshot(CommunitySnapshot snapshot)
    {
        if (_scanCancellation is not null)
        {
            _pendingCommunitySnapshot = snapshot;
            return;
        }
        var updates = new List<(GoodsEntry Product, CommunityObservation Observation, IReadOnlyList<DestinationQuote> Quotes)>();
        foreach (var observation in snapshot.Observations)
        {
            var product = _products.SingleOrDefault(p =>
                (p.IsBarter ? p.CatalogId : p.Id) == observation.ProductId &&
                ModeOf(p).ToString().Equals(observation.Mode, StringComparison.OrdinalIgnoreCase));
            if (product is null || product.PostId != observation.SourcePostId ||
                !_quotesByProduct.TryGetValue(product.Id, out var quotes))
                throw new InvalidOperationException("Community observation does not match the local goods catalog. Nothing was applied.");
            var destinations = observation.Prices.Select(p => p.PostId == 0 ? SmugglerDestinationId : p.PostId).ToHashSet();
            if (destinations.Any(id => !quotes.Any(q => q.PostId == id)) ||
                quotes.Any(q => q.PostId != SmugglerDestinationId && !destinations.Contains(q.PostId)))
                throw new InvalidOperationException("Community destination set does not match the local catalog. Nothing was applied.");
            updates.Add((product, observation, quotes));
            if (!product.IsBarter && observation.PriceBasis == "normalized-v1" && observation.SourcePrice is { } sourcePrice)
                CommunityPurchasePrice.ForRecipient(sourcePrice, PurchaseDiscountPercentFor(product.PostId));
        }
        foreach (var value in _communityValues.Values) value.ClearRemote();
        foreach (var (product, observation, quotes) in updates)
        {
            if (!product.IsBarter && observation.PriceBasis == "normalized-v1" && observation.SourcePrice is { } source)
                CommunityValue(product.Id, CommunityBuyKey, GetCurrentBuyPrice(product))
                    .SetRemote(source, observation.Sequence, observation.ReceivedAt,
                        CommunityObservationOrigin(observation, observation.Review?.SourceManual == true));
            foreach (var price in observation.Prices)
            {
                var quote = quotes.Single(q => q.PostId == (price.PostId == 0 ? SmugglerDestinationId : price.PostId));
                CommunityValue(product.Id, quote.PostId, quote.DestinationPrice)
                    .SetRemote(price.SalePrice, observation.Sequence, observation.ReceivedAt,
                        CommunityObservationOrigin(observation, observation.Review?.ManualPostIds.Contains(price.PostId) == true));
            }
            // A complete observation without the optional Smuggler quote marks it inactive.
            if (!observation.Prices.Any(price => price.PostId == 0) &&
                quotes.FirstOrDefault(quote => quote.PostId == SmugglerDestinationId) is { } smuggler)
                CommunityValue(product.Id, SmugglerDestinationId, smuggler.DestinationPrice)
                    .SetRemote(0, observation.Sequence, observation.ReceivedAt,
                        CommunityObservationOrigin(observation, false));
            CommunityValue(product.Id, CommunityStockKey, product.IsWeeklyLimited
                ? product.ScannedMarketStock ?? product.MaxStock : product.StockInitialized ? product.CurrentStock : product.MaxStock)
                .SetRemote(observation.Stock, observation.Sequence, observation.ReceivedAt,
                    CommunityObservationOrigin(observation, observation.Review?.StockManual == true));
        }
        _communityApplicationsEnabled = true;
        var selectedProduct = SelectedProduct;
        var rotationChanged = false;
        var rotatingProducts = _products.Where(product => product.HasRotationAlternatives)
            .ToDictionary(product => product.CatalogId, product => product.PostId);
        foreach (var observation in CommunityBarterRotation.Latest(snapshot.Observations, rotatingProducts,
            _plannerPreferences.BarterRotationUpdatesByPostId))
        {
            var product = updates.Single(update => update.Observation == observation).Product;
            rotationChanged |= ActivateRotation(product, observation.ReceivedAt, observation.Sequence);
        }
        if (rotationChanged && IsBarterMode)
        {
            RefreshProducts();
            if (selectedProduct is not null)
            {
                var selection = selectedProduct.IsInactiveRotation
                    ? selectedProduct.RotationAlternatives.Single(product => !product.IsInactiveRotation)
                    : selectedProduct;
                ProductList.SelectedItem = selection;
            }
        }
        RefreshCommunityValues();
    }

    private void RefreshCommunityValues()
    {
        if (_applyingCommunity || _initializing) return;
        _applyingCommunity = true;
        try
        {
            foreach (var ((productId, postId), value) in _communityValues)
            {
                var product = _products.Single(p => p.Id == productId);
                var number = value.Value;
                if (postId == CommunityBuyKey)
                {
                    if (value.RemoteWins)
                        number = CommunityPurchasePrice.ForRecipient((int)value.RemoteValue!.Value,
                            PurchaseDiscountPercentFor(product.PostId));
                    SetDetectedBuyPrice(productId, number);
                    if (!value.RemoteWins && value.LocalTime == 0)
                        product.CurrentBuyPriceIsEstimated = _communityOriginalFlags[(productId, postId)];
                }
                else if (postId == CommunityStockKey)
                {
                    var stock = (int)Math.Clamp(number, 0, int.MaxValue);
                    if (product.IsWeeklyLimited)
                    {
                        if (!product.StockInitialized)
                        {
                            product.CurrentStock = Math.Min(product.MaxStock, product.SourceCount);
                            product.StockInitialized = true;
                        }
                        product.ScannedMarketStock = !value.RemoteWins && value.LocalTime == 0 &&
                            _communityOriginallyUncapped.Contains(productId) ? null : stock;
                    }
                    else
                    {
                        product.CurrentStock = stock;
                        product.StockInitialized = true;
                    }
                    if (SelectedProduct?.Id == productId)
                        StockInput.Text = product.CurrentStock.ToString(System.Globalization.CultureInfo.CurrentCulture);
                }
                else
                {
                    var quote = _quotesByProduct[productId].Single(q => q.PostId == postId);
                    if (!value.RemoteWins && value.LocalTime == 0 && !_communityOriginalFlags[(productId, postId)])
                    {
                        quote.IsManualPrice = false;
                        quote.SetDefaultDestinationPrice(number);
                    }
                    else quote.DestinationPrice = number;
                }
            }
            RefreshQuotes();
            RefreshManualQuoteMetrics();
            RefreshCommunityProvenance();
            RefreshDestinationTotals();
            InvalidateAutoResults("Market data changed. Press CALC to refresh routes.");
        }
        finally { _applyingCommunity = false; }
    }

    private void DisableCommunityApplication()
    {
        foreach (var value in _communityValues.Values) value.ClearRemote();
        _communityApplicationsEnabled = false;
        _pendingCommunitySnapshot = null;
        RefreshCommunityValues();
    }

    private void RetainCommunityValues()
    {
        foreach (var ((productId, postId), value) in _communityValues)
        {
            if (!value.RemoteWins) continue;
            var product = _products.Single(p => p.Id == productId);
            var number = value.Value;
            if (postId == CommunityBuyKey)
            {
                number = CommunityPurchasePrice.ForRecipient((int)value.RemoteValue!.Value, PurchaseDiscountPercentFor(product.PostId));
                _plannerPreferences.ManualBuyPricesByProductId[productId] = number;
                _communityOriginalFlags[(productId, postId)] = false;
            }
            else if (postId == CommunityStockKey)
            {
                number = Math.Clamp(number, 0, int.MaxValue);
                _plannerPreferences.RetainedMarketStockByProductId[productId] = (int)number;
                _communityOriginallyUncapped.Remove(productId);
            }
            else
            {
                if (!_plannerPreferences.ManualDestinationPricesByProductId.TryGetValue(productId, out var prices))
                    _plannerPreferences.ManualDestinationPricesByProductId[productId] = prices = [];
                prices[postId] = number;
                _communityOriginalFlags[(productId, postId)] = true;
            }
            value.RetainCurrentAsLocal(number);
            _plannerPreferences.MarketLocalTimes[$"{productId}:{postId}"] = value.LocalTime;
        }
        foreach (var value in _communityValues.Values) value.ClearRemote();
        _communityApplicationsEnabled = false;
        _pendingCommunitySnapshot = null;
        if (!_helpClosing) RefreshCommunityValues();
        SavePlannerPreferences();
    }

    private void SetCommunityEndpoint(string endpoint)
    {
        if (_communityEndpoint == endpoint) return;
        DisableCommunityApplication();
        _communityValues.Clear();
        _communityOriginalFlags.Clear();
        _communityOriginallyUncapped.Clear();
        _communityEndpoint = endpoint;
        RefreshCommunityProvenance();
    }

    private void ApplyPendingCommunitySnapshot()
    {
        if (_pendingCommunitySnapshot is not { } snapshot) return;
        _pendingCommunitySnapshot = null;
        if (_helpClosing) return;
        try { ApplyCommunitySnapshot(snapshot); }
        catch (Exception exception) when (IsCommunityError(exception))
        {
            SetStatus($"Community data was not applied: {exception.Message}", true);
        }
    }

    private static bool IsCommunityError(Exception exception) =>
        exception is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or
            ArgumentException or OperationCanceledException or KeyNotFoundException or FormatException or OverflowException;

    private static string CommunityNetworkError(Exception exception) =>
        NetworkErrorHelp.Describe(exception, new Uri(CommunityClient.ProductionEndpoint).Host);

    private void CommunityConsumption(GoodsEntry product, int quantity)
    {
        if (_communityValues.TryGetValue((product.Id, CommunityStockKey), out var value))
            value.Consume(quantity);
    }

    private static string CommunityObservationOrigin(CommunityObservation observation, bool manual) =>
        manual ? "Community manual" : observation.Review is not null ? "Community reviewed OCR" : "Community";

    private string CommunityProvenance(int productId, int postId)
    {
        if (!_communityValues.TryGetValue((productId, postId), out var value)) return string.Empty;
        return value.Time > 0
            ? $"{value.Origin}; {DateTimeOffset.FromUnixTimeMilliseconds(value.Time).ToLocalTime():G}"
            : value.Origin;
    }

    private bool LocalCommunityReadIsCurrent(int productId, int postId, long capturedAt) =>
        capturedAt <= 0 || !_communityValues.TryGetValue((productId, postId), out var value) ||
        capturedAt >= value.LocalTime;

    private void RefreshCommunityProvenance()
    {
        if (SelectedProduct is not { } product) return;
        var buy = CommunityProvenance(product.Id, CommunityBuyKey);
        var stock = CommunityProvenance(product.Id, CommunityStockKey);
        BuyPriceInput.ToolTip = buy.Length == 0 ? null : buy;
        StockInput.ToolTip = stock.Length == 0 ? null : $"Observed supply: {stock}";
    }
}
