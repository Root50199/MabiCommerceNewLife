using System.Windows;

namespace MabiCommerceNewLife;

public partial class MainWindow
{
    private ChecklistWindow? _checklistWindow;
    private readonly HashSet<string> _checkedDepartureItems = [];

    private void ChecklistSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var settings = _plannerPreferences.ChecklistOverlay;
        settings.Enabled = ChecklistOverlayCheckBox.IsChecked == true;
        settings.OnlyWithRoute = ChecklistOnlyWithRouteCheckBox.IsChecked == true;
        settings.RouteOnly = RelevantChecklistOnlyCheckBox.IsChecked == true;
        SavePlannerPreferences();
        RefreshChecklist();
    }

    private void RefreshChecklist()
    {
        if (_initializing || !IsLoaded || _helpClosing || _tutorialHost is not null) return;
        var settings = _plannerPreferences.ChecklistOverlay;
        if (!settings.Enabled)
        {
            _checklistWindow?.Close();
            return;
        }
        var stops = RouteMapStops();
        if (settings.OnlyWithRoute && stops is null)
        {
            _checklistWindow?.Hide();
            return;
        }
        if (_checklistWindow is null)
        {
            _checklistWindow = new ChecklistWindow(settings, SavePlannerPreferences, _checkedDepartureItems) { FontFamily = FontFamily };
            if (settings.Left is null && settings.Top is null)
            {
                // Keep the initial checklist clear of the boat overlay's top-right default.
                _checklistWindow.Left = SystemParameters.WorkArea.Left + 20;
            }
            _checklistWindow.Closed += (_, _) =>
            {
                _checklistWindow = null;
                if (!_helpClosing) ChecklistOverlayCheckBox.IsChecked = false;
            };
        }
        var relevant = settings.OnlyWithRoute && settings.RouteOnly && stops is not null;
        var context = new DepartureChecklistContext(
            stops is null ? "" : string.Join(" \u2192 ", stops.Select(PostName)),
            SelectedTransport?.Name ?? "the selected transport",
            SelectedGuaranteeLetter, GetGuaranteeLetterSupply(SelectedGuaranteeLetter) == 0,
            ActiveExpansionCoverCost > 0, ExpansionCoverSupply == 0, IsBarterMode, IsGroupMode,
            _plannerPreferences.CommerceModifierCounts, _plannerPreferences.AccessoryEnchantIds,
            _plannerPreferences.AccessoryEnchantRolls,
            SelectedTransport is { IsAlpaca: true } ? DepartureCompanion.Alpaca
                : SelectedTransport is { IsWilliamPartner: true } ? DepartureCompanion.William
                : SelectedTransport is { IsPartner: true } ? DepartureCompanion.Partner : DepartureCompanion.None);
        _checklistWindow.ShowChecklist(DepartureChecklist.Build(context, relevant), relevant);
        if (!_checklistWindow.IsVisible) _checklistWindow.Show();
    }

    private void ResetDepartureChecklist()
    {
        _checkedDepartureItems.Clear();
        _checklistWindow?.ResetChecks();
    }
}
