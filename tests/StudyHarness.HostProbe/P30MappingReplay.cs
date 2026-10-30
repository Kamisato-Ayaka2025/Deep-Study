using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.HostProbe;

internal static class P30MappingReplay
{
    public static int Run(string[] args)
    {
        var cachePath = Path.GetFullPath(args[Array.IndexOf(args, "--cache") + 1]);
        var output = Path.GetFullPath(args[Array.IndexOf(args, "--output") + 1]);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = cachePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM batches";
        var units = new List<BookKnowledgeUnit>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var unit = JsonSerializer.Deserialize<BookKnowledgeUnit>(reader.GetString(0), Wire.Options)!;
                if (unit.Sources.Any(s => s.Reference.Locator.SourceLabel.Contains("计算机网络"))) units.Add(unit);
            }
        units = units.OrderBy(u => int.Parse(u.Key.Split('u')[1])).ToList();
        command.CommandText = "SELECT key,json FROM derived";
        var derived = new Dictionary<string, string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) derived.Add(reader.GetString(0), reader.GetString(1));
        var candidate = derived.Where(p => p.Key.StartsWith("layout-v1-")).Select(p => JsonSerializer.Deserialize<PlanCandidate>(p.Value, Wire.Options)!)
            .Single(p => p.Title.Contains("计算机网络"));
        var all = units.SelectMany(u => u.Points).ToArray(); var index = string.Join('\n', all.Select(p => p.Key + " " + p.Title));
        var model = "deepseek-flash"; var maps = new List<KnowledgeCoverage>(); var missing = 0;
        foreach (var points in all.Chunk(40))
        {
            var key = "mapping-v2-" + PlanRules.Hash(new { model, candidate, points, index });
            if (!derived.TryGetValue(key, out var raw)) { missing++; continue; }
            maps.AddRange(BookKnowledgeMapping.Parse(raw, points, candidate, all.Select(p => p.Key).ToHashSet()));
        }
        var refs = units.SelectMany(u => u.Sources).Select(s => s.Reference).Distinct().ToArray();
        var collection = new PlanCollection(refs[0].LibraryId, new(Guid.Empty, "offline-replay", "", true, []), refs, KnowledgeUnits: units);
        var before = candidate with { KnowledgeCoverage = maps };
        var after = BookKnowledge.BindMappedPages(before, [collection]);
        string? Binding(PlanCandidate p) => CourseMaterials.MissingBindingError(new(p, [], [], "offline", null, Collections: [collection]));
        var report = new { batches = units.Count, knowledgePoints = all.Length, mappings = maps.Count, missingCachedGroups = missing,
            before = Binding(before), after = Binding(after), knowledgeValidation = BookKnowledge.Validate(after, [collection]),
            n54Pages = after.Coverage!.Where(c => c.State == "included" && c.NodeKeys.Contains("n54")).Select(c => c.EntryKey).ToArray(), realCalls = 0, readOnly = true };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, Wire.Options));
        Console.WriteLine(JsonSerializer.Serialize(report, Wire.Options));
        return missing == 0 && Binding(after) is null && BookKnowledge.Validate(after, [collection]) is null ? 0 : 1;
    }
}
