using System.Text.Json.Serialization;
namespace StudyHarness.Contracts;
// Wire contract for host <-> Runner (Plan.md section 5.5). Only registered resource
// identifiers travel over this channel: never a command string, an arbitrary path, an
// environment variable or a raw error message. Numeric native error codes are evidence and
// may travel; exception text, prompts, model output and paths may not.
public enum RunnerRequestKind { StartRun, StopRun }
public enum RunnerReplyKind { Accepted, CompileResult, CaseResult, RunFinished }
public enum RunnerPurpose { Compile, Run }
public sealed record StartRun(string SandboxProfileId, string SourceBundleId, string ToolchainId, string TestSuiteId, RunnerPurpose Purpose);
public sealed record StopRun();
// Absent rather than null: a StopRun frame must not carry a StartRun payload, and a reply
// that has no case id carries no case id. The omission is part of the wire shape, so a reader
// cannot mistake an empty field for a supplied one.
public sealed record RunnerRequest(Guid RunId, RunnerRequestKind Kind, string HandshakeSecret,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StartRun? Start = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StopRun? Stop = null,
    int ProtocolVersion = RunnerProtocol.Version, int SchemaVersion = 1);
public sealed record RunnerReply(Guid RunId, long Sequence, RunnerReplyKind Kind, bool Succeeded,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CaseId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ErrorCode? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NativeError = null,
    int SchemaVersion = 1);
public static class RunnerProtocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 65536;
    public const int HandshakeSecretBytes = 32;
}
// The registered identifier sets. A request naming anything outside these sets is refused
// before it reaches the filesystem or a process launch, which is what keeps the protocol
// free of caller-chosen commands, paths and environment variables.
public static class RunnerResources
{
    public static readonly IReadOnlyList<string> SandboxProfiles = ["study-harness-p1"];
    public static readonly IReadOnlyList<string> Toolchains = ["msvc", "mingw"];
    public static readonly IReadOnlyList<string> SourceBundles = ["hello", "escape", "loop", "output-flood", "output-flood-tree", "resource-limits", "cpu-limit"];
    // smoke: compile and run a normal program. isolation: run the escape fixture against
    // synthetic targets the Runner chooses. stop: run a program that never terminates.
    public static readonly IReadOnlyList<string> TestSuites = ["smoke", "isolation", "stop"];
    public static bool IsRegistered(IReadOnlyList<string> set, string? value) => value is not null && set.Contains(value);
    public static bool IsValidToolchain(string? value) => IsRegistered(Toolchains, value);
    public static bool IsValidSourceBundle(string? value) => IsRegistered(SourceBundles, value);
    public static bool IsValidTestSuite(string? value) => IsRegistered(TestSuites, value);
    public static bool IsValidSandboxProfile(string? value) => IsRegistered(SandboxProfiles, value);
}
