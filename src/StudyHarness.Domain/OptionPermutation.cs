namespace StudyHarness.Domain;

// No answer, IO or random-generator dependency: the caller supplies a uniform index.
public static class OptionPermutation
{
    public const int AlgorithmVersion = 1;

    public static string[] Create(IReadOnlyList<string> optionIds, Func<int, int> nextIndex)
    {
        ArgumentNullException.ThrowIfNull(nextIndex);
        if (!IsPermutation(optionIds, optionIds))
            throw new ArgumentException("Option identities must be nonempty and distinct.", nameof(optionIds));
        var result = optionIds.ToArray();
        for (var i = result.Length - 1; i > 0; i--)
        {
            var j = nextIndex(i + 1);
            if (j < 0 || j > i) throw new ArgumentOutOfRangeException(nameof(nextIndex));
            (result[i], result[j]) = (result[j], result[i]);
        }
        return result;
    }

    public static bool IsPermutation(IReadOnlyList<string>? expected, IReadOnlyList<string>? actual) =>
        expected is { Count: > 0 } && actual is not null && expected.Count == actual.Count &&
        expected.All(id => !string.IsNullOrWhiteSpace(id)) &&
        expected.Distinct(StringComparer.Ordinal).Count() == expected.Count &&
        actual.Distinct(StringComparer.Ordinal).Count() == actual.Count &&
        expected.ToHashSet(StringComparer.Ordinal).SetEquals(actual);

    public static string Label(int zeroBasedIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(zeroBasedIndex);
        var value = (long)zeroBasedIndex + 1;
        var label = "";
        while (value > 0)
        {
            value--;
            label = (char)('A' + value % 26) + label;
            value /= 26;
        }
        return label;
    }
}
