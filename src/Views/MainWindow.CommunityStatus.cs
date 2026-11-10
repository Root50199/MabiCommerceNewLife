using System.Net;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace MabiCommerceNewLife;

public partial class MainWindow
{
    private enum CommunityHealth { Off, Healthy, Waiting, Failed }
    private CommunityHealth _communityHealth;
    private string _communityHealthMessage = "Price sharing OFF.";
    private Exception? _communityIssue;
    private DateTimeOffset? _communityRetryAt;
    private bool _communityAutoRetry;
    private bool _communityRetryRefresh;
    private bool _communityChecking;

    private TimeSpan CommunityWaitRemaining => _communityRetryAt is { } at && at > DateTimeOffset.UtcNow
        ? at - DateTimeOffset.UtcNow : TimeSpan.Zero;

    private void SetCommunityHealth(CommunityHealth health, string message)
    {
        _communityHealth = health;
        _communityHealthMessage = message;
        _communityIssue = null;
        _communityRetryAt = null;
        _communityAutoRetry = false;
        _communityRetryRefresh = false;
        RefreshCommunityHealth();
    }

    private void CommunityHealthy()
    {
        if (!_communityScanQueue.HasFailures)
            SetCommunityHealth(CommunityHealth.Healthy, "Price sharing connected.");
    }

    private void CommunityFailed(Exception exception, bool refresh = false)
    {
        SetCommunityHealth(exception is CommunityApiException { StatusCode: HttpStatusCode.TooManyRequests } ||
            CommunityRetry.IsTemporary(exception) ? CommunityHealth.Waiting : CommunityHealth.Failed,
            exception is CommunityApiException { StatusCode: HttpStatusCode.TooManyRequests }
                ? "Price sharing rate limited." : "Price sharing request failed.");
        _communityIssue = exception;
        _communityRetryRefresh = refresh;
        if (exception is CommunityApiException { RetryAfter: { } delay }) SetCommunityRetryAt(delay);
        RefreshCommunityHealth();
    }

    private void SetCommunityRetryAt(TimeSpan delay)
    {
        var now = DateTimeOffset.UtcNow;
        _communityRetryAt = delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now.Add(delay);
    }

    private void CommunityRetryWaiting(Exception exception, TimeSpan delay) =>
        CommunityRetryWaiting(exception, delay, false);

    private void CommunityRetryWaiting(Exception exception, TimeSpan delay, bool refresh)
    {
        CommunityFailed(exception, refresh);
        _communityAutoRetry = true;
        SetCommunityRetryAt(delay);
        RefreshCommunityButtonState();
    }

    private TimeSpan CommunityManualWait => _communityRetryRefresh && _communityRefreshGate.Remaining > CommunityWaitRemaining
        ? _communityRefreshGate.Remaining : CommunityWaitRemaining;

    private string CommunityWaitDescription => CommunityManualWait > TimeSpan.Zero
        ? $"Wait {Math.Ceiling(CommunityManualWait.TotalSeconds):N0}s" +
            (_communityAutoRetry ? "; retrying automatically." : " before retrying.")
        : _communityAutoRetry ? "Retrying automatically." : "Manual retry available.";

    private bool CanRetryCommunity => (_communityHealth is CommunityHealth.Waiting or CommunityHealth.Failed ||
        _communityScanQueue.HasFailures) &&
        !_helpClosing && _plannerPreferences.CommunitySharingEnabled &&
        !_communityConnecting && !_communityChecking && !_communityRefreshGate.IsBusy && !_communityBatchUploading &&
        !_communityAutoRetry && CommunityManualWait == TimeSpan.Zero;

    private void RefreshCommunityHealth()
    {
        if (CommunityStatusDot is null) return;
        CommunityStatusDot.Fill = _communityHealth switch
        {
            CommunityHealth.Healthy => Brushes.LimeGreen,
            CommunityHealth.Waiting => Brushes.Gold,
            CommunityHealth.Failed => Brushes.IndianRed,
            _ => Brushes.Gray
        };
        var text = _communityHealthMessage;
        if (_communityScanQueue.Count > 0) text += $" {CommunityQueueSummary} Click for details.";
        if (_communityHealth is CommunityHealth.Waiting or CommunityHealth.Failed)
            text += $" {CommunityWaitDescription} Click for details.";
        CommunityStatusButton.ToolTip = text;
        System.Windows.Automation.AutomationProperties.SetName(CommunityStatusButton, text);
    }

