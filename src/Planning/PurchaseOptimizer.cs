namespace MabiCommerceNewLife;

public enum PurchaseObjective { NetProfit, ProfitPerMinute, ProfitPerMapUnit }

public sealed record PurchaseGood(
    int Id, int SourcePostId, int Stock, decimal BuyPrice, int Weight, int UnitsPerSlot,
    IReadOnlyDictionary<int, decimal> SellPrices, bool IsGroupGood = false,
    bool IsLocked = false, int? WeeklyRemaining = null, bool IsBuyPriceEstimated = false,
    IReadOnlySet<int>? EstimatedSellDestinations = null);

public sealed record PurchaseTransport(int Id, int Slots, int Weight, bool IsAvailable = true, bool IsFlight = false);

public sealed record PurchaseDestination(int Id, decimal? Minutes = null, decimal? MapDistance = null);

public sealed record PurchaseRequest(
    int SourcePostId, decimal Funds, IReadOnlyList<PurchaseGood> Goods,
    IReadOnlyList<PurchaseTransport> Transports, IReadOnlyList<PurchaseDestination> Destinations,
    PurchaseObjective Objective = PurchaseObjective.NetProfit, bool GroupMode = false,
    int MaxSearchNodesPerPair = 200_000, int MaxTotalSearchNodes = 2_000_000,
    IReadOnlyDictionary<(int DestinationId, int TransportId), decimal>? TransportMinutes = null);

public sealed record PurchaseLine(int GoodId, int Quantity, int Slots, int Weight, decimal Cost, decimal Sale);

public sealed record PurchasePlan(
    int DestinationId, int TransportId, IReadOnlyList<PurchaseLine> Lines,
    ProfitResult Profit, int UsedSlots, int UsedWeight, decimal Score, bool HasEstimatedPrices);

public sealed record PurchaseSearchResult(IReadOnlyList<PurchasePlan> Plans, bool IsExhaustive, long VisitedNodes)
{
    public PurchasePlan? Best => Plans.Count > 0 ? Plans[0] : null;
}

