using System.Text.Json;
using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class PortableConfigTests
{
    public sealed class TestSettings
    {
        public decimal MaterialValue { get; set; } = 134000m;
        public bool PartnerEnabled { get; set; }
        public int[] AvailableTransportIds { get; set; } = [];
    }

    [TestMethod]
    public void ProfilesAreIndependentPerExecutableFolderAndRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), $"portable-config-{Guid.NewGuid():N}");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            var path = PortableConfig.SettingsPath(first);
            Assert.AreEqual(Path.Combine(first, "config.json"), path);
            Assert.AreEqual(Path.Combine(first, "trade-history.json"), PortableConfig.HistoryPath(first));
            var settings = PortableConfig.Load(path, () => new TestSettings());
            settings.MaterialValue = 0m;
            settings.PartnerEnabled = true;
            settings.AvailableTransportIds = [1, 3];
            PortableConfig.Save(path, settings);
            var loaded = PortableConfig.Load(path, () => new TestSettings());
            Assert.AreEqual(0m, loaded.MaterialValue);
            Assert.IsTrue(loaded.PartnerEnabled);
            CollectionAssert.AreEqual(new[] { 1, 3 }, loaded.AvailableTransportIds);
            Assert.IsFalse(File.Exists(path + ".tmp"));
            Assert.AreEqual(134000m, PortableConfig.Load(PortableConfig.SettingsPath(second),
                () => new TestSettings()).MaterialValue);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void FailedSavePreservesExistingConfigAndReportsFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"portable-config-{Guid.NewGuid():N}.json");
        try
        {
            PortableConfig.Save(path, new TestSettings { MaterialValue = 20m });
            var original = File.ReadAllText(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsException<UnauthorizedAccessException>(() =>
                    PortableConfig.Save(path, new TestSettings { MaterialValue = 30m }));
            Assert.AreEqual(original, File.ReadAllText(path));
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void InvalidConfigIsNotSilentlyReset()
    {
        var path = Path.Combine(Path.GetTempPath(), $"portable-config-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{invalid");
            Assert.ThrowsException<JsonException>(() => PortableConfig.Load(path, () => new TestSettings()));
            Assert.AreEqual("{invalid", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
