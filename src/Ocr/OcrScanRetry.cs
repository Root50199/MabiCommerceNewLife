using System.Diagnostics;
using System.Threading.Channels;

namespace MabiCommerceNewLife;

public static class OcrScanRetry
{
    public static async Task<T> ReadAsync<T>(T initial, Func<byte[]> capture,
        Func<byte[], Task<T>> recognize, Func<T, bool> isComplete, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (isComplete(initial)) return initial;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var images = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var producer = CaptureRetriesAsync();
        var last = initial;
        try
        {
            await foreach (var image in images.Reader.ReadAllAsync(cancellationToken))
            {
                last = await recognize(image);
                cancellationToken.ThrowIfCancellationRequested();
                if (isComplete(last)) return last;
            }
            return last;
        }
        finally
        {
            stop.Cancel();
            await producer;
        }

        async Task CaptureRetriesAsync()
        {
            var clock = Stopwatch.StartNew();
            try
            {
                // Capture on a fixed schedule even when OCR takes longer than a second.
                for (var attempt = 1; attempt <= 5; attempt++)
                {
                    var delay = TimeSpan.FromSeconds(attempt) - clock.Elapsed;
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, stop.Token);
                    stop.Token.ThrowIfCancellationRequested();
                    images.Writer.TryWrite(capture());
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            finally
            {
                images.Writer.TryComplete();
            }
        }
    }
}
