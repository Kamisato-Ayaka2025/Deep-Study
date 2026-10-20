using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Core;

namespace StudyHarness.HostProbe;
internal static class P2Harness
{
    public static async Task<int> Run(string[] args)
    {
        string? Value(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var root = Value("--data-root");
        if (root is null) return 64;
        if (args.Contains("--p2-seed"))
        {
            var source = Path.Combine(root, "synthetic-source"); var library = Path.Combine(root, "library-A");
            Directory.CreateDirectory(source); Directory.CreateDirectory(library);
            await File.WriteAllTextAsync(Path.Combine(source, "guide.md"), "# 变量\n\n变量保存可变化的值。\n\n# 示例\n\nint count = 1;");
            var pdf = new PdfDocumentBuilder(); var page = pdf.AddPage(300, 400);
            page.AddText("A variable stores a value.", 12, new PdfPoint(20, 300), pdf.AddStandard14Font(Standard14Font.Helvetica));
            await File.WriteAllBytesAsync(Path.Combine(source, "sample.pdf"), pdf.Build());
            await File.WriteAllTextAsync(Path.Combine(source, "unsupported.txt"), "synthetic unsupported fixture");
            var knowledge = new KnowledgeStore(root); var current = await knowledge.CurrentAsync();
            var switched = await knowledge.SwitchAsync(new(Guid.NewGuid(), current?.Revision ?? 0, HostSelection.FromFolderPicker(library)));
            if (!switched.Ok) return 1;
            var imported = await knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(source)), switched.Value!.Id);
            Console.WriteLine(JsonSerializer.Serialize(new { imported = imported.Ok, files = imported.Value?.Files.Count }, Wire.Options));
            return imported.Ok ? 0 : 1;
        }
        if (args.Contains("--p2-real"))
        {
            var store = new SqliteSettingsStore(root); var settings = await store.ReadAsync(default);
            if (!settings.HasApiKey) { Console.WriteLine("blocked: configure API key in the desktop settings"); return 2; }
            using var client = DeepSeekProvider.CreateClient();
            var gate = new ModelEgress(new DeepSeekProvider(store, client));
            var review = gate.Prepare(settings.Model, [new("p2-minimal-probe", 0, ModelSourceKind.UserText, "Reply with the word OK.")]);
            var reply = await gate.SendAsync(review, gate.Approve(review, ConsentMode.Each));
            // No key, prompt, response, credential ciphertext or personal path in evidence.
            Console.WriteLine(JsonSerializer.Serialize(new { success = reply.Ok, code = reply.Error?.Code.ToString(), model = settings.Model,
                usage = reply.Value?.Usage, isFake = reply.Value?.IsFake, responseCharacters = reply.Value?.Text.Length,
                time = DateTimeOffset.UtcNow, realCodeEnabled = false }, Wire.Options));
            return reply.Ok ? 0 : 1;
        }
        return 64;
    }
}
