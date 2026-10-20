using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

// Publisher identity is independent of page suitability and network safety.
public static class TrustedSources
{
    public const string Version = "registry-20261020.1";
    private static readonly SourceRegistryEntry[] PreferredEntries = [
        new SourceRegistryEntry("oi", "OI Wiki 维护社区", "oi.wiki", ["/"], [], ["algorithms"], ["zh"], ["beginner", "intermediate"], ["tutorial", "reference"],
            "主站镜像列表列明Menci维护、Azure+阿里云CDN；中文社区维护，非大学教材；大陆直连待用户核验", "2026-10-19", "verified", Version, ["/basic/prefix-sum/", "/basic/binary/"]),
        new SourceRegistryEntry("oi-linear", "OI Wiki 维护社区", "oi.wiki", ["/math/linear-algebra/"], [], ["linear-algebra"], ["zh"], ["beginner", "intermediate"], ["reference"],
            "主站列明镜像，Azure+阿里云CDN中文线性代数专题；优先面向国内读者，侧重计算与算法，不冒充完整大学教材；大陆用户侧可达性待人工核验", "2026-10-19", "verified", Version, ["/math/linear-algebra/vector/", "/math/linear-algebra/matrix/"]),
        new SourceRegistryEntry("oi-cpp", "OI Wiki 维护社区", "oi.wiki", ["/lang/"], [], ["cpp"], ["zh"], ["beginner", "intermediate"], ["tutorial", "reference"],
            "主站列明镜像，Azure+阿里云CDN中文C++语法/标准库教学正文；社区维护，非ISO标准；大陆用户侧可达性待人工核验", "2026-10-19", "verified", Version, ["/lang/basic/", "/lang/var/"])

    ];
    public static IReadOnlyList<SourceRegistryEntry> Entries { get; } = Array.AsReadOnly(PreferredEntries.Concat(PreferredEntries.Select(e => e with
    {
        Id = e.Id + "-main", CanonicalHost = "oi-wiki.org",
        AuthorityBasis = "OI Wiki主站关于页及维护仓库已核验；主站声明海外部署，作为补充；大陆可达性待用户核验"
    })).Append(new SourceRegistryEntry("oi-original", "OI Wiki 维护社区", "github.com",
        ["/OI-wiki/OI-wiki/raw/refs/heads/master/docs/basic/prefix-sum.md", "/OI-wiki/OI-wiki/raw/refs/heads/master/docs/math/linear-algebra/matrix.md"],
        [], ["algorithms", "linear-algebra"], ["zh"], ["beginner", "intermediate"], ["reference"],
        "OI Wiki官方维护仓库原文；服务端抓取已验证保留公式，外部代码/图片引用不代表已取得；作为中文镜像正文不足时的补充，大陆直连待核验",
        "2026-10-20", "verified", Version, [])).ToArray());
    public static string? RawAlternative(string url)
    {
        var uri = WebAddress.Parse(url);
        if (uri.Host is not ("oi.wiki" or "oi-wiki.org") || uri.Query.Length > 0) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        return path is "/basic/prefix-sum" or "/math/linear-algebra/matrix"
            ? "https://github.com/OI-wiki/OI-wiki/raw/refs/heads/master/docs" + path + ".md" : null;
    }
    public static IReadOnlyList<string> StartingUrls(SourceRegistryEntry entry, string query)
    {
        var paths = entry.Subjects.Contains("algorithms") ? [] : entry.StartingPages ?? [];
        if (entry.Subjects.Contains("algorithms") && query.Contains("二分", StringComparison.Ordinal)) paths = ["/basic/binary/"];
        else if (entry.Subjects.Contains("algorithms") && query.Contains("前缀和", StringComparison.Ordinal) && !query.Contains("二分", StringComparison.Ordinal)) paths = ["/basic/prefix-sum/"];
        return paths.Select(p => "https://" + entry.CanonicalHost + p).ToArray();
    }
    public static string Subject(string query)
    {
        if (Regex.IsMatch(query, @"线性代数|线代|linear\s+algebra", RegexOptions.IgnoreCase)) return "linear-algebra";
        if (Regex.IsMatch(query, @"算法|竞赛|二分|排序|algorithm|competitive|NOIP|CSP", RegexOptions.IgnoreCase)) return "algorithms";
        return Regex.IsMatch(query, @"c\+\+|cpp", RegexOptions.IgnoreCase) ? "cpp" : "unknown";
    }
    public static SourceRegistryEntry? Recommend(string query) => Entries.FirstOrDefault(e => e.Status == "verified" && e.Subjects.Contains(Subject(query)));
    public static bool Contains(SourceRegistryEntry entry, Uri uri) => entry.Status == "verified" &&
        (uri.Host == entry.CanonicalHost || entry.AllowedSubdomains.Contains(uri.Host)) &&
        entry.AllowedPathPrefixes.Any(p => uri.AbsolutePath == p || p.EndsWith('/') && uri.AbsolutePath.StartsWith(p, StringComparison.Ordinal));
    public static SourceRegistryEntry? Identify(string url) => Entries.OrderByDescending(e => e.AllowedPathPrefixes.Max(p => p.Length)).FirstOrDefault(e => Contains(e, WebAddress.Parse(url)));
    public static string Explain(string url) => Identify(url) is { } e
        ? $"{e.Publisher}；{string.Join('/', e.Languages)}；{string.Join('/', e.ContentRoles)}。依据：{e.AuthorityBasis}（核验{e.ReviewedAt}）。"
        : "补充来源，发布者身份未核验；请核对正文。";
}

