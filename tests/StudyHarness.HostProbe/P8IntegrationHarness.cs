using System.Diagnostics;
using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.HostProbe;

internal static class P8IntegrationHarness
{
    internal static readonly IReadOnlyDictionary<string, (string Goal, int Minutes, string Source)> Cases = new Dictionary<string, (string, int, string)>
    {
        ["ocr"] = ("只学习已识别的PDF页序101、201、401：DNS查询、TCP公平性、移动路由，恰好三堂充分理论课，每页主题一堂。其他页明确列为资料缺口，不安排实践", 480, ""),
        ["text"] = ("仅学习教材中的集合、概率和区间算法三个主题，恰好三堂充分的理论课，每个主题一堂；无需实践项目", 1200, LongText()),
        ["oi"] = ("学习二分查找、前缀和和Dijkstra最短路，恰好三堂理论课和三道C++单文件标准输入输出练习，每算法一课一题", 480, ""),
        ["open-web"] = ("学习DNS缓存原理，安排一堂理论课和一个外部低频查询观察项目", 120, ""),
        ["math"] = ("学习集合并交补和德摩根律，安排一堂充分的理论课和一道纸笔证明题", 120,
            "# 集合\n集合由元素组成。并集的成员属于至少一方，交集的成员属于双方。补集相对于固定全集U定义。德摩根律：(A∪B)的补集=A的补集∩B的补集。证明：任取x∈U，x不属于A∪B等价于x既不属于A也不属于B。集合相等须证明双向包含。例如U={1,2,3,4}, A={1,2}, B={2,3}，并集={1,2,3}，补集={4}。"),
        ["network"] = ("理解DNS缓存与查询过程，安排一堂理论课和一个在外部终端观察DNS查询的操作项目", 480,
            "# DNS\nDNS是应用层的分布式命名系统，将域名映射到资源记录。递归解析器代表客户查询，迭代查询可能返回转介。缓存以TTL限制有效期；缓存命中不必访问权威服务器。DNS不保证所有域名只有一个地址。外部实验可在自有计算机用nslookup example.com观察服务器、域名与返回地址；保存实际输出和时间，多次查询比较，不能仅凭响应变快证明TTL或全部查询路径。不要向公共服务器发高频请求。"),
        ["c"] = ("学习C语言数组求和，安排一堂理论课和一道可由外部断言测试链接的C函数练习", 120,
            "# C数组求和\nC数组下标从0开始，访问范围为0到n-1。函数long long sum_array(const int *a,int n)在n>=0且n>0时a指向至少n个有效元素的前提下返回总和。n=0允许a为空且返回0。使用long long累计以降低int溢出风险，输入须保证总和可表示。循环不变式：处理前i项后acc等于这些项的和。测试空数组、单元素、负数和混合值；学生自行外部编译测试，软件不执行。"),
        ["cpp"] = ("学习C++竞赛中的二分查找与前缀和，每个算法恰好一堂理论课和一道单文件标准输入输出练习", 480,
            "# 二分查找\n非递减数组中lower_bound寻找首个不小于目标的位置，半开区间[lo,hi)，mid=lo+(hi-lo)/2。若a[mid]<x则lo=mid+1，否则hi=mid。不存在返回n。时间O(log n)，边界空数组、重复值、左右端。\n# 前缀和\np[0]=0，p[i+1]=p[i]+a[i]；半开区间[l,r)之和=p[r]-p[l]。预处理O(n)，查询O(1)，前缀值须用足够宽的整数。竞赛题用main和标准输入输出，题面给确定输入格式与边界。测试代码由宿主生成，软件不运行。"),
        ["cross"] = ("把离散概率与网络丢包联系起来，安排一堂理论课和一道纸笔计算题，讨论独立假设和模型局限", 1200,
            "# 概率与网络\n若每次传输独立成功概率p，直到首次成功的尝试数N取正整数且P(N=k)=(1-p)^(k-1)p，0<p<=1时E[N]=1/p。例如p=0.8，恰好第三次成功的概率0.032，平均1.25次。p=0时永不成功，有限期望公式不适用。真实丢包可能相关或拥塞，不能把独立几何模型直接当实测结论；重传还有超时、时延和协议开销。")
    };
    private static string LongText()
    {
        var text = new System.Text.StringBuilder();
        foreach (var topic in new[] {
            ("集合", "并集至少属于一方，交集同时属于双方；补集相对于固定全集。集合相等须证明双向包含。德摩根律是并集之补等于补集之交，交集之补等于补集之并，可对任意元素逐项否定归属条件证明。"),
            ("概率", "独立重复试验成功概率p，首次成功的次数N满足P(N=k)=(1-p)^(k-1)p；0<p<=1时E[N]=1/p。p=0不保证终止，相关失败不能直接套独立几何模型。p=0.8时第三次首次成功概率0.032，均值1.25。"),
            ("区间算法", "非递减数组上用半开区间二分求首个>=目标的位置，lo=0,hi=n，mid=lo+(hi-lo)/2；a[mid]<x则lo=mid+1，否则hi=mid，返回lo。前缀和p[0]=0,p[i+1]=p[i]+a[i]，区间[l,r)和=p[r]-p[l]，须考虑整数范围。") })
        {
            text.AppendLine("# " + topic.Item1);
            for (var i = 1; i <= 1100; i++) text.AppendLine($"合成检索样本{i}：{topic.Item2}");
        }
        return text.ToString();
    }
    public static async Task<int> Run(string[] args)
    {
        string? Value(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var credentialRoot = Value("--data-root"); var output = Value("--evidence-root"); var scenario = Value("--scenario") ?? "math";
        var resumePreparation = args.Contains("--resume-preparation") && scenario == "ocr";
        var runPrefix = resumePreparation ? $"resume-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-" : "";
        if (credentialRoot is null || output is null || !Cases.TryGetValue(scenario, out var sample) || Directory.Exists(output) && !resumePreparation) return 64;
        Directory.CreateDirectory(output); var checks = new List<string>(); var timings = new List<object>();
        var totalWait = Stopwatch.StartNew();
        void Check(bool value, string name) { if (!value) throw new InvalidDataException(name); checks.Add(name); }
        try
        {
            var credentials = new SqliteSettingsStore(credentialRoot); var settings = new SettingsService(credentials);
            var web = new RecordedWeb(new DeepSeekWebSources(credentials, DeepSeekWebSources.CreateClient()), output);
            var knowledge = new KnowledgeStore(output, web); var libraryPath = Path.Combine(output, "library"); Directory.CreateDirectory(libraryPath);
            var library = await knowledge.CurrentAsync() ?? (await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(libraryPath)))).Value!;
            IReadOnlyList<SourceRef> selectedReferences;
            var preparation = Stopwatch.StartNew();
            if (scenario == "ocr")
            {
                var pdf = Value("--authorized-pdf") ?? throw new InvalidDataException("Authorized PDF required");
                using (var source = File.OpenRead(pdf))
                    Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)).Equals("1fd9f5f23b8362a112a7a49c4154bfc0353f741831f9cca6a154d86736dde36d", StringComparison.OrdinalIgnoreCase), "authorized-source-fingerprint");
                var existing = resumePreparation ? (await knowledge.CollectionsAsync(library.Id)).SingleOrDefault() : null;
                var imported = existing is null ? await knowledge.ImportCollectionAsync(HostSelection.FromFilePicker(pdf), library.Id) : Result<CollectionSnapshot>.Success(existing);
                Check(imported.Ok && imported.Value!.Collection.EntryCount == 505, "native-full-page-inventory");
                using var ocrClient = DeepSeekProvider.CreateClient(); using var capClient = DeepSeekCapabilities.CreateClient();
                var ocrProvider = new CountedOcr(new DeepSeekOcrProvider(credentials, ocrClient));
                var ocr = new OcrService(knowledge, settings, new DeepSeekCapabilities(output, credentials, capClient), ocrProvider);
                var reviewed = await ocr.PrepareAsync(library.Id, imported.Value!.Collection.Id, [101, 201, 401]); Check(reviewed.Ok, "authorized-three-page-preview");
                await File.WriteAllTextAsync(Path.Combine(output, "ocr-scope.txt"), reviewed.Value!.Preview);
                var result = await ocr.RunAsync(reviewed.Value, true); Check(result.Ok && result.Value == 3, "production-ocr-selected-pages-only");
                var cold = preparation.Elapsed.TotalSeconds; preparation.Restart();
                var callsBeforeCache = ocrProvider.Calls;
                Check((await ocr.RunAsync(reviewed.Value, true)).Value == 3, "ocr-cache-reuse");
                var cacheSeconds = preparation.Elapsed.TotalSeconds;
                Check(ocrProvider.Calls == callsBeforeCache, "ocr-cache-no-provider-dispatch");
                using (var source = File.OpenRead(pdf))
                    Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)).Equals("1fd9f5f23b8362a112a7a49c4154bfc0353f741831f9cca6a154d86736dde36d", StringComparison.OrdinalIgnoreCase), "source-unchanged-after-ocr");
                timings.Add(new { phase = resumePreparation ? "ocr-preparation-resumed-cache" : "ocr-preparation", firstPassSeconds = cold, cachedSeconds = cacheSeconds, providerCalls = ocrProvider.Calls, authorizedPages = new[] { 101, 201, 401 }, fullBookUploaded = false });
                var ready = (await knowledge.CollectionsAsync(library.Id)).Single();
                var recognized = ready.Manifest.Entries.Where(e => e.State == "ocrReady").ToArray();
                Check(recognized.Select(e => e.Key).Order().SequenceEqual(new[] { "page-101", "page-201", "page-401" }) &&
                    recognized.All(e => e.QualityFlags.Contains("unverified")), "only-authorized-three-ocr-pages-and-quality-flags-retained");
                selectedReferences = [ready.References.First(r => r.Locator.Page == 101)];
            }
            else if (scenario is "oi" or "open-web")
            {
                using var directoryClient = OiDirectory.CreateClient();
                var acquired = await new WebLearningService(web, knowledge, knowledge, libraryPath, new OiDirectory(directoryClient)).AcquireAsync(
                    scenario == "oi" ? "二分查找 前缀和 Dijkstra" : "DNS缓存原理 递归解析器 TTL", scenario == "oi" ? "oi-wiki.org" : "", true, null, default);
                selectedReferences = acquired.References;
                await File.WriteAllTextAsync(Path.Combine(output, "acquisition.txt"), acquired.Report);
                var cold = preparation.Elapsed.TotalSeconds; preparation.Restart();
                Check((await knowledge.ResolveAsync(selectedReferences[0])).Text is { Length: > 0 }, "cached-web-body-readable");
                timings.Add(new { phase = "web-preparation", coldSeconds = cold, cachedBodyReadSeconds = preparation.Elapsed.TotalSeconds });
            }
            else
            {
                var sourcePath = Path.Combine(output, "synthetic-textbook.md"); await File.WriteAllTextAsync(sourcePath, sample.Source);
                var imported = await knowledge.ImportCollectionAsync(HostSelection.FromFilePicker(sourcePath), library.Id); Check(imported.Ok, "import-synthetic-collection");
                selectedReferences = [imported.Value!.References[0]];
                var cold = preparation.Elapsed.TotalSeconds; preparation.Restart();
                var cached = await knowledge.ImportCollectionAsync(HostSelection.FromFilePicker(sourcePath), library.Id);
                Check(cached.Value!.Collection.Version == imported.Value.Collection.Version, "cache-same-version");
                timings.Add(new { phase = "source", coldSeconds = cold, cachedSeconds = preparation.Elapsed.TotalSeconds, sourceCharacters = sample.Source.Length });
            }
            var plans = new SqlitePlanningStore(output); var learning = new SqliteLearningStore(output, plans);
            using var client = DeepSeekProvider.CreateClient(); using var metadata = DeepSeekCapabilities.CreateClient();
            var capabilities = new DeepSeekCapabilities(output, credentials, metadata);
            var provider = new RecordingProvider(new DeepSeekProvider(credentials, client), output, runPrefix + "plan-lab");
            var theoryProvider = new RecordingProvider(new DeepSeekTheoryProvider(credentials, client), output, runPrefix + "theory");
            var planning = new PlanningService(plans, knowledge, settings, new(provider, capabilities), new PlanParser(), learning);
            var draft = (await planning.StartAsync(new(Guid.NewGuid(), 0, new(sample.Goal, "具备高中数学和基本计算机操作基础", sample.Goal, sample.Minutes, "Adaptive", selectedReferences)))).Value!;
            var timer = Stopwatch.StartNew(); var preview = await planning.PrepareAsync(draft.Id, draft.Revision); Check(preview.Ok, "prepare-" + preview.Error?.Message);
            var proposed = await planning.ProposeAsync(preview.Value!, true); Check(proposed.Ok, "propose-" + proposed.Error?.Message);
            timings.Add(new { phase = "plan", seconds = timer.Elapsed.TotalSeconds }); Check(timer.Elapsed.TotalSeconds <= 600, "plan-target-600s");
            Check(proposed.Value!.Plan!.Candidate.SchemaVersion == 2 && proposed.Value.Plan.Candidate.Strategy is not null, "automatic-strategy-v2");
            Check(proposed.Value.Plan.Candidate.WeeklyMinutes == sample.Minutes, "exact-weekly-time");
            var confirmed = await planning.ConfirmAsync(new(Guid.NewGuid(), proposed.Value.Revision, new(draft.Id, proposed.Value.Plan.Hash, true))); Check(confirmed.Ok, "explicit-plan-freeze"); draft = confirmed.Value!;
            await File.WriteAllTextAsync(Path.Combine(output, runPrefix + "confirmed-plan.json"), JsonSerializer.Serialize(draft, Wire.Options));
            var generation = new GenerationService(plans, learning, learning, knowledge, settings, new(theoryProvider, capabilities), new TheoryLessonParser());
            var labs = new LabService(plans, learning, learning, knowledge, settings, new(provider, capabilities), new LabZipExporter());
            var graph = PlanRules.Graph(draft.Id, draft.Plan!.Candidate);
            foreach (var node in draft.Plan.Candidate.Nodes.OrderBy(n => graph.Layers[PlanRules.NodeId(draft.Id, n.Key)]))
            {
                timer.Restart();
                if (node.Type == "theory")
                {
                    var queued = await generation.GenerateAsync(draft.Id, node.Key); Check(queued.Ok, "queue-" + node.Key + "-" + queued.Error?.Message);
                    for (var i = 0; i < 3; i++) { await generation.TickAsync(); if (!(await learning.TasksAsync()).Any(t => t.NodeKey == node.Key && t.Status == GenerationStatus.Queued)) break; await Task.Delay(3200); }
                    var read = await learning.GetLessonAsync(draft.Id, node.Key); Check(read.Ok, "theory-ready-" + node.Key);
                    Check(timer.Elapsed.TotalSeconds <= 300, "lesson-target-300s-" + node.Key);
                    Check(read.Value!.Questions.Count > 0 && read.Value.Blocks.Any(b => b.Kind == "example"), "quiz-and-substantial-example-" + node.Key);
                    await File.WriteAllTextAsync(Path.Combine(output, runPrefix + node.Key + "-reader.json"), JsonSerializer.Serialize(read.Value, Wire.Options));
                    // Isolated synthetic traversal, never learner mastery evidence.
                    foreach (var q in read.Value.Questions)
                        foreach (var option in q.Options)
                            if ((await learning.SubmitAnswerAsync(new(Guid.NewGuid(), 0, new(read.Value.Identity, q.Id, option.Id)))).Value?.Correct == true) break;
                }
                else
                {
                    var package = await labs.GenerateAsync(draft.Id, node.Key); Check(package.Ok, "practice-ready-" + node.Key + "-" + package.Error?.Message);
                    Check(!(await learning.CompletedNodesAsync(draft.Id)).Contains(node.Key), "generation-not-completion-" + node.Key);
                    Check((await labs.ExportAsync(draft.Id, node.Key, Path.GetFullPath(Path.Combine(output, node.Key + ".zip")))).Ok, "export-" + node.Key);
                    Check((await labs.ConfirmAsync(draft.Id, node.Key, package.Value!.Hash, true)).Ok, "synthetic-attestation-" + node.Key);
                }
                timings.Add(new { phase = node.Type, node.Key, seconds = timer.Elapsed.TotalSeconds });
            }
            if (scenario is "text" or "oi" or "ocr")
            {
                var first = draft.Plan.Candidate.Nodes.First(n => n.Type == "theory"); timer.Restart();
                Check((await generation.GenerateAsync(draft.Id, first.Key, true)).Ok, "cached-source-regeneration-request");
                for (var i = 0; i < 3; i++) { await generation.TickAsync(); if (!(await learning.TasksAsync()).Any(t => t.NodeKey == first.Key && t.Status == GenerationStatus.Queued)) break; await Task.Delay(3200); }
                Check((await learning.TasksAsync()).Last(t => t.NodeKey == first.Key).Status == GenerationStatus.Ready, "cached-source-regeneration-ready");
                Check(timer.Elapsed.TotalSeconds <= 300, "cached-source-target-300s");
                timings.Add(new { phase = "cached-source-lesson", first.Key, seconds = timer.Elapsed.TotalSeconds });
            }
            var restored = new SqlitePlanningStore(output); Check((await restored.GetAsync(draft.Id))!.Plan!.Hash == draft.Plan.Hash, "restart-frozen-plan");
            await File.WriteAllTextAsync(Path.Combine(output, runPrefix + "p8-real-summary.json"), JsonSerializer.Serialize(new { success = true, scenario, checks, timings,
                totalWaitSeconds = totalWait.Elapsed.TotalSeconds, calls = provider.Calls + theoryProvider.Calls, tasks = await learning.TasksAsync(), codeExecuted = false, syntheticProgressOnly = true, qualityReviewPending = true, manualPending = true }, Wire.Options));
            Console.WriteLine($"P8 {scenario}: {checks.Count} checks; {provider.Calls + theoryProvider.Calls} requests; quality review pending."); return 0;
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(output, resumePreparation ? $"p8-real-failure-resume-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json" : "p8-real-failure.json"), JsonSerializer.Serialize(new { scenario, checks, timings, totalWaitSeconds = totalWait.Elapsed.TotalSeconds, failure = e.Message, codeExecuted = false }, Wire.Options));
            Console.WriteLine("P8 integration failed; preserved evidence at " + output); return 1;
        }
    }
    private sealed class CountedOcr(IOcrProvider inner) : IOcrProvider
    {
        public int Calls { get; private set; }
        public Task<Result<OcrPageResult>> RecognizeAsync(ApprovedOcrPage request, CancellationToken ct)
        { Calls++; return inner.RecognizeAsync(request, ct); }
    }
    private sealed class RecordedWeb(IWebSources inner, string root) : IWebSources
    {
        private int fetches;
        public async Task<IReadOnlyList<WebHit>> SearchAsync(string query, CancellationToken ct)
        {
            var hits = await inner.SearchAsync(query, ct);
            await File.WriteAllTextAsync(Path.Combine(root, "web-search.json"), JsonSerializer.Serialize(new { query, hits }, Wire.Options), ct);
            return hits;
        }
        public async Task<WebPage> FetchAsync(string url, CancellationToken ct)
        {
            var path = Path.Combine(root, $"web-fetch-{++fetches}.json");
            try
            {
                var page = await inner.FetchAsync(url, ct);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { url, page.Title, page.RetrievedAt, characters = page.Content.Length, success = true }, Wire.Options), ct);
                return page;
            }
            catch (InvalidOperationException e)
            {
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { url, success = false, failure = e.Message }, Wire.Options), ct); throw;
            }
        }
    }
    private sealed class RecordingProvider(IModelProvider inner, string root, string prefix) : IModelProvider
    {
        public string Destination => inner.Destination; public int Calls { get; private set; }
        public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            var watch = Stopwatch.StartNew(); Calls++; var reply = await inner.SendAsync(request, ct);
            await File.WriteAllTextAsync(Path.Combine(root, $"{prefix}-reply-{Calls}.json"), JsonSerializer.Serialize(new { elapsedSeconds = watch.Elapsed.TotalSeconds, request.Model, request.MaxOutputTokens, reply }, Wire.Options));
            return reply;
        }
    }
}
