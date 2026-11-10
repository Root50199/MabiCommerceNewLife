using System.ComponentModel;
using System.Windows;

namespace MabiCommerceNewLife;

public partial class MainWindow
{
    private readonly CommunityScanQueue _communityScanQueue = new();
    private Task<bool>? _communityBatchTask;
    private CancellationTokenSource? _communityBatchCancellation;
    private bool _communityBatchUploading;
    private bool _communityQueuePrompt;
    private bool _communityCloseApproved;

    private string CommunityQueueSummary => _communityScanQueue.Count == 0 ? "" :
        $"{_communityScanQueue.Count} goods pending sharing. " +
        (_communityBatchUploading ? "Uploading outpost batch." :
            _communityScanQueue.HasFailures ? "Upload failed; use Retry to resend the pending batch." :
            _communityScanQueue.NextUploadIn is { } delay
                ? $"Next batch in {Math.Ceiling(delay.TotalSeconds):N0}s, or sooner if the outpost is complete."
                : "Waiting to upload.");

    private async Task<bool> FlushCommunityBatchesAsync(bool force = false)
    {
        if (_communityBatchTask is { IsCompleted: false } running)
        {
            if (!force) return false;
            if (!await running) return false;
        }
        if (_communityScanQueue.Count == 0) return true;
        if (_helpClosing || !CommunitySharingEnabled || (_communityQueuePrompt && !force) ||
            CommunityWaitRemaining > TimeSpan.Zero || _communityChecking || _communityAutoRetry)
            return false;
        var task = UploadCommunityBatchesAsync(force);
        _communityBatchTask = task;
        return await task;
    }

    private async Task<bool> UploadCommunityBatchesAsync(bool force)
    {
        if (_communitySharing is not { } sharing || _communityClient is not { ConnectedSession: { } session } client)
            return false;
        using var uploadCancellation = CancellationTokenSource.CreateLinkedTokenSource(sharing.Token);
        _communityBatchCancellation = uploadCancellation;
        var token = uploadCancellation.Token;
        _communityBatchUploading = true;
        try
        {
            while (_communityScanQueue.TakeNext(force) is { } batch)
            {
                try
                {
                    await client.UploadBatchAsync(batch, session, token);
                    token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_communitySharing, sharing)) return false;
                    _communityScanQueue.Complete(batch);
                    CommunityHealthy();
                    SetStatus($"Shared {batch.Scans.Count} goods in one outpost batch. " +
                        (_communityScanQueue.Count > 0 ? $"{_communityScanQueue.Count} goods still pending." : "No pending uploads."));
                }
                catch (Exception exception) when (IsCommunityError(exception))
                {
                    if (!token.IsCancellationRequested && ReferenceEquals(_communitySharing, sharing))
                    {
                        _communityScanQueue.Fail(batch);
                        CommunityFailed(exception);
                        SetStatus("Outpost upload failed; pending goods kept in memory. Click the status dot to retry. Local prices are unchanged.", true);
                    }
                    return false;
                }
            }
            return !force || _communityScanQueue.Count == 0;
        }
        finally
        {
            _communityBatchCancellation = null;
            _communityBatchUploading = false;
            if (!_helpClosing) RefreshCommunityHealth();
        }
    }

    private bool DeferClosingForCommunityQueue(CancelEventArgs e)
    {
        if (_communityCloseApproved || _communityScanQueue.Count == 0) return false;
        e.Cancel = true;
        if (_communityQueuePrompt) return true;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (await ConfirmPendingCommunityUploadsAsync())
            {
                _communityCloseApproved = true;
                Close();
            }
        }));
        return true;
    }

    private async Task<bool> ConfirmPendingCommunityUploadsAsync()
    {
        if (_communityScanQueue.Count == 0) return true;
        if (_communityQueuePrompt) return false;
        _communityQueuePrompt = true;
        try
        {
            var prompt = new CommunityPendingWindow(this, _communityScanQueue.Count);
            prompt.ShowDialog();
            if (prompt.Choice == CommunityPendingChoice.Discard)
            {
                _communityBatchCancellation?.Cancel();
                _communityScanQueue.Clear();
                RefreshCommunityHealth();
                return true;
            }
            if (prompt.Choice != CommunityPendingChoice.Upload) return false;
            IsEnabled = false;
            try
            {
                if (await FlushCommunityBatchesAsync(force: true)) return true;
                SetStatus("Could not finish pending uploads. App kept open; check the status dot, retry later, or close and choose Discard.", true);
                return false;
            }
            finally { IsEnabled = true; }
        }
        finally { _communityQueuePrompt = false; }
    }
}
