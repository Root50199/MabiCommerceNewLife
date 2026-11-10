using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MabiCommerceNewLife;

public sealed record CommunityPrivacy(string ConsentVersion, int PrivateIpRetentionDays, string ObservationRetention);
public sealed record CommunityTicket(string ScanId, long ExpiresAt, Guid Session);
public sealed record CommunityPrice(int PostId, int SalePrice, int? Profit);
public sealed record CommunityReview(bool SourceManual, bool StockManual, IReadOnlyList<int> ManualPostIds);
public sealed record CommunityScan(string ScanId, string Mode, int ProductId, int SourcePostId,
    int? SourcePrice, int Stock, IReadOnlyList<CommunityPrice> Prices,
    string PriceBasis = "legacy", int? ObservedSourcePrice = null, decimal? PurchaseDiscountPercent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CommunityReview? Review = null);
public sealed record CommunityReceipt(long Sequence, long ReceivedAt, bool Duplicate);
public sealed record CommunityBatchReceipt(string ScanId, long Sequence, long ReceivedAt, bool Duplicate);
public sealed record CommunityObservation(long Sequence, long ReceivedAt, string Mode, int ProductId,
    int SourcePostId, int? SourcePrice, int Stock, IReadOnlyList<CommunityPrice> Prices,
    string PriceBasis = "legacy", int? ObservedSourcePrice = null, decimal? PurchaseDiscountPercent = null,
    CommunityReview? Review = null);
public sealed record CommunitySnapshot(IReadOnlyList<CommunityObservation> Observations, long Revision, long NextCursor = 0);
public sealed record CommunitySyncPage(IReadOnlyList<CommunityObservation> Observations, long NextCursor, long Revision, bool HasMore);

