using System.Text.RegularExpressions;
namespace StudyHarness.Domain;
public static partial class SettingsPolicy
{
    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9._:/-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ModelPattern();
    public static bool IsValidModel(string? model) => model is not null && ModelPattern().IsMatch(model);
}
