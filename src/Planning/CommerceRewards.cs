using System.Globalization;
using System.Text;

namespace MabiCommerceNewLife;

public enum GuaranteeLetterKind { None, Ogre, Goblin, Imp, OgreFine, GoblinFine, ImpFine, IrusanSpecial }

// Percentages come from the client letter descriptions; Irusan's Special letter is not in the client tables and uses the wiki values.
public sealed record GuaranteeLetterInfo(GuaranteeLetterKind Kind, string Name, int NormalPercent, int BarterPercent,
    bool BoostsGold, int DucatsGranted);

public static class GuaranteeLetters
{
    // A blank or 0 market value turns the letter off for automated selection; the value also prices using a held letter.
    public static bool IsAvailable(int? marketValue) => marketValue is > 0;

    public static IReadOnlyList<GuaranteeLetterInfo> All { get; } =
    [
        new(GuaranteeLetterKind.Ogre, "Ogre's Letter of Guarantee", 30, 15, true, 300),
        new(GuaranteeLetterKind.Goblin, "Goblin's Letter of Guarantee", 40, 20, true, 400),
        new(GuaranteeLetterKind.Imp, "Imp's Letter of Guarantee", 50, 25, true, 500),
        new(GuaranteeLetterKind.OgreFine, "Ogre's Fine Letter of Guarantee", 90, 45, true, 900),
        new(GuaranteeLetterKind.GoblinFine, "Goblin's Fine Letter of Guarantee", 120, 60, true, 1_200),
        new(GuaranteeLetterKind.ImpFine, "Imp's Fine Letter of Guarantee", 150, 75, true, 1_500),
        new(GuaranteeLetterKind.IrusanSpecial, "Irusan's Special Letter of Guarantee", 300, 75, false, 3_000)
    ];

    public static GuaranteeLetterInfo? Get(GuaranteeLetterKind kind) => All.FirstOrDefault(letter => letter.Kind == kind);
}

// PurchaseDiscountPercent is the rating + equipment discount already inside BuyPrice; only the seasonal score undoes it.
public readonly record struct RewardLine(int Quantity, decimal BuyPrice, decimal SalePrice, decimal Weight,
    decimal PurchaseDiscountPercent = 0);

// ExtraDucatPercent adds Ducats only (titles, William); ExtraProfitPercent raises profit like Commerce Mastery (group buffs).
public sealed record RewardModifiers(string MasteryRank, GuaranteeLetterKind Letter, decimal GoldPerDucat, int LetterSupply = -1,
    decimal LetterMarketValue = 0, decimal ExtraDucatPercent = 0, decimal ExtraProfitPercent = 0, decimal MerchantRatingPercent = 0);

public sealed record CommerceRewardBreakdown(
    bool IsBarter,
    string MasteryRank,
    int MasteryPercent,
    GuaranteeLetterInfo? Letter,
    bool LetterApplied,
    string? LetterNote,
    decimal GrossSale,
    decimal PurchaseCost,
    decimal BaseProfit,
    decimal MasteryBonus,
    decimal LetterBonus,
    decimal LetterGoldBonus,
    decimal LetterDucats,
    decimal LetterCostGold,
    decimal DucatGain,
    decimal RawGold,
    decimal GoldPerDucat,
    decimal MaterialGold,
    decimal MerchantRating,
    decimal Exp,
    decimal SeasonalScore,
    decimal ExtraDucatPercent = 0,
    decimal ExtraDucatBonus = 0,
    decimal ExtraProfitPercent = 0,
    decimal ExtraProfitBonus = 0)
{
    public decimal DucatGold => DucatGain * GoldPerDucat;

    public decimal TotalGold => RawGold + DucatGold - MaterialGold - LetterCostGold;
}

public static class CommerceRewardModel
{
    private static readonly string[] MasteryRanks = ["N", "F", "E", "D", "C", "B", "A", "9", "8", "7", "6", "5", "4", "3", "2", "1"];

    // Commerce Mastery "Additional Rewards Earned": rank N 0% through rank 1 15%.
    public static int MasteryPercent(string? rank)
    {
        var index = Array.IndexOf(MasteryRanks, rank?.Trim() ?? string.Empty);
        return index < 0 ? 0 : index;
    }

