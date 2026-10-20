using System.Collections.ObjectModel;

namespace StudyHarness.Domain;

public enum CourseKind { Theory, Lab }

public sealed record CourseDefinition(Guid Id, int Order, CourseKind Kind, string Title,
    IReadOnlyList<string> Objectives, int EstimatedMinutes, IReadOnlyList<Guid> PrerequisiteIds);

public sealed record CourseAvailability(bool PrerequisitesMet, bool CanGenerate, bool CanRead,
    bool CanAnswer, bool CanStartLab);

// These facts must come from authoritative storage, never from model output or UI flags.
// Materials-only policy: published lab content does not imply execution or verified results.
public sealed record CourseFacts(bool PlanConfirmed, bool SubjectActive, bool HasPublishedLesson,
    bool HasActiveGenerationTask, bool ReferenceValidated);

/// <summary>An immutable, validated graph belonging to one subject.</summary>
public sealed class CourseGraph
{
    private readonly IReadOnlyDictionary<Guid, CourseDefinition> byId;
    public IReadOnlyList<CourseDefinition> Nodes { get; }
    public IReadOnlyDictionary<Guid, int> Layers { get; }

    private CourseGraph(CourseDefinition[] nodes, Dictionary<Guid, int> layers)
    {
        Nodes = Array.AsReadOnly(nodes);
        byId = new ReadOnlyDictionary<Guid, CourseDefinition>(nodes.ToDictionary(n => n.Id));
        Layers = new ReadOnlyDictionary<Guid, int>(layers);
    }

    public static CourseGraph Create(IEnumerable<CourseDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var nodes = definitions.Select(n =>
        {
            if (n is null || n.Id == Guid.Empty || n.Order < 0 || !Enum.IsDefined(n.Kind) ||
                string.IsNullOrWhiteSpace(n.Title) || n.EstimatedMinutes <= 0 ||
                n.Objectives is null || n.Objectives.Count == 0 || n.Objectives.Any(string.IsNullOrWhiteSpace) ||
                n.PrerequisiteIds is null)
                throw new ArgumentException("Invalid course identity, type, title, objectives or duration.", nameof(definitions));
            return n with
            {
                Objectives = Array.AsReadOnly(n.Objectives.ToArray()),
                PrerequisiteIds = Array.AsReadOnly(n.PrerequisiteIds.ToArray())
            };
        }).OrderBy(n => n.Order).ToArray();
        if (nodes.Length == 0 || nodes.Select(n => n.Id).Distinct().Count() != nodes.Length ||
            nodes.Select(n => n.Order).Distinct().Count() != nodes.Length)
            throw new ArgumentException("The graph must be nonempty with unique IDs and display orders.", nameof(definitions));

        var byId = nodes.ToDictionary(n => n.Id);
        var children = nodes.ToDictionary(n => n.Id, _ => new List<Guid>());
        var remaining = new Dictionary<Guid, int>();
        var layers = nodes.ToDictionary(n => n.Id, _ => 0);
        foreach (var node in nodes)
        {
            if (node.PrerequisiteIds.Distinct().Count() != node.PrerequisiteIds.Count ||
                node.PrerequisiteIds.Any(id => id == node.Id || !byId.ContainsKey(id)))
                throw new ArgumentException("Prerequisites must be distinct, existing nodes other than self.", nameof(definitions));
            remaining[node.Id] = node.PrerequisiteIds.Count;
            foreach (var parent in node.PrerequisiteIds) children[parent].Add(node.Id);
        }

        // Kahn traversal avoids recursion depth limits for model-provided graphs.
        var ready = new Queue<Guid>(nodes.Where(n => remaining[n.Id] == 0).Select(n => n.Id));
        var visited = 0;
        while (ready.TryDequeue(out var parent))
        {
            visited++;
            foreach (var child in children[parent])
            {
                layers[child] = Math.Max(layers[child], layers[parent] + 1);
                if (--remaining[child] == 0) ready.Enqueue(child);
            }
        }
        if (visited != nodes.Length)
            throw new ArgumentException("The course graph contains a cycle.", nameof(definitions));
        return new CourseGraph(nodes, layers);
    }

    public CourseAvailability GetAvailability(Guid nodeId, IReadOnlySet<Guid> completed, CourseFacts facts)
    {
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(facts);
        if (!byId.TryGetValue(nodeId, out var node))
            throw new ArgumentException("Node does not belong to this subject.", nameof(nodeId));
        var prerequisitesMet = node.PrerequisiteIds.All(completed.Contains);
        var canAct = facts.SubjectActive; // Prerequisites describe the suggested order, not permission.
        return new(prerequisitesMet,
            facts.PlanConfirmed && canAct && !facts.HasPublishedLesson && !facts.HasActiveGenerationTask,
            facts.HasPublishedLesson,
            node.Kind == CourseKind.Theory && facts.HasPublishedLesson && canAct,
            node.Kind == CourseKind.Lab && facts.PlanConfirmed && facts.HasPublishedLesson && canAct);
    }
}
