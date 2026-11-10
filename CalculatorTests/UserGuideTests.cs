using System.Xml.Linq;
using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class UserGuideTests
{
    [TestMethod]
    public void SharingConsentIsThemedConciseAndDefaultsToCancel()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "GuideTargets", "SharingConsentWindow.xaml"));
        XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        Assert.AreEqual("None", (string?)document.Root!.Attribute("WindowStyle"));
        var summary = Named("ConsentSummary").Attribute("Text")!.Value;
        Assert.IsTrue(summary.Length < 350);
        StringAssert.Contains(summary, "30 days");
        StringAssert.Contains(summary, "price history stays");
        StringAssert.Contains(summary, "screenshots");
        Assert.IsNull(Named("PrivacyDetails").Attribute("IsExpanded"));
        Assert.AreEqual("#E9D9B8", (string?)Named("ConsentSummary").Attribute("Foreground"));
        Assert.AreEqual("#E9D9B8", (string?)Named("PrivacyDetails").Attribute("Foreground"));
        Assert.AreEqual("True", (string?)Named("DeclineSharingButton").Attribute("IsDefault"));
        Assert.AreEqual("True", (string?)Named("DeclineSharingButton").Attribute("IsCancel"));
        Assert.AreEqual("{DynamicResource CommerceActionButton}", (string?)Named("AcceptSharingButton").Attribute("Style"));
    }

    [TestMethod]
    public void AppIdentityAndFullRepositoryLinkAreVisible()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "GuideTargets", "MainWindow.xaml"));
        XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        Assert.AreEqual("Left", (string?)Named("AppVersionText").Attribute("TextAlignment"));
        Assert.AreEqual("100", (string?)Named("AppVersionText").Attribute("Canvas.Top"));
        Assert.AreEqual("11", (string?)Named("AppVersionText").Attribute("Height"));
        Assert.AreEqual("TextBlock", Named("AppVersionText").Name.LocalName,
            "Use the normal client-font line height so the small version remains visible above the tabs.");
        Assert.IsTrue(document.Descendants().Any(element => (string?)element.Attribute("Text") == "by ROOT50199"));
        var link = Named("RepositoryLink");
        Assert.AreEqual("https://github.com/Root50199/MabiCommerceNewLife", link.Value);
        Assert.AreEqual(link.Value, (string?)link.Attribute("NavigateUri"));
        Assert.AreEqual("RepositoryLink_RequestNavigate", (string?)link.Attribute("RequestNavigate"));
        Assert.IsTrue(link.Ancestors().Contains(Named("SettingsMenuCanvas")));
        Assert.AreEqual("602", (string?)Named("SettingsMenuCanvas").Attribute("Height"));
    }

    [TestMethod]
    public void UpdatePromptUsesAppThemeAndShortExplicitConsentWithDefaultNo()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "GuideTargets", "AppUpdateWindow.xaml"));
        XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        Assert.AreEqual("None", (string?)document.Root!.Attribute("WindowStyle"));
        Assert.AreEqual("CenterOwner", (string?)document.Root.Attribute("WindowStartupLocation"));
        Assert.IsNotNull(Named("BackgroundArt"));
        CollectionAssert.AreEqual(new[] { "UPDATE AVAILABLE", "Download and install?" },
            document.Descendants().Where(element => element.Name.LocalName == "TextBlock" && element.Attribute("Text") is not null)
                .Select(element => element.Attribute("Text")!.Value).ToArray());
        var yes = Named("AcceptUpdateButton");
        var no = Named("DeclineUpdateButton");
        Assert.AreEqual("Yes", (string?)yes.Attribute("Content"));
        Assert.AreEqual("No", (string?)no.Attribute("Content"));
        Assert.AreEqual("{DynamicResource CommerceActionButton}", (string?)yes.Attribute("Style"));
        Assert.AreEqual((string?)yes.Attribute("Style"), (string?)no.Attribute("Style"));
        Assert.AreEqual("True", (string?)no.Attribute("IsDefault"));
        Assert.AreEqual("True", (string?)no.Attribute("IsCancel"));
        Assert.IsNull(yes.Attribute("IsDefault"));
    }

    [TestMethod]
    public void SettingsExposeUpdateChecksWithoutDuplicateTooltipsOrScannerDependency()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "GuideTargets", "MainWindow.xaml"));
        XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        var check = Named("CheckUpdatesOnOpenCheckBox");
        Assert.AreEqual("CheckUpdatesOnOpen_Changed", (string?)check.Attribute("Checked"));
        Assert.AreEqual("CheckUpdatesOnOpen_Changed", (string?)check.Attribute("Unchecked"));
        Assert.IsNull(check.Attribute("IsEnabled"));
        var button = Named("CheckForUpdatesButton");
        Assert.AreEqual("CheckForUpdates_Click", (string?)button.Attribute("Click"));
        Assert.IsNull(button.Attribute("ToolTip"));
        Assert.IsNull(check.Attribute("ToolTip"));
        StringAssert.Contains(Named("UpdateInfo").Attribute("Message")!.Value, "permission");
    }

    [TestMethod]
    public void NotificationsWrapAndStackErrorsWithIndividualDismissButtons()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "GuideTargets", "MainWindow.xaml"));
        XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        var panel = Named("StatusPanel");
        Assert.AreEqual("4", (string?)panel.Attribute("Canvas.Bottom"));
        Assert.IsNull(panel.Attribute("Height"), "Notifications must grow upward to fit wrapped text.");
        Assert.AreEqual("100", (string?)panel.Attribute("Panel.ZIndex"));
        var scroll = Named("StatusErrorScroll");
        Assert.AreEqual("220", (string?)scroll.Attribute("MaxHeight"));
        Assert.AreEqual("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility"));
        var errors = Named("StatusNotifications");
        var text = errors.Descendants().Single(element => element.Name.LocalName == "TextBlock");
        Assert.AreEqual("Wrap", (string?)text.Attribute("TextWrapping"));
        Assert.IsNull(text.Attribute("TextTrimming"));
        var dismiss = errors.Descendants().Single(element => element.Name.LocalName == "Button");
        Assert.AreEqual("DismissStatusError_Click", (string?)dismiss.Attribute("Click"));
        Assert.AreEqual("X", (string?)dismiss.Attribute("Content"));
        Assert.IsNull(panel.Attribute("IsHitTestVisible"), "Dismiss controls must receive clicks.");
        Assert.AreEqual("DismissStatusNotification_Click", (string?)errors.Descendants()
            .Single(element => element.Name.LocalName == "Border").Attribute("MouseLeftButtonUp"));
        Assert.AreEqual("{Binding Message}", (string?)text.Attribute("Text"));
        Assert.AreEqual("{Binding IsError, Converter={StaticResource BooleanToVisibility}}",
            (string?)dismiss.Attribute("Visibility"));
    }

    [TestMethod]
    public void SettingsGroupsScannerDependenciesAndProfitCurrencyAndUsesSmallRefreshArt()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "GuideTargets", "MainWindow.xaml"));
        XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        foreach (var row in new[] { "SaveScanRegionSettingsRow", "ScanDebugSettingsRow", "SharingSettingsRow" })
        {
            Assert.AreEqual("{Binding IsChecked, ElementName=ScreenReaderModeCheckBox}", (string?)Named(row).Attribute("IsEnabled"), row);
            Assert.AreEqual("16,0,0,0", (string?)Named(row).Attribute("Margin"), row);
            Assert.AreEqual(Named("CalculatorSettingsRows"), Named(row).Parent, row);
            Assert.IsNull(Named(row).Attribute("Canvas.Top"), "Collapsed dev rows must not leave a fixed-position gap.");
        }
        var settings = Named("SettingsMenuCanvas");
        foreach (var text in settings.Descendants().Where(element =>
                     element.Name.LocalName is "TextBlock" or "TextBox" or "RadioButton" or "Button" &&
                     element.Attribute("FontSize") is not null))
            Assert.IsTrue(double.Parse(text.Attribute("FontSize")!.Value, System.Globalization.CultureInfo.InvariantCulture) >= 10,
                $"Settings text is too small: {text}");
        var gold = Named("TradeProfitInGoldRadio");
        var ducats = Named("TradeProfitInDucatsRadio");
        Assert.AreEqual("ProfitCurrency", (string?)gold.Attribute("GroupName"));
        Assert.AreEqual((string?)gold.Attribute("GroupName"), (string?)ducats.Attribute("GroupName"));
        Assert.AreEqual("TradeProfitCurrency_Changed", (string?)gold.Attribute("Checked"));
        Assert.AreEqual((string?)gold.Attribute("Checked"), (string?)ducats.Attribute("Checked"));
        Assert.IsTrue(document.Descendants().Any(element => (string?)element.Attribute("Text") == "CALCULATOR BEHAVIOR"));
        var brackets = Named("SettingsMenuCanvas").Descendants()
            .Where(element => ((string?)element.Attribute("Source"))?.Contains("Bracket", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.AreEqual(8, brackets.Length, "Only the two major category headings retain four bracket images each.");
        foreach (var bracket in brackets)
            Assert.IsTrue(bracket.Parent!.Elements().Any(element =>
                (string?)element.Attribute("Text") is "GENERAL" or "CALCULATOR BEHAVIOR"));
        var refresh = Named("CommunityRefreshButton");
        Assert.AreEqual("15", (string?)refresh.Attribute("Width"));
        Assert.AreEqual("15", (string?)refresh.Attribute("Height"));
        Assert.AreEqual("323", (string?)refresh.Attribute("Canvas.Left"));
        Assert.AreEqual("207", (string?)refresh.Attribute("Canvas.Top"));
        Assert.IsTrue(refresh.Descendants().Any(element => (string?)element.Attribute("Source") == "{DynamicResource RefreshIconArt}"));
        Assert.IsNull(refresh.Attribute("Content"));
        Assert.IsNull(refresh.Attribute("ToolTip"));
        foreach (var name in new[] { "ReadPriceListButton", "GroupReadPriceListButton", "FerryBufferSecondsInput", "PortalLoadBufferSecondsInput" })
            Assert.IsNull(Named(name).Attribute("ToolTip"), name);
        Assert.AreEqual("0.85", (string?)Named("CommunityRefreshIcon").Attribute("Opacity"));
        Assert.AreEqual(refresh.Parent, Named("CommunityRefreshInfo").Parent);
        Assert.AreEqual("0,30,0,0", (string?)Named("AutoResultsScrollViewer").Attribute("Margin"),
            "The scroll viewport, not just its heading, must clear the refresh corner.");
        var loadProfitGrid = Named("LoadProfitResultsGrid").Parent!;
        Assert.AreEqual("32", (string?)loadProfitGrid.Elements().First()
            .Elements().First().Attribute("Height"), "Keep enough vertical space for every destination.");
        var columns = Named("LoadProfitColumnHeaders");
        Assert.AreEqual("17", (string?)columns.Attribute("Height"));
        Assert.AreEqual("38,0,0,0", (string?)columns.Elements().Single(element =>
            (string?)element.Attribute("Text") == "TOWN").Attribute("Margin"));
    }

    [TestMethod]
    public void TradeHistoryTotalsHaveMoreSpaceBelowTitleWithoutMovingTheList()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "GuideTargets", "TradeHistoryWindow.xaml"));
        XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        Assert.AreEqual("64", (string?)Named("TotalsPanel").Attribute("Canvas.Top"));
        Assert.AreEqual("104", (string?)Named("PaperPanel").Attribute("Canvas.Top"));
    }

    [TestMethod]
    public void EveryManualTopicHasConciseTourInstructionsAndValidTargets()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var files = new Dictionary<GuideSurface, string>
        {
            [GuideSurface.Main] = "MainWindow.xaml",
            [GuideSurface.Inventory] = "InventoryWindow.xaml",
            [GuideSurface.Materials] = "BarterMaterialsWindow.xaml",
            [GuideSurface.Settings] = "MainWindow.xaml",
            [GuideSurface.RouteMap] = "RouteMapWindow.xaml"
        };
        var targets = files.ToDictionary(pair => pair.Key, pair =>
            XDocument.Load(Path.Combine(AppContext.BaseDirectory, "GuideTargets", pair.Value))
                .Descendants().Select(element => (string?)element.Attribute(x + "Name")).OfType<string>().ToHashSet());
        targets[GuideSurface.RouteMap].UnionWith(XDocument.Load(Path.Combine(AppContext.BaseDirectory, "GuideTargets", "RouteMapToolbar.xaml"))
            .Descendants().Select(element => (string?)element.Attribute(x + "Name")).OfType<string>());
        Assert.AreEqual(UserGuide.Topics.Count, UserGuide.Topics.Select(topic => topic.Title).Distinct().Count());
        foreach (var topic in UserGuide.Topics)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(topic.Instructions), topic.Title);
            Assert.IsFalse(string.IsNullOrWhiteSpace(topic.TourText), topic.Title);
            Assert.IsTrue(topic.TourText.Length < topic.Instructions.Length, topic.Title);
            Assert.IsTrue(topic.Tab is >= 0 and <= 2, topic.Title);
            Assert.IsTrue(topic.Targets.Length > 0, topic.Title);
            foreach (var target in topic.Targets)
                Assert.IsTrue(targets[topic.Surface].Contains(target), $"{topic.Title}: missing {target}");
        }
    }

    [TestMethod]
    public void GuideCoversMajorFunctionsAndSafeExit()
    {
        var manual = string.Join("\n", UserGuide.Topics.Select(topic => topic.Instructions));
        foreach (var feature in new[] { "TRADE", "GROUP", "BARTER", "SCAN", "CALC", "LOAD PROFIT",
                     "CLEAR LOAD", "Sell", "Trade History", "Commerce Expansion Cover", "Copy for Sheets",
                     "Market Gold", "Merchant Ratings", "config.json", "Escape", "Next/Back" })
            StringAssert.Contains(manual, feature);
        CollectionAssert.AreEquivalent(Enum.GetValues<GuideSurface>(),
            UserGuide.Topics.Select(topic => topic.Surface).Distinct().ToArray());
        StringAssert.Contains(UserGuide.Topics[0].TourText, "X or Escape exits");
    }

    [TestMethod]
    public void EveryManualTopicHasAPackagedPngIllustration()
    {
        Assert.AreEqual(UserGuide.Topics.Count, UserGuide.Topics.Select(topic => topic.ScreenshotFileName).Distinct().Count());
        foreach (var topic in UserGuide.Topics)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "UserGuide", topic.ScreenshotFileName);
            Assert.IsTrue(File.Exists(path), $"{topic.Title}: illustration missing");
            var header = File.ReadAllBytes(path).Take(24).ToArray();
            CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, header.Take(8).ToArray(), topic.Title);
            Assert.IsTrue(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)) >= 80, topic.Title);
            Assert.IsTrue(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)) >= 50, topic.Title);
        }
    }

    [TestMethod]
    public void RevisedTourFocusesOnlyTheRequestedControls()
    {
        Assert.AreEqual("3. Goods", UserGuide.Topics[2].Title);
        Assert.IsFalse(UserGuide.Topics[2].TourText.Contains("stock", StringComparison.OrdinalIgnoreCase));
        CollectionAssert.AreEquivalent(new[] { "AutoSelectedStartModeButton", "AutoBestOverallModeButton", "AutoCalculateButton" },
            UserGuide.Topics[8].Targets);
        CollectionAssert.Contains(UserGuide.Topics[9].Targets, "AutoLetterOverridePicker");
        CollectionAssert.Contains(UserGuide.Topics[5].Targets, "ReadPriceListButton");
        CollectionAssert.Contains(UserGuide.Topics[5].Targets, "GroupReadPriceListButton");
        Assert.IsFalse(UserGuide.Topics[10].TourText.Contains("never sells", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(GuideSurface.RouteMap, UserGuide.Topics[12].Surface);
        CollectionAssert.Contains(UserGuide.Topics[11].Targets, "RouteMapButton");
        CollectionAssert.AreEqual(new[] { "PaperPanel", "LetterPicker" }, UserGuide.Topics[16].Targets);
        Assert.AreEqual(GuideSurface.Materials, UserGuide.Topics[15].Surface);
        CollectionAssert.AreEqual(new[] { "ShoppingListButton" }, UserGuide.Topics[17].Targets);
        Assert.AreEqual(GuideSurface.Settings, UserGuide.Topics[18].Surface);
        Assert.AreEqual("20. Help bubbles", UserGuide.Topics[19].Title);
        Assert.IsTrue(UserGuide.Topics[19].Targets.Length >= 3);
        Assert.AreEqual(21, UserGuide.Topics.Count);
        foreach (var topic in UserGuide.Topics)
            Assert.IsFalse(topic.TourText.Contains("during this tour", StringComparison.OrdinalIgnoreCase));
    }
}
