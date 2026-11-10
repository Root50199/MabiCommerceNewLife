using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using MabiCommerceNewLife;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CalculatorTests;

[TestClass]
public sealed class NetworkErrorHelpTests
{
    [TestMethod]
    public void AccessDeniedOffersOutboundOnlyGuidanceWithoutClaimingFirewallProof()
    {
        var text = NetworkErrorHelp.Describe(new HttpRequestException("Failed", new SocketException((int)SocketError.AccessDenied)), "example.test");
        StringAssert.Contains(text, "may be blocking");
        StringAssert.Contains(text, "TCP 443");
        StringAssert.Contains(text, "example.test");
        StringAssert.Contains(text, "No inbound rule");
        StringAssert.Contains(text, "Outbound Rules");
        StringAssert.Contains(text, "Do not turn off your firewall");
    }

    [TestMethod]
    public void TimeoutDoesNotDiagnoseFirewall()
    {
        StringAssert.Contains(NetworkErrorHelp.Describe(new TaskCanceledException("Timeout"), "example.test"),
            "does not identify a firewall block");
    }

    [TestMethod]
    public void DnsAndTlsHaveDistinctGuidance()
    {
        StringAssert.Contains(NetworkErrorHelp.Describe(new HttpRequestException("DNS", new SocketException((int)SocketError.HostNotFound)), "example.test"),
            "DNS settings");
        StringAssert.Contains(NetworkErrorHelp.Describe(new HttpRequestException("TLS", new AuthenticationException()), "example.test"),
            "Never disable certificate validation");
    }

    [TestMethod]
    public void HttpResponseAndLocalValidationDoNotGetFirewallAdvice()
    {
        Assert.AreEqual("Unavailable", NetworkErrorHelp.Describe(new HttpRequestException("Unavailable", null, HttpStatusCode.ServiceUnavailable), "example.test"));
        Assert.AreEqual("Invalid package", NetworkErrorHelp.Describe(new InvalidDataException("Invalid package"), "example.test"));
    }
}
