using System.Windows;
using System.Windows.Controls;

namespace MabiCommerceNewLife;

public enum TooltipCategory
{
    None,
    Calculation,
    ItemStatus
}

public static class TooltipSettings
{
    private static readonly List<WeakReference<FrameworkElement>> Elements = [];

    public static bool HelpBubblesEnabled { get; private set; } = true;
    public static bool CalculationBreakdownsEnabled { get; private set; } = true;
    public static bool ItemStatusTooltipsEnabled { get; private set; } = true;

    public static event EventHandler? Changed;

    public static bool IsEnabled(TooltipCategory category) => category switch
    {
        TooltipCategory.Calculation => CalculationBreakdownsEnabled,
        TooltipCategory.ItemStatus => ItemStatusTooltipsEnabled,
        _ => true
    };

    public static void Configure(bool helpBubblesEnabled, bool calculationBreakdownsEnabled, bool itemStatusTooltipsEnabled)
    {
        if (HelpBubblesEnabled == helpBubblesEnabled &&
            CalculationBreakdownsEnabled == calculationBreakdownsEnabled &&
            ItemStatusTooltipsEnabled == itemStatusTooltipsEnabled) return;

        HelpBubblesEnabled = helpBubblesEnabled;
        CalculationBreakdownsEnabled = calculationBreakdownsEnabled;
        ItemStatusTooltipsEnabled = itemStatusTooltipsEnabled;
        RefreshTrackedElements();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    internal static void Track(FrameworkElement element)
    {
        if (Elements.Any(reference => reference.TryGetTarget(out var existing) && ReferenceEquals(existing, element))) return;
        Elements.Add(new WeakReference<FrameworkElement>(element));
    }

    internal static void RefreshTrackedElements()
    {
        for (var index = Elements.Count - 1; index >= 0; index--)
        {
            if (!Elements[index].TryGetTarget(out var element))
            {
                Elements.RemoveAt(index);
                continue;
            }
            TooltipCategoryService.Refresh(element);
        }
    }
}

public static class TooltipCategoryService
{
    public static readonly DependencyProperty CategoryProperty = DependencyProperty.RegisterAttached(
        "Category", typeof(TooltipCategory), typeof(TooltipCategoryService),
        new PropertyMetadata(TooltipCategory.None, OnCategoryChanged));

    public static TooltipCategory GetCategory(DependencyObject element) =>
        (TooltipCategory)element.GetValue(CategoryProperty);

    public static void SetCategory(DependencyObject element, TooltipCategory value) =>
        element.SetValue(CategoryProperty, value);

    internal static void Refresh(FrameworkElement element)
    {
        var category = GetCategory(element);
        if (category != TooltipCategory.None)
            ToolTipService.SetIsEnabled(element, TooltipSettings.IsEnabled(category));
    }

    private static void OnCategoryChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not FrameworkElement element || e.NewValue is not TooltipCategory category ||
            category == TooltipCategory.None) return;
        TooltipSettings.Track(element);
        Refresh(element);
    }
}