    // perLoadLetterEffects adds the letter's Ducats on use and subtracts its market value once; per-unit views leave both out.
    public static CommerceRewardBreakdown Calculate(IEnumerable<RewardLine> lines, bool isBarter, RewardModifiers modifiers,
        bool perLoadLetterEffects = true)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(modifiers);
        var lineList = lines.ToList();
        var rate = modifiers.GoldPerDucat > 0 ? modifiers.GoldPerDucat : 1m;
        var grossSale = lineList.Sum(line => line.Quantity * line.SalePrice);
        var purchaseCost = lineList.Sum(line => line.Quantity * line.BuyPrice);
        // Barter goods cost materials, not Ducats, so the whole sale counts as commerce profit.
        var baseProfit = isBarter ? grossSale : grossSale - purchaseCost;
        var rewardBase = Math.Max(0m, baseProfit);
        var masteryPercent = MasteryPercent(modifiers.MasteryRank);
        var masteryBonus = decimal.Floor(rewardBase * masteryPercent / 100m);

        var letter = GuaranteeLetters.Get(modifiers.Letter);
        string? letterNote = null;
        var letterApplied = letter is not null;
        if (letter is not null && modifiers.LetterSupply == 0)
        {
            letterApplied = false;
            letterNote = $"No {letter.Name} left in your inventory supply.";
        }
        else if (letter is not null && rewardBase <= 0)
        {
            letterApplied = false;
            letterNote = "A Letter of Guarantee only boosts a profitable sale.";
        }

        var letterPercent = letterApplied ? (isBarter ? letter!.BarterPercent : letter!.NormalPercent) : 0;
        var letterBonus = decimal.Floor(rewardBase * letterPercent / 100m);
        var letterGoldBonus = letterApplied && letter!.BoostsGold ? letterBonus : 0m;
        var letterDucats = letterApplied && perLoadLetterEffects ? letter!.DucatsGranted : 0m;
        var letterCost = letterApplied && perLoadLetterEffects ? Math.Max(0m, modifiers.LetterMarketValue) : 0m;
        var extraDucatPercent = Math.Max(0m, modifiers.ExtraDucatPercent);
        var extraProfitPercent = Math.Max(0m, modifiers.ExtraProfitPercent);
        var extraDucatBonus = decimal.Floor(rewardBase * extraDucatPercent / 100m);
        var extraProfitBonus = decimal.Floor(rewardBase * extraProfitPercent / 100m);
        // Accessory "Merchant Rating gain" enchants add rating only; letters never raise rating.
        var ratingBonus = decimal.Floor(rewardBase * Math.Max(0m, modifiers.MerchantRatingPercent) / 100m);
        var ducatGain = baseProfit + masteryBonus + letterBonus + letterDucats + extraDucatBonus + extraProfitBonus;
        var rawGold = rewardBase + masteryBonus + letterGoldBonus + extraProfitBonus;
        // A barter line's BuyPrice is the Gold value of its materials; it is never converted to Ducats.
        var materialGold = isBarter ? purchaseCost : 0m;

        var expPercent = masteryPercent + letterPercent + extraProfitPercent;
        var exp = 0m;
        var seasonal = 0m;
        foreach (var line in lineList)
        {
            var perItem = isBarter ? line.SalePrice : line.SalePrice - line.BuyPrice;
            if (perItem > 0 && line.Weight > 0)
                exp += ExpUnits(perItem, line.Weight) * line.Quantity * 30m;
            seasonal += isBarter
                ? decimal.Floor(line.SalePrice * line.Quantity / 34m)
                : decimal.Floor((line.SalePrice - OriginalBuyPrice(line)) * line.Quantity / 17m);
        }
        exp = decimal.Floor(exp * (100m + expPercent) / 100m);

