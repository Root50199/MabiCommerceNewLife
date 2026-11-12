using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class CommunityBarterRotationTests
{
    private static readonly Dictionary<int, int> RotatingPosts = new()
    {
        [21006] = 401, [21007] = 401, [22006] = 402, [22007] = 402
    };

    private static CommunityObservation Observation(int product, int post, long time, long sequence = 1) =>
        new(sequence, time, "barter", product, post, null, 0, []);

    [TestMethod]
    public void NewestRotatingScanPerOutpostWinsRegardlessOfInputOrderOrStock()
    {
        var old = Observation(21006, 401, 100, 4);
        var latest = Observation(21007, 401, 200, 3);
        var otherPost = Observation(22006, 402, 150, 2);
        foreach (var observations in new[] { new[] { latest, otherPost, old }, new[] { old, otherPost, latest } })
            CollectionAssert.AreEquivalent(new[] { latest, otherPost },
                CommunityBarterRotation.Latest(observations, RotatingPosts, new Dictionary<int, CommunityBarterRotationUpdate>()).ToArray());
    }

    [TestMethod]
    public void SequenceBreaksEqualServerTimestampTies()
    {
        var latest = Observation(21007, 401, 200, 5);
        var result = CommunityBarterRotation.Latest(
            [latest, Observation(21006, 401, 200, 4)], RotatingPosts, new Dictionary<int, CommunityBarterRotationUpdate>());
        Assert.AreEqual(latest, result.Single());
    }

    [TestMethod]
    public void NewerOrEqualLocalChoiceAndReplayedSharedScanAreKept()
    {
        foreach (var localTime in new long[] { 200, 201 })
            Assert.AreEqual(0, CommunityBarterRotation.Latest(
                [Observation(21007, 401, 200)], RotatingPosts,
                new Dictionary<int, CommunityBarterRotationUpdate> { [401] = new(localTime) }).Count);
        Assert.AreEqual(1, CommunityBarterRotation.Latest(
            [Observation(21007, 401, 200)], RotatingPosts,
            new Dictionary<int, CommunityBarterRotationUpdate> { [401] = new(199) }).Count);
    }

    [TestMethod]
    public void LaterSequenceAtTheSameTimeAppliesButReplaysDoNot()
    {
        var updates = new Dictionary<int, CommunityBarterRotationUpdate> { [401] = new(200, 4) };
        Assert.AreEqual(0, CommunityBarterRotation.Latest([Observation(21006, 401, 200, 4)], RotatingPosts, updates).Count);
        Assert.AreEqual(1, CommunityBarterRotation.Latest([Observation(21007, 401, 200, 5)], RotatingPosts, updates).Count);
        Assert.AreEqual(0, CommunityBarterRotation.Latest([Observation(21006, 401, 199, 6)], RotatingPosts, updates).Count);
    }

    [TestMethod]
    public void StandardGoodsWrongModesAndWrongOutpostsDoNotChooseARotation()
    {
        Assert.AreEqual(0, CommunityBarterRotation.Latest(
            [Observation(21001, 401, 300),
             Observation(21006, 402, 300),
             Observation(21006, 401, 300) with { Mode = "trade" },
             Observation(22006, 402, 300) with { Mode = "group" }],
            RotatingPosts, new Dictionary<int, CommunityBarterRotationUpdate>()).Count);
    }

    [TestMethod]
    public void MissingOutpostObservationsDoNotClearItsChoice()
    {
        var times = new Dictionary<int, CommunityBarterRotationUpdate> { [401] = new(100), [402] = new(100) };
        var next = Observation(21007, 401, 200);
        Assert.AreEqual(next, CommunityBarterRotation.Latest([next], RotatingPosts, times).Single());
        Assert.AreEqual(100L, times[402].Time);
        Assert.AreEqual(0, CommunityBarterRotation.Latest([], RotatingPosts, times).Count);
    }
}
