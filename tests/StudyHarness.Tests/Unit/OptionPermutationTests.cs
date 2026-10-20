using StudyHarness.Domain;
using Xunit;

namespace StudyHarness.Tests.Unit;

public sealed class OptionPermutationTests
{
    [Fact]
    public void All24EquallyLikelyDrawSequences_GiveEachPermutationOnce()
    {
        string[] original = ["a", "b", "c", "d"];
        var seen = new HashSet<string>();
        var positions = new int[4];
        for (var four = 0; four < 4; four++)
        for (var three = 0; three < 3; three++)
        for (var two = 0; two < 2; two++)
        {
            var draws = new Queue<int>([four, three, two]);
            var bounds = new List<int>();
            var result = OptionPermutation.Create(original, bound => { bounds.Add(bound); return draws.Dequeue(); });
            Assert.Equal(new[] { 4, 3, 2 }, bounds);
            Assert.True(OptionPermutation.IsPermutation(original, result));
            Assert.True(seen.Add(string.Join(",", result)));
            positions[Array.IndexOf(result, "a")]++;
        }
        Assert.Equal(24, seen.Count);
        Assert.All(positions, count => Assert.Equal(6, count));
        Assert.Equal(new[] { "a", "b", "c", "d" }, original);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void LabelsExtendPastAlphabet(int index, string label) => Assert.Equal(label, OptionPermutation.Label(index));

    [Fact]
    public void InvalidIdsOrRandomBoundsCannotProduceMapping()
    {
        Assert.False(OptionPermutation.IsPermutation(["a", "b"], ["a", "a"]));
        Assert.False(OptionPermutation.IsPermutation(["a", "b"], ["a", "c"]));
        Assert.False(OptionPermutation.IsPermutation(["a", "b"], ["a"]));
        Assert.False(OptionPermutation.IsPermutation(["a", "a"], ["a", "a"]));
        Assert.False(OptionPermutation.IsPermutation(["a"], null));
        Assert.Throws<ArgumentException>(() => OptionPermutation.Create(["a", "a"], _ => 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => OptionPermutation.Create(["a", "b"], upper => upper));
        Assert.Throws<ArgumentOutOfRangeException>(() => OptionPermutation.Create(["a", "b"], _ => -1));
    }
}
