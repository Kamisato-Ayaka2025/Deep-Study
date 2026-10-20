using System.Text.Json;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

// Per-request session over the consented immutable snapshot. No filesystem access.
public sealed class MaterialRetrieval(IReadOnlyList<ModelSource> sources)
{
    // Tool results are plain JSON text, never HTML. Keep Chinese readable instead
    // of expanding every character into a six-character Unicode escape.
    private static readonly JsonSerializerOptions ToolJson = new(Wire.Options)
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public const int MaxCalls = 12;
    public const int MaxCharacters = 65536;
    private int characters;
    private readonly List<string> readIds = [];
    public int Calls { get; private set; }
    public IReadOnlyList<string> ReadIds => readIds.AsReadOnly();
    public string Catalog => JsonSerializer.Serialize(sources.Select(s => new { id = s.Id, label = s.Label, characters = s.Text.Length }), ToolJson);
    public string Invoke(string name, string arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (++Calls > MaxCalls || arguments.Length > 4096) throw new InvalidDataException("Retrieval budget exceeded");
        using var json = JsonDocument.Parse(arguments);
        var root = json.RootElement;
        var field = name switch { "read_material" => "id", "search_materials" => "query", _ => throw new InvalidDataException("Unknown tool") };
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
            !root.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Invalid tool arguments");
        var input = value.GetString()!;
        string result;
        if (name == "read_material")
        {
            var source = sources.SingleOrDefault(s => s.Id == input) ?? throw new InvalidDataException("Source outside approved scope");
            result = JsonSerializer.Serialize(new { id = source.Id, label = source.Label, text = source.Text }, ToolJson);
            if (!readIds.Contains(source.Id)) readIds.Add(source.Id);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(input) || input.Length > 128) throw new InvalidDataException("Invalid search");
            var terms = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            result = JsonSerializer.Serialize(sources.Where(s => terms.Any(t => s.Text.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                (s.Label?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false))).Take(20)
                .Select(s => new { id = s.Id, label = s.Label, characters = s.Text.Length }), ToolJson);
        }
        characters += result.Length;
        if (characters > MaxCharacters) throw new InvalidDataException("Retrieval text budget exceeded");
        return result;
    }
}
