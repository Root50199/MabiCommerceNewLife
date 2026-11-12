using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MabiCommerceNewLife;

public partial class MainWindow
{
    private UserManualWindow? _manualWindow;
    private TutorialHost? _tutorialHost;
    private TutorialHost? _tutorialChildHost;
    private Window? _tutorialChild;
    private GuideSurface _tutorialChildSurface;
    private readonly List<Window> _tutorialHiddenWindows = [];
    private IInputElement? _tutorialPreviousFocus;
    private int _tutorialOriginalTab;
    private int _tutorialStep;
    private int _tutorialRevision;
    private bool _helpClosing;
    private UIElement? _tutorialSettingsContent;
    private (Visibility Visibility, bool IsEnabled)? _tutorialShoppingButtonState;
    private (Visibility Trade, Visibility Group)? _tutorialScanButtonState;
    private readonly List<InfoBubble> _tutorialPreviewBubbles = [];

    private void OfferFirstRunTutorial(object? sender, EventArgs e)
    {
        ContentRendered -= OfferFirstRunTutorial;
        if (_plannerPreferences.TutorialPromptShown) return;
        _plannerPreferences.TutorialPromptShown = true;
        SavePlannerPreferences();
        OpenUserManual(offerTour: true);
    }

    private void ManualButton_Click(object sender, RoutedEventArgs e) => OpenUserManual();

    private void OpenUserManual(bool offerTour = false)
    {
        SettingsPopup.IsOpen = false;
        if (_manualWindow is { IsLoaded: true })
        {
            _manualWindow.Activate();
            return;
        }
        var manual = new UserManualWindow(this, offerTour);
        manual.Closed += (_, _) => _manualWindow = null;
        manual.TourRequested += (_, _) => StartTutorial();
        _manualWindow = manual;
        manual.Show();
    }

    private void StartTutorial()
    {
        if (_tutorialHost is not null) return;
        SettingsPopup.IsOpen = false;
        PostPicker.IsDropDownOpen = TransportPicker.IsDropDownOpen = false;
        _tutorialPreviousFocus = Keyboard.FocusedElement;
        _tutorialOriginalTab = TradeMiddleTabs.SelectedIndex;
        var windows = HelpOwnedWindows(this);
        if (_routeMapWindow is { IsVisible: true } routeMap)
            windows = windows.Concat(new[] { routeMap }).Concat(HelpOwnedWindows(routeMap));
        foreach (var overlay in new Window?[] { _boatTimerWindow, _checklistWindow }.OfType<Window>())
            windows = windows.Concat(new[] { overlay }).Concat(HelpOwnedWindows(overlay));
        foreach (var window in windows.Distinct().Where(window => window.IsVisible).ToArray())
        {
            _tutorialHiddenWindows.Add(window);
            window.Hide();
        }
        _tutorialHost = AttachTutorial(this);
        _tutorialStep = 0;
        Activate();
        ShowTutorialStep();
    }

    private TutorialHost AttachTutorial(Window window)
    {
        var host = new TutorialHost(window);
        host.Overlay.NextRequested += (_, _) =>
        {
            if (_tutorialStep == UserGuide.Topics.Count - 1) StopTutorial();
            else { _tutorialStep++; ShowTutorialStep(); }
        };
        host.Overlay.BackRequested += (_, _) =>
        {
            if (_tutorialStep <= 0) return;
            _tutorialStep--;
            ShowTutorialStep();
        };
        host.Overlay.ExitRequested += (_, _) => StopTutorial();
        return host;
    }