        return new CommerceRewardBreakdown(isBarter, modifiers.MasteryRank, masteryPercent, letter, letterApplied, letterNote,
            grossSale, purchaseCost, baseProfit, masteryBonus, letterBonus, letterGoldBonus, letterDucats,
            letterCost, ducatGain, rawGold, rate, materialGold, rewardBase + masteryBonus + extraProfitBonus + ratingBonus, exp, Math.Max(0m, seasonal),
            extraDucatPercent, extraDucatBonus, extraProfitPercent, extraProfitBonus);
    }

    // The score uses the buy price before purchase discounts (wiki: discount-rate effects are not reflected), so a
    // discounted price is scaled back to the nearest whole original price. Matches five exports at 7-8% discount.
    private static decimal OriginalBuyPrice(RewardLine line)
    {
        var discount = Math.Clamp(line.PurchaseDiscountPercent, 0m, 99m);
        return discount == 0 ? line.BuyPrice : decimal.Round(line.BuyPrice * 100m / (100m - discount), MidpointRounding.AwayFromZero);
    }

    // Fits every export so far: the game appears to multiply single-precision √profit × √weight, so perfect squares
    // land just below the integer (√2 × √2 = 1.99999988 → 1; a 1-item 2-profit weight-2 sale gave 30 EXP, not 60).
    private static decimal ExpUnits(decimal perItemProfit, decimal weight)
    {
        var product = (float)Math.Sqrt((double)perItemProfit) * (float)Math.Sqrt((double)weight);
        return (decimal)Math.Floor(product);
    }

    public static string Compact(decimal value)
    {
        var culture = CultureInfo.CurrentCulture;
        var magnitude = Math.Abs(value);
        if (magnitude >= 1_000_000m) return (value / 1_000_000m).ToString(magnitude >= 10_000_000m ? "0.#" : "0.##", culture) + "M";
        if (magnitude >= 10_000m) return (value / 1_000m).ToString(magnitude >= 100_000m ? "0" : "0.#", culture) + "k";
        return value.ToString("N0", culture);
    }

    private static string Rate(decimal rate) => rate.ToString("0.###", CultureInfo.CurrentCulture);

    // Short cell text: Gold + Ducats × Gold-per-Ducat (− material Gold) = total Gold.
    public static string FormatFormula(CommerceRewardBreakdown reward)
    {
        var text = $"{Compact(reward.RawGold)}+{Compact(reward.DucatGain)}×{Rate(reward.GoldPerDucat)}";
        if (reward.IsBarter) text += $"−{Compact(reward.MaterialGold)}";
        if (reward.LetterCostGold > 0) text += $"−{Compact(reward.LetterCostGold)}";
        return $"{text}={Compact(reward.TotalGold)}";
    }

    public static string BasicTooltip(CommerceRewardBreakdown reward)
    {
        var materials = reward.IsBarter ? $" − {reward.MaterialGold:N0} material Gold" : string.Empty;
        var letter = reward.LetterCostGold > 0 ? $" − {reward.LetterCostGold:N0} letter value" : string.Empty;
        return $"{reward.RawGold:N0} Gold + {reward.DucatGain:N0} Ducats × {Rate(reward.GoldPerDucat)} Gold per Ducat{materials}{letter} = {reward.TotalGold:N0} Gold";
    }

    public static string DetailedTooltip(CommerceRewardBreakdown reward)
    {
        var builder = new StringBuilder();
        if (reward.IsBarter)
        {
            builder.AppendLine($"Sale {reward.GrossSale:N0} Ducats. Barter goods cost no Ducats, so the whole sale counts as profit.");
            builder.AppendLine($"Materials: {reward.MaterialGold:N0} Gold from your saved barter material values.");
        }
        else
        {
            builder.AppendLine($"Sale {reward.GrossSale:N0} − purchase {reward.PurchaseCost:N0} = profit {reward.BaseProfit:N0} Ducats.");
        }
        builder.AppendLine($"Commerce Mastery rank {reward.MasteryRank}: +{reward.MasteryPercent}% = +{reward.MasteryBonus:N0} Ducats and Gold.");
        if (reward.Letter is null)
            builder.AppendLine("Letter of Guarantee: none selected.");
        else if (!reward.LetterApplied)
            builder.AppendLine($"{reward.Letter.Name}: not applied. {reward.LetterNote}");
        else
        {
            var percent = reward.IsBarter ? reward.Letter.BarterPercent : reward.Letter.NormalPercent;
            var target = reward.Letter.BoostsGold ? "Ducats and Gold" : "Ducats and EXP only, not Gold";
            var useDucats = reward.LetterDucats > 0
                ? $", plus {reward.LetterDucats:N0} Ducats on use" +
                  (reward.LetterCostGold > 0 ? $", minus its {reward.LetterCostGold:N0} Gold market value" : string.Empty)
                : $"; its {reward.Letter.DucatsGranted:N0} Ducats on use and market value are not counted per unit";
            builder.AppendLine($"{reward.Letter.Name}: +{percent}% = +{reward.LetterBonus:N0} ({target}){useDucats}.");
        }
        if (reward.ExtraProfitPercent > 0)
            builder.AppendLine($"Merchant Group buffs: +{reward.ExtraProfitPercent:0.##}% profit = +{reward.ExtraProfitBonus:N0} Ducats and Gold.");
        if (reward.ExtraDucatPercent > 0)
            builder.AppendLine($"Titles and partner: +{reward.ExtraDucatPercent:0.##}% = +{reward.ExtraDucatBonus:N0} Ducats.");
        builder.AppendLine($"Ducats earned: {reward.DucatGain:N0} × {Rate(reward.GoldPerDucat)} Gold per Ducat = {reward.DucatGold:N0} Gold.");
        builder.AppendLine($"Gold earned: {reward.RawGold:N0}.");
        var deductions = (reward.IsBarter ? $" − {reward.MaterialGold:N0}" : string.Empty) +
            (reward.LetterCostGold > 0 ? $" − {reward.LetterCostGold:N0}" : string.Empty);
        builder.AppendLine($"Total: {reward.RawGold:N0} + {reward.DucatGold:N0}{deductions} = {reward.TotalGold:N0} Gold.");
        builder.AppendLine(reward.IsBarter
            ? $"EXP ≈ {reward.Exp:N0} · Seasonal score {reward.SeasonalScore:N0}."
            : $"Merchant Rating +{reward.MerchantRating:N0} · EXP ≈ {reward.Exp:N0} · Seasonal score {reward.SeasonalScore:N0}.");
        builder.Append("Purchase discounts are already included in entered and scanned prices.");
        return builder.ToString();
    }
}

