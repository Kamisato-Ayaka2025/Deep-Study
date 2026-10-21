using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Domain;
using Xunit;

namespace StudyHarness.Tests.Unit;

public sealed class AdaptiveBudgetTests
{
    [Fact]
    public void LessonFallback_DoublesContextWithoutIncreasingPlanningOrVerifiedLimits()
    {
        var now = DateTimeOffset.UtcNow;
        var unknown = AdaptiveBudgetPolicy.Conservative("probe", now);
        var input = new string('x', 53248);
        var budget = AdaptiveBudgetPolicy.Create(unknown, "probe", input, false, now, true);
        Assert.Equal(4096, budget.OutputReserve); Assert.Equal(8192, budget.SafetyMargin);
        Assert.Throws<ContextBudgetExceededException>(() => AdaptiveBudgetPolicy.Create(unknown, "probe", input + "x", false, now, true));
        Assert.Throws<ContextBudgetExceededException>(() => AdaptiveBudgetPolicy.Create(unknown, "probe", input, true, now));
        Assert.Throws<ContextBudgetExceededException>(() => AdaptiveBudgetPolicy.Create(unknown with { Verified = true }, "probe", input, false, now, true));
        Assert.Contains("能力未取得", new ContextBudgetExceededException(60000, 4096, 8192, 65536, false).UserMessage("probe"));
    }
    [Theory]
    [InlineData("0.5", 30)] [InlineData("2", 120)] [InlineData("8", 480)]
    [InlineData("20", 1200)] [InlineData("168", 10080)] [InlineData("0.05", 3)]
    public void Hours_ConvertExactly(string value, int expected)
    {
        Assert.True(WeeklyStudyTime.TryMinutes(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), out var minutes));
        Assert.Equal(expected, minutes);
    }

    [Theory]
    [InlineData("0")] [InlineData("-1")] [InlineData("168.1")] [InlineData("0.01")]
    [InlineData("0.0166666666666666666666666667")] [InlineData("79228162514264337593543950335")]
    public void Hours_RejectInvalidOrLossyInput(string value) => Assert.False(WeeklyStudyTime.TryMinutes(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), out _));

    [Fact]
    public void LegacyMinutes_KeepExactRemainder()
    {
        Assert.Equal("0 小时 1 分钟（原值 1 分钟）", WeeklyStudyTime.Format(1));
        Assert.Equal("1 小时 1 分钟（原值 61 分钟）", WeeklyStudyTime.Format(61));
        Assert.Equal("0.05 小时", WeeklyStudyTime.Format(3));
    }

    [Fact]
    public void Budget_CountsUnicodeAndRejectsOverflowBeforeDispatch()
    {
        var now = DateTimeOffset.UtcNow;
        var caps = new ModelCapabilities("probe", 1048576, 393216, true, now, true);
        var budget = AdaptiveBudgetPolicy.Create(caps, "probe", "中", false, now);
        Assert.Equal(3, budget.InputUpperBound); Assert.Equal(16384, budget.OutputReserve);
        Assert.Equal(3, budget.MaxDispatches); Assert.Equal(2, budget.MaxTransientRetries);
        Assert.Throws<ContextBudgetExceededException>(() => AdaptiveBudgetPolicy.Create(caps, "probe", new string('中', 131073), false, now));
        Assert.Throws<ContextBudgetExceededException>(() => AdaptiveBudgetPolicy.Create(caps with { ContextWindow = 20000, MaxOutputTokens = 16384 }, "probe", "small", false, now));
    }

    [Fact]
    public void ReasoningLesson_ReservesSharedOutputWithinCapabilitiesAndContext()
    {
        var now = DateTimeOffset.UtcNow;
        var caps = new ModelCapabilities("probe", 1048576, 393216, true, now, true);
        Assert.Equal(32768, AdaptiveBudgetPolicy.Create(caps, "probe", "json", false, now, true).OutputReserve);
        Assert.Equal(32768, AdaptiveBudgetPolicy.Create(caps, "probe", "json", true, now).OutputReserve);
        Assert.Equal(4096, AdaptiveBudgetPolicy.Create(caps with { Verified = false }, "probe", "json", true, now).OutputReserve);
        Assert.Equal(10000, AdaptiveBudgetPolicy.Create(caps with { MaxOutputTokens = 10000 }, "probe", "json", true, now).OutputReserve);
        Assert.Equal(10000, AdaptiveBudgetPolicy.Create(caps with { MaxOutputTokens = 10000 }, "probe", "json", false, now, true).OutputReserve);
        Assert.Equal(4096, AdaptiveBudgetPolicy.Create(caps with { Verified = false }, "probe", "json", false, now, true).OutputReserve);
        Assert.Throws<ContextBudgetExceededException>(() => AdaptiveBudgetPolicy.Create(caps with { ContextWindow = 40000, MaxOutputTokens = 32768 }, "probe", "json", false, now, true));
    }

    [Fact]
    public void StaleUnknownOrDifferentModel_NeverInheritsMillionTokenWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var caps = new ModelCapabilities("probe", 1048576, 393216, true, now, true);
        foreach (var invalid in new[] { caps with { Model = "other" }, caps with { ObservedAt = now.AddDays(-2) }, caps with { Verified = false }, caps with { ObservedAt = now.AddMinutes(1) } })
        {
            Assert.False(AdaptiveBudgetPolicy.IsFresh(invalid, "probe", now));
            Assert.Equal(4096, AdaptiveBudgetPolicy.Create(invalid, "probe", "short", true, now).OutputReserve);
            Assert.Throws<ContextBudgetExceededException>(() => AdaptiveBudgetPolicy.Create(invalid, "probe", new string('x', 30000), true, now));
        }
    }
}
