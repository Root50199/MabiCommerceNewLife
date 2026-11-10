using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MabiCommerceNewLife;

public partial class TutorialOverlay : UserControl
{
    private IReadOnlyList<FrameworkElement> _targets = [];
    private bool _showCard;
    private bool _layoutDirty = true;
    private Rect _lastBounds = Rect.Empty;
    private Rect[] _lastAreas = [];
    public event EventHandler? BackRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? ExitRequested;

    public TutorialOverlay()
    {
        InitializeComponent();
        LayoutUpdated += Overlay_LayoutUpdated;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Exit();
        };
    }

    private void Overlay_LayoutUpdated(object? sender, EventArgs e) => UpdateSpotlight();

    public void Stop()
    {
        LayoutUpdated -= Overlay_LayoutUpdated;
        _targets = [];
    }

    public void ShowStep(GuideTopic topic, int index, IReadOnlyList<FrameworkElement> targets)
    {
        _targets = targets;
        _layoutDirty = true;
        _showCard = true;
        TourCard.Visibility = Visibility.Visible;
        StepNumber.Text = $"Step {index + 1} of {UserGuide.Topics.Count} - X or Escape to exit";
        StepTitle.Text = topic.Title;
        StepInstructions.Text = topic.TourText;
        BackButton.IsEnabled = index > 0;
        NextButton.Content = index == UserGuide.Topics.Count - 1 ? "Finish" : "Next";
        StepScroll.ScrollToTop();
        UpdateLayout();
        UpdateSpotlight();
        NextButton.Focus();
    }

    public void ShowBackdrop(IReadOnlyList<FrameworkElement>? targets = null)
    {
        _targets = targets ?? [];
        _layoutDirty = true;
        _showCard = false;
        TourCard.Visibility = Visibility.Collapsed;
        UpdateSpotlight();
    }

    private void UpdateSpotlight()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        var areas = _targets.Where(target => target.IsVisible && target.ActualWidth > 0 && target.ActualHeight > 0)
            .Select(target =>
            {
                var area = target.TransformToVisual(this).TransformBounds(new Rect(target.RenderSize));
                area.Inflate(5, 5);
                area.Intersect(bounds);
                return area;
            }).Where(area => !area.IsEmpty).ToArray();
        var highlight = Rect.Empty;
        foreach (var area in areas) highlight.Union(area);
        if (!_layoutDirty && bounds == _lastBounds && areas.SequenceEqual(_lastAreas)) return;
        _layoutDirty = false;
        _lastBounds = bounds;
        _lastAreas = areas;
        var spotlights = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var area in areas) spotlights.Children.Add(new RectangleGeometry(area, 5, 5));
        spotlights.Freeze();
        var geometry = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), spotlights);
        geometry.Freeze();
        DimMask.Data = geometry;
        SpotlightOutline.Data = highlight.IsEmpty ? null : spotlights;
        if (!_showCard) return;

        TourCard.Width = Math.Min(340, Math.Max(240, ActualWidth - 32));
        if (!highlight.IsEmpty)
        {
            var sideSpace = Math.Max(highlight.Left, ActualWidth - highlight.Right) - 32;
            if (sideSpace >= 240) TourCard.Width = Math.Min(TourCard.Width, sideSpace);
        }
        TourCard.Measure(new Size(TourCard.Width, double.PositiveInfinity));
        var cardSize = TourCard.DesiredSize;
        var left = (ActualWidth - cardSize.Width) / 2;
        var top = ActualHeight - cardSize.Height - 16;
        if (!highlight.IsEmpty)
        {
            if (ActualWidth - highlight.Right >= cardSize.Width + 32)
            {
                left = highlight.Right + 16;
                top = Math.Clamp(highlight.Top, 52, Math.Max(52, ActualHeight - cardSize.Height - 16));
            }
            else if (highlight.Left >= cardSize.Width + 32)
            {
                left = highlight.Left - cardSize.Width - 16;
                top = Math.Clamp(highlight.Top, 52, Math.Max(52, ActualHeight - cardSize.Height - 16));
            }
            else if (ActualHeight - highlight.Bottom >= cardSize.Height + 32)
                top = highlight.Bottom + 16;
            else if (highlight.Top >= cardSize.Height + 68)
                top = highlight.Top - cardSize.Height - 16;
        }
        var cardBounds = new Rect(
            Math.Clamp(left, 16, Math.Max(16, ActualWidth - cardSize.Width - 16)),
            Math.Clamp(top, 52, Math.Max(52, ActualHeight - cardSize.Height - 16)), cardSize.Width, cardSize.Height);
        if (areas.Any(area => area.IntersectsWith(cardBounds)))
        {
            var candidates = areas.SelectMany(area => new[]
            {
                new Rect(area.Right + 16, Math.Max(52, area.Top), cardSize.Width, cardSize.Height),
                new Rect(area.Left - cardSize.Width - 16, Math.Max(52, area.Top), cardSize.Width, cardSize.Height),
                new Rect((ActualWidth - cardSize.Width) / 2, area.Bottom + 16, cardSize.Width, cardSize.Height),
                new Rect((ActualWidth - cardSize.Width) / 2, area.Top - cardSize.Height - 16, cardSize.Width, cardSize.Height)
            });
            foreach (var candidate in candidates)
            {
                if (candidate.Left < 16 || candidate.Top < 52 || candidate.Right > ActualWidth - 16 ||
                    candidate.Bottom > ActualHeight - 16 || areas.Any(area => area.IntersectsWith(candidate))) continue;
                cardBounds = candidate;
                break;
            }
        }
        Canvas.SetLeft(TourCard, cardBounds.Left);
        Canvas.SetTop(TourCard, cardBounds.Top);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
    private void Next_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);
    public void Exit() => ExitRequested?.Invoke(this, EventArgs.Empty);
    private void Exit_Click(object sender, RoutedEventArgs e) => Exit();
}