public static class PurchasePriceRange
{
    // Discounts only lower buy prices, so they widen the lower bound of a scan sanity check.
    public static bool IsPlausibleBuyPrice(decimal price, int catalogMin, int catalogMax, decimal discountPercent)
    {
        if (price <= 0 || catalogMax <= 0) return false;
        return price >= LowestPlausible(catalogMin, discountPercent) && price <= catalogMax;
    }

    public static decimal LowestPlausible(int catalogMin, decimal discountPercent) =>
        decimal.Floor(catalogMin * (100m - Math.Clamp(discountPercent, 0m, 100m)) / 100m);
}

public static class GroupDestinationBonus
{
    public const decimal Percent = 10m;

    // Observed in game: the party's selected destination pays floor(base × 1.1), e.g. 1,256 → 1,381 and 891 → 980.
    public static decimal Apply(decimal basePrice) =>
        basePrice <= 0 ? basePrice : decimal.Floor(basePrice * (100m + Percent) / 100m);
}

public sealed class MerchantRatingLevel
{
    public int Level { get; set; }
    public int Value { get; set; }
    public decimal Discount { get; set; }
}

public static class MerchantRatingTable
{
    // Client credit-level table: the highest level at or below the entered rating sets the purchase discount.
    public static decimal DiscountPercent(int ratingLevel, IEnumerable<MerchantRatingLevel> levels) =>
        levels.Where(level => level.Level <= ratingLevel).OrderBy(level => level.Level).LastOrDefault()?.Discount ?? 0m;
}

public enum CommerceModifierGroup { Title, Partner, Talent, Speed }

