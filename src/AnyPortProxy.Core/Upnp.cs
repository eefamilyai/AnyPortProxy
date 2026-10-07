using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace AnyPortProxy.Core;

public sealed class UpnpException(string message) : Exception(message);

public sealed record UpnpMapping(int ExternalPort, string Protocol, string InternalClient, int InternalPort, string Description);

/// <summary>Minimal UPnP Internet Gateway Device client: lets us open ports on the user's router automatically.</summary>
public sealed class UpnpGateway
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    private static readonly string[] SearchTargets =
    {
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    };

    public required Uri ControlUrl { get; init; }
    public required string ServiceType { get; init; }
    public required IPAddress LocalAddress { get; init; }
    public string FriendlyName { get; init; } = "your router";

    public static async Task<UpnpGateway?> DiscoverAsync(int timeoutMs = 3000, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var tasks = NetInfo.GatewayInterfaceAddresses().Select(ip => DiscoverOnAsync(ip, cts.Token)).ToList();
        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks);
            tasks.Remove(done);
            var gw = await done;
            if (gw is not null)
            {
                cts.Cancel();
                return gw;
            }
        }
        return null;
    }

    private static async Task<UpnpGateway?> DiscoverOnAsync(IPAddress local, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            var dest = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            foreach (var st in SearchTargets)
            {
                var msg = Encoding.ASCII.GetBytes(
                    $"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {st}\r\n\r\n");
                await udp.SendAsync(msg, dest, ct);
            }

            var seen = new HashSet<string>();
            while (true)
            {
                var res = await udp.ReceiveAsync(ct);
                var location = Header(Encoding.ASCII.GetString(res.Buffer), "LOCATION");
                if (location is null || !seen.Add(location) || !Uri.TryCreate(location, UriKind.Absolute, out var uri)) continue;
                try
                {
                    var gw = await LoadAsync(uri, local, ct);
                    if (gw is not null) return gw;
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // Not a usable gateway description; keep listening.
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
            return null;
        }
    }

    private static string? Header(string response, string name)
    {
        foreach (var line in response.Split("\r\n"))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }
        return null;
    }

    private static async Task<UpnpGateway?> LoadAsync(Uri location, IPAddress local, CancellationToken ct)
    {
        var doc = XDocument.Parse(await Http.GetStringAsync(location, ct));
        var all = doc.Descendants().ToList();
        string? Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim();

        var services = all.Where(e => e.Name.LocalName == "service")
            .Select(e => (Type: Child(e, "serviceType") ?? "", Control: Child(e, "controlURL")))
            .Where(s => s.Control is not null && (s.Type.Contains("WANIPConnection") || s.Type.Contains("WANPPPConnection")))
            .OrderBy(s => s.Type.Contains("WANIPConnection:2") ? 0 : s.Type.Contains("WANIPConnection") ? 1 : 2)
            .ToList();
        if (services.Count == 0) return null;

        var baseText = all.FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value.Trim();
        var baseUri = Uri.TryCreate(baseText, UriKind.Absolute, out var b) ? b : location;
        return new UpnpGateway
        {
            ControlUrl = new Uri(baseUri, services[0].Control),
            ServiceType = services[0].Type,
            LocalAddress = local,
            FriendlyName = all.FirstOrDefault(e => e.Name.LocalName == "friendlyName")?.Value.Trim() ?? "your router",
        };
    }

    private async Task<XElement> SoapAsync(string action, params (string Name, string Value)[] args)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in args) sb.Append($"<{k}>{SecurityElement.Escape(v)}</{k}>");
        var body =
            "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
            $"<u:{action} xmlns:u=\"{ServiceType}\">{sb}</u:{action}></s:Body></s:Envelope>";

        using var req = new HttpRequestMessage(HttpMethod.Post, ControlUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml"),
        };
        req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{ServiceType}#{action}\"");

        using var resp = await Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        XDocument doc;
        try
        {
            doc = XDocument.Parse(text);
        }
        catch
        {
            throw new UpnpException($"The router gave an unexpected answer (HTTP {(int)resp.StatusCode}).");
        }

        if (!resp.IsSuccessStatusCode)
        {
            var code = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value;
            var desc = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value;
            throw new UpnpException(Friendly(code, desc));
        }
        return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == action + "Response")
               ?? throw new UpnpException("The router gave an unexpected answer.");
    }

    private static string Friendly(string? code, string? desc) => code switch
    {
        "606" => "The router refused (its UPnP is set to read-only or needs approval).",
        "714" => "That forward doesn't exist on the router.",
        "718" => "The router already forwards this port to a different device.",
        "724" => "The router requires the same internal and external port.",
        "725" => "The router only accepts permanent forwards.",
        "501" => "The router couldn't do it (action failed).",
        _ => $"The router said no ({code} {desc}).".Replace(" )", ")"),
    };

    public async Task<string?> GetExternalIpAsync()
    {
        var r = await SoapAsync("GetExternalIPAddress");
        return r.Elements().FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value.Trim();
    }

    public Task AddPortMappingAsync(int port, string protocol, string description) =>
        SoapAsync("AddPortMapping",
            ("NewRemoteHost", ""),
            ("NewExternalPort", port.ToString()),
            ("NewProtocol", protocol),
            ("NewInternalPort", port.ToString()),
            ("NewInternalClient", LocalAddress.ToString()),
            ("NewEnabled", "1"),
            ("NewPortMappingDescription", description),
            ("NewLeaseDuration", "0"));

    public Task DeletePortMappingAsync(int port, string protocol) =>
        SoapAsync("DeletePortMapping",
            ("NewRemoteHost", ""),
            ("NewExternalPort", port.ToString()),
            ("NewProtocol", protocol));

    public async Task<UpnpMapping?> GetMappingAsync(int port, string protocol)
    {
        try
        {
            var r = await SoapAsync("GetSpecificPortMappingEntry",
                ("NewRemoteHost", ""), ("NewExternalPort", port.ToString()), ("NewProtocol", protocol));
            string V(string n) => r.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value ?? "";
            return new UpnpMapping(port, protocol, V("NewInternalClient"), int.TryParse(V("NewInternalPort"), out var ip) ? ip : port,
                V("NewPortMappingDescription"));
        }
        catch (UpnpException)
        {
            return null;
        }
    }
}
