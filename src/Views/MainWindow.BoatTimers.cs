using System.Windows;

namespace MabiCommerceNewLife;

public partial class MainWindow
{
    private BoatTimerWindow? _boatTimerWindow;

    private void BoatTimerSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _plannerPreferences.BoatTimerOverlay.Enabled = BoatTimerOverlayCheckBox.IsChecked == true;
        _plannerPreferences.BoatTimerOverlay.RouteOnly = RouteBoatTimersOnlyCheckBox.IsChecked == true;
        _plannerPreferences.BoatTimerOverlay.OnlyWithRoute = BoatTimersOnlyWithRouteCheckBox.IsChecked == true;
        SavePlannerPreferences();
        RefreshBoatTimers();
    }

    private void RefreshBoatTimers()
    {
        if (_initializing || !IsLoaded || _helpClosing || _tutorialHost is not null) return;
        var settings = _plannerPreferences.BoatTimerOverlay;
        if (!settings.Enabled)
        {
            _boatTimerWindow?.Close();
            return;
        }
        var stops = settings.OnlyWithRoute ? RouteMapStops() : null;
        if (settings.OnlyWithRoute && stops is null)
        {
            _boatTimerWindow?.Hide();
            return;
        }
        if (_boatTimerWindow is null)
        {
            _boatTimerWindow = new BoatTimerWindow(settings, SavePlannerPreferences) { FontFamily = FontFamily };
            _boatTimerWindow.Closed += (_, _) =>
            {
                _boatTimerWindow = null;
                if (!_helpClosing) BoatTimerOverlayCheckBox.IsChecked = false;
            };
        }
        var now = DateTimeOffset.UtcNow;
        var filtered = settings.RouteOnly && stops is not null;
        var journeys = settings.RouteOnly && stops is not null ? SelectedRouteLegs(stops, now)
            .Select(leg => leg.Ship).OfType<ShipTravelEstimate>() : null;
        _boatTimerWindow.ShowTimers(_shipScheduleEstimator.GetBoatTimers(now, journeys), filtered);
        if (!_boatTimerWindow.IsVisible) _boatTimerWindow.Show();
    }

    private List<(int From, int To, ShipTravelEstimate? Ship)> SelectedRouteLegs(
        IReadOnlyList<int> stops, DateTimeOffset departureTime)
    {
        var transportId = SelectedTransport?.Id ?? TransportIds.Backpack;
        var legs = new List<(int From, int To, ShipTravelEstimate? Ship)>();
        foreach (var (from, to) in stops.Zip(stops.Skip(1)))
        {
            var ship = _shipScheduleEstimator.Estimate(from, to, departureTime, transportId, PostName,
                _plannerPreferences.FerryBufferEnabled ? _plannerPreferences.FerryBufferSeconds : 0,
                ModifierTotals(IsGroupMode).TransportSpeedPercent, _plannerPreferences.PortalLoadBufferSeconds);
            legs.Add((from, to, ship));
            var minutes = GetManualQuoteMinutes(from, to, transportId, out _, departureTime);
            if (minutes is > 0) departureTime = departureTime.AddMinutes((double)minutes.Value);
        }
        return legs;
    }
}