public static class TeachingPages
{
    public static PageAssessment Assess(WebPage page, string query)
    {
        var uri = WebAddress.Parse(page.Url);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(page.Content)));
        var text = page.Content;
        var blocked = Regex.IsMatch(text, @"ignore (all |previous |the )*instructions|reveal.{0,20}(key|secret)|忽略.{0,12}(指令|规则)|泄露.{0,12}(密钥|凭据)|access denied|captcha|verify you are human", RegexOptions.IgnoreCase);
        var root = uri.AbsolutePath == "/";
        var catalog = Regex.IsMatch(uri.AbsolutePath, @"/(search|tag|category|about|intro/about|syllabus|lecture-notes)/?$", RegexOptions.IgnoreCase) || uri.Query.Length > 0;
        var intro = Regex.IsMatch(page.Title, @"^(欢迎|关于|课程简介|Course Overview|About\b)|简介$", RegexOptions.IgnoreCase);
        // At least one actual explanatory sentence or a formal/code definition. Length alone never grants eligibility.
        var explanation = Regex.IsMatch(text, @"(定义|定理|证明|例如|例题|表示|称为|复杂度|等于|是指|首先|假设|因此|可以|用于|返回|包含|变量)|\b(is|are|means|define[ds]?|because|example|suppose|matrix|vector|return|stores?|initialize[ds]?)\b|```|\$\$", RegexOptions.IgnoreCase);
        var teachingSection = Regex.Match(text, @"(?m)^(?:\\?#){1,6}\s*(?:正文|定义|定理|证明|例题|算法过程|实现|程序示例|Definition|Theorem|Proof|Example)\b.*$", RegexOptions.IgnoreCase);
        // Course metadata is discovery material even on an authoritative educational platform.
        var courseMetadata = Regex.IsMatch(uri.AbsolutePath, @"/(course|courses)/", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(text, @"课程简介|课程概述|课程介绍|教学大纲|课程大纲|课程周期|开课状态|Course Overview|Session Overview|Session Activities|Syllabus", RegexOptions.IgnoreCase);
        var metadataOnly = courseMetadata && !teachingSection.Success;
        var bodyStart = (intro || courseMetadata) && teachingSection.Success ? teachingSection.Index : 0;
        var lines = Regex.Matches(text, @"(?m)^.*(?:\r?\n|$)").Cast<Match>().Where(m => m.Length > 0).ToArray();
        var spans = new List<SourceSpan>();
        foreach (var line in lines)
        {
            if (line.Index < bodyStart) continue;
            if (Regex.IsMatch(line.Value, @"(?i)^\s*(advertisement|sponsored|广告|buy now|subscribe|cookie|©)|^\s*\[[^\]]+\]\([^)]*\)\s*$")) continue;
            spans.Add(new(uri.AbsoluteUri, null, line.Index, line.Index + line.Length));
        }
        var extracted = string.Concat(spans.Select(s => text[s.Start..s.End]));
        var subject = TrustedSources.Subject(query);
        var relevant = subject switch
        {
            "algorithms" => Regex.IsMatch(extracted, @"算法|二分|排序|竞赛|区间|复杂度|algorithm|binary|sort|search", RegexOptions.IgnoreCase),
            "linear-algebra" => Regex.IsMatch(extracted, @"线性|矩阵|向量|行列式|linear|matrix|vector|determinant", RegexOptions.IgnoreCase),
            "cpp" => Regex.IsMatch(extracted, @"C\+\+|cpp|变量|函数|指针|编译|模板|iostream|variable|function", RegexOptions.IgnoreCase),
            _ => LessonSourceSelector.Score(extracted, LessonSourceSelector.Terms(query)) > 0
        };
        var advertisement = Regex.IsMatch(page.Title, @"广告|advertisement|sponsored|buy now", RegexOptions.IgnoreCase);
        // These two registered mathematical pages have demonstrated loss of inline formula
        // operands in the rendered fetch route. Require math markup, not length or guessed repair.
        var mathMarkupMissing = TrustedSources.RawAlternative(page.Url) is not null &&
            !SourceText.Markers(text).Contains('$');
        var usable = !blocked && !root && !catalog && (!intro || teachingSection.Success) && !metadataOnly && !advertisement && relevant && explanation && !mathMarkupMissing && !string.IsNullOrWhiteSpace(extracted);
        var kind = blocked ? "blocked" : root || catalog ? "catalog" : metadataOnly || intro && !teachingSection.Success ? "intro" : usable ? "body" : "unknown";
        return new(hash, kind, [query, page.Title], usable,
            [usable ? "包含可定位教学解释；发布者身份与正文资格分别判断" : mathMarkupMissing ? "已知数学网页提取路径缺失公式标记；需原始正文，不猜补" : "无可确认教学正文或页面仅用于发现"],
            blocked ? ["untrustedInstructionsOrAccessBlock"] : mathMarkupMissing ? ["mathMarkupUnavailable", "contentNotIndependentlyVerified"] : ["contentNotIndependentlyVerified"], usable ? spans : []);
    }
    public static string Extract(WebPage page, PageAssessment assessment) => string.Concat(assessment.TeachingSpans.Select(s => page.Content[s.Start..s.End]));
}
