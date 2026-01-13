using System.Net;
using Microsoft.AspNetCore.Http;

namespace ArcEnCiel.Link.Swarm;

internal static class ArcEnCielLinkCors
{
    private static readonly string[] AllowedDomainSuffixes = [".arcenciel.io"];
    private static readonly HashSet<string> AllowedDomainNames = ["arcenciel.io"];
    private static readonly HashSet<string> LocalHostnames = ["localhost"];
    private static readonly string[] LocalTlds = [".local", ".lan"];

    public static bool TryGetAllowedOrigin(HttpContext context, out string? allowedOrigin)
    {
        allowedOrigin = null;
        string? originHeader = context.Request.Headers.Origin.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(originHeader))
        {
            if (!Uri.TryCreate(originHeader, UriKind.Absolute, out Uri? origin))
            {
                return false;
            }

            if (IsSameOrigin(origin, context.Request))
            {
                allowedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
                return true;
            }

            string? normalized = NormalizeAllowedOrigin(origin);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                allowedOrigin = normalized;
                return true;
            }

            return false;
        }

        IPAddress? clientIp = context.Connection.RemoteIpAddress;
        if (clientIp is not null && IPAddress.IsLoopback(clientIp))
        {
            return true;
        }

        if (clientIp is not null && AllowPrivateOrigins() && IsPrivateIp(clientIp))
        {
            return true;
        }

        return false;
    }

    public static bool TryGetSameOrigin(HttpContext context, out string? allowedOrigin)
    {
        allowedOrigin = null;
        string? originHeader = context.Request.Headers.Origin.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(originHeader))
        {
            if (!Uri.TryCreate(originHeader, UriKind.Absolute, out Uri? origin))
            {
                return false;
            }

            if (!IsSameOrigin(origin, context.Request))
            {
                return false;
            }

            allowedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
            return true;
        }

        string? refererHeader = context.Request.Headers.Referer.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(refererHeader) &&
            Uri.TryCreate(refererHeader, UriKind.Absolute, out Uri? referer) &&
            IsSameOrigin(referer, context.Request))
        {
            allowedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
            return true;
        }

        IPAddress? clientIp = context.Connection.RemoteIpAddress;
        if (clientIp is not null && IPAddress.IsLoopback(clientIp))
        {
            allowedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
            return true;
        }

        return false;
    }

    public static void ApplyCorsHeaders(HttpResponse response, string? allowedOrigin, bool allowCredentials = true)
    {
        response.Headers["Vary"] = "Origin";
        response.Headers["Access-Control-Allow-Private-Network"] = "true";
        response.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "content-type";
        response.Headers["Access-Control-Max-Age"] = "600";

        if (!string.IsNullOrWhiteSpace(allowedOrigin))
        {
            response.Headers["Access-Control-Allow-Origin"] = allowedOrigin;
            if (allowCredentials)
            {
                response.Headers["Access-Control-Allow-Credentials"] = "true";
            }
        }
    }

    private static bool AllowPrivateOrigins()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ARCENCIEL_DEV")))
            {
                return true;
            }
        }
        catch
        {
            // ignored
        }

        return ArcEnCielLinkRuntime.Config.AllowPrivateOrigins;
    }

    private static bool IsSameOrigin(Uri origin, HttpRequest request)
    {
        string requestHost = request.Host.Host;
        if (string.IsNullOrWhiteSpace(requestHost))
        {
            return false;
        }

        if (!string.Equals(origin.Host, requestHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int originPort = origin.Port;
        int requestPort = request.Host.Port ?? (string.Equals(request.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        return originPort == requestPort;
    }

    private static string? NormalizeAllowedOrigin(Uri origin)
    {
        if (!string.Equals(origin.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
            !(AllowPrivateOrigins() && string.Equals(origin.Scheme, "http", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        string host = origin.Host.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        if (AllowPrivateOrigins() && IsPrivateHost(host))
        {
            return $"{origin.Scheme}://{origin.Authority}";
        }

        if (AllowedDomainNames.Contains(host) || AllowedDomainSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
        {
            if (!string.Equals(origin.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return $"{origin.Scheme}://{origin.Authority}";
        }

        return null;
    }

    private static bool IsPrivateHost(string host)
    {
        if (IPAddress.TryParse(host, out IPAddress? ip))
        {
            return IsPrivateIp(ip);
        }

        if (LocalHostnames.Contains(host))
        {
            return true;
        }

        if (LocalTlds.Any(tld => host.EndsWith(tld, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return !host.Contains('.');
    }

    private static bool IsPrivateIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] bytes = ip.GetAddressBytes();
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
            {
                return true;
            }
        }

        return false;
    }
}
