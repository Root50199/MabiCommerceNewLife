using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class CommunitySharingConsentTests
{
    [TestMethod]
    public void CurrentTermsRemainAcceptedAcrossAppUpdatesAndRebuilds()
    {
        // A persisted consent contract is deliberately independent of assembly/release versions.
        Assert.AreEqual("outpost-batches-v1", CommunitySharingConsent.CurrentVersion);
        Assert.IsTrue(CommunitySharingConsent.IsCurrent(CommunityClient.ProductionEndpoint, "outpost-batches-v1"));
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow(CommunityClient.ProductionEndpoint, null)]
    [DataRow(CommunityClient.ProductionEndpoint, "old-terms")]
    [DataRow("http://127.0.0.1:8787/", "outpost-batches-v1")]
    [DataRow("https://example.com/", "outpost-batches-v1")]
    public void MissingConsentOrChangedTermsOrServiceRequireNewConsent(string? endpoint, string? version) =>
        Assert.IsFalse(CommunitySharingConsent.IsCurrent(endpoint, version));
}
