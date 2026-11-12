namespace MabiCommerceNewLife;

public sealed record CommunityBarterRotationUpdate(long Time, long? Sequence = null);

public static class CommunityBarterRotation
{
    public static IReadOnlyList<CommunityObservation> Latest(
        IEnumerable<CommunityObservation> observations,
        IReadOnlyDictionary<int, int> rotatingPostByProductId,
        IReadOnlyDictionary<int, CommunityBarterRotationUpdate> updatesByPostId) =>
        observations.Where(observation =>
                observation.Mode.Equals("barter", StringComparison.OrdinalIgnoreCase) &&
                rotatingPostByProductId.TryGetValue(observation.ProductId, out var postId) &&
                postId == observation.SourcePostId)
            .GroupBy(observation => observation.SourcePostId)
            .Select(group => group.OrderByDescending(observation => observation.ReceivedAt)
                .ThenByDescending(observation => observation.Sequence).First())
            .Where(observation => !updatesByPostId.TryGetValue(observation.SourcePostId, out var previous) ||
                observation.ReceivedAt > previous.Time ||
                observation.ReceivedAt == previous.Time && previous.Sequence is { } sequence && observation.Sequence > sequence)
            .ToArray();
}
