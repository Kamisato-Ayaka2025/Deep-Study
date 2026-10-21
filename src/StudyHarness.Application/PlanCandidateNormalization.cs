using StudyHarness.Contracts;

namespace StudyHarness.Application;

// Used only on new model proposals, before validation. Frozen documents are never rewritten.
public static class PlanCandidateNormalization
{
    public static PlanCandidate Apply(PlanCandidate plan, PlanningInput input, IReadOnlyList<PlanSource> sentSources)
    {
        if (plan.SchemaVersion != 2) return plan;
        var allowed = sentSources.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var sources = plan.SourceKeys.Concat(plan.Nodes.Where(n => n.Lab is not null)
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
