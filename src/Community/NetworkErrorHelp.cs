using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

namespace MabiCommerceNewLife;

public static class NetworkErrorHelp
{
    public static string Describe(Exception exception, string host)
    {
        var chain = new List<Exception>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
            chain.Add(current);
        var socket = chain.OfType<SocketException>().FirstOrDefault();
        var http = chain.OfType<HttpRequestException>().FirstOrDefault();
        // An HTTP response proves the service was reached; do not blame a firewall.
        if (http?.StatusCode is not null) return exception.Message;
        var reason = socket?.SocketErrorCode switch
        {
            SocketError.AccessDenied => "Windows reported network access denied. A firewall or security policy may be blocking this app.",
            SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData =>
                "The server name could not be resolved. Check your internet connection and DNS settings.",
            SocketError.ConnectionRefused => "The server refused the connection. The service may be unavailable or a network device may be rejecting it.",
            SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown =>
                "The network or server is unreachable. Check your internet connection, VPN and network policy.",
            _ => null
        };
        if (chain.Any(error => error is AuthenticationException) || http?.HttpRequestError == HttpRequestError.SecureConnectionError)
            reason = "The secure TLS connection failed. Check Windows date/time, trusted certificates and antivirus/proxy HTTPS inspection. Never disable certificate validation.";
        else if (http?.HttpRequestError == HttpRequestError.NameResolutionError)
            reason = "The server name could not be resolved. Check your internet connection and DNS settings.";
        else if (reason is null && (exception is OperationCanceledException || socket?.SocketErrorCode == SocketError.TimedOut))
            reason = "The connection timed out. This can mean an outage, slow/offline network, VPN, proxy or firewall; it does not identify a firewall block.";
        else if (reason is null && http is not null)
            reason = "The network request failed. The cause may be the connection, service, VPN, proxy or security policy.";
        if (reason is null) return exception.Message;
        return $"{reason}\n\nRequired: outbound HTTPS (TCP 443) to {host}. No inbound rule or port forwarding is needed. " +
            "If blocked, open Windows Security > Firewall & network protection > Advanced settings > Outbound Rules. " +
            "Check for a blocking rule for MabiCommerceNewLife.exe; ask your administrator to allow this app's outbound HTTPS. " +
            "Also check third-party antivirus/firewall rules. Do not turn off your firewall. " +
            $"Retry once connectivity is restored.\n\nDetails: {exception.Message}";
    }
}
