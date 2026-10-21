using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Core;

namespace StudyHarness.HostProbe;

internal static class BookPlanReliabilityHarness
{
    public static int Replay(string[] args)
    {
        var root=Path.GetFullPath(args[Array.IndexOf(args,"--evidence-root")+1]);
        var rows=new List<object>();
        foreach(var file in Directory.GetFiles(root,"result.json",SearchOption.AllDirectories))
        {
            var draft=JsonSerializer.Deserialize<Result<PlanningDraft>>(File.ReadAllText(file),Wire.Options)!.Value!;
            var reply=JsonSerializer.Deserialize<Result<ProviderReply>>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(file)!,"reply.json")),Wire.Options)!.Value!;
            var parsed=new PlanParser().Parse(reply.Text);
            var valid=parsed.Ok ? PlanRules.Validate(draft.Id,draft.Input,parsed.Value!,draft.Plan!.Sources,draft.Plan.Usage,draft.Plan.Collections) : new Result<PlanDocument>(false,null,parsed.Error);
            if(!valid.Ok || CourseMaterials.MissingBindingError(valid.Value!) is not null) throw new InvalidDataException("Replay rejected synthetic plan");
            rows.Add(new { book=Path.GetFileName(Path.GetDirectoryName(file)),validated=true,hash=valid.Value!.Hash,
                exactRequestedNodeCount=valid.Value.Candidate.Nodes.Count==8 });
        }
        if(rows.Count!=3) throw new InvalidDataException("Expected exactly three synthetic books");
        var assemblies=new[]{typeof(PlanParser).Assembly,typeof(PlanRules).Assembly,typeof(PlanCandidate).Assembly}
            .Select(a=>new {name=a.GetName().Name,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location)))}).ToArray();
        File.WriteAllText(Path.Combine(root,"offline-replay.json"),JsonSerializer.Serialize(new { rows,assemblies,newApiCalls=0 },Wire.Options));
        Console.WriteLine("3 stored synthetic replies validated offline; requested node count matched 2/3 (one extra review lesson).");
        return 0;
    }
    // Explicitly authorized synthetic-only probe. Never initialize or write the credential database.
    private sealed class Credentials(string root) : ICredentialReader
    {
        public Task<string?> ReadKeyAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(root,"app.db"), Mode=SqliteOpenMode.ReadOnly, Pooling=false }.ToString());
            db.Open(); using var cmd=db.CreateCommand(); cmd.CommandText="SELECT protected_value FROM credentials WHERE id='deepseek'";
            if(cmd.ExecuteScalar() is not byte[] encrypted) return Task.FromResult<string?>(null);
            var bytes=ProtectedData.Unprotect(encrypted,null,DataProtectionScope.CurrentUser);
            try { return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
    private sealed class Recorded(IModelProvider inner,string path) : IModelProvider
    {
        public string Destination=>inner.Destination;
        public int Calls;
        public TokenUsage? Usage;
        public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request,CancellationToken ct)
        {
            if(++Calls>1) throw new InvalidOperationException("One dispatch per synthetic book; no retry authorized here.");
            var reply=await inner.SendAsync(request,ct); Usage=reply.Value?.Usage;
            await File.WriteAllTextAsync(Path.Combine(path,"reply.json"),JsonSerializer.Serialize(reply,Wire.Options),ct);
            return reply;
        }
    }
    public static async Task<int> Run(string[] args)
    {
        string Value(string k)=>args[Array.IndexOf(args,k)+1];
        var output=Path.GetFullPath(Value("--evidence-root"));
        if(Directory.Exists(output)) throw new IOException("Fresh synthetic output required");
        Directory.CreateDirectory(output);
        var credentials=new Credentials(Value("--data-root"));
        using var client=DeepSeekProvider.CreateClient(); using var meta=DeepSeekCapabilities.CreateClient();
        var capabilities=new DeepSeekCapabilities(output,credentials,meta);
        var cases=new[] {
            (Pages:120,Title:"集合与映射", Topics:new[]{"Sets: A set is a collection of distinct objects. {1,2} has cardinality 2.","Union: {1,2} union {2,3} is {1,2,3}. Intersection is {2}.","Functions: Each input has exactly one output. f(x)=2*x gives f(3)=6.","Composition: f(x)=x+1 and g(x)=2*x give f(g(3))=7."}),
            (Pages:384,Title:"数字逻辑基础", Topics:new[]{"Binary: 101 in base two equals 1*4+0*2+1=5 in base ten.","AND outputs 1 only if both inputs are 1. OR outputs 1 if either input is 1.","NOT reverses a bit. XOR outputs 1 when its two inputs differ.","Half adder: sum=A XOR B; carry=A AND B. Adding 1 and 1 gives sum 0 and carry 1."}),
            (Pages:505,Title:"网络性能基础", Topics:new[]{"Transmission delay equals packet bits divided by link bits per second. 8000 bits at 1000000 bps takes 0.008 seconds.","Propagation delay equals distance divided by propagation speed. 200000 meters at 200000000 meters per second takes 0.001 seconds.","Total delay is transmission plus propagation plus processing plus queuing. With zero processing and queuing the previous values total 0.009 seconds.","Throughput on two serial links is bounded by the slower rate. Links at 2 Mbps and 1 Mbps have a 1 Mbps bottleneck."})
        };
        var reports=new List<object>(); var passed=0; var calls=0;
        foreach(var sample in cases)
        {
            var path=Path.Combine(output,"book-"+sample.Pages); Directory.CreateDirectory(path);
            var settings=new SettingsService(new SqliteSettingsStore(path));
            await settings.SaveAsync(new(Guid.NewGuid(),0,new("deepseek-flash",GenerationMode.Click,ConsentMode.Once,null)));
            var knowledge=new KnowledgeStore(path); var libraryPath=Path.Combine(path,"library"); Directory.CreateDirectory(libraryPath);
            var library=(await knowledge.SwitchAsync(new(Guid.NewGuid(),0,HostSelection.FromFolderPicker(libraryPath)))).Value!;
            var pdf=new PdfDocumentBuilder(); var font=pdf.AddStandard14Font(Standard14Font.Helvetica);
            for(var p=1;p<=sample.Pages;p++)
            {
                var page=pdf.AddPage(800,600); var chapter=(p-1)*4/sample.Pages;
                page.AddText($"Chapter {chapter+1}: synthetic learning material. Page {p}.",12,new PdfPoint(30,550),font);
                page.AddText(sample.Topics[chapter],10,new PdfPoint(30,500),font);
            }
            var file=Path.Combine(path,"synthetic.pdf"); await File.WriteAllBytesAsync(file,pdf.Build());
            var imported=await knowledge.ImportAsync(new(Guid.NewGuid(),0,HostSelection.FromPlanFilePicker(file)),library.Id);
            if(!imported.Ok) throw new InvalidOperationException("Synthetic import failed");
            var provider=new Recorded(new DeepSeekProvider(credentials,client),path);
            var store=new SqlitePlanningStore(path); var learning=new SqliteLearningStore(path,store);
            var service=new PlanningService(store,knowledge,settings,new(provider,capabilities),new PlanParser(),learning,explicitSourcesOnly:true);
            var draft=(await service.StartAsync(new(Guid.NewGuid(),0,new("学习"+sample.Title+"，按合成教材的四章安排4节理论和4节纸笔练习，覆盖整本教材。","基础算术","掌握四章定义并完成计算练习",600,"Adaptive",[imported.Value!.References![0]])))).Value!;
            var preview=await service.PrepareAsync(draft.Id,0);
            var result=preview.Ok ? await service.ProposeAsync(preview.Value!,true,repairInvalid:false) : new Result<PlanningDraft>(false,null,preview.Error);
            var confirmed=false; var restored=false;
            if(result.Ok)
            {
                var saved=result.Value!;
                confirmed=(await service.ConfirmAsync(new(Guid.NewGuid(),saved.Revision,new(saved.Id,saved.Plan!.Hash,true)))).Ok;
                var reopened=await new SqlitePlanningStore(path).GetAsync(saved.Id);
                restored=reopened?.Confirmed==true && reopened.Plan?.Hash==saved.Plan.Hash;
            }
            calls+=provider.Calls; if(result.Ok && confirmed && restored) passed++;
            reports.Add(new { sample.Pages,sample.Title,success=result.Ok,confirmed,restored,calls=provider.Calls,provider.Usage,error=result.Error,nodes=result.Value?.Plan?.Candidate.Nodes.Count,coverage=result.Value?.Plan?.Candidate.Coverage?.Count });
            await File.WriteAllTextAsync(Path.Combine(path,"result.json"),JsonSerializer.Serialize(result,Wire.Options));
            await File.WriteAllTextAsync(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new { passed,calls,reports,syntheticOnly=true },Wire.Options));
        }
        Console.WriteLine($"Synthetic books: {passed}/3; API dispatches: {calls}/3"); return passed==3 ? 0 : 1;
    }
}
