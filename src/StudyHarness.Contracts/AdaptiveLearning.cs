namespace StudyHarness.Contracts;

// P8 contracts are additive. V1 plan/material serialization and hashes are unchanged.
public sealed record ModelCapabilities(string Model, int ContextWindow, int MaxOutputTokens,
    bool SupportsImages, DateTimeOffset ObservedAt, bool Verified, int SchemaVersion = 1);
public sealed record ContextBudget(int InputUpperBound, int OutputReserve, int SafetyMargin,
    int ResponseBytes, int MaxDispatches, int MaxTransientRetries, int TargetSeconds,
    int DeadlineSeconds, int SchemaVersion = 1);
public sealed record StrategyDecision(string Domain, IReadOnlyList<string> TeachingModes,
    string Reason, IReadOnlyList<string> MaterialCapabilities, IReadOnlyList<string> Limitations,
    IReadOnlyList<string> Clarifications, int SchemaVersion = 1);
public sealed record CollectionEntry(string Key, string Locator, string Heading, string State,
    string? ContentHash, string? Failure, IReadOnlyList<string> QualityFlags);
public sealed record CoverageManifest(Guid CollectionId, string Version, string DirectoryHash,
    bool DirectoryComplete, IReadOnlyList<CollectionEntry> Entries, int SchemaVersion = 1);
