namespace MabiCommerceNewLife;

public static class CommunitySharingConsent
{
    // Change only when the consent terms change, never for an app version or rebuild.
    public const string CurrentVersion = "outpost-batches-v1";

    public static bool IsCurrent(string? endpoint, string? version) =>
        endpoint == CommunityClient.DefaultEndpoint && version == CurrentVersion;
}