    internal (string Details, string Wait, bool CanRetry) CommunityIssueDetails()
    {
        if (_communityHealth is CommunityHealth.Healthy or CommunityHealth.Off && _communityScanQueue.Count == 0)
            return (_communityHealthMessage, "", false);
        if (_communityHealth == CommunityHealth.Healthy && !_communityScanQueue.HasFailures)
            return ($"{CommunityQueueSummary}\n\nPrices apply locally immediately. Each outpost/mode sends one batch " +
                "after 60s idle, at four minutes, or when all goods are queued. New accepted rescans replace earlier ones.\n\n" +
                "Pending uploads live only in memory. Turning sharing OFF discards them; closing asks first.", "", false);
        var details = _communityIssue is null ? _communityHealthMessage : CommunityNetworkError(_communityIssue);
        if (_communityScanQueue.Count > 0) details += $"\n\n{CommunityQueueSummary}";
        details += "\n\n" + (_communityIssue switch
        {
            CommunityApiException { StatusCode: HttpStatusCode.TooManyRequests } => _communityRetryAt is null
                ? "The service is limiting requests but supplied no usable wait time. Try again later; repeated reconnects can extend the problem."
                : "The service is limiting requests. Wait for the timer; repeated reconnects can extend the problem.",
            CommunityApiException { StatusCode: HttpStatusCode.Unauthorized } =>
                "The saved sharing authorization was rejected or expired. Retry checks the same saved identity; it will not silently create a replacement. Contact the service owner if rejection continues.",
            InvalidDataException or JsonException =>
                "The server response or saved credentials failed validation. Check for an app update and a damaged local credentials file; no replacement was created.",
            UnauthorizedAccessException or IOException =>
                "The app could not read or save sharing credentials. Use a writable app folder and check file permissions. Retry preserves the existing identity.",
            _ => "Check your connection and service availability, then retry."
        });
        details += "\n\nTurn sharing OFF in Settings to cancel.";
        details += "\n\nLocal planning still works. Accepted scans wait in memory only. Failed batches need manual retry; OFF discards them. Closing asks before discarding.";
        return (details, CommunityWaitDescription, CanRetryCommunity);
    }

    private async void CommunityStatus_Click(object sender, RoutedEventArgs e)
    {
        if (_communityHealth is not (CommunityHealth.Waiting or CommunityHealth.Failed) && _communityScanQueue.Count == 0) return;
        if (new CommunityStatusWindow(this).ShowDialog() != true || !CanRetryCommunity) return;
        if (_communityIssue is CommunityApiException { StatusCode: HttpStatusCode.Unauthorized } &&
            _communityScanQueue.Count == 0)
            StopCommunitySharing();
        if (_communityScanQueue.HasFailures)
        {
            await FlushCommunityBatchesAsync(force: true);
            return;
        }
        if (!CommunitySharingEnabled)
        {
            await ConnectCommunitySharingAsync();
            return;
        }
        if (_communityRetryRefresh)
        {
            CommunityRefresh_Click(sender, e);
            return;
        }
        if (_communitySharing is not { } sharing || _communityClient is not { } client) return;
        var cancellationToken = sharing.Token;
        _communityChecking = true;
        try
        {
            await CommunityRetry.RunAsync(() => client.GetPrivacyAsync(cancellationToken), false, false,
                CommunityRetryWaiting, cancellationToken);
            if (!_helpClosing && ReferenceEquals(_communitySharing, sharing))
            {
                CommunityHealthy();
                SetStatus("Price sharing reachable. Pending batches remain available; scans not accepted for sharing stay local.");
            }
        }
        catch (Exception exception) when (IsCommunityError(exception))
        {
            if (!_helpClosing && ReferenceEquals(_communitySharing, sharing)) CommunityFailed(exception);
        }
        finally
        {
            _communityChecking = false;
            _communityAutoRetry = false;
            if (!_helpClosing) RefreshCommunityButtonState();
        }
    }
}
