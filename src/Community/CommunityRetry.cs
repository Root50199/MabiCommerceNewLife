using System.Net;
using System.Net.Http;

namespace MabiCommerceNewLife;

public static class CommunityRetry
{
    public static TimeSpan? DelayFor(Exception exception, bool startup)
    {
        if (exception is not CommunityApiException { StatusCode: HttpStatusCode.TooManyRequests, RetryAfter: { } delay } ||
            delay < TimeSpan.Zero || (!startup && delay >= TimeSpan.FromMinutes(1)))
            return null;
        return delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay;
    }

    public static bool IsTemporary(Exception exception) => exception is OperationCanceledException ||
        exception is HttpRequestException http &&
        (http.StatusCode is null || (int)http.StatusCode >= 500);

    public static async Task<T> RunAsync<T>(Func<Task<T>> attempt, bool startup, bool safeStartupRetry,
        Action<Exception, TimeSpan> waiting, CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? wait = null, Func<TimeSpan>? minimumWait = null)
    {
        wait ??= WaitAsync;
        var temporaryRetries = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await attempt(); }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                var delay = DelayFor(exception, startup);
                if (delay is null && startup && safeStartupRetry && IsTemporary(exception) && temporaryRetries < 2)
                {
                    temporaryRetries++;
                    delay = exception is CommunityApiException { RetryAfter: { } serverWait } && serverWait > TimeSpan.Zero
                        ? serverWait : TimeSpan.FromSeconds(5 * temporaryRetries);
                }
                if (delay is null) throw;
                if (minimumWait?.Invoke() is { } minimum && minimum > delay) delay = minimum;
                waiting(exception, delay.Value);
                await wait(delay.Value, cancellationToken);
            }
        }
    }

    private static async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        // Task.Delay has a platform maximum; long server waits remain cancellable.
        while (delay > TimeSpan.FromDays(1))
        {
            await Task.Delay(TimeSpan.FromDays(1), cancellationToken);
            delay -= TimeSpan.FromDays(1);
        }
        await Task.Delay(delay, cancellationToken);
    }
}