// Selectable commerce factors from the Mabinogi World Wiki. Values are per stack; MaxCount is how many can be selected.
public sealed record CommerceModifier(
    string Id,
    CommerceModifierGroup Group,
    string Name,
    string Effect,
    string Detail,
    int MaxCount = 1,
    decimal DucatPercent = 0,
    decimal ProfitPercent = 0,
    decimal PurchaseDiscountPercent = 0,
    int ExtraSlots = 0,
    int ExtraWeight = 0,
    bool GroupGoodsOnly = false,
    string? ExclusiveGroup = null,
    decimal MerchantRatingPercent = 0,
    decimal TransportSpeedPercent = 0);

public sealed record CommerceModifierTotals(decimal DucatPercent, decimal ProfitPercent, decimal PurchaseDiscountPercent,
    int ExtraSlots, int ExtraWeight, decimal MerchantRatingPercent = 0, decimal TransportSpeedPercent = 0);

public static class TransportSpeed
{
    // Speed bonuses add to the transport's base speed, so travel time divides by (1 + bonus).
    public static decimal ApplyToMinutes(decimal minutes, decimal speedPercent) =>
        speedPercent <= 0 ? minutes : minutes * 100m / (100m + speedPercent);
}

public static class CommerceModifiers
{
    public static IReadOnlyList<CommerceModifier> All { get; } =
    [
        new("title-event-pass", CommerceModifierGroup.Title, "Event pass title (Bonus Ducats +5%)", "+5% Ducats",
            "2nd Titles from adventure or event passes, such as Erinn Adventure Pass Challenger. Their stat effects end 30 days after you obtain them. Only one title can be equipped.",
            DucatPercent: 5, ExclusiveGroup: "title"),
        new("partner-william-bonus", CommerceModifierGroup.Partner, "William summoned (Likeability tier)", "+1% Ducats per tier",
            "Enter 0-3: Likeability 80-129 gives 1%, 130-239 gives 2%, 240+ gives 3%. William must be summoned when the trade completes. Partner transports already include their slot and weight effects.",
            MaxCount: 3, DucatPercent: 1),
        new("talent-mercantile-grandmaster", CommerceModifierGroup.Talent, "Grandmaster Merchant", "+1 slot, +100 weight",
            "Mercantile talent Grandmaster bonus: every transport gets Commerce Inventory Slot +1 and Commerce Weight +100.",
            ExtraSlots: 1, ExtraWeight: 100),
        new("speed-transport-potion", CommerceModifierGroup.Speed, "Transport Speed Increase Potion", "+10% transport speed",
            "Lasts 30 minutes or 1 hour. Stacks with other transport speed effects. Select it only while it is active.",
            TransportSpeedPercent: 10),
        new("speed-floating-stone", CommerceModifierGroup.Speed, "Commerce Floating Stone (1 hr.)", "+20% transport speed",
            "Lasts 1 hour. Stacks with other transport speed effects. Select it only while it is active.",
            TransportSpeedPercent: 20),
        new("speed-walk-30", CommerceModifierGroup.Speed, "Speed Walk Potion 30%", "+30% movement speed",
            "Lasts 5 or 10 minutes and affects Commerce transports. Does not stack with another movement speed potion.",
            TransportSpeedPercent: 30, ExclusiveGroup: "speed-walk"),
        new("speed-walk-40", CommerceModifierGroup.Speed, "Speed Walk Potion 40%", "+40% movement speed",
            "Lasts 5 or 10 minutes and affects Commerce transports. Does not stack with another movement speed potion.",
            TransportSpeedPercent: 40, ExclusiveGroup: "speed-walk"),
        new("speed-commerce-reforge", CommerceModifierGroup.Speed, "Transport Speed reforge · level", "+3% transport speed per level",
            "Commerce Enhancement Outfit reforge: Transport Speed rises 3% per level (level 15 = 45%, level 20 = 60%). Enter the level of the outfit you wear (0-20).",
            MaxCount: 20, TransportSpeedPercent: 3)
    ];

    public static string GroupName(CommerceModifierGroup group) => group switch
    {
        CommerceModifierGroup.Title => "Titles · reward estimates",
        CommerceModifierGroup.Partner => "Partners · reward estimates",
        CommerceModifierGroup.Talent => "Talents · cargo capacity",
        _ => "Transport speed · route time and profit per minute"
    };

