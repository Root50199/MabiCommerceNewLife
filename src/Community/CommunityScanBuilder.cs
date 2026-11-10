namespace MabiCommerceNewLife;

public static class CommunityScanBuilder
{
    public static CommunityScan BuildReviewed(CommunityTicket ticket, string mode, int productId, int sourcePostId,
        decimal? sourcePrice, int? stock, IReadOnlyList<PriceListOcrCandidate> candidates,
        IReadOnlyCollection<int> expectedPostIds, decimal purchaseDiscountPercent, CommunityReview review)
        => BuildReviewed(ticket.ScanId, mode, productId, sourcePostId, sourcePrice, stock, candidates,
            expectedPostIds, purchaseDiscountPercent, review);

    public static CommunityScan BuildReviewed(string scanId, string mode, int productId, int sourcePostId,
        decimal? sourcePrice, int? stock, IReadOnlyList<PriceListOcrCandidate> candidates,
        IReadOnlyCollection<int> expectedPostIds, decimal purchaseDiscountPercent, CommunityReview review)
    {
        if (mode is not ("trade" or "group" or "barter") || productId <= 0 || sourcePostId <= 0)
            throw new InvalidOperationException("Not shared: invalid reviewed goods identifiers.");
        if (stock is null or < 0 or > 1_000_000_000)
            throw new InvalidOperationException("Not shared: review must confirm Inventory stock.");
        int? source = mode == "barter" ? null : Whole(sourcePrice ??
            throw new InvalidOperationException("Not shared: review must confirm the source price."));
        if (source is <= 0) throw new InvalidOperationException("Not shared: invalid reviewed source price.");
        if (candidates.Any(c => !c.Include || c.Price <= 0) ||
            candidates.Select(c => c.PostId).Distinct().Count() != candidates.Count ||
            !expectedPostIds.Order().SequenceEqual(candidates.Select(c => c.PostId).Where(id => id != -1).Order()))
            throw new InvalidOperationException("Not shared: confirm every destination; partial reviews stay local.");
        var prices = candidates.Select(c =>
        {
            var sale = Whole(c.Price);
            if (c.PostId < -1 || (mode == "group" && c.PostId == -1))
                throw new InvalidOperationException("Not shared: invalid reviewed destination.");
            // A review confirms the sale, not an invented/recalculated OCR profit.
            int? profit = mode != "barter" && c.ObservedProfit is { } observed &&
                sale - observed == source ? Whole(observed) : null;
            return new CommunityPrice(c.PostId == -1 ? 0 : c.PostId, sale, profit);
        }).OrderBy(p => p.PostId).ToArray();
        if (prices.Length is < 1 or > 13 || review.ManualPostIds.Distinct().Count() != review.ManualPostIds.Count ||
            review.ManualPostIds.Any(id => !prices.Any(p => p.PostId == id)) ||
            (mode == "barter" && review.SourceManual))
            throw new InvalidOperationException("Not shared: invalid review provenance.");
        var discount = mode == "barter" ? 0 : purchaseDiscountPercent;
        var normalized = source is null ? null : (int?)CommunityPurchasePrice.Normalize(source.Value, discount);
        return new CommunityScan(scanId, mode, productId, sourcePostId, normalized, stock.Value,
            prices, "normalized-v1", source, discount, review);
    }

    public static CommunityScan Build(CommunityTicket ticket, string mode, int productId, int sourcePostId,
        bool autoAccepted, OcrSourcePrice? sourcePrice, OcrStockRead? stock,
        IReadOnlyList<PriceListOcrCandidate> candidates, IReadOnlyCollection<int> expectedPostIds,
        decimal purchaseDiscountPercent = 0)
        => Build(ticket.ScanId, mode, productId, sourcePostId, autoAccepted, sourcePrice, stock, candidates,
            expectedPostIds, purchaseDiscountPercent);

    public static CommunityScan Build(string scanId, string mode, int productId, int sourcePostId,
        bool autoAccepted, OcrSourcePrice? sourcePrice, OcrStockRead? stock,
        IReadOnlyList<PriceListOcrCandidate> candidates, IReadOnlyCollection<int> expectedPostIds,
        decimal purchaseDiscountPercent = 0)
    {
        if (!autoAccepted || stock is not { IsReliable: true, Count: >= 0 and <= 1_000_000_000 })
            throw new InvalidOperationException("Not shared: automatic upload needs price checks and reliable Inventory stock; otherwise confirm a complete OCR review.");
        if (mode is not ("trade" or "group" or "barter"))
            throw new InvalidOperationException("Not shared: unrecognized goods mode.");
        if (productId <= 0 || sourcePostId <= 0)
            throw new InvalidOperationException("Not shared: invalid product or source identifier.");
        int? source = mode == "barter" ? null : Whole(sourcePrice?.Price
            ?? throw new InvalidOperationException("Not shared: source price was not independently read."));
        if (source is <= 0) throw new InvalidOperationException("Not shared: source price is invalid.");
        var prices = candidates.Select(candidate =>
        {
            if (!candidate.Include || !candidate.IsCrossChecked)
                throw new InvalidOperationException("Not shared: every destination must pass its OCR checks.");
            var sale = Whole(candidate.Price);
            if (sale < 0 || candidate.PostId < -1 || (mode == "group" && candidate.PostId == -1))
                throw new InvalidOperationException("Not shared: invalid sale price or destination.");
            int? profit = mode == "barter" ? null : Whole(candidate.ObservedProfit
                ?? throw new InvalidOperationException("Not shared: an independently read profit is missing."));
            if (source is not null && sale - (long)profit!.Value != source)
                throw new InvalidOperationException("Not shared: exact sale/profit/source arithmetic disagrees. Local scan behavior is unchanged.");
            return new CommunityPrice(candidate.PostId == -1 ? 0 : candidate.PostId, sale, profit);
        }).OrderBy(p => p.PostId).ToArray();
        if (prices.Length is < 1 or > 13 || candidates.Select(p => p.PostId).Distinct().Count() != prices.Length ||
            !expectedPostIds.Order().SequenceEqual(candidates.Select(p => p.PostId).Where(id => id != -1).Order()))
            throw new InvalidOperationException("Not shared: incomplete or duplicate destination list.");
        var discount = mode == "barter" ? 0 : purchaseDiscountPercent;
        var normalized = source is null ? null : (int?)CommunityPurchasePrice.Normalize(source.Value, discount);
        return new CommunityScan(scanId, mode, productId, sourcePostId, normalized,
            stock.Count.Value, prices, "normalized-v1", source, discount);
    }

    private static int Whole(decimal number)
    {
        if (number != decimal.Truncate(number) || number is < -1_000_000_000 or > 1_000_000_000)
            throw new InvalidOperationException("Not shared: price/profit must be a whole number within backend limits.");
        return (int)number;
    }
}
