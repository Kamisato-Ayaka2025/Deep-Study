using StudyHarness.Contracts;

namespace StudyHarness.Application;

// Used only on new model proposals, before validation. Frozen documents are never rewritten.
public static class PlanCandidateNormalization
{
    public static PlanCandidate Apply(PlanCandidate plan, PlanningInput input, IReadOnlyList<PlanSource> sentSources)
    {
        if (plan.SchemaVersion != 2) return plan;
        var originalNodes = plan.Nodes;
        var hasExam = input.ExamCollectionIds is { Count: > 0 };
        var nodes = plan.Nodes.Select(n => n.Type == "quiz" ? n with { Type = hasExam ? "quiz" : "theory", Lab = null } :
            n.Type == "lab" && n.Lab?.Toolchain == "paper-exercise" ? n with { Type = "theory", Lab = null } : n).ToArray();
        var convertedPaperKeys = originalNodes.Where(n => n.Type == "lab" && n.Lab?.Toolchain == "paper-exercise").Select(n => n.Key).ToHashSet();
        nodes = nodes.Select(n => n.Lab?.PreviousLabKey is { } previous && convertedPaperKeys.Contains(previous)
            ? n with { Lab = n.Lab with { Mode = "independent", PreviousLabKey = null } } : n).ToArray();
        plan = plan with { Nodes = nodes, Strategy = plan.Strategy is null ? null : plan.Strategy with
        { TeachingModes = nodes.Select(n => n.Type == "lab" ? n.Lab!.Kind! : n.Type).Distinct().ToArray(),
          MaterialCapabilities = nodes.Where(n => n.Type == "lab").Select(n => n.Lab!.Toolchain).Distinct().ToArray() } };
        var allowed = sentSources.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var sources = plan.SourceKeys.Concat(originalNodes.Where(n => n.Lab is not null)
            .SelectMany(n => n.Lab!.SourceKeys).Where(allowed.Contains)).Distinct(StringComparer.Ordinal).ToArray();
        var defaults = plan.Defaults.Select(d => d.Field switch
        {
            "background" when string.IsNullOrWhiteSpace(input.Background) => d with { Value = plan.Background },
            "goal" when string.IsNullOrWhiteSpace(input.Goal) => d with { Value = plan.Goal },
            "weeklyMinutes" when input.WeeklyMinutes is null => d with { Value = plan.WeeklyMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            _ => d
        }).ToArray();
        return plan with { SourceKeys = sources, Defaults = defaults };
    }
}
