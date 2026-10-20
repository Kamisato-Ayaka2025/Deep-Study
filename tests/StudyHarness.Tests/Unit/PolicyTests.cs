using StudyHarness.Contracts;
using StudyHarness.Application;
using StudyHarness.Domain;
using StudyHarness.Infrastructure;
using Xunit;
namespace StudyHarness.Tests.Unit;
public sealed class PolicyTests
{
    [Theory]
    [InlineData("deepseek-chat", true)]
    [InlineData("model/version", true)]
    [InlineData("", false)]
    [InlineData(" bad name ", false)]
    [InlineData("model\n", false)]
    public void ModelName_ValidatesBoundary(string input, bool expected) =>
        Assert.Equal(expected, SettingsPolicy.IsValidModel(input));
    [Fact]
    public async Task FakeProvider_DeterministicAndExplicitlyFake()
    {
        var provider = new ModelEgress(new FakeProvider());
        var first = await provider.ProbeAsync();
        Assert.Equal(first, await provider.ProbeAsync());
        Assert.True(first.Value!.IsFake);
        Assert.DoesNotContain("sensitive", first.Value.Text);
        var failure = await new ModelEgress(new FakeProvider(ErrorCode.AUTH_INVALID)).ProbeAsync();
        Assert.Equal(ErrorCode.AUTH_INVALID, failure.Error!.Code);
    }
    [Fact]
    public async Task FakeProvider_CancellationDoesNotReturnSuccess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModelEgress(new FakeProvider()).ProbeAsync(cts.Token));
    }
}
