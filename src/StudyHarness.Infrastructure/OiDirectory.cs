using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using StudyHarness.Application;

namespace StudyHarness.Infrastructure;

// Only these two public, credential-free official endpoints. Never follows redirects.
public sealed class OiDirectory(HttpClient client) : IOiDirectory
{
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = async (context, ct) =>
        {
            if (context.DnsEndPoint.Host is not ("oi-wiki.org" or "oi.wiki") || context.DnsEndPoint.Port != 443) throw new HttpRequestException("Unexpected directory endpoint");
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var address = addresses.FirstOrDefault(PublicAddress) ?? throw new HttpRequestException("No public address");
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(address, 443), ct); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        }
    }) { Timeout = TimeSpan.FromSeconds(45) };
    private static bool PublicAddress(IPAddress value)
    {
        if (value.IsIPv4MappedToIPv6) value = value.MapToIPv4();
        if (IPAddress.IsLoopback(value)) return false;
        var b = value.GetAddressBytes();
        return b.Length == 4 ? b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 169 && b[1] == 254) &&
            !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127) :
            b.Length == 16 && (b[0] & 0xe0) == 0x20; // global unicast only
    }
    private async Task<string> Read(string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream(); var bytes = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(bytes, ct)) > 0)
        { if (buffer.Length + count > 2097152) throw new InvalidDataException("Directory exceeds 2MiB"); buffer.Write(bytes, 0, count); }
        return new UTF8Encoding(false, true).GetString(buffer.ToArray());
    }
    public Task<SiteDirectory> ReadAsync(CancellationToken ct) => ReadAsync("oi-wiki.org", ct);
    public async Task<SiteDirectory> ReadAsync(string host, CancellationToken ct)
    {
        if (host is not ("oi-wiki.org" or "oi.wiki")) throw new InvalidOperationException("Unexpected directory endpoint");
        var origin = "https://" + host + "/";
        var sitemap = await Read(origin + "sitemap.xml", ct);
        var navigation = await Read(origin, ct);
        using var xml = XmlReader.Create(new StringReader(sitemap), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2097152 });
        var document = XDocument.Load(xml); var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        string? Address(string value)
        {
            if (!Uri.TryCreate(new Uri(origin), WebUtility.HtmlDecode(value), out var uri) || uri.Scheme != "https" || uri.Host is not ("oi-wiki.org" or "oi.wiki") || uri.Port != 443 || uri.UserInfo.Length > 0 || uri.Query.Length > 0) return null;
            return new UriBuilder(uri) { Host = host, Fragment = "" }.Uri.AbsoluteUri;
        }
        var sitemapUrls = document.Descendants().Where(e => e.Name.LocalName == "loc").Select(e => Address(e.Value)).Where(u => u is not null).Cast<string>().Distinct().ToArray();
        if (document.Root?.Name.LocalName != "urlset" || sitemapUrls.Length == 0) throw new InvalidDataException("Unsupported sitemap");
        foreach (var url in sitemapUrls) entries[url] = new Uri(url).AbsolutePath;
        var navigationUrls = new HashSet<string>();
        foreach (Match match in Regex.Matches(navigation, "<a\\b[^>]*href=(?:[\"']([^\"']+)[\"']|([^\\s>]+))[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
        {
            var url = Address(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value); if (url is null) continue; navigationUrls.Add(url);
            var title = WebUtility.HtmlDecode(Regex.Replace(match.Groups[3].Value, "<[^>]+>", "", RegexOptions.None, TimeSpan.FromSeconds(1))).Trim();
            entries[url] = title.Length > 0 && title.Length <= 500 ? title : new Uri(url).AbsolutePath;
        }
        if (entries.Count > 10000 || navigationUrls.Count == 0) throw new InvalidDataException("Directory incomplete or exceeds entry limit");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((host == "oi-wiki.org" ? "" : origin + "\n") + sitemap + "\n" + navigation)));
        return new(fingerprint, DateTimeOffset.UtcNow, entries.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new WebHit(p.Value, p.Key)).ToArray(), sitemapUrls.Length, navigationUrls.Count);
    }
}