    private void ShowTutorialStep()
    {
        if (_tutorialHost is null) return;
        var topic = UserGuide.Topics[_tutorialStep];
        var revision = ++_tutorialRevision;
        RestoreTutorialPreviews();
        if (topic.Targets.Contains("ReadPriceListButton"))
        {
            _tutorialScanButtonState = (ReadPriceListButton.Visibility, GroupReadPriceListButton.Visibility);
        }
        if (topic.Targets.Contains("ShoppingListButton"))
        {
            _tutorialShoppingButtonState = (ShoppingListButton.Visibility, ShoppingListButton.IsEnabled);
            ShoppingListButton.Visibility = Visibility.Visible;
            ShoppingListButton.IsEnabled = true;
        }
        var window = (Window)this;
        var host = _tutorialHost;
        if (topic.Surface == GuideSurface.Main)
        {
            CloseTutorialChild();
            TradeMiddleTabs.SelectedIndex = topic.Tab;
            Activate();
        }
        else
        {
            _tutorialHost.Overlay.ShowBackdrop();
            if (_tutorialChild is null || _tutorialChildSurface != topic.Surface)
            {
                CloseTutorialChild();
                _tutorialChildSurface = topic.Surface;
                _tutorialChild = CreateTutorialChild(topic.Surface);
                if (_tutorialChild is null)
                {
                    StopTutorial();
                    return;
                }
                _tutorialChild.Topmost = Topmost;
                TopmostGuard.Watch(_tutorialChild);
                _tutorialChildHost = AttachTutorial(_tutorialChild);
                _tutorialChild.Closed += TutorialChild_Closed;
                _tutorialChild.Show();
                var workArea = SystemParameters.WorkArea;
                _tutorialChild.Left = Math.Clamp(Left + (ActualWidth - _tutorialChild.Width) / 2,
                    workArea.Left, Math.Max(workArea.Left, workArea.Right - _tutorialChild.Width));
                _tutorialChild.Top = Math.Clamp(Top + (ActualHeight - _tutorialChild.Height) / 2,
                    workArea.Top, Math.Max(workArea.Top, workArea.Bottom - _tutorialChild.Height));
            }
            window = _tutorialChild;
            host = _tutorialChildHost!;
        }
        if (_tutorialScanButtonState is not null)
        {
            ReadPriceListButton.Visibility = Visibility.Visible;
            GroupReadPriceListButton.Visibility = Visibility.Visible;
        }
        FrameworkElement? ResolveTarget(string name) => topic.Surface switch
        {
            GuideSurface.Settings => FindName(name) as FrameworkElement,
            GuideSurface.RouteMap => (_tutorialChild as RouteMapWindow)?.TutorialTarget(name),
            _ => window.FindName(name) as FrameworkElement
        };
        foreach (var bubble in topic.Targets.Select(ResolveTarget).OfType<InfoBubble>())
        {
            bubble.TutorialPreview = true;
            _tutorialPreviewBubbles.Add(bubble);
        }
        var currentStep = _tutorialStep;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_tutorialHost is null || revision != _tutorialRevision) return;
            window.UpdateLayout();
            var missingTarget = topic.Targets.FirstOrDefault(name => ResolveTarget(name) is null);
            if (missingTarget is not null)
            {
                StopTutorial();
                SetStatus($"Tutorial target '{missingTarget}' was not found. Open the book to read '{topic.Title}'.", true);
                return;
            }
            var targets = topic.Targets.Select(name => ResolveTarget(name)!)
                .Where(target => target.IsVisible && target.ActualWidth > 0 && target.ActualHeight > 0).ToArray();
            if (targets.Length == 0)
            {
                StopTutorial();
                SetStatus($"Tutorial could not display '{topic.Title}'. Open the book to read its instructions.", true);
                return;
            }
            host.Overlay.ShowStep(topic, currentStep, targets);
            if (topic.Surface == GuideSurface.Settings)
                _tutorialHost.Overlay.ShowBackdrop([SettingsButton]);
        }));
    }

    private void TutorialChild_Closed(object? sender, EventArgs e) => StopTutorial();

    private void CloseTutorialChild()
    {
        _tutorialChildHost?.Detach();
        _tutorialChildHost = null;
        if (_tutorialChild is not null)
        {
            _tutorialChild.Closed -= TutorialChild_Closed;
            _tutorialChild.Close();
            _tutorialChild = null;
        }
        if (_tutorialSettingsContent is not null)
        {
            if (VisualTreeHelper.GetParent(_tutorialSettingsContent) is Panel parent)
                parent.Children.Remove(_tutorialSettingsContent);
            SettingsPopup.Child = _tutorialSettingsContent;
            _tutorialSettingsContent = null;
        }
    }

    private void StopTutorial()
    {
        if (_tutorialHost is null) return;
        _tutorialRevision++;
        RestoreTutorialPreviews();
        CloseTutorialChild();
        TradeMiddleTabs.SelectedIndex = _tutorialOriginalTab;
        _tutorialHost.Detach();
        _tutorialHost = null;
        if (!_helpClosing)
            foreach (var window in _tutorialHiddenWindows.Where(window => window.IsLoaded))
                window.Show();
        _tutorialHiddenWindows.Clear();
        if (!_helpClosing)
        {
            Activate();
            _tutorialPreviousFocus?.Focus();
        }
        _tutorialPreviousFocus = null;
    }

    private Window? CreateTutorialChild(GuideSurface surface)
    {
        if (surface == GuideSurface.Inventory) return new InventoryWindow(this);
        if (surface == GuideSurface.Materials) return new BarterMaterialsWindow(this);
        if (surface == GuideSurface.BoatTimers)
        {
            var boats = new BoatTimerWindow(new BoatTimerOverlaySettings(), () => { }) { FontFamily = FontFamily };
            boats.ShowTimers(_shipScheduleEstimator.GetBoatTimers(DateTimeOffset.UtcNow), filtered: false);
            var preview = boats.CreateTutorialPreview();
            preview.Owner = this;
            return preview;
        }
        if (surface == GuideSurface.Checklist)
        {
            var checklist = new ChecklistWindow(new ChecklistOverlaySettings(), () => { }, []) { FontFamily = FontFamily };
            var context = new DepartureChecklistContext("", "", GuaranteeLetterKind.None, false, false, false, false, false,
                _plannerPreferences.CommerceModifierCounts, _plannerPreferences.AccessoryEnchantIds,
                _plannerPreferences.AccessoryEnchantRolls);
            checklist.ShowChecklist(DepartureChecklist.Build(context, relevant: false), relevant: false);
            var preview = checklist.CreateTutorialPreview();
            preview.Owner = this;
            return preview;
        }
        if (surface == GuideSurface.RouteMap)
        {
            if (_routeMapData is null)
            {
                SetStatus("Tutorial route map data is missing from Data/RouteMaps.", true);
                return null;
            }
            var map = new RouteMapWindow(_routeMapData, new RouteMapOverlaySettings(), () => { }, tutorialPreview: true)
                { Owner = this };
            map.SetPlan(_routeMapData.Plan(PostIds.Dunbarton, PostIds.Bangor,
                postName: id => _posts.First(post => post.Id == id).Name));
            return map;
        }
        _tutorialSettingsContent = SettingsPopup.Child;
        SettingsPopup.Child = null;
        var content = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x49, 0x3A, 0x26)) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        content.Children.Add(_tutorialSettingsContent);
        var settings = new Window
        {
            Owner = this, Title = "Settings - Tutorial", Width = 650, Height = SettingsMenuCanvas.Height,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Content = content,
            ShowInTaskbar = false, FontFamily = FontFamily
        };
        settings.Resources.MergedDictionaries.Add(Resources);
        return settings;
    }

    private void RestoreTutorialPreviews()
    {
        if (_tutorialScanButtonState is { } scanState)
        {
            ReadPriceListButton.Visibility = scanState.Trade;
            GroupReadPriceListButton.Visibility = scanState.Group;
            _tutorialScanButtonState = null;
        }
        if (_tutorialShoppingButtonState is { } state)
        {
            ShoppingListButton.Visibility = state.Visibility;
            ShoppingListButton.IsEnabled = state.IsEnabled;
            _tutorialShoppingButtonState = null;
        }
        foreach (var bubble in _tutorialPreviewBubbles) bubble.TutorialPreview = false;
        _tutorialPreviewBubbles.Clear();
    }

    private static IEnumerable<Window> HelpOwnedWindows(Window owner)
    {
        foreach (Window window in owner.OwnedWindows)
        {
            yield return window;
            foreach (var child in HelpOwnedWindows(window)) yield return child;
        }
    }
}
