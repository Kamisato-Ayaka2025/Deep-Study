using StudyHarness.Domain;
using Xunit;

namespace StudyHarness.Tests.Unit;

public sealed class CourseGraphTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-000000000003");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-000000000004");
    private static readonly Guid E = Guid.Parse("00000000-0000-0000-0000-000000000005");
    private static CourseDefinition Node(Guid id, int order, params Guid[] parents) =>
        new(id, order, CourseKind.Theory, $"Course {order}", ["Explain the concept"], 30, parents);
    private static CourseGraph Branches() => CourseGraph.Create([
        Node(E, 4, C, B), Node(D, 3, B), Node(C, 2, A), Node(B, 1), Node(A, 0)]);
    private static readonly CourseFacts Empty = new(true, true, false, false, false);

    [Fact]
    public void T01_AllBranchesAvailable_AndLayersRemainSuggestedOrder()
    {
        var graph = Branches();
        Assert.Equal(new[] { 0, 0, 1, 1, 2 }, graph.Nodes.Select(n => graph.Layers[n.Id]));
        Assert.True(graph.GetAvailability(A, new HashSet<Guid>(), Empty).CanGenerate);
        Assert.True(graph.GetAvailability(C, new HashSet<Guid> { A }, Empty).CanGenerate);
        Assert.True(graph.GetAvailability(D, new HashSet<Guid> { A }, Empty).CanGenerate);
        Assert.True(graph.GetAvailability(E, new HashSet<Guid> { A, C }, Empty).CanGenerate);
        Assert.True(graph.GetAvailability(E, new HashSet<Guid> { B, C }, Empty).CanGenerate);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-order")]
    [InlineData("self")]
    [InlineData("missing")]
    [InlineData("duplicate-edge")]
    [InlineData("cycle")]
    [InlineData("disconnected-cycle")]
    [InlineData("kind")]
    [InlineData("zero-time")]
    [InlineData("negative-time")]
    [InlineData("empty-id")]
    [InlineData("title")]
    [InlineData("objectives")]
    [InlineData("null-node")]
    [InlineData("null-parents")]
    public void InvalidGraph_IsRejected(string defect)
    {
        CourseDefinition[] nodes = defect switch
        {
            "empty" => [],
            "duplicate-id" => [Node(A, 0), Node(A, 1)],
            "duplicate-order" => [Node(A, 0), Node(B, 0)],
            "self" => [Node(A, 0, A)],
            "missing" => [Node(A, 0, B)],
            "duplicate-edge" => [Node(A, 0), Node(B, 1, A, A)],
            "cycle" => [Node(A, 0, B), Node(B, 1, A)],
            "disconnected-cycle" => [Node(A, 0), Node(B, 1, C), Node(C, 2, B)],
            "kind" => [Node(A, 0) with { Kind = (CourseKind)42 }],
            "zero-time" => [Node(A, 0) with { EstimatedMinutes = 0 }],
            "negative-time" => [Node(A, 0) with { EstimatedMinutes = -1 }],
            "empty-id" => [Node(Guid.Empty, 0)],
            "title" => [Node(A, 0) with { Title = " " }],
            "objectives" => [Node(A, 0) with { Objectives = [] }],
            "null-node" => [null!],
            _ => [Node(A, 0) with { PrerequisiteIds = null! }]
        };
        Assert.Throws<ArgumentException>(() => CourseGraph.Create(nodes));
    }

    [Fact]
    public void ValidatedGraph_CannotBeChangedViaCallerOwnedCollections()
    {
        var parents = new[] { A };
        var objectives = new[] { "Original objective" };
        var graph = CourseGraph.Create([Node(A, 0), Node(B, 1, parents) with { Objectives = objectives }]);
        parents[0] = B;
        objectives[0] = "Changed";
        Assert.Equal(A, graph.Nodes[1].PrerequisiteIds[0]);
        Assert.Equal("Original objective", graph.Nodes[1].Objectives[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<Guid>)graph.Nodes[1].PrerequisiteIds)[0] = B);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<Guid, int>)graph.Layers)[B] = 99);
    }

    [Fact]
    public void T02_GenerationRequiresConfirmation_AndNeverReplacesPublishedLesson()
    {
        var graph = Branches();
        var completed = new HashSet<Guid>();
        Assert.False(graph.GetAvailability(A, completed, Empty with { PlanConfirmed = false }).CanGenerate);
        Assert.False(graph.GetAvailability(A, completed, Empty with { HasActiveGenerationTask = true }).CanGenerate);
        Assert.False(graph.GetAvailability(A, completed, Empty with { HasPublishedLesson = true }).CanGenerate);
        Assert.False(graph.GetAvailability(A, completed, Empty with { SubjectActive = false }).CanGenerate);
    }

    [Theory]
    [InlineData(CourseKind.Theory)]
    [InlineData(CourseKind.Lab)]
    public void T03_PublishedLessonCanAdvanceWithoutPrerequisiteCompletion(CourseKind kind)
    {
        var graph = CourseGraph.Create([Node(A, 0), Node(B, 1, A) with { Kind = kind }]);
        var locked = graph.GetAvailability(B, new HashSet<Guid>(), Empty with
        {
            HasPublishedLesson = true, ReferenceValidated = true
        });
        Assert.True(locked.CanRead);
        Assert.False(locked.CanGenerate);
        Assert.Equal(kind == CourseKind.Theory, locked.CanAnswer);
        Assert.False(locked.PrerequisitesMet);
        Assert.Equal(kind == CourseKind.Lab, locked.CanStartLab);
        var absent = graph.GetAvailability(B, new HashSet<Guid>(), Empty);
        Assert.False(absent.CanRead);
        Assert.True(absent.CanGenerate);
    }

    [Fact]
    public void PublishedMaterialAndActiveSubject_AreRequiredForLabEligibility()
    {
        var graph = CourseGraph.Create([Node(A, 0) with { Kind = CourseKind.Lab }]);
        var completed = new HashSet<Guid>();
        Assert.False(graph.GetAvailability(A, completed, Empty with { ReferenceValidated = true }).CanStartLab);
        var published = Empty with { HasPublishedLesson = true };
        Assert.True(graph.GetAvailability(A, completed, published).CanStartLab);
        var validated = published with { ReferenceValidated = true };
        Assert.True(graph.GetAvailability(A, completed, validated).CanStartLab);
        Assert.False(graph.GetAvailability(A, completed, validated).CanAnswer);
        Assert.False(graph.GetAvailability(A, completed, validated with { SubjectActive = false }).CanStartLab);
    }

    [Fact]
    public void TheoryAnswers_RequirePublishedLessonAndActiveSubject()
    {
        var graph = Branches();
        var completed = new HashSet<Guid>();
        Assert.False(graph.GetAvailability(A, completed, Empty).CanAnswer);
        Assert.True(graph.GetAvailability(A, completed, Empty with { HasPublishedLesson = true }).CanAnswer);
        Assert.False(graph.GetAvailability(A, completed, Empty with
        {
            HasPublishedLesson = true, SubjectActive = false
        }).CanAnswer);
        Assert.Throws<ArgumentException>(() => graph.GetAvailability(Guid.NewGuid(), completed, Empty));
    }

    [Fact]
    public void LongChain_DoesNotDependOnRecursiveStackDepth()
    {
        var nodes = Enumerable.Range(0, 10000).Select(i => Node(Guid.NewGuid(), i)).ToArray();
        for (var i = 1; i < nodes.Length; i++) nodes[i] = nodes[i] with { PrerequisiteIds = [nodes[i - 1].Id] };
        var graph = CourseGraph.Create(nodes.Reverse());
        Assert.Equal(9999, graph.Layers[nodes[^1].Id]);
    }
}
