using StudyHarness.Contracts;

namespace StudyHarness.Application;

public static class StrategyRules
{
    private static bool Text(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Contains('\0');
    public static bool Valid(StrategyDecision? strategy)
    {
        if (strategy is null || strategy.SchemaVersion != 1 || !Text(strategy.Domain, 200) || !Text(strategy.Reason, 2000) ||
            strategy.TeachingModes is null || strategy.TeachingModes.Count is < 1 or > 3 ||
            strategy.TeachingModes.Distinct().Count() != strategy.TeachingModes.Count ||
            strategy.TeachingModes.Any(m => m is not ("theory" or "exercise" or "project")) ||
            strategy.MaterialCapabilities is null || strategy.MaterialCapabilities.Count > 4 ||
            strategy.MaterialCapabilities.Distinct().Count() != strategy.MaterialCapabilities.Count ||
            strategy.MaterialCapabilities.Any(c => c is not ("paper-exercise" or "manual-project" or "portable-c" or "portable-cpp")) ||
            strategy.Limitations is null || strategy.Limitations.Count > 20 || strategy.Limitations.Any(s => !Text(s, 2000)) ||
            strategy.Clarifications is null || strategy.Clarifications.Count > 10 || strategy.Clarifications.Any(s => !Text(s, 2000))) return false;
        var exercise = strategy.TeachingModes.Contains("exercise");
        var project = strategy.TeachingModes.Contains("project");
        if (!exercise && !project) return strategy.MaterialCapabilities.Count == 0;
        if (strategy.MaterialCapabilities.Count == 0) return false;
        if (strategy.MaterialCapabilities.Contains("paper-exercise") && !exercise ||
            strategy.MaterialCapabilities.Contains("manual-project") && !project) return false;
        if (exercise && !strategy.MaterialCapabilities.Any(c => c is "paper-exercise" or "portable-c" or "portable-cpp")) return false;
        if (project && !strategy.MaterialCapabilities.Any(c => c is "manual-project" or "portable-c")) return false;
        // C++ competitive-programming files are exercise-only in the current contract.
        return !strategy.MaterialCapabilities.Contains("portable-cpp") || exercise;
    }
}
