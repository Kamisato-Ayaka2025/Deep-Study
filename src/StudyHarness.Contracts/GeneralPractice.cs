namespace StudyHarness.Contracts;

public sealed record PracticeTask(string Id, string Action, string Input,
    string ExpectedObservation, string Acceptance);
public sealed record GeneralPractice(string Kind, string Capability, string Title,
    IReadOnlyList<string> Objectives, IReadOnlyList<string> Prerequisites,
    IReadOnlyList<PracticeTask> Tasks, string Assessment, IReadOnlyList<string> Sources,
    int SchemaVersion = 5);
