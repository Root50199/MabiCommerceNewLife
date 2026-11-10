using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class CommunityRefreshGateTests
{
    [TestMethod]
    public void FirstRefreshIsImmediateAndNextAttemptRequiresFullSixtySeconds()
    {
        var clock = new ManualClock();
        var gate = new CommunityRefreshGate(clock);
        Assert.IsTrue(gate.CanRefresh);
        Assert.IsTrue(gate.TryBegin());
        Assert.AreEqual(TimeSpan.FromSeconds(60), gate.Remaining);
        Assert.IsFalse(gate.TryBegin());
        gate.Complete();
        clock.Advance(TimeSpan.FromMilliseconds(59999));
        Assert.AreEqual(TimeSpan.FromMilliseconds(1), gate.Remaining);
        Assert.IsFalse(gate.TryBegin());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.IsTrue(gate.TryBegin());
        Assert.AreEqual(TimeSpan.FromSeconds(60), gate.Remaining);
    }

    [TestMethod]
    public void FailureCancellationOrDisablingCannotResetAttemptCooldown()
    {
        var clock = new ManualClock();
        var gate = new CommunityRefreshGate(clock);
        Assert.IsTrue(gate.TryBegin());
        gate.Complete();
        gate.Complete();
        Assert.IsFalse(gate.TryBegin());
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.IsTrue(gate.TryBegin());
    }

    [TestMethod]
    public void SlowRefreshBlocksOverlapEvenAfterMinuteElapses()
    {
        var clock = new ManualClock();
        var gate = new CommunityRefreshGate(clock);
        gate.TryBegin();
        clock.Advance(TimeSpan.FromSeconds(90));
        Assert.AreEqual(TimeSpan.Zero, gate.Remaining);
        Assert.IsFalse(gate.CanRefresh);
        Assert.IsFalse(gate.TryBegin());
        gate.Complete();
        Assert.IsTrue(gate.TryBegin());
        Assert.AreEqual(TimeSpan.FromSeconds(60), gate.Remaining);
    }

    [TestMethod]
    public void AutomaticRetryRestartsCooldownAndKeepsBusyUntilCompleted()
    {
        var clock = new ManualClock();
        var gate = new CommunityRefreshGate(clock);
        gate.TryBegin();
        clock.Advance(TimeSpan.FromMinutes(1));
        gate.RecordRetry();
        Assert.IsTrue(gate.IsBusy);
        Assert.AreEqual(TimeSpan.FromMinutes(1), gate.Remaining);
        gate.Complete();
        Assert.IsFalse(gate.IsBusy);
        Assert.IsFalse(gate.CanRefresh);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan amount) => _timestamp += amount.Ticks;
    }
}
