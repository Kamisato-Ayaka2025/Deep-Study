using System.Text.RegularExpressions;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

// Reviewed directory-only text. Never infer that every page ending in "简介" is optional.
public static class SiteNavigation
{
    private const string ContestOverview = "本章主要介绍计算机编程比赛直接相关的知识，包括各种赛事、赛制、题型，以及赛场上常见的坑点与技巧．学习路线，与常用的学习资源也可以在本章找到．本章亦设出题板块，介绍出竞赛题的相关知识．";

    public static bool IsReviewedOverview(WebPage page)
    {
        var uri = WebAddress.Parse(page.Url);
        if (uri.Host is not ("oi.wiki" or "oi-wiki.org") || uri.AbsolutePath.TrimEnd('/') != "/contest" ||
            page.Title != "比赛相关简介" || TeachingPages.Assess(page, page.Title).PageKind != "intro")
            return false;
        var text = Regex.Replace(page.Content, @"\\([\\`*_{}\[\]()#+.!<>:;,/&=~-])", "$1");
        var footer = text.IndexOf("本页面最近更新：", StringComparison.Ordinal);
        if (footer >= 0) text = text[..footer];
        text = Regex.Replace(text, @"(?m)^\s*#{1,6}\s+比赛相关简介\s*$", "");
        return Regex.Replace(text, @"\s+", "") == ContestOverview;
    }

    public static bool IsChild(CollectionEntry parent, CollectionEntry child)
    {
        var first = WebAddress.Parse(parent.Locator); var second = WebAddress.Parse(child.Locator);
        return first.Host == second.Host && second.AbsolutePath.StartsWith(first.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal) &&
            second.AbsolutePath.TrimEnd('/') != first.AbsolutePath.TrimEnd('/');
    }
}

public sealed class SourcePreparationException(string message) : IOException(message);
