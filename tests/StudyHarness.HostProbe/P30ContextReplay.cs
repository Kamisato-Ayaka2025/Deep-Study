using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.HostProbe;

internal static class P30ContextReplay
{
    public static int Run(string[] args)
    {
        string Arg(string key) => Path.GetFullPath(args[Array.IndexOf(args, key) + 1]);
        using var plans = Open(Arg("--plans"));
        using var source = Open(Arg("--sources"));
        using var cmd = plans.CreateCommand(); cmd.CommandText = "SELECT json FROM drafts";
        var documents = new List<PlanDocument>();
        using (var reader = cmd.ExecuteReader()) while (reader.Read())
        {
            using var json = JsonDocument.Parse(reader.GetString(0));
            var plan = json.RootElement.GetProperty("plan").Deserialize<PlanDocument>(Wire.Options);
            if (plan?.Candidate.Title.Contains("计算机网络") == true) documents.Add(plan);
        }
        var planDocument = documents.Single();
        var bodies = new Dictionary<SourceRef, string>();
        using var chunks = source.CreateCommand(); chunks.CommandText = "SELECT reference,text FROM chunks";
        using (var reader = chunks.ExecuteReader()) while (reader.Read())
            bodies[JsonSerializer.Deserialize<SourceRef>(reader.GetString(0), Wire.Options)!] = reader.GetString(1);
        var report = planDocument.Candidate.Nodes.Select(node =>
        {
            var before = BookKnowledge.LessonUnits(planDocument, node).SelectMany(u => u.Sources).DistinctBy(s => s.Reference).ToArray();
            var after = BookKnowledge.LessonSources(planDocument, node);
            var rawLength = after.Sum(s => bodies[s.Reference].Length);
            var sources = after.Select(s => new ModelSource("c" + s.Reference.VersionId.ToString("N"), 0, ModelSourceKind.OriginalMaterial,
                "[资料id=c" + s.Reference.VersionId.ToString("N") + "]\n" + CourseMaterials.WithQuality(planDocument.Collections, s, bodies[s.Reference]))).ToArray();
            var serialized = JsonSerializer.Serialize(CourseMaterials.PackContext(sources), new JsonSerializerOptions(Wire.Options)
                { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var bytes = System.Text.Encoding.UTF8.GetByteCount(serialized);
            return new { node.Key, node.Title, beforeBlocks = before.Length, beforeCharacters = before.Sum(s => bodies[s.Reference].Length),
                afterBlocks = after.Count, afterCharacters = rawLength, serializedSourceBytes = bytes,
                withinLocalLimits = rawLength <= CourseMaterials.MaximumLessonCharacters && after.Count <= CourseMaterials.MaximumContextReferences && bytes < 786432,
                pages = after.Select(s => s.Reference.Locator.Page).Distinct().ToArray() };
        }).ToArray();
        File.WriteAllText(Arg("--output"), JsonSerializer.Serialize(new { readOnly = true, realCalls = 0, lessons = report }, Wire.Options));
        Console.WriteLine(JsonSerializer.Serialize(new { lessons = report.Length, failures = report.Where(r => !r.withinLocalLimits), first = report[0], maximumCharacters = report.Max(r => r.afterCharacters), maximumSourceBytes = report.Max(r => r.serializedSourceBytes) }, Wire.Options));
        return report.All(r => r.withinLocalLimits) ? 0 : 1;
    }
    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); return db;
    }
}
