using StudyHarness.Contracts;

namespace StudyHarness.Application;

public sealed record WebHit(string Title, string Url);
public sealed record WebPage(string Url, string Title, string Content, DateTimeOffset RetrievedAt, PageAssessment? Assessment = null, string? OriginalContent = null);
public sealed record WebAcquisition(IReadOnlyList<SourceRef> References, string Report);
public interface IWebSources
{
    Task<IReadOnlyList<WebHit>> SearchAsync(string query, CancellationToken ct);
    Task<WebPage> FetchAsync(string url, CancellationToken ct);
}
public interface IWebKnowledge
{
    Task<IReadOnlyList<SourceRef>> SaveWebAsync(Guid libraryId, IReadOnlyList<WebPage> pages, string query, CancellationToken ct);
}

// Only explicit host UI authorization enters this workflow. No model tools can
// invoke it, and neither local material nor learning history enters a search.
public sealed class WebLearningService(IWebSources web, IKnowledgeStore knowledge, IWebKnowledge storage, string defaultLibrary, IOiDirectory? directory = null)
{
    public async Task<WebAcquisition> AcquireAsync(string query, string site, bool allow, IProgress<string>? progress, CancellationToken ct)
    {
        if (!allow) throw new InvalidOperationException("请先授权联网获取。");
        query = query.Trim(); site = site.Trim();
        if (query.Length is < 1 or > 100 || query.Any(char.IsControl)) throw new InvalidOperationException("搜索主题请输入1–100个字符。");
        Uri? target = null;
        if (site.Length > 0)
        {
            target = WebAddress.Parse(site.Contains("://", StringComparison.Ordinal) ? site : "https://" + site);
            if (target.Query.Length != 0 || target.Fragment.Length != 0) throw new InvalidOperationException("指定来源请使用不带查询参数的站点或正文URL。");
        }
        var recommendation = target is null ? TrustedSources.Recommend(query) : null;
        var preferred = target ?? (recommendation is null ? null : new Uri("https://" + recommendation.CanonicalHost + recommendation.AllowedPathPrefixes[0]));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token; token.ThrowIfCancellationRequested();
        SiteDirectory? index = null;
        if (preferred?.Host is "oi-wiki.org" or "oi.wiki" && preferred.AbsolutePath == "/" && directory is not null && storage is ISiteKnowledge)
        { progress?.Report("正在整理站点目录…"); index = await directory.ReadAsync(preferred.Host, token); }
        var pages = new List<WebPage>(); var failures = 0; var searches = 0; var fetches = 0;
        var errors = new List<string>(); var attempted = new HashSet<string>(StringComparer.Ordinal);
        async Task Acquire(Uri? scope, bool fallback)
        {
            if (searches >= 2 || fetches >= 3) return;
            var search = query + (scope is null ? "" : " site:" + scope.Host);
            if (search.Length > 128) throw new InvalidOperationException("主题与站点合计过长，请缩短搜索主题。");
            progress?.Report("正在查找学习资料…"); searches++;
            var hits = await web.SearchAsync(search, token);
            var urls = new List<string>();
            if (recommendation is not null)
            {
                if (fallback)
                    urls.AddRange(TrustedSources.StartingUrls(recommendation, query).Select(TrustedSources.RawAlternative).OfType<string>());
                var preferredEntry = fallback ? TrustedSources.Entries.FirstOrDefault(e => e.Id == recommendation.Id + "-main") : recommendation;
                if (preferredEntry is not null) urls.AddRange(TrustedSources.StartingUrls(preferredEntry, query));
            }
            if (target is not null && target.AbsolutePath != "/") urls.Add(target.AbsoluteUri);
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var hit in hits.OrderByDescending(h => terms.Count(t => h.Title.Contains(t, StringComparison.OrdinalIgnoreCase))).ThenBy(h => h.Url, StringComparer.Ordinal))
            {
                try
                {
                    var candidate = hit.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + hit.Url[7..] : hit.Url;
                    var uri = WebAddress.Parse(candidate);
                    if (new[] { ".pdf", ".zip", ".exe", ".doc", ".docx", ".ppt", ".pptx", ".png", ".jpg" }.Any(e => uri.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
                    if (scope is not null && (uri.Host != scope.Host || !(uri.AbsolutePath == scope.AbsolutePath || uri.AbsolutePath.StartsWith(scope.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal)))) continue;
                    if (!fallback && recommendation is not null && !TrustedSources.Contains(recommendation, uri)) continue;
                    urls.Add(uri.AbsoluteUri);
                }
                catch (InvalidOperationException) { }
            }
            if (index is not null && scope?.Host is "oi-wiki.org" or "oi.wiki")
            {
                var paths = urls.Select(u => new Uri(u).AbsolutePath.TrimEnd('/')).ToHashSet(StringComparer.Ordinal);
                urls = index.Entries.Where(e => paths.Contains(new Uri(e.Url).AbsolutePath.TrimEnd('/')) || terms.Any(t => e.Title.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(e => terms.Count(t => e.Title.Contains(t, StringComparison.OrdinalIgnoreCase))).ThenBy(e => e.Url, StringComparer.Ordinal).Select(e => e.Url).ToList();
            }
            var cap = recommendation is not null && !fallback ? 2 : 3;
            foreach (var url in urls.Distinct(StringComparer.Ordinal))
            {
                if (fetches >= cap) break;
                if (!attempted.Add(url)) continue;
                token.ThrowIfCancellationRequested(); progress?.Report("正在整理学习资料…"); fetches++;
                try
                {
                    var page = await web.FetchAsync(url, token);
                    if (page.Url != url) throw new InvalidOperationException("抓取回执来源不匹配。");
                    var assessment = TeachingPages.Assess(page, query);
                    if (!assessment.UsableForTeaching) { failures++; errors.Add(assessment.PageKind); continue; }
                    pages.Add(page with { Content = TeachingPages.Extract(page, assessment), OriginalContent = page.Content, Assessment = assessment });
                }
                catch (InvalidOperationException) { failures++; errors.Add("正文取得失败"); }
            }
        }
        await Acquire(preferred, false);
        var supplemented = false;
        if (pages.Count == 0 && target is null && recommendation is not null)
        { supplemented = true; index = null; await Acquire(null, true); }
        if (pages.Count == 0) throw new InvalidOperationException("未取得可用教学正文；简介、目录、广告与搜索摘要不作教材。可修改主题/站点或导入资料。" +
            $"已用{searches}次搜索、{fetches}次抓取。" + string.Join("；", errors.Distinct()));
        token.ThrowIfCancellationRequested();
        var library = await knowledge.CurrentAsync(token);
        if (library is null)
        {
            var switched = await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(defaultLibrary)), token);
            if (!switched.Ok) throw new InvalidOperationException(switched.Error!.Message);
            library = switched.Value!;
        }
        if (index is not null && pages.All(p => new Uri(p.Url).Host == preferred!.Host) && storage is ISiteKnowledge collections)
        {
            progress?.Report("正在整理站点目录…");
            var snapshot = await collections.SaveSiteAsync(library.Id, index!, pages, token);
            if (snapshot.References.Count == 0) throw new InvalidOperationException("已取得目录，但正文不在目录范围内；请调整主题后重试。");
            return new(snapshot.References.Take(24).ToArray(), $"已冻结OI Wiki目录{index!.Entries.Count}项（sitemap {index.SitemapCount}、导航{index.NavigationCount}），已取正文{snapshot.Collection.ReadyCount}项；其余按课取得，不表示已经阅读全文。\n正文沿用已授权抓取服务；旧目录下首次取得的正文保持不变。\n" + string.Join("\n", pages.Select(p => TrustedSources.Explain(p.Url))) + "");
        }
        var refs = await storage.SaveWebAsync(library.Id, pages, query, token);
        return new(refs, $"已保存{pages.Count}页正文，{failures}页不可用；本批{searches}次搜索+{fetches}次抓取（上限2+3），无自动重试。\n" +
            "来源可能变化或互相冲突，请在确认计划前核对；旧课程保持原快照。抓取服务未提供最终重定向URL。\n" +
            (supplemented ? "推荐正文不足；使用授权范围内补充来源。\n" : "") + string.Join("\n", pages.Select(p => $"{p.Title} · {p.Url} · {p.RetrievedAt:O}\n{TrustedSources.Explain(p.Url)}\n正文资格：{p.Assessment!.PageKind}；原快照与提取跨度已保存。")));
    }
}

public static class WebAddress
{
    public static Uri Parse(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.Port != 443 || uri.HostNameType != UriHostNameType.Dns ||
            !uri.Host.Contains('.') || uri.Host.EndsWith('.') ||
            new[] { "localhost", ".localhost", ".local", ".internal", ".home", ".lan", ".test", ".invalid" }.Any(s => uri.Host.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("仅支持公开HTTPS网页；不允许本机、IP地址、内网站点、凭据URL或自定义端口。");
        return new UriBuilder(uri) { Fragment = "" }.Uri;
    }
}
