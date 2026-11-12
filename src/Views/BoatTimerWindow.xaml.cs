using System.Windows;

namespace MabiCommerceNewLife;

public sealed class BoatTimerOverlaySettings : HoverOverlaySettings;

public partial class BoatTimerWindow : Window
{
    private readonly HoverOverlayChrome _chrome;

    public BoatTimerWindow(BoatTimerOverlaySettings settings, Action saveSettings)
    {
        InitializeComponent();
        _chrome = new HoverOverlayChrome(this, BodyBackground, settings, saveSettings, "BOAT TIMERS");
    }

    public void ShowTimers(IReadOnlyList<BoatTimer> timers, bool filtered)
    {
        ScopeText.Text = filtered ? "Boats on the active route" : "All boats";
        TimersList.ItemsSource = timers.Select(timer => new TimerRow(
            $"{timer.From} {(timer.Bidirectional ? "\u2194" : "\u2192")} {timer.To}",
            $"{(timer.BoardingIn == TimeSpan.Zero ? "Boarding now" : $"Boards in {Countdown(timer.BoardingIn)}")}  |  Departs in {Countdown(timer.DepartureIn)}")).ToArray();
        EmptyText.Text = filtered ? "No boats on the active route." : "Boat schedules are unavailable.";
        EmptyText.Visibility = timers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Countdown(TimeSpan time)
    {
        var seconds = (int)Math.Ceiling(time.TotalSeconds);
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    public void ResetPosition() => _chrome.ResetPosition();

    internal Window CreateTutorialPreview() => _chrome.CreateTutorialPreview();

    private sealed record TimerRow(string Route, string Countdown);
}