public sealed class CommunityApiException(string message, System.Net.HttpStatusCode status, string code,
    TimeSpan? retryAfter = null)
    : HttpRequestException(message, null, status)
{
    public string Code { get; } = code;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed class CommunityClient : IDisposable
{
    public const string ProductionEndpoint = "https://mabicommerce-community.mabi-projects.workers.dev/";
    public const string DefaultEndpoint = ProductionEndpoint;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly HttpClient _http;
    private string? _token;
    private Guid _session = Guid.NewGuid();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private CommunitySnapshot? _snapshot;
    private readonly CommunityCredentialStore? _credentials;
    public bool IsConnected => _token is not null;
    public Guid? ConnectedSession => IsConnected ? _session : null;
    public Uri Endpoint => _http.BaseAddress!;

    public CommunityClient(string endpoint, HttpMessageHandler? handler = null, string? credentialPath = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = ValidateEndpoint(endpoint),
            Timeout = TimeSpan.FromSeconds(5),
            MaxResponseContentBufferSize = 2 * 1024 * 1024
        };
        _http.DefaultRequestHeaders.Add("X-Community-Format", "normalized-v2");
        _credentials = credentialPath is null ? null : new CommunityCredentialStore(credentialPath);
    }

    public static Uri ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new ArgumentException("Sharing requires the approved production HTTPS address or an HTTP loopback test endpoint, without a path.");
        if (uri.AbsoluteUri == ProductionEndpoint) return uri;
        if (uri.Scheme == "http" && (uri.Host == "127.0.0.1" || uri.Host == "localhost"))
            return new UriBuilder(uri) { Host = "127.0.0.1" }.Uri;
        throw new ArgumentException("Sharing endpoint is not an approved production HTTPS or HTTP loopback address.");
    }

    public async Task<CommunityPrivacy> GetPrivacyAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/privacy");
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        // The policy has descriptive fields beyond the consent/retention values.
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var root = document.RootElement;
        var version = root.GetProperty("consentVersion").GetString();
        var retention = root.GetProperty("privateIpRetentionDays").GetInt32();
        var history = root.GetProperty("observationRetention").GetString();
        if (version != "2026-11-09" || retention != 30 || history != "indefinite" ||
            !root.TryGetProperty("installationAssociationRetentionDays", out var associationDays) ||
            associationDays.GetInt32() != 30 ||
            !root.TryGetProperty("installationAssociationExpiryScope", out var associationScope) ||
            associationScope.GetString() != "post_migration_observations")
            throw new InvalidDataException("Backend privacy policy differs from this client's consent text. Sharing remains off.");
        return new CommunityPrivacy(version, retention, history);
    }

    public async Task ConnectAsync(CommunityPrivacy acceptedPolicy, CancellationToken cancellationToken)
    {
        if (IsConnected) throw new InvalidOperationException("Already connected.");
        if (acceptedPolicy != new CommunityPrivacy("2026-11-09", 30, "indefinite"))
            throw new InvalidDataException("Unsupported consent policy.");
        cancellationToken.ThrowIfCancellationRequested();
        if (_credentials?.Load(Endpoint.AbsoluteUri, acceptedPolicy.ConsentVersion) is { } saved)
        {
            // Existing backend supports authenticated tickets, not a session-check endpoint.
            // Verify saved authorization without reading prices or submitting an observation.
            using var verify = JsonRequest(HttpMethod.Post, "v1/scan-tickets", new { });
            verify.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", saved.Token);
            var ticket = await SendAsync<TicketResponse>(verify, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateTicket(ticket);
            _token = saved.Token;
            _session = Guid.NewGuid();
            return;
        }
        using var request = JsonRequest(HttpMethod.Post, "v1/installations",
            new { consentVersion = acceptedPolicy.ConsentVersion, acceptPrivacy = true });
        var registration = await SendAsync<Registration>(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParse(registration.InstallationId, out var installationId) || installationId == Guid.Empty ||
            registration.ConsentVersion != acceptedPolicy.ConsentVersion ||
            registration.Token is null || registration.Token.Length != 64 ||
            registration.Token.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new InvalidDataException("Backend returned invalid registration credentials.");
        _credentials?.Save(new CommunityCredentialStore.Credentials
        {
            Endpoint = Endpoint.AbsoluteUri,
            ConsentVersion = registration.ConsentVersion,
            InstallationId = registration.InstallationId,
            Token = registration.Token
        });
        _token = registration.Token;
        _session = Guid.NewGuid();
    }

    public void Disconnect()
    {
        _token = null;
        _session = Guid.NewGuid();
    }

    public bool IsCurrentSession(CommunityTicket ticket) => IsConnected && ticket.Session == _session;

    public async Task<CommunityTicket> BeginScanAsync(CancellationToken cancellationToken)
    {
        var session = _session;
        using var request = Authorized(HttpMethod.Post, "v1/scan-tickets", new { });
        var ticket = await SendAsync<TicketResponse>(request, cancellationToken);
        ValidateTicket(ticket);
        if (!IsConnected || session != _session) throw new InvalidOperationException("Sharing was disconnected.");
        return new CommunityTicket(ticket.ScanId, ticket.ExpiresAt, session);
    }

    public async Task<CommunityReceipt> UploadAsync(CommunityTicket ticket, CommunityScan scan, CancellationToken cancellationToken)
    {
        if (!IsConnected || ticket.Session != _session) throw new InvalidOperationException("Sharing was disconnected.");
        if (ticket.ScanId != scan.ScanId || ticket.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new InvalidOperationException("Scan upload ticket expired. No upload was queued; scan again.");
        using var request = Authorized(HttpMethod.Post, "v1/scans", scan);
        var receipt = await SendAsync<CommunityReceipt>(request, cancellationToken);
        if (receipt.Sequence <= 0 || receipt.ReceivedAt <= 0)
            throw new InvalidDataException("Backend returned an invalid scan receipt.");
        return receipt;
    }

    public async Task<CommunitySnapshot> ReadLatestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/latest");
        var snapshot = await SendAsync<CommunitySnapshot>(request, cancellationToken);
        ValidateObservations(snapshot.Observations);
        if (snapshot.Revision < 0 || snapshot.NextCursor < 0 || snapshot.Observations.Count > 500 ||
            snapshot.Observations.Select(o => (o.Mode, o.ProductId)).Distinct().Count() != snapshot.Observations.Count)
            throw new InvalidDataException("Backend returned invalid snapshot metadata.");
        return snapshot;
    }

    public async Task<IReadOnlyList<CommunityBatchReceipt>> UploadBatchAsync(
        CommunityPendingBatch batch, Guid session, CancellationToken cancellationToken)
    {
        if (!IsConnected || session != _session) throw new InvalidOperationException("Sharing was disconnected.");
        if (batch.Scans.Count is < 1 or > CommunityScanQueue.MaxBatchScans ||
            batch.Scans.Any(scan => scan.Mode != batch.Outpost.Mode || scan.SourcePostId != batch.Outpost.PostId) ||
            batch.Scans.Select(scan => scan.ScanId).Distinct().Count() != batch.Scans.Count ||
            batch.Scans.Select(scan => scan.ProductId).Distinct().Count() != batch.Scans.Count)
            throw new InvalidOperationException("Invalid outpost sharing batch.");
        using var request = Authorized(HttpMethod.Post, "v1/scan-batches", new { scans = batch.Scans });
        var response = await SendAsync<BatchResponse>(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected || session != _session) throw new InvalidOperationException("Sharing was disconnected.");
        if (response.Receipts is null || response.Receipts.Count != batch.Scans.Count ||
            response.Receipts.Any(receipt => receipt is null || receipt.Sequence <= 0 ||
                receipt.ReceivedAt is <= 0 or > 253402300799999) ||
            response.Receipts.Select(receipt => receipt.Sequence).Distinct().Count() != batch.Scans.Count ||
            !response.Receipts.Select(receipt => receipt.ScanId).Order()
                .SequenceEqual(batch.Scans.Select(scan => scan.ScanId).Order()))
            throw new InvalidDataException("Backend returned invalid batch receipts. Pending scans were kept for manual retry.");
        return response.Receipts;
    }

    private static void ValidateObservations(IReadOnlyList<CommunityObservation> observations)
    {
        if (observations is null || observations.Any(o => o is null || o.Sequence <= 0 ||
                o.ReceivedAt is <= 0 or > 253402300799999 ||
                o.Mode is not ("trade" or "group" or "barter") || o.ProductId <= 0 || o.SourcePostId <= 0 ||
                o.Stock is < 0 or > 1_000_000_000 || o.Prices is null || o.Prices.Count is < 1 or > 13 ||
                o.SourcePrice is > 1_000_000_000 || o.ObservedSourcePrice is > 1_000_000_000 ||
                o.PriceBasis is not ("legacy" or "normalized-v1") ||
                (o.Mode == "barter" ? o.SourcePrice is not null : o.SourcePrice is null or <= 0) ||
                (o.PriceBasis == "normalized-v1" && (o.PurchaseDiscountPercent is null or < 0 or > 99 ||
                    (o.Mode == "barter" ? o.ObservedSourcePrice is not null || o.PurchaseDiscountPercent != 0 :
                        o.ObservedSourcePrice is null or <= 0 ||
                        CommunityPurchasePrice.Normalize(o.ObservedSourcePrice.Value, o.PurchaseDiscountPercent.Value) != o.SourcePrice))) ||
                o.Prices.Any(p => p is null || p.PostId < 0 || p.SalePrice is < 0 or > 1_000_000_000 ||
                    p.Profit is < -1_000_000_000 or > 1_000_000_000 ||
                    (o.Mode == "barter" ? p.Profit is not null :
                        p.Profit is null ? o.Review is null :
                        p.SalePrice - (long)p.Profit.Value != (o.PriceBasis == "legacy" ? o.SourcePrice : o.ObservedSourcePrice))) ||
                (o.Review is { } review && (o.PriceBasis != "normalized-v1" || review.ManualPostIds is null ||
                    (o.Mode == "barter" && review.SourceManual) ||
                    review.ManualPostIds.Distinct().Count() != review.ManualPostIds.Count ||
                    review.ManualPostIds.Any(id => !o.Prices.Any(p => p.PostId == id)))) ||
                o.Prices.Select(p => p.PostId).Distinct().Count() != o.Prices.Count))
            throw new InvalidDataException("Backend returned an invalid observation snapshot. Nothing was applied.");
    }

    public async Task<CommunitySnapshot> SyncAsync(CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            if (_snapshot is null)
            {
                var initial = await ReadLatestAsync(cancellationToken);
                if (initial.Observations.Any(o => o.Sequence > initial.NextCursor))
                    throw new InvalidDataException("Snapshot cursor precedes its observations.");
                return _snapshot = initial;
            }
            var cursor = _snapshot.NextCursor;
            var revision = _snapshot.Revision;
            var staged = _snapshot.Observations.ToDictionary(o => (o.Mode, o.ProductId));
            for (var pageNumber = 0; pageNumber < 100; pageNumber++)
            {
                CommunitySyncPage page;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get,
                        $"v1/sync?cursor={cursor}&revision={revision}&limit=200");
                    page = await SendAsync<CommunitySyncPage>(request, cancellationToken);
                }
                catch (CommunityApiException exception) when (exception.Code == "sync_reset_required")
                {
                    var reset = await ReadLatestAsync(cancellationToken);
                    if (reset.Observations.Any(o => o.Sequence > reset.NextCursor))
                        throw new InvalidDataException("Reset cursor precedes its observations.");
                    return _snapshot = reset;
                }
                ValidateObservations(page.Observations);
                if (page.Revision != revision || page.NextCursor < cursor || page.Observations.Count > 200 ||
                    page.Observations.Any(o => o.Sequence <= cursor || o.Sequence > page.NextCursor) ||
                    !page.Observations.Select(o => o.Sequence).SequenceEqual(page.Observations.Select(o => o.Sequence).Order().Distinct()) ||
                    (page.HasMore && page.NextCursor == cursor))
                    throw new InvalidDataException("Invalid incremental sync page. Cursor was not advanced.");
                foreach (var observation in page.Observations)
                    staged[(observation.Mode, observation.ProductId)] = observation;
                cursor = page.NextCursor;
                if (!page.HasMore)
                    return _snapshot = new CommunitySnapshot(staged.Values.OrderBy(o => o.Sequence).ToArray(), revision, cursor);
            }
            throw new InvalidOperationException("Sync exceeded its page limit. Cursor was not advanced; sync again.");
        }
        finally { _syncLock.Release(); }
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path, object body)
    {
        if (_token is null) throw new InvalidOperationException("Sharing is off.");
        var request = JsonRequest(method, path, body);
        request.Headers.Authorization = new("Bearer", _token);
        return request;
    }

    private static HttpRequestMessage JsonRequest(HttpMethod method, string path, object body) =>
        new(method, path) { Content = JsonContent.Create(body, options: JsonOptions) };

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Backend returned an empty JSON response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var code = document.RootElement.GetProperty("error").GetString();
        TimeSpan? retryAfter = null;
        if (response.Headers.TryGetValues("Retry-After", out var values) &&
            System.Net.Http.Headers.RetryConditionHeaderValue.TryParse(
                string.Join(", ", values), out var retry))
        {
            retryAfter = retry.Delta ?? retry.Date - (response.Headers.Date ?? DateTimeOffset.UtcNow);
            if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
        }
        throw new CommunityApiException($"Community backend returned {(int)response.StatusCode}: {code}. Local planning is unchanged.",
            response.StatusCode, code ?? "unknown_error", retryAfter);
    }

    public void Dispose()
    {
        Disconnect();
        _http.Dispose();
    }

    private sealed record Registration(string InstallationId, string Token, string ConsentVersion);
    private sealed record TicketResponse(string ScanId, long ExpiresAt);
    private sealed record BatchResponse(IReadOnlyList<CommunityBatchReceipt> Receipts);

    private static void ValidateTicket(TicketResponse ticket)
    {
        if (!Guid.TryParse(ticket.ScanId, out var id) || id == Guid.Empty ||
            ticket.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new InvalidDataException("Backend returned an invalid or expired scan ticket.");
    }
}
