namespace StudyHarness.Application;

public sealed record CachedMaterial(string Name, string Location, bool KnowledgeReady)
{
    public string Status => KnowledgeReady ? "已整理知识点 · 缓存完成" : "已导入";
}
public sealed record CacheCatalogPage(IReadOnlyList<CachedMaterial> Items, int Page, int Total, string? Warning)
{
    public const int PageSize = 8;
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
}
public interface ICacheCatalog
{
    Task<CacheCatalogPage> ReadAsync(int page, CancellationToken ct = default);
}