public static class PurchaseOptimizer
{
    public static PurchaseSearchResult Search(PurchaseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SourcePostId <= 0 || request.Funds < 0 || request.MaxSearchNodesPerPair <= 0 || request.MaxTotalSearchNodes <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Source must be positive, funds non-negative and search budgets positive.");
        if (request.Goods.Select(good => good.Id).Distinct().Count() != request.Goods.Count)
            throw new ArgumentException("Each good must have a unique ID.", nameof(request));

        var workItems = new List<SearchWork>();
        foreach (var destination in request.Destinations.DistinctBy(destination => destination.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (destination.Id == request.SourcePostId) continue;
            var candidates = request.Goods
                .Where(good => good.SourcePostId == request.SourcePostId &&
                    good.IsGroupGood == request.GroupMode && !good.IsLocked &&
                    good.Stock > 0 && good.BuyPrice > 0 && good.Weight > 0 && good.UnitsPerSlot > 0 &&
                    good.SellPrices.TryGetValue(destination.Id, out var sale) && sale > good.BuyPrice &&
                    good.WeeklyRemaining is null or > 0)
                .Select(good => new Candidate(good, good.SellPrices[destination.Id],
                    Math.Min(good.Stock, good.WeeklyRemaining ?? good.Stock),
                    good.IsBuyPriceEstimated || good.EstimatedSellDestinations?.Contains(destination.Id) == true))
                .OrderByDescending(candidate => candidate.Margin / candidate.Good.Weight)
                .ThenBy(candidate => candidate.Good.Id)
                .ToArray();
            if (candidates.Length == 0) continue;

            foreach (var transport in request.Transports.Where(transport => transport.IsAvailable &&
                         (request.GroupMode || !transport.IsFlight) && transport.Slots > 0 && transport.Weight > 0)
                         .DistinctBy(transport => transport.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var goods = PruneDominatedCandidates(candidates, request.Funds, transport);
                if (goods.Length == 0) continue;
                var divisor = request.Objective switch
                {
                    PurchaseObjective.ProfitPerMinute when request.TransportMinutes is not null =>
                        request.TransportMinutes.TryGetValue((destination.Id, transport.Id), out var minutes) ? minutes : null,
                    PurchaseObjective.ProfitPerMinute => destination.Minutes,
                    PurchaseObjective.ProfitPerMapUnit => destination.MapDistance,
                    _ => 1m
                };
                if (divisor is not > 0) continue;
                workItems.Add(new SearchWork(destination, goods, transport, divisor.Value));
            }
        }

        if (workItems.Count == 0) return new PurchaseSearchResult([], true, 0);
        var selectedWorkItems = workItems.Take(request.MaxTotalSearchNodes).ToArray();
        var allWorkScheduled = selectedWorkItems.Length == workItems.Count;
        var perPairBudget = Math.Min(request.MaxSearchNodesPerPair,
            Math.Max(1, request.MaxTotalSearchNodes / selectedWorkItems.Length));
        var results = new System.Collections.Concurrent.ConcurrentBag<SearchWorkResult>();
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
        };

        Parallel.ForEach(selectedWorkItems, options, work =>
        {
            var pair = SearchPair(work.Goods, request.Funds, work.Transport, perPairBudget, cancellationToken);
            if (pair.Lines.Count == 0)
            {
                results.Add(new SearchWorkResult(null, pair.IsExhaustive, pair.VisitedNodes));
                return;
            }

            var plan = new PurchasePlan(work.Destination.Id, work.Transport.Id, pair.Lines, pair.Profit,
                pair.Lines.Sum(line => line.Slots), pair.Lines.Sum(line => line.Weight),
                pair.Profit.NetProfit / work.Divisor,
                pair.Lines.Any(line => work.Goods.First(candidate => candidate.Good.Id == line.GoodId).HasEstimatedPrice));
            results.Add(new SearchWorkResult(plan, pair.IsExhaustive, pair.VisitedNodes));
        });

        var completed = results.ToArray();
        var plans = completed.Where(result => result.Plan is not null).Select(result => result.Plan!).ToArray();
        return new PurchaseSearchResult(plans.OrderByDescending(plan => plan.Score)
            .ThenBy(plan => plan.Profit.PurchaseCost).ThenBy(plan => plan.DestinationId)
            .ThenBy(plan => plan.TransportId).ToArray(),
            allWorkScheduled && completed.All(result => result.IsExhaustive),
            completed.Sum(result => (long)result.VisitedNodes));
    }

    private static Candidate[] PruneDominatedCandidates(Candidate[] candidates, decimal funds, PurchaseTransport transport)
    {
        return candidates.Where(candidate => !candidates.Any(other =>
            other.Good.Id != candidate.Good.Id &&
            other.HasEstimatedPrice == candidate.HasEstimatedPrice &&
            other.Margin >= candidate.Margin &&
            other.Good.BuyPrice <= candidate.Good.BuyPrice &&
            other.Good.Weight <= candidate.Good.Weight &&
            other.Good.UnitsPerSlot >= candidate.Good.UnitsPerSlot &&
            (long)other.Stock >= (long)MaxQuantity(other, funds, transport.Weight, transport.Slots) + candidate.Stock &&
            (other.Margin > candidate.Margin || other.Good.BuyPrice < candidate.Good.BuyPrice ||
             other.Good.Weight < candidate.Good.Weight || other.Good.UnitsPerSlot > candidate.Good.UnitsPerSlot ||
             other.Stock > candidate.Stock)))
            .OrderByDescending(candidate => candidate.Margin / candidate.Good.Weight)
            .ThenBy(candidate => candidate.Good.Id)
            .ToArray();
    }

    private static PairResult SearchPair(Candidate[] goods, decimal funds, PurchaseTransport transport,
        int nodeLimit, CancellationToken cancellationToken)
    {
        var quantities = new int[goods.Length];
        var bestQuantities = new int[goods.Length];
        var bestProfit = 0m;
        var visited = 0;
        var exhaustive = true;

        foreach (var order in new[]
        {
            Enumerable.Range(0, goods.Length).ToArray(),
            Enumerable.Range(0, goods.Length).OrderByDescending(index => goods[index].Margin / goods[index].Good.BuyPrice).ToArray(),
            Enumerable.Range(0, goods.Length).OrderByDescending(index => goods[index].Margin).ToArray()
        })
        {
            var seed = new int[goods.Length];
            var cashLeft = funds;
            var weightLeft = transport.Weight;
            var slotsLeft = transport.Slots;
            var seedProfit = 0m;
            foreach (var index in order)
            {
                var candidate = goods[index];
                var count = MaxQuantity(candidate, cashLeft, weightLeft, slotsLeft);
                seed[index] = count;
                cashLeft -= count * candidate.Good.BuyPrice;
                weightLeft -= count * candidate.Good.Weight;
                slotsLeft -= count == 0 ? 0 : 1 + (count - 1) / candidate.Good.UnitsPerSlot;
                seedProfit += count * candidate.Margin;
            }
            if (seedProfit <= bestProfit) continue;
            bestProfit = seedProfit;
            Array.Copy(seed, bestQuantities, seed.Length);
        }

        void Visit(int index, decimal cashLeft, int weightLeft, int slotsLeft, decimal profit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visited >= nodeLimit) { exhaustive = false; return; }
            visited++;
            if (profit > bestProfit)
            {
                bestProfit = profit;
                Array.Copy(quantities, bestQuantities, quantities.Length);
            }
            if (index == goods.Length || slotsLeft == 0) return;

            decimal upperBound = profit;
            for (var next = index; next < goods.Length; next++)
            {
                var candidate = goods[next];
                var count = MaxQuantity(candidate, cashLeft, weightLeft, slotsLeft);
                upperBound += count * candidate.Margin;
            }
            if (upperBound <= bestProfit) return;

            var good = goods[index];
            for (var count = MaxQuantity(good, cashLeft, weightLeft, slotsLeft); count >= 0; count--)
            {
                quantities[index] = count;
                var slots = count == 0 ? 0 : 1 + (count - 1) / good.Good.UnitsPerSlot;
                Visit(index + 1, cashLeft - count * good.Good.BuyPrice,
                    weightLeft - count * good.Good.Weight, slotsLeft - slots, profit + count * good.Margin);
                if (!exhaustive) break;
            }
            quantities[index] = 0;
        }

        Visit(0, funds, transport.Weight, transport.Slots, 0);
        var lines = goods.Select((candidate, index) => (candidate, quantity: bestQuantities[index]))
            .Where(entry => entry.quantity > 0)
            .Select(entry => new PurchaseLine(entry.candidate.Good.Id, entry.quantity,
                1 + (entry.quantity - 1) / entry.candidate.Good.UnitsPerSlot,
                entry.quantity * entry.candidate.Good.Weight,
                entry.quantity * entry.candidate.Good.BuyPrice,
                entry.quantity * entry.candidate.SalePrice)).ToArray();
        var profit = ProfitCalculator.Calculate(goods.Select((candidate, index) =>
            new ProfitLine(bestQuantities[index], candidate.Good.BuyPrice, candidate.SalePrice)));
        return new PairResult(lines, profit, exhaustive, visited);
    }

    private static int MaxQuantity(Candidate candidate, decimal cash, int weight, int slots)
    {
        var good = candidate.Good;
        var limit = Math.Min(candidate.Stock, Math.Min((long)weight / good.Weight, (long)slots * good.UnitsPerSlot));
        if (limit == 0) return 0;
        if (cash / limit >= good.BuyPrice) return (int)limit;
        return (int)decimal.Floor(cash / good.BuyPrice);
    }

    private sealed record Candidate(PurchaseGood Good, decimal SalePrice, int Stock, bool HasEstimatedPrice)
    {
        public decimal Margin => SalePrice - Good.BuyPrice;
    }

    private sealed record SearchWork(PurchaseDestination Destination, Candidate[] Goods, PurchaseTransport Transport, decimal Divisor);
    private sealed record SearchWorkResult(PurchasePlan? Plan, bool IsExhaustive, int VisitedNodes);

    private sealed record PairResult(IReadOnlyList<PurchaseLine> Lines, ProfitResult Profit, bool IsExhaustive, int VisitedNodes);
}