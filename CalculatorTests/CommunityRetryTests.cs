using System.Net;
using System.Net.Http;
using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class CommunityRetryTests
{
    private static CommunityApiException Limited(double? seconds) =>
        new("Limited", HttpStatusCode.TooManyRequests, "rate_limited",
            seconds is { } value ? TimeSpan.FromSeconds(value) : null);

    [TestMethod]
    public void OnlyKnownSubMinuteLimitsRetryOutsideStartup()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(59), CommunityRetry.DelayFor(Limited(59), false));
        Assert.AreEqual(TimeSpan.FromSeconds(1), CommunityRetry.DelayFor(Limited(0), false));
        foreach (var seconds in new double?[] { null, -1, 60, 3600 })
            Assert.IsNull(CommunityRetry.DelayFor(Limited(seconds), false));
        Assert.AreEqual(TimeSpan.FromHours(1), CommunityRetry.DelayFor(Limited(3600), true));
        Assert.IsNull(CommunityRetry.DelayFor(Limited(null), true));
        Assert.IsNull(CommunityRetry.DelayFor(new CommunityApiException("bad", HttpStatusCode.Forbidden,
            "denied", TimeSpan.FromSeconds(1)), true));
    }

    [TestMethod]
    public async Task RepeatedLimitsHonorEachWaitAndRecover()
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();
        var result = await CommunityRetry.RunAsync(() =>
        {
            attempts++;
            if (attempts < 3) throw Limited(attempts * 2);
            return Task.FromResult(42);
        }, false, false, (_, _) => { }, default, (delay, _) =>
        {
            waits.Add(delay);
            return Task.CompletedTask;
        });
        Assert.AreEqual(42, result);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) }, waits);
    }

    [TestMethod]
    public async Task OffDuringWaitCancelsBeforeAnotherAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => CommunityRetry.RunAsync<int>(() =>
        {
            attempts++;
            throw Limited(30);
        }, false, false, (_, _) => cancellation.Cancel(), cancellation.Token));
        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    public async Task StartupSafeReadRetriesTemporaryErrorsOnlyTwice()
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => CommunityRetry.RunAsync<int>(() =>
        {
            attempts++;
            throw new HttpRequestException("offline");
        }, true, true, (_, _) => { }, default, (delay, _) =>
        {
            waits.Add(delay);
            return Task.CompletedTask;
        }));
        Assert.AreEqual(3, attempts);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) }, waits);
    }

    [TestMethod]
    public async Task StartupTemporaryServerFailureHonorsSuppliedWait()
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();
        await CommunityRetry.RunAsync(() =>
        {
            if (++attempts < 3)
                throw new CommunityApiException("Unavailable", HttpStatusCode.ServiceUnavailable,
                    "unavailable", TimeSpan.FromSeconds(45));
            return Task.FromResult(true);
        }, true, true, (_, _) => { }, default, (delay, _) =>
        {
            waits.Add(delay);
            return Task.CompletedTask;
        });
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(45) }, waits);
    }

    [TestMethod]
    public async Task AmbiguousRegistrationFailureAndNonStartupNetworkFailureNeverReplay()
    {
        foreach (var startup in new[] { false, true })
        {
            var attempts = 0;
            await Assert.ThrowsExceptionAsync<HttpRequestException>(() => CommunityRetry.RunAsync<int>(() =>
            {
                attempts++;
                throw new HttpRequestException("Response lost");
            }, startup, false, (_, _) => Assert.Fail("Must not retry"), default));
            Assert.AreEqual(1, attempts);
        }
    }

    [TestMethod]
    public async Task ValidationAndLongOrUnknownNonStartupLimitsRemainExplicit()
    {
        foreach (var error in new Exception[] { new InvalidDataException("bad policy"), Limited(60), Limited(null) })
        {
            var attempts = 0;
            try
            {
                await CommunityRetry.RunAsync<int>(() => { attempts++; throw error; },
                    false, false, (_, _) => Assert.Fail("Must not retry"), default);
                Assert.Fail("Expected original exception");
            }
            catch (Exception actual) when (ReferenceEquals(actual, error)) { }
            Assert.AreEqual(1, attempts);
        }
    }

    [TestMethod]
    public async Task RefreshRetryKeepsGlobalMinuteCooldown()
    {
        var attempts = 0;
        TimeSpan? actualWait = null;
        await CommunityRetry.RunAsync(() =>
        {
            if (++attempts == 1) throw Limited(5);
            return Task.FromResult(true);
        }, false, false, (_, _) => { }, default,
            (delay, _) => { actualWait = delay; return Task.CompletedTask; },
            () => TimeSpan.FromSeconds(58));
        Assert.AreEqual(TimeSpan.FromSeconds(58), actualWait);
    }
}
