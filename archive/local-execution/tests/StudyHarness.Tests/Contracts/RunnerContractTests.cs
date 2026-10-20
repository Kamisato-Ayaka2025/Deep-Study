using System.Text.Json;
using StudyHarness.Contracts;
using Xunit;
namespace StudyHarness.Tests.Contracts;
// The Runner wire contract is a public contract: it carries a schema version, it serialises
// to the same shape the other messages use, and its registered identifier sets are the whole
// vocabulary a caller has. A change to any of those has to break a test here first.
public sealed class RunnerContractTests
{
    [Fact]
    public void RunnerRequest_RoundTripsWithSchemaVersionAndProtocolVersion()
    {
        var request = new RunnerRequest(Guid.NewGuid(), RunnerRequestKind.StartRun, "secret",
            new StartRun("study-harness-p1", "hello", "msvc", "smoke", RunnerPurpose.Run));
        var json = JsonSerializer.Serialize(request, Wire.Options);
        Assert.Contains("\"schemaVersion\":1", json);
        Assert.Contains("\"protocolVersion\":1", json);
        Assert.Equal(request, JsonSerializer.Deserialize<RunnerRequest>(json, Wire.Options));
    }
    [Fact]
    public void StopRun_TravelsWithoutAStartPayload()
    {
        var request = new RunnerRequest(Guid.NewGuid(), RunnerRequestKind.StopRun, "secret", Stop: new StopRun());
        var json = JsonSerializer.Serialize(request, Wire.Options);
        Assert.DoesNotContain("\"start\"", json);
        var round = JsonSerializer.Deserialize<RunnerRequest>(json, Wire.Options)!;
        Assert.Null(round.Start);
        Assert.Equal(RunnerRequestKind.StopRun, round.Kind);
    }
    [Fact]
    public void RunnerReply_CarriesRunIdSequenceAndStableErrorCodes()
    {
        var reply = new RunnerReply(Guid.NewGuid(), 7, RunnerReplyKind.CaseResult, false, "outside-write-denied", ErrorCode.ISOLATION_UNAVAILABLE, 5);
        var json = JsonSerializer.Serialize(reply, Wire.Options);
        // Enum names, not numbers: the schema tests and the wire form both depend on it.
        Assert.Contains("\"ISOLATION_UNAVAILABLE\"", json);
        Assert.Contains("\"CaseResult\"", json);
        Assert.Equal(reply, JsonSerializer.Deserialize<RunnerReply>(json, Wire.Options));
    }
    [Fact]
    public void RegisteredIdentifierSets_AreTheOnlyVocabulary()
    {
        Assert.True(RunnerResources.IsValidSandboxProfile("study-harness-p1"));
        Assert.True(RunnerResources.IsValidToolchain("msvc"));
        Assert.True(RunnerResources.IsValidToolchain("mingw"));
        Assert.True(RunnerResources.IsValidSourceBundle("hello"));
        Assert.False(RunnerResources.IsValidSourceBundle(@"..\..\Windows\System32"));
        Assert.False(RunnerResources.IsValidToolchain("cmd.exe"));
        Assert.False(RunnerResources.IsValidTestSuite(null));
        Assert.Equal(RunnerProtocol.MaxFrameBytes, 65536);
        Assert.Equal(RunnerProtocol.HandshakeSecretBytes, 32);
    }
}
