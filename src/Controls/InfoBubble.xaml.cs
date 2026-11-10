using System.Windows;
using System.Windows.Controls;

namespace MabiCommerceNewLife;

public enum InfoBubbleType
{
    Tutorial,
    Information
}

public partial class InfoBubble : UserControl
{
    private bool _tutorialPreview;
    internal bool TutorialPreview
    {
        get => _tutorialPreview;
        set { _tutorialPreview = value; UpdateVisibility(); }
    }
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(InfoBubble), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TipTypeProperty = DependencyProperty.Register(
        nameof(TipType), typeof(InfoBubbleType), typeof(InfoBubble),
        new PropertyMetadata(InfoBubbleType.Information, OnTipTypeChanged));

    public static readonly DependencyProperty MaxPopupWidthProperty = DependencyProperty.Register(
        nameof(MaxPopupWidth), typeof(double), typeof(InfoBubble), new PropertyMetadata(300d));

    public static readonly DependencyProperty CategoryProperty = DependencyProperty.Register(
        nameof(Category), typeof(TooltipCategory), typeof(InfoBubble), new PropertyMetadata(TooltipCategory.None));

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public InfoBubbleType TipType
    {
        get => (InfoBubbleType)GetValue(TipTypeProperty);
        set => SetValue(TipTypeProperty, value);
    }

    public double MaxPopupWidth
    {
        get => (double)GetValue(MaxPopupWidthProperty);
        set => SetValue(MaxPopupWidthProperty, value);
    }

    public TooltipCategory Category
    {
        get => (TooltipCategory)GetValue(CategoryProperty);
        set => SetValue(CategoryProperty, value);
    }

    public string AccessibleName => TipType == InfoBubbleType.Tutorial
        ? "Show usage tip"
        : "Show information";

    public InfoBubble()
    {
        InitializeComponent();
        Loaded += InfoBubble_Loaded;
        Unloaded += InfoBubble_Unloaded;
        UpdateVisibility();
    }

    private static void OnTipTypeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is InfoBubble bubble)
            bubble.UpdateVisibility();
    }

    private void TooltipSettings_Changed(object? sender, EventArgs e) => UpdateVisibility();

    private void InfoBubble_Loaded(object sender, RoutedEventArgs e)
    {
        TooltipSettings.Changed += TooltipSettings_Changed;
        UpdateVisibility();
    }

    private void InfoBubble_Unloaded(object sender, RoutedEventArgs e)
    {
        TooltipSettings.Changed -= TooltipSettings_Changed;
        TipToggle.IsChecked = false;
    }

    private void TipPopup_Opened(object? sender, EventArgs e) => TipPopupBorder.MaxWidth = MaxPopupWidth;

    private void UpdateVisibility() =>
        Visibility = TutorialPreview || TooltipSettings.HelpBubblesEnabled && TooltipSettings.IsEnabled(Category)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
}
