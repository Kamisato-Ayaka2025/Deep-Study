using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

// Explicit synthetic fixture for deterministic tests/demo, never selected by saved settings.
public sealed class TheoryFixtureProvider(string variant = "") : IModelProvider
{
    public string Destination => "local:deterministic-fake";
    public int Calls { get; private set; }
    public static TheoryLesson Lesson(string source) => new([
        new("intro", "heading", "集合与元素"),
        new("definition", "paragraph", "**集合**是确定对象的全体。元素是否属于集合是可以判断的。下面用简单例子理解这个概念。"),
        new("example", "example", "例如 A={1,2}，1是A的元素，3不是A的元素；相同元素重复列出不会增加元素个数。"),
        new("formula", "math", "A=\\{1,2\\},\\quad |A|=2"),
        new("code", "code", "// 仅展示，不运行\nint count = 2;\n// <script>window.injected=1</script>"),
        new("diagram", "diagram", "确定对象 -> 判断成员 -> 形成集合"),
        new("first", "question", QuestionId: "q1"),
        new("summary", "paragraph", "列举集合时要关注成员身份，重复列举不会改变集合。"),
        new("last", "question", QuestionId: "q2")],
        [new("q1", "集合{1,2}有几个元素？", [new("a", "2个"), new("b", "3个")], "a", "1和2是两个不同元素。"),
         new("q2", "集合{1,1,2}与{1,2}相同吗？", [new("a", "不同"), new("b", "相同")], "b", "重复列举元素不改变集合。")], [source]);
    public static TheoryLesson NewLesson(string source) => Lesson(source) with
    {
        SchemaVersion = 3,
        Questions = [
            new("q1", "集合{1,2}有几个元素？", [new("a", "2个"), new("b", "3个"), new("c", "1个"), new("d", "0个")], "a", "1和2是两个不同元素。"),
            new("q2", "下列哪个集合等于{1,2}？", [new("a", "{1}"), new("b", "{1,1,2}"), new("c", "{2}"), new("d", "{1,2,3}")], "b", "重复列举元素不改变集合。")]
    };
    public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken cancellationToken)
    {
        Calls++; await Task.Delay(150, cancellationToken);
        var source = request.Sources.FirstOrDefault(s => s.Kind == ModelSourceKind.OriginalMaterial);
        if (source is null) return Result<ProviderReply>.Fail(ErrorCode.INSUFFICIENT_SOURCES, "Synthetic source missing");
        var lesson = NewLesson(source.Id);
        if (variant.Length > 0) lesson = lesson with { Blocks = lesson.Blocks.Select(b => b.Id == "definition" ? b with { Text = b.Text + "（" + variant + "）" } : b).ToArray() };
        return Result<ProviderReply>.Success(new(JsonSerializer.Serialize(lesson, Wire.Options), true, Model: request.Model));
    }
}
