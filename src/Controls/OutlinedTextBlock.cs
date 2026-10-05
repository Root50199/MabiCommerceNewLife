using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MabiCommerceNewLife;

public sealed class OutlinedTextBlock : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
        typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(
        typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment), typeof(TextAlignment), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(TextAlignment.Left, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping), typeof(TextWrapping), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(TextWrapping.NoWrap, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OutlineBrushProperty = DependencyProperty.Register(
        nameof(OutlineBrush), typeof(Brush), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OutlineThicknessProperty = DependencyProperty.Register(
        nameof(OutlineThickness), typeof(double), typeof(OutlinedTextBlock), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public OutlinedTextBlock()
    {
        var fontDirectory = Path.Combine(AppContext.BaseDirectory, "Data", "Fonts") + Path.DirectorySeparatorChar;
        FontFamily = new FontFamily(new Uri(fontDirectory, UriKind.Absolute), "./#NanumGothic");
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => (TextAlignment)GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    public Brush OutlineBrush
    {
        get => (Brush)GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    public double OutlineThickness
    {
        get => (double)GetValue(OutlineThicknessProperty);
        set => SetValue(OutlineThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var formatted = CreateFormattedText(availableSize.Width, availableSize.Height);
        var desiredWidth = formatted.WidthIncludingTrailingWhitespace + OutlineThickness * 2;
        var desiredHeight = formatted.Height + OutlineThickness * 2;
        if (!double.IsInfinity(availableSize.Width)) desiredWidth = Math.Min(desiredWidth, availableSize.Width);
        if (!double.IsInfinity(availableSize.Height)) desiredHeight = Math.Min(desiredHeight, availableSize.Height);
        return new Size(Math.Max(0, desiredWidth), Math.Max(0, desiredHeight));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (string.IsNullOrEmpty(Text) || ActualWidth <= 0 || ActualHeight <= 0) return;

        var formatted = CreateFormattedText(ActualWidth - OutlineThickness * 2, ActualHeight - OutlineThickness * 2);
        var geometry = formatted.BuildGeometry(new Point(OutlineThickness, OutlineThickness));
        if (OutlineThickness > 0 && OutlineBrush is not null)
        {
            // Match the client's bitmap-font outline: the glyph shape dilated one step in all eight directions,
            // composited once so overlapping offsets keep the outline's single translucency.
            var (outlineFill, outlineOpacity) = SplitOpacity(OutlineBrush);
            drawingContext.PushOpacity(outlineOpacity);
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    drawingContext.PushTransform(new TranslateTransform(dx * OutlineThickness, dy * OutlineThickness));
                    drawingContext.DrawGeometry(outlineFill, null, geometry);
                    drawingContext.Pop();
                }
            }

            drawingContext.Pop();
        }

        drawingContext.DrawGeometry(Foreground, null, geometry);
    }

    private static (Brush Fill, double Opacity) SplitOpacity(Brush brush)
    {
        if (brush is not SolidColorBrush solid) return (brush, 1);
        var color = solid.Color;
        var opaque = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
        opaque.Freeze();
        return (opaque, color.A / 255d * solid.Opacity);
    }

    private FormattedText CreateFormattedText(double availableWidth, double availableHeight)
    {
        var formatted = new FormattedText(
            Text ?? string.Empty,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal),
            FontSize,
            Foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = TextAlignment
        };

        if (!double.IsInfinity(availableWidth) && availableWidth > 0)
        {
            formatted.MaxTextWidth = availableWidth;
            if (TextWrapping == TextWrapping.NoWrap)
            {
                formatted.MaxLineCount = 1;
                formatted.Trimming = TextTrimming.CharacterEllipsis;
            }
        }

        if (!double.IsInfinity(availableHeight) && availableHeight > 0)
        {
            formatted.MaxTextHeight = availableHeight;
        }

        return formatted;
    }
}