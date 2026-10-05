namespace MabiCommerceNewLife;

public readonly record struct ProfitLine(int Quantity, decimal BuyPrice, decimal SalePrice);

public readonly record struct ProfitResult(decimal GrossSale, decimal PurchaseCost, decimal NetProfit)
{
    public decimal? PerMinute(decimal? minutes) => minutes > 0 ? NetProfit / minutes.Value : null;
    public decimal? PerDistance(decimal? distance) => distance > 0 ? NetProfit / distance.Value : null;
}

public static class ProfitCalculator
{
    public static ProfitResult Calculate(IEnumerable<ProfitLine> lines)
    {
        var grossSale = 0m;
        var purchaseCost = 0m;
        foreach (var line in lines)
        {
            grossSale += line.Quantity * line.SalePrice;
            purchaseCost += line.Quantity * line.BuyPrice;
        }

        return new ProfitResult(grossSale, purchaseCost, grossSale - purchaseCost);
    }
}

public static class CommerceRewardCalculator
{
    private const decimal SeasonalScoreProfitDivisor = 17m;

    public static decimal CalculateUnbuffedRawGold(ProfitResult profit) => profit.NetProfit;

    public static decimal CalculateUnbuffedSeasonalScore(IEnumerable<decimal> itemRawProfits)
    {
        ArgumentNullException.ThrowIfNull(itemRawProfits);
        return itemRawProfits.Sum(itemProfit => decimal.Floor(itemProfit / SeasonalScoreProfitDivisor));
    }
}