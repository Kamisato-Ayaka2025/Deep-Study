using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using StudyHarness.Contracts;
using Xunit;
namespace StudyHarness.Tests.Contracts;
public sealed class SchemaTests
{
    [Fact]
    public void StrategyDecision_IsVersionedAndRejectsExecutableCapabilities()
    {
        var schema = JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "schemas", "strategy-decision.schema.json"));
        var decision = new StrategyDecision("computer networks", ["theory", "project"], "Observe protocol behavior", ["manual-project"], [], []);
        var json = JsonSerializer.SerializeToNode(decision, Wire.Options)!;
        Assert.True(schema.Evaluate(json).IsValid);
        json["materialCapabilities"] = new JsonArray("run-shell"); Assert.False(schema.Evaluate(json).IsValid);
        json["materialCapabilities"] = new JsonArray("manual-project"); json["schemaVersion"] = 2; Assert.False(schema.Evaluate(json).IsValid);
    }
    [Fact]
    public void LabMaterialSchema_IsVersionedAndRejectsPrivateSolutions()
    {
        var schema = JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "schemas", "lab-material.schema.json"));
        var node = JsonSerializer.SerializeToNode(StudyHarness.Infrastructure.LabFixtureProvider.Material, Wire.Options)!;
        Assert.True(schema.Evaluate(node).IsValid);
        node["referenceFiles"] = new JsonArray(); Assert.False(schema.Evaluate(node).IsValid);
        node.AsObject().Remove("referenceFiles"); node.AsObject().Remove("schemaVersion"); Assert.False(schema.Evaluate(node).IsValid);
    }
    [Fact]
    public void SourceReference_ConformsToSchema_AndRejectsUnversionedReferences()
    {
        var schema = JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "schemas", "source-ref.schema.json"));
        var reference = new SourceRef(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('A', 64), new("sample.md", null, "Heading", "block"));
        var json = JsonSerializer.SerializeToNode(reference, Wire.Options)!;
        Assert.True(schema.Evaluate(json).IsValid);
        Assert.Equal(reference, JsonSerializer.Deserialize<SourceRef>(json, Wire.Options));
        json["contentHash"] = "invalid"; Assert.False(schema.Evaluate(json).IsValid);
    }
    private static JsonSchema Schema => JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "schemas", "settings.schema.json"));
    [Fact]
    public void SettingsDto_ConformsToVersionedSchema_AndRoundTrips()
    {
        var dto = new SettingsDto("deepseek-chat", GenerationMode.Click, ConsentMode.Each, false, 0);
        var json = JsonSerializer.Serialize(dto, Wire.Options);
        Assert.True(Schema.Evaluate(JsonNode.Parse(json)).IsValid);
        Assert.Equal(dto, JsonSerializer.Deserialize<SettingsDto>(json, Wire.Options));
    }
    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("revision", "-1")]
    [InlineData("generationMode", "\"unsupported\"")]
    [InlineData("model", "\"model\\n\"")]
    [InlineData("apiKey", "\"should-not-be-public\"")]
    public void SettingsSchema_RejectsInvalidAndSecretFields(string property, string value)
    {
        var node = JsonSerializer.SerializeToNode(new SettingsDto("deepseek-chat", GenerationMode.Click, ConsentMode.Each, false, 0), Wire.Options)!;
        node[property] = JsonNode.Parse(value);
        Assert.False(Schema.Evaluate(node).IsValid);
    }
    [Fact]
    public void Error_StableWireCodeAndCorrelation_NoPrivateDetails()
    {
        var result = Result<SettingsDto>.Fail(ErrorCode.REVISION_CONFLICT, "请重新载入");
        var json = JsonSerializer.Serialize(result, Wire.Options);
        Assert.Contains("\"REVISION_CONFLICT\"", json);
        Assert.NotEqual(Guid.Empty, result.Error!.CorrelationId);
        Assert.False(result.Ok);
        Assert.Null(result.Value);
    }
}