    public static CommerceModifierTotals Totals(IReadOnlyDictionary<string, int> counts, bool groupGoods)
    {
        decimal ducats = 0, profit = 0, discount = 0, rating = 0, speed = 0;
        int slots = 0, weight = 0;
        foreach (var modifier in All)
        {
            if (!counts.TryGetValue(modifier.Id, out var count) || count <= 0) continue;
            count = Math.Min(count, modifier.MaxCount);
            if (modifier.GroupGoodsOnly && !groupGoods) continue;
            ducats += modifier.DucatPercent * count;
            profit += modifier.ProfitPercent * count;
            discount += modifier.PurchaseDiscountPercent * count;
            slots += modifier.ExtraSlots * count;
            weight += modifier.ExtraWeight * count;
            rating += modifier.MerchantRatingPercent * count;
            speed += modifier.TransportSpeedPercent * count;
        }
        return new CommerceModifierTotals(ducats, profit, discount, slots, weight, rating, speed);
    }
}

public enum EnchantPosition { Prefix, Suffix }

// Accessory enchants that affect commerce. Ranged enchants roll a fixed value when applied, entered per slot.
public sealed record AccessoryEnchant(
    string Id,
    EnchantPosition Position,
    string Name,
    string Effect,
    decimal PurchaseDiscountPercent = 0,
    decimal MerchantRatingPercent = 0,
    int MinRoll = 0,
    int MaxRoll = 0,
    bool RollIsDiscount = false)
{
    public bool HasRoll => MaxRoll > 0;
}

public sealed record AccessoryEnchantTotals(decimal PurchaseDiscountPercent, decimal MerchantRatingPercent);

public static class AccessoryEnchants
{
    public const int SlotCount = 2;

    public static IReadOnlyList<AccessoryEnchant> All { get; } =
    [
        new("haggler", EnchantPosition.Prefix, "Haggler", "1% purchase discount", PurchaseDiscountPercent: 1),
        new("trustworthy", EnchantPosition.Prefix, "Trustworthy", "1% purchase discount, +3% Merchant Rating gain",
            PurchaseDiscountPercent: 1, MerchantRatingPercent: 3),
        new("first", EnchantPosition.Prefix, "First", "+1% Merchant Rating gain", MerchantRatingPercent: 1),
        new("silk", EnchantPosition.Prefix, "Silk", "+1-4% Merchant Rating gain (rolled)", MinRoll: 1, MaxRoll: 4),
        new("commerce", EnchantPosition.Suffix, "Commerce", "1% purchase discount", PurchaseDiscountPercent: 1),
        new("road", EnchantPosition.Suffix, "Road", "1-3% purchase discount (rolled)", MinRoll: 1, MaxRoll: 3, RollIsDiscount: true),
        new("trader", EnchantPosition.Suffix, "Trader", "3% purchase discount, +2% Merchant Rating gain",
            PurchaseDiscountPercent: 3, MerchantRatingPercent: 2)
    ];

    public static string SlotKey(int slot, EnchantPosition position) => $"{slot}-{position.ToString().ToLowerInvariant()}";

    public static AccessoryEnchant? Find(string? id, EnchantPosition position) =>
        All.FirstOrDefault(enchant => enchant.Id == id && enchant.Position == position);

    public static AccessoryEnchantTotals Totals(IReadOnlyDictionary<string, string> enchantIds, IReadOnlyDictionary<string, int> rolls)
    {
        decimal discount = 0, rating = 0;
        for (var slot = 1; slot <= SlotCount; slot++)
        {
            foreach (var position in new[] { EnchantPosition.Prefix, EnchantPosition.Suffix })
            {
                var key = SlotKey(slot, position);
                if (!enchantIds.TryGetValue(key, out var id) || Find(id, position) is not { } enchant) continue;
                discount += enchant.PurchaseDiscountPercent;
                rating += enchant.MerchantRatingPercent;
                if (!enchant.HasRoll) continue;
                var roll = Math.Clamp(rolls.TryGetValue(key, out var value) ? value : enchant.MinRoll, enchant.MinRoll, enchant.MaxRoll);
                if (enchant.RollIsDiscount) discount += roll;
                else rating += roll;
            }
        }
        return new AccessoryEnchantTotals(discount, rating);
    }
}
