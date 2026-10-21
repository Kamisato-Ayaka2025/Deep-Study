using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.HostProbe;

internal static class P14Harness
{
    public static async Task<int> LessonPreview(string[] args)
    {
        var root = Path.GetFullPath(args[Array.IndexOf(args, "--evidence") + 1]);
        var knowledge = new KnowledgeStore(root);
        var plans = new SqlitePlanningStore(root);
        var draft = (await plans.ListAsync()).Single(d => d.Confirmed && d.Plan?.Hash == args[Array.IndexOf(args, "--plan-hash") + 1]);
        var learning = new SqliteLearningStore(root, plans);
        var settings = new SettingsService(args.Contains("--credentials") ? new ReadOnlySettings(args[Array.IndexOf(args, "--credentials") + 1]) : new SqliteSettingsStore(root));
        var generation = new GenerationService(plans, learning, learning, knowledge, settings,
            new(new PreviewOnlyProvider(), new CachedCapabilities(root)), new TheoryLessonParser());
        var result = await generation.PrepareAsync(draft.Id, "n1");
        Save(root, "lesson-preview-check.json", new { result.Ok, result.Error, selection = await learning.SelectionAsync(draft.Id, "n1"),
            budget = result.Value?.Reviews["n1"].Budget, model = result.Value?.Model });
        Console.WriteLine(JsonSerializer.Serialize(new { result.Ok, result.Error, budget = result.Value?.Reviews["n1"].Budget }, Wire.Options));
        return result.Ok ? 0 : 1;
    }
    private sealed class PreviewOnlyProvider : IModelProvider
    {
        public string Destination => "local:preview-only";
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct) => throw new InvalidOperationException("Preview must never dispatch");
    }
    private sealed class CachedCapabilities(string root) : IModelCapabilitySource
    {
        public Task<ModelCapabilities> GetAsync(string model, CancellationToken ct)
        {
            using var db = new SqliteConnection($"Data Source={Path.Combine(root, "model-capabilities.db")};Mode=ReadOnly;Pooling=False"); db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json FROM capabilities WHERE model=$m"; cmd.Parameters.AddWithValue("$m", model);
            return Task.FromResult(cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ModelCapabilities>(json, Wire.Options)! : AdaptiveBudgetPolicy.Conservative(model, DateTimeOffset.UtcNow));
        }
    }
    public static async Task<int> Inspect(string[] args)
    {
        var output=Path.GetFullPath(args[Array.IndexOf(args,"--evidence")+1]);
        var knowledge=new KnowledgeStore(output); var library=(await knowledge.CurrentAsync())!;
        var book=(await knowledge.CollectionsAsync(library.Id)).Single();
        foreach(var page in await knowledge.RenderPagesAsync(library.Id,book.Collection.Id,[16,101,201,401],default))
            await File.WriteAllBytesAsync(Path.Combine(output,$"original-page-{page.Page}.png"),page.Png);
        return 0;
    }
    public static async Task<int> Plan(string[] args)
    {
        string Value(string name) => args[Array.IndexOf(args,name)+1];
        var output=Path.GetFullPath(Value("--evidence"));
        var repair=args.Contains("--repair-saved-candidate");
        var report=repair ? Path.Combine(output,"plan-repair-1") : output;
        Directory.CreateDirectory(report);
        if(File.Exists(Path.Combine(report,"plan-result.json"))) throw new IOException("Existing plan result retained; no automatic paid repeat");
        var knowledge=new KnowledgeStore(output); var library=(await knowledge.CurrentAsync())!;
        var book=(await knowledge.CollectionsAsync(library.Id)).Single();
        var state=await knowledge.ScanStateAsync(library.Id,book.Collection.Id,default);
        if(state.TotalPages!=505 || state.PendingPages.Count!=0 || state.SourceFingerprint!="1FD9F5F23B8362A112A7A49C4154BFC0353F741831F9CCA6A154D86736DDE36D") throw new IOException("Complete authorized textbook required");
        var credentials=new ReadOnlySettings(Value("--credentials")); var settings=new SettingsService(credentials);
        using var client=DeepSeekProvider.CreateClient(); using var meta=DeepSeekCapabilities.CreateClient();
        var cap=new DeepSeekCapabilities(output,credentials,meta); var provider=new ReceiptPlan(new DeepSeekProvider(credentials,client),report,repair ? Path.Combine(output,"plan-reply-3.json") : null);
        var store=new SqlitePlanningStore(output); var learning=new SqliteLearningStore(output,store);
        var planning=new PlanningService(store,knowledge,settings,new(provider,cap),new PlanParser(),learning,explicitSourcesOnly:true);
        var input=new PlanningInput("依据已导入的《计算机网络：自顶向下方法》第8版完整教材，制定系统学习计算机网络的计划。覆盖全书主要章节，从应用层到传输层、网络层、链路层、无线移动网络和网络安全，并安排必要的协议分析和实践。",null,null,null,"Adaptive",[book.References[0]]);
        var draft=(await planning.StartAsync(new(Guid.NewGuid(),0,input))).Value!;
        var preview=await planning.PrepareAsync(draft.Id,draft.Revision);
        if(!preview.Ok) { Save(report,"plan-prepare-failure.json",preview.Error!); return 1; }
        Save(report,"plan-preview.json",new { draft.Id,preview.Value!.Review.Model,preview.Value.Review.Budget,preview.Value.Review.MaxOutputTokens,sources=preview.Value.Review.Sources.Select(s=>new {s.Id,s.Label,characters=s.Text.Length}),totalCharacters=preview.Value.Review.Sources.Sum(s=>s.Text.Length) });
        var result=await planning.ProposeAsync(preview.Value,true);
        Save(report,"plan-result.json",result);
        if(!result.Ok) { Console.WriteLine("PLAN_FAILED "+result.Error?.Code); return 1; }
        var plan=result.Value!.Plan!; var candidate=plan.Candidate;
        var text=new StringBuilder("# "+candidate.Title+"\n\n依据用户指定505页教材生成的待确认计划，尚未开始学习。\n\n");
        text.AppendLine($"每周 {candidate.WeeklyMinutes} 分钟，共 {plan.Weeks.Count} 周；{candidate.Nodes.Count} 个学习单元。\n");
        foreach(var item in candidate.Defaults) text.AppendLine($"- 默认 {item.Field}：{item.Value}（{item.Reason}）");
        foreach(var week in plan.Weeks) { text.AppendLine($"\n## 第 {week.Index} 周（{week.EstimatedMinutes} 分钟）\n"); foreach(var key in week.NodeKeys) {var node=candidate.Nodes.Single(n=>n.Key==key); text.AppendLine($"- **{node.Title}**（{node.EstimatedMinutes} 分钟，{node.Type}）：{string.Join("；",node.Objectives)}");} }
        foreach(var item in candidate.Limitations) text.AppendLine("\n限制："+item);
        await File.WriteAllTextAsync(Path.Combine(output,"学习计划.md"),text.ToString());
        Console.WriteLine($"PLAN_COMPLETE nodes={candidate.Nodes.Count} weeks={plan.Weeks.Count}"); return 0;
    }
    private sealed class ReceiptPlan(IModelProvider inner,string output,string? replay=null) : IModelProvider
    {
        private int calls;
        public string Destination=>inner.Destination;
        public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request,CancellationToken ct)
        {
            var number=++calls; var started=DateTimeOffset.UtcNow;
            var replayed=number==1 && replay is not null;
            Result<ProviderReply> reply;
            if(replayed)
            {
                using var recorded=JsonDocument.Parse(await File.ReadAllTextAsync(replay!,ct));
                var prior=recorded.RootElement.GetProperty("reply").Deserialize<Result<ProviderReply>>(Wire.Options)!;
                reply=prior with { Value=prior.Value! with { Usage=null } };
            }
            else reply=await inner.SendAsync(request,ct);
            Save(output,$"plan-reply-{number}.json",new {started,ended=DateTimeOffset.UtcNow,request.Model,request.MaxOutputTokens,replayed,repairInstructions=request.PlanRepair?.Instructions,reply});
            return reply;
        }
    }
    public static async Task<int> Run(string[] args)
    {
        string Value(string name) => args[Array.IndexOf(args,name)+1];
        var output = Path.GetFullPath(Value("--evidence")); Directory.CreateDirectory(output);
        var pdf = Path.GetFullPath(Value("--pdf"));
        using var source = File.OpenRead(pdf);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(source));
        if (hash != "1FD9F5F23B8362A112A7A49C4154BFC0353F741831F9CCA6A154D86736DDE36D") throw new InvalidDataException("Only the user-authorized textbook is accepted");
        var credentials = new ReadOnlySettings(Value("--credentials")); var settings = new SettingsService(credentials);
        var knowledge = new KnowledgeStore(output); var libraryPath = Path.Combine(output,"library"); Directory.CreateDirectory(libraryPath);
        var library = await knowledge.CurrentAsync() ?? (await knowledge.SwitchAsync(new(Guid.NewGuid(),0,HostSelection.FromFolderPicker(libraryPath)))).Value!;
        var existing = (await knowledge.CollectionsAsync(library.Id)).SingleOrDefault();
        var imported = existing ?? (await knowledge.ImportCollectionAsync(HostSelection.FromFilePicker(pdf),library.Id)).Value!;
        using var client = DeepSeekProvider.CreateClient(); using var capClient = DeepSeekCapabilities.CreateClient();
        var capabilities = new DeepSeekCapabilities(output,credentials,capClient);
        var provider = new ReceiptOcr(new DeepSeekOcrProvider(credentials,client),output);
        var service = new AutomaticPdfScanService(knowledge,settings,capabilities,provider);
        using var cancellation = new CancellationTokenSource();
        var monitor = Task.Run(async () => { while (!cancellation.IsCancellationRequested) { if (File.Exists(Path.Combine(output,"cancel.request"))) { cancellation.Cancel(); return; } try { await Task.Delay(250,cancellation.Token); } catch (OperationCanceledException) { return; } } });
        var config = await settings.GetAsync();
        Save(output,"scope.json",new { hash, bytes=source.Length, imported.Collection.EntryCount,model=config.Model,batchSize=20,allPagesAuthorized=true,sourceReadOnly=true });
        try
        {
            var result = await service.RunAsync(library.Id,imported.Collection.Id,true,new InlineProgress<PdfScanProgress>(p => Save(output,"progress.json",p)),cancellation.Token);
            Save(output,"scan-result.json",new { result.Ok,result.Error,result.Value,ended=DateTimeOffset.UtcNow });
            Console.WriteLine(result.Ok ? "SCAN_COMPLETE" : "SCAN_FAILED " + result.Error?.Code); return result.Ok ? 0 : 1;
        }
        catch (OperationCanceledException) { Save(output,"scan-paused.json",new { state="paused",time=DateTimeOffset.UtcNow }); return 2; }
        finally { cancellation.Cancel(); await monitor; }
    }
    private static void Save(string output,string file,object data)
    { var path=Path.Combine(output,file); File.WriteAllText(path+".tmp",JsonSerializer.Serialize(data,Wire.Options)); File.Move(path+".tmp",path,true); }
    private sealed class ReceiptOcr(IOcrProvider inner,string output) : IOcrProvider
    {
        public async Task<Result<OcrPageResult>> RecognizeAsync(ApprovedOcrPage request,CancellationToken ct)
        {
            var started=DateTimeOffset.UtcNow;
            var result=await inner.RecognizeAsync(request,ct);
            await File.AppendAllTextAsync(Path.Combine(output,"usage.jsonl"),JsonSerializer.Serialize(new { request.Page.Page,request.Model,started,ended=DateTimeOffset.UtcNow,result.Ok,error=result.Error?.Code,usage=result.Value?.Usage },Wire.Options)+"\n");
            return result;
        }
    }
    private sealed class ReadOnlySettings(string root) : ISettingsStore,ICredentialReader
    {
        private SqliteConnection Open() { var db=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(root,"app.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false }.ToString()); db.Open(); return db; }
        public Task<SettingsDto> ReadAsync(CancellationToken ct)
        { using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="SELECT model,generation,consent,revision FROM settings WHERE id=1"; using var r=cmd.ExecuteReader(); if (!r.Read()) throw new IOException("Settings unavailable"); return Task.FromResult(new SettingsDto(r.GetString(0),Enum.Parse<GenerationMode>(r.GetString(1)),Enum.Parse<ConsentMode>(r.GetString(2)),true,r.GetInt64(3))); }
        public Task<Result<SettingsDto>> SaveAsync(Command<SaveSettings> command,CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> ReadKeyAsync(CancellationToken ct)
        { using var db=Open(); using var cmd=db.CreateCommand(); cmd.CommandText="SELECT protected_value FROM credentials WHERE id='deepseek'"; if (cmd.ExecuteScalar() is not byte[] encrypted) return Task.FromResult<string?>(null); var bytes=ProtectedData.Unprotect(encrypted,null,DataProtectionScope.CurrentUser); try { return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes)); } finally { CryptographicOperations.ZeroMemory(bytes); } }
    }
}
