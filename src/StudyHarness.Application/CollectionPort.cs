using StudyHarness.Contracts;

namespace StudyHarness.Application;

public interface ICollectionKnowledge
{
    Task<Result<CollectionSnapshot>> ImportCollectionAsync(HostSelection selection, Guid library, CancellationToken ct = default);
    Task<IReadOnlyList<CollectionSnapshot>> CollectionsAsync(Guid library, CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeFragment>> RetrieveAsync(Guid library, string query, IReadOnlyList<Guid> collections, int maxCharacters, CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeFragment>> RetrieveFrozenAsync(PlanCollection collection, string query, IReadOnlyList<string> entryKeys, int maxCharacters, CancellationToken ct = default);
}

// One lesson request shares the same fetch allowance across every collection.
public sealed class SourceAcquisitionBudget
{
    private int fetches;
    private readonly DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(3);
    public void ReserveFetch()
    {
        if (DateTimeOffset.UtcNow >= deadline || Interlocked.Increment(ref fetches) > 3)
            throw new IOException("本课资料获取已达到3次抓取/3分钟额度；请缩小课程范围。未自动续爬。");
    }
    public TimeSpan Remaining => deadline - DateTimeOffset.UtcNow;
}
public interface IBoundedCollectionKnowledge : ICollectionKnowledge
{
    Task<IReadOnlyList<KnowledgeFragment>> RetrieveFrozenAsync(PlanCollection collection, string query,
        IReadOnlyList<string> entryKeys, int maxCharacters, SourceAcquisitionBudget budget, CancellationToken ct);
}
