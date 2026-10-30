using StudyHarness.Contracts;

namespace StudyHarness.Application;

public sealed record PlanningSession(Guid? DraftId, string Request, string Background, string Goal,
    string Hours, string WebTopic, string WebSite, string Clarification, IReadOnlyList<SourceRef> References,
    IReadOnlyList<SourceRef> WebReferences, string WebReport, string OcrPages = "", Guid? OcrCollectionId = null,
    IReadOnlyList<Guid>? PendingCollections = null, IReadOnlyList<Guid>? ExamCollectionIds = null);

public interface IPlanningSessionStore
{
    PlanningSession? Load();
    void Save(PlanningSession session);
}
