namespace MabiCommerceNewLife;

public static class CommunityPurchasePrice
{
    public static int Normalize(int observedPrice, decimal discountPercent) =>
        Round(observedPrice / Factor(discountPercent));

    public static int ForRecipient(int basePrice, decimal discountPercent) =>
        Round(basePrice * Factor(discountPercent));

    private static decimal Factor(decimal discountPercent)
    {
        if (discountPercent < 0 || discountPercent > 99 || decimal.Round(discountPercent, 2) != discountPercent)
            throw new InvalidOperationException("Purchase discount must be between 0 and 99 percent, with at most two decimal places.");
        return (100m - discountPercent) / 100m;
    }

    private static int Round(decimal price)
    {
        var result = decimal.Round(price, 0, MidpointRounding.AwayFromZero);
        if (result is < 1 or > 1_000_000_000)
            throw new InvalidOperationException("Purchase price is outside the supported range.");
        return (int)result;
    }
}
