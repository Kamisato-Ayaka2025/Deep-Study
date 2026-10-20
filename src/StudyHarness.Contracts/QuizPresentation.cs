namespace StudyHarness.Contracts;

// Presentation metadata is stored separately from immutable TheoryLesson content.
public sealed record QuizOptionOrder(int AlgorithmVersion, IReadOnlyList<string> OrderedOptionIds);
public sealed record PresentedLessonOption(string Id, string Text, string DisplayLabel);
public sealed record OptionLabelMapping(string OriginalLabel, string DisplayLabel);
