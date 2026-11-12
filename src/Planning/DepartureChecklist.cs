namespace MabiCommerceNewLife;

public sealed record DepartureChecklistItem(string Key, string Text, string? ParentKey = null);

public enum DepartureCompanion { None, Alpaca, Partner, William }

public sealed record DepartureChecklistContext(
    string Route,
    string Transport,
    GuaranteeLetterKind Letter,
    bool NeedsLetterPurchase,
    bool UsesCover,
    bool NeedsCoverPurchase,
    bool IsBarter,
    bool IsGroup,
    IReadOnlyDictionary<string, int> Modifiers,
    IReadOnlyDictionary<string, string> Enchants,
    IReadOnlyDictionary<string, int> EnchantRolls,
    DepartureCompanion Companion = DepartureCompanion.None);

public static class DepartureChecklist
{
    public static IReadOnlyList<DepartureChecklistItem> Build(DepartureChecklistContext? context, bool relevant = true)
    {
        relevant &= context is not null;
        context ??= new("", "", GuaranteeLetterKind.None, false, false, false, false, false,
            new Dictionary<string, int>(), new Dictionary<string, string>(), new Dictionary<string, int>());
        var items = new List<DepartureChecklistItem>();
        void Add(string key, string text) => items.Add(new DepartureChecklistItem(key + ":" + text, text));
        var selected = CommerceModifiers.All.Where(modifier => context.Modifiers.GetValueOrDefault(modifier.Id) > 0 &&
            (!modifier.GroupGoodsOnly || !relevant || context.IsGroup)).ToArray();
        if (relevant)
        {
            Add("route", $"Confirm route: {context.Route}.");
            Add("transport", $"Use {context.Transport}; check cargo, slots and weight.");
            var companions = new List<string>();
            if (context.Companion != DepartureCompanion.None)
                companions.Add(context.Companion == DepartureCompanion.Partner ? "commerce partner" : context.Companion.ToString());
            if (selected.Any(modifier => modifier.Group == CommerceModifierGroup.Partner) && context.Companion != DepartureCompanion.William)
                companions.Add("William");
            if (companions.Count > 0) Add("partner", $"Summon {string.Join(" and ", companions)}.");
        }
        else Add("partner", "Summon Alpaca / partner.");
        var grandmaster = selected.Any(modifier => modifier.Group == CommerceModifierGroup.Talent);
        if (!relevant || grandmaster) Add("talent", "Activate Grandmaster Merchant.");
        if (relevant)
        {
            if (GuaranteeLetters.Get(context.Letter) is { } letter)
                Add("letter", letter.Name);
        }
        else Add("letter", "Letter of Guarantee");
        var gear = new List<DepartureChecklistItem>();
        void GearItem(string key, string text) => gear.Add(new(key + ":" + text, text));
        if (selected.FirstOrDefault(modifier => modifier.Id == "speed-commerce-reforge") is { } reforge)
            GearItem("chest", $"Commerce chest piece (Transport Speed reforge level {Math.Min(context.Modifiers[reforge.Id], reforge.MaxCount)})");
        for (var slot = 1; slot <= AccessoryEnchants.SlotCount; slot++)
        {
            var names = new List<string>();
            foreach (var position in Enum.GetValues<EnchantPosition>())
            {
                var key = AccessoryEnchants.SlotKey(slot, position);
                if (!context.Enchants.TryGetValue(key, out var id) || AccessoryEnchants.Find(id, position) is not { } enchant) continue;
                names.Add(enchant.Name + (enchant.HasRoll
                    ? $" ({Math.Clamp(context.EnchantRolls.GetValueOrDefault(key, enchant.MinRoll), enchant.MinRoll, enchant.MaxRoll)}%)" : ""));
            }
            if (names.Count > 0) GearItem("accessory-" + slot, $"Accessory {slot}: {string.Join(" + ", names)}");
        }
        var titles = selected.Where(modifier => modifier.Group == CommerceModifierGroup.Title).Select(modifier => modifier.Name).ToArray();
        GearItem("title", titles.Length == 0 ? "Equip the correct commerce title." : $"Equip {string.Join(", ", titles)}.");
        var gearKey = "gear:" + string.Join("|", gear.Select(item => item.Key));
        items.Add(new(gearKey, "Gear"));
        items.AddRange(gear.Select(item => item with { ParentKey = gearKey }));
        if (!relevant || context.UsesCover)
            Add("cover", "Expansion Cover");
        var speed = selected.Where(modifier => modifier.Group == CommerceModifierGroup.Speed && modifier.Id != "speed-commerce-reforge").ToArray();
        if (!relevant) Add("speed", speed.Length == 0 ? "Check speed potions and Commerce Floating Stone."
            : $"Check speed potions and Commerce Floating Stone; configured: {string.Join(", ", speed.Select(modifier => modifier.Name))}.");
        else foreach (var modifier in speed) Add("modifier-" + modifier.Id, $"Check {modifier.Name} is active.");
        Add("bandit-candy", "Bandit Candy");
        Add("arcana", "Check Arcana.");
        if (!relevant || context.IsGroup) Add("group", "Check destination for Group commerce.");
        return items;
    }
}
