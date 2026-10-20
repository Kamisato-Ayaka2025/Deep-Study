using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class HybridDesktopCheck
{
    public static async Task RunAsync(string root, string evidence)
    {
        LearningWindow? window = null;
        var checks = new List<string>();
        try
        {
            if (!File.Exists(Path.Combine(root, "hybrid-synthetic.marker"))) throw new InvalidDataException("Isolated marker required");
            Directory.CreateDirectory(evidence);
            void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
            var restart = File.Exists(Path.Combine(evidence, "ui.json"));
            var plans = new SqlitePlanningStore(root);
            var knowledge = new KnowledgeStore(root);
            var store = new SqliteLearningStore(root, plans);
            PlanningDraft draft;
            if (!restart)
            {
                Check((await plans.ListAsync()).Count == 0, "fresh-isolated-root");
                var folder = Path.Combine(root, "library"); Directory.CreateDirectory(folder);
                var file = Path.Combine(root, "fixture.md");
                await File.WriteAllTextAsync(file, "# 集合\n集合是确定对象的全体，元素不重复。\n");
                var library = (await knowledge.SwitchAsync(new(Guid.NewGuid(), (await knowledge.CurrentAsync())?.Revision ?? 0, HostSelection.FromFolderPicker(folder)))).Value!;
                await knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromFilePicker(file)), library.Id);
                var reference = (await knowledge.SearchAsync(library.Id, "", [])).Fragments[0].Reference;
                var input = new PlanningInput("集合", "初学", "理解集合", 120, "Math", [reference]);
                draft = (await plans.StartAsync(new(Guid.NewGuid(), 0, input))).Value!;
                var candidate = new PlanCandidate("混合知识验收", "初学", "理解集合", 120, "Math", [], [], [], ["s1"],
                    [new("stable", "集合基础", "theory", ["理解集合"], [], 20, null),
                     new("mixed", "集合与资料", "theory", ["理解集合"], [], 20, null)]);
                PlanSource[] sources = [new("s1", reference, true)];
                var doc = PlanRules.Validate(draft.Id, input, candidate, sources, null);
                Check(doc.Ok, "valid-synthetic-plan");
                draft = (await plans.SaveProposalAsync(new(Guid.NewGuid(), 0, doc.Value!), draft.Id)).Value!;
                draft = (await plans.ConfirmAsync(new(Guid.NewGuid(), draft.Revision, new(draft.Id, doc.Value!.Hash, true)))).Value!;
                foreach (var key in new[] { "stable", "mixed" })
                {
                    var content = TheoryFixtureProvider.NewLesson("s1");
                    if (key == "stable") content = content with { SchemaVersion = 4, SourceKeys = [] };
                    var result = await store.PublishAsync(new(Guid.NewGuid(), 0, TheoryKnowledgePolicy.Label(content)),
                        draft.Id, key, draft.Plan!.Hash, key == "stable" ? [] : sources);
                    Check(result.Ok, "publish-" + key);
                }
            }
            else draft = (await plans.ListAsync()).Single();
            var provider = new TheoryFixtureProvider();
            var service = new GenerationService(plans, store, store, knowledge, new(new SqliteSettingsStore(root)), new(provider), new TheoryLessonParser());
            window = new(service, draft.Id, "stable", root); window.Show();
            await window.ReaderInitialized.Task.WaitAsync(TimeSpan.FromSeconds(30));
            async Task<bool> Js(string code) => await window.Browser.ExecuteScriptAsync(code) == "true";
            async Task Wait(string code)
            {
                for (var i = 0; i < 100; i++) { if (await Js(code)) return; await Task.Delay(100); }
                throw new InvalidOperationException("Reader timed out");
            }
            var identities = new Dictionary<string, string>();
            foreach (var key in new[] { "stable", "mixed" })
            {
                window.Model.Selected = window.Model.Nodes.Single(n => n.Key == key); await window.Model.OpenAsync();
                await Wait($"current?.identity.nodeKey==='{key}'");
                var reader = (await store.GetLessonAsync(draft.Id, key)).Value!;
                identities[key] = reader.Identity.LessonHash;
                Check(reader.Sources.Count == (key == "stable" ? 0 : 1), "honest-source-count-" + key);
                Check(await Js(key == "stable" ? "document.body.textContent.includes('未引用外部资料')" : "document.body.textContent.includes('混合方式')"), "notice-rendered-" + key);
                Check(await Js("document.querySelectorAll('.question input').length===8"), "four-options-" + key);
                Check(await Js("!window.injected"), "content-not-executed-" + key);
                if (!restart)
                {
                    foreach (var id in new[] { "q1", "q2" })
                    {
                        var option = id == "q1" ? "a" : "b";
                        // Exercise the same application answer path as the Reader message bridge.
                        await window.Model.SubmitAsync(reader.Identity.LessonHash, id, option, Guid.NewGuid());
                    }
                    await window.Model.OpenAsync();
                }
                Check((await store.CompletedNodesAsync(draft.Id)).Contains(key), "completion-persists-" + key);
                await Task.Delay(300);
                await using var image = File.Create(Path.Combine(evidence, (restart ? "restart-" : "") + key + ".png"));
                await window.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            }
            if (restart) Check(JsonSerializer.Serialize(identities) == await File.ReadAllTextAsync(Path.Combine(evidence, "hashes.json")), "restart-identities-unchanged");
            else await File.WriteAllTextAsync(Path.Combine(evidence, "hashes.json"), JsonSerializer.Serialize(identities));
            Check(provider.Calls == 0, "no-model-calls-on-read-or-restart");
            await File.WriteAllTextAsync(Path.Combine(evidence, restart ? "restart.json" : "ui.json"), JsonSerializer.Serialize(new { checks, failed = 0, manualPending = true }, Wire.Options));
            window.Close(); System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "failure.json"), JsonSerializer.Serialize(new { checks, error = e.ToString() }, Wire.Options));
            window?.Close(); System.Windows.Application.Current.Shutdown(1);
        }
    }
}
