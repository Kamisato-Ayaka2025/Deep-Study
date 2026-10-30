using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class P31DesktopCheck
{
    private sealed class Caps : IModelCapabilitySource
    { public Task<ModelCapabilities> GetAsync(string m, CancellationToken ct = default) => Task.FromResult(new ModelCapabilities(m, 1048576, 32768, true, DateTimeOffset.UtcNow, true)); }
    private sealed class Provider : IModelProvider
    {
        public string Destination => "local:exam-fake";
        public int Exams, Books, Lessons;
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest r, CancellationToken ct)
        {
            var prompt = r.Sources[0].Text; string text;
            if (r.Sources[0].Id == "book-knowledge")
            {
                var exam = prompt.Contains("purpose=exam-reference-v1");
                if (exam) Exams++; else Books++;
                text = JsonSerializer.Serialize(new { points = new[] { new { title = exam ? "Q1 / 计算题 / CIDR" : "CIDR定义", detail = exam ? "原题/24；分值未提供；分析推断：需计算地址数量；难度估计基础；原卷未提供答案" : "32位IPv4地址；/24表示24位网络前缀，剩余8位用于地址数量计算", evidenceIds = r.Sources.Skip(1).Select(s => s.Id).ToArray() } }, nonTeachingIds = Array.Empty<string>() }, Wire.Options);
            }
            else if (r.Sources[0].Id == "knowledge-mapping")
            {
                var points = JsonSerializer.Deserialize<BookKnowledgePoint[]>(r.Sources.Single(s => s.Id == "points").Text, Wire.Options)!;
                text = JsonSerializer.Serialize(new { knowledgeCoverage = points.Select(p => new KnowledgeCoverage(p.Key, "included", ["n1"], null, [])) }, Wire.Options);
            }
            else if (r.Sources[0].Id == "planning-v2")
            {
                if (!prompt.Contains("考题参照模式") || !prompt.Contains("Q1")) throw new InvalidOperationException("Exam missing from planning prompt");
                text = JsonSerializer.Serialize(new PlanCandidate("期末CIDR复习", "基础", "通过期末考试", 120, "Adaptive", [], [], [], ["s1", "s2"],
                    [new("n1", "CIDR前缀与地址数量计算", "theory", ["解释CIDR并计算/24地址数量，练习Q1同类题"], [], 45, null)], 2,
                    Strategy: new("计算机网络", ["theory"], "教材概念配合原题训练", [], [], []),
                    Coverage: [new("d1/page-1", "included", ["n1"], null), new("d2/page-1", "included", ["n1"], null)]), Wire.Options);
            }
            else
            {
                if (!prompt.Contains("模型推导，非官方答案") || !prompt.Contains("Q1")) throw new InvalidOperationException("Exam missing from lesson prompt");
                Lessons++;
                text = JsonSerializer.Serialize(new TheoryLesson([new("h", "heading", "CIDR与Q1"), new("p", "paragraph", "IPv4地址32位，/24表示前24位是网络前缀。"), new("e", "example", "Q1解题示范：剩余8位，地址总数2^8=256。这是模型推导，非官方答案。变式/25剩余7位，地址总数128。"), new("q", "question", null, "q1")],
                    [new("q1", "/24子网有多少个地址（含网络与广播地址）？", [new("a", "256"), new("b", "128"), new("c", "24"), new("d", "32")], "a", "2的8次方等于256。")], r.Sources.Skip(1).Select(s => s.Id).ToArray(), 3), Wire.Options);
            }
            return Task.FromResult(Result<ProviderReply>.Success(new(text, false, Usage: new(10, 10, 20))));
        }
    }
    internal static async Task RunAsync(string root, string evidence)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            Check(File.Exists(Path.Combine(root, "p31-synthetic.marker")), "isolated-root");
            var restart = File.Exists(Path.Combine(evidence, "ui.json"));
            var k = new KnowledgeStore(root); var settings = new SettingsService(new SqliteSettingsStore(root));
            var provider = new Provider(); var caps = new Caps(); var plans = new SqlitePlanningStore(root); var learning = new SqliteLearningStore(root, plans);
            var service = new PlanningService(plans, k, settings, new(provider, caps), new PlanParser(), explicitSourcesOnly: true, knowledgeCache: new BookKnowledgeCache(root));
            var planning = new PlanningViewModel(service, learning, sessionStore: new SqlitePlanningSessionStore(root));
            var workspace = new WorkspaceViewModel(new(k, settings, new(provider)));
            var shell = new ShellViewModel(settings, new(provider), new SafeLog(Path.Combine(root, "p31.log")));
            var old = System.Windows.Application.Current.MainWindow;
            var window = new MainWindow(shell, workspace, planning); window.Show(); System.Windows.Application.Current.MainWindow = window; old.Close(); await window.Ready.Task;
            window.ImportExpander.IsExpanded = true;
            if (!restart)
            {
                string Pdf(string name, string body)
                {
                    var b = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
                    var font = b.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
                    b.AddPage(600, 400).AddText(body, 12, new(20, 300), font);
                    var path = Path.Combine(root, name); File.WriteAllBytes(path, b.Build()); return path;
                }
                var book = Pdf("textbook.pdf", "IPv4 uses 32 bits. CIDR /24 has 8 host bits. Address count is 2^8.");
                var exam = Pdf("exam.pdf", "Q1: How many addresses are in a /24 subnet? Explain your method.");
                await workspace.ImportManyAsync([HostSelection.FromPlanFilePicker(book), HostSelection.FromExamFilePicker(exam)]);
                Check(workspace.ImportQueueCount == 0 && workspace.Materials.Count == 2, "mixed-import-queue-complete");
                Check(workspace.Materials.Count(m => m.IsExam) == 1 && workspace.Materials.Single(m => m.IsExam).Label == "exam.pdf", "distinct-material-roles");
                planning.Request = "结合教材和考题复习CIDR"; planning.Background = "基础"; planning.Goal = "通过期末考试"; planning.Minutes = "120";
                await planning.FormSubmissionAsync();
                Check(planning.CanSendPlan && planning.Preview.Contains("exam-reference-v1") && planning.Preview.Contains("考题参照模式"), "dedicated-exam-preview");
                Check(provider.Exams == 0 && provider.Books == 0, "preview-zero-dispatch");
                var restored = new PlanningViewModel(service, learning, sessionStore: new SqlitePlanningSessionStore(root)); await restored.RestoreSessionAsync();
                Check(restored.ExamCollectionIds.SequenceEqual(planning.ExamCollectionIds) && restored.LocalReferences.Count == 2, "mixed-session-restores-roles-and-references");
                await planning.ProposeAsync(true);
                Check(planning.Summary.Contains("Q1") && provider.Exams == 1 && provider.Books == 1, "independent-analysis-combined-plan");
                planning.AckFrozen = true; await planning.ConfirmAsync(); Check(planning.ConfirmedSubject is not null, "mixed-plan-confirmed");
            }
            var draft = (await plans.ListAsync()).Single(d => d.Confirmed);
            Check(draft.Input.ExamCollectionIds?.Count == 1 && draft.Plan!.Collections!.Count(ExamReference.IsExam) == 1, "frozen-role-persistence");
            var generation = new GenerationService(plans, learning, learning, k, settings, new(provider, caps), new TheoryLessonParser());
            var review = await generation.PrepareAsync(draft.Id, "n1");
            Check(review.Ok && review.Value!.Preview.Contains("Q1: How many") && review.Value.Preview.Contains("IPv4 uses 32 bits"), "lesson-retrieves-both-originals");
            Check(review.Value!.Preview.Contains("模型推导，非官方答案") && review.Value.Preview.Contains("本课关联考题"), "lesson-exam-guidance");
            if (!restart)
            {
                Check((await generation.GenerateAsync(draft.Id, "n1")).Ok, "lesson-enqueued"); await generation.TickAsync();
            }
            Check((await learning.GetLessonAsync(draft.Id, "n1")).Ok, "exam-guided-lesson-published");
            if (restart) Check(provider.Exams == 0 && provider.Books == 0 && provider.Lessons == 0, "restart-no-generation");
            if (!restart)
            {
                planning.NewDraft(); Check(planning.ExamCollectionIds.Count == 0, "new-draft-clears-exam-role");
                await workspace.DeleteMaterialAsync(workspace.Materials.Single(m => m.IsExam));
                Check(workspace.ExamCollectionIds.Count == 0 && workspace.Materials.Count == 1, "remove-exam-keeps-textbook");
            }
            window.UpdateLayout(); var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(Path.Combine(evidence, restart ? "restart.png" : "ui.png"))) encoder.Save(output);
            await File.WriteAllTextAsync(Path.Combine(evidence, restart ? "restart.json" : "ui.json"), JsonSerializer.Serialize(new { checks, failed = 0, realRemoteCalls = 0 }, Wire.Options));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "failure.json"), JsonSerializer.Serialize(new { checks, error = e.ToString() }, Wire.Options));
            System.Windows.Application.Current.Shutdown(1);
        }
    }
}
