using StudyHarness.Contracts;

namespace StudyHarness.Application;

public sealed record SiteDirectory(string Fingerprint, DateTimeOffset RetrievedAt, IReadOnlyList<WebHit> Entries, int SitemapCount, int NavigationCount);
public interface IOiDirectory
{
    Task<SiteDirectory> ReadAsync(CancellationToken ct);
    async Task<SiteDirectory> ReadAsync(string host, CancellationToken ct)
    {
        if (host != "oi-wiki.org") throw new InvalidOperationException("Directory host not supported.");
        return await ReadAsync(ct);
    }
}
public interface ISiteKnowledge
{
    Task<CollectionSnapshot> SaveSiteAsync(Guid library, SiteDirectory directory, IReadOnlyList<WebPage> pages, CancellationToken ct);
}
