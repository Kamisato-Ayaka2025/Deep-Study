using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;
internal static class P14DesktopCheck
{
    private sealed class Capabilities : IModelCapabilitySource
    { public Task<ModelCapabilities> GetAsync(string m,CancellationToken ct=default) => Task.FromResult(new ModelCapabilities(m,1048576,16384,true,DateTimeOffset.UtcNow,true)); }
    private sealed class Ocr : IOcrProvider
    {
        public int Calls;
        public async Task<Result<OcrPageResult>> RecognizeAsync(ApprovedOcrPage request,CancellationToken ct)
        { Calls++; await Task.Delay(120,ct); return Result<OcrPageResult>.Success(new($"# 第{request.Page.Page}页\n网络由主机和链路组成。协议定义消息格式和通信规则。",[],new(100,20,120),request.Model,OcrService.PromptVersion)); }
    }
    public static async Task RunAsync(string root,string evidence)
    {
        var checks=new List<string>();
        void Check(bool ok,string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        async Task Wait(Func<bool> done) { for (var i=0;i<600;i++) { if(done()) return; await Task.Delay(50); } throw new TimeoutException(); }
        try
        {
            if (!File.Exists(Path.Combine(root,"p14-synthetic.marker"))) throw new IOException("Synthetic root required");
            Directory.CreateDirectory(evidence);
            var knowledge=new KnowledgeStore(root); var settings=new SettingsService(new SqliteSettingsStore(root)); var provider=new FakeProvider(); var ocr=new Ocr(); var cap=new Capabilities();
            var workspace=new WorkspaceViewModel(new(knowledge,settings,new(provider)),new(knowledge,settings,cap,ocr),new(knowledge,settings,cap,ocr));
            var planning=new PlanningViewModel(new(new SqlitePlanningStore(root),knowledge,settings,new(provider,cap),new PlanParser(),explicitSourcesOnly:true),sessionStore:new SqlitePlanningSessionStore(root));
            var shell=new ShellViewModel(settings,new(provider),new SafeLog(Path.Combine(root,"check.log")));
            var old=System.Windows.Application.Current.MainWindow; var window=new MainWindow(shell,workspace,planning);
            window.Show(); System.Windows.Application.Current.MainWindow=window; old.Close(); await window.Ready.Task;
            window.ImportExpander.IsExpanded=true; window.UpdateLayout();
            Check(!Walk(window).OfType<Expander>().Any(e => e.Header is string header && header.Contains("高级扫描")),"advanced-scan-panel-removed");
            Button Button(string id) => Walk(window).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b)==id);
            void Click(string id) => Button(id).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var path=Path.Combine(root,"scan.pdf"); var pausePath=Path.Combine(evidence,"paused-count.txt"); var restart=File.Exists(pausePath);
            var library=(await knowledge.CurrentAsync())!;
            if (File.Exists(Path.Combine(evidence,"removed.marker")))
            {
                Check(workspace.Materials.Count==0 && planning.LocalReferences.Count==0 && planning.PendingCollections.Count==0,"removed-binding-stays-empty-after-restart");
                Check(ocr.Calls==0,"removed-restart-no-paid-work");
                Check((await knowledge.CollectionsAsync(library.Id)).Count==2,"removed-restart-keeps-both-caches");
                await workspace.ImportAsync(HostSelection.FromPlanFilePicker(path));
                Check(ocr.Calls==0 && workspace.References.Count==1,"removed-restart-readd-zero-ocr");
                File.WriteAllText(Path.Combine(evidence,"retention.json"),JsonSerializer.Serialize(new { checks,failed=0,realNetwork=false },Wire.Options));
                System.Windows.Application.Current.Shutdown(0); return;
            }
            if (!restart)
            {
                var pdf=new UglyToad.PdfPig.Writer.PdfDocumentBuilder(); for(var i=0;i<45;i++) pdf.AddPage(150,200); await File.WriteAllBytesAsync(path,pdf.Build());
                var importing=workspace.ImportAsync(HostSelection.FromPlanFilePicker(path));
                await Wait(() => workspace.ScanCompleted>=3);
                Check(workspace.Busy && Button("CancelPdfScan").IsEnabled,"cancel-enabled-while-running");
                Click("CancelPdfScan"); await importing;
                Check(workspace.ScanCompleted>=3 && workspace.ScanCompleted<45,"cancel-retains-partial-progress");
                Check(workspace.Status.Contains("暂停") && !workspace.Busy,"paused-visible");
                Check(planning.PendingCollections.Count==1,"partial-book-not-ready-for-plan");
                Check(workspace.ScanPercent>0 && workspace.ScanPercent<100,"partial-progress-bar");
                File.WriteAllText(pausePath,workspace.ScanCompleted.ToString());
            }
            else
            {
                var saved=int.Parse(File.ReadAllText(pausePath));
                Check(ocr.Calls==0,"restart-no-automatic-paid-work");
                Check(workspace.ScanCompleted==saved,"restart-durable-page-count");
                Click("ContinueMaterialScan"); await Wait(() => !workspace.Busy && workspace.ScanCompleted==45);
                Check(ocr.Calls==45-saved,"resume-only-unfinished-pages");
                Check(workspace.ScanTotal==45 && workspace.ScanPercent==100,"full-progress-bar");
                Check(planning.PendingCollections.Count==0 && workspace.References.Count==1,"complete-book-auto-bound");
                var calls=ocr.Calls; Click("ContinuePdfScan"); await Wait(() => !workspace.Busy); Check(ocr.Calls==calls,"repeat-complete-zero-dispatch");
                planning.Request="学习网络协议"; planning.Goal="通过考试"; planning.Hours="10";
                Click("PreparePlan"); await Wait(() => !planning.Busy);
                Check(planning.CanSendPlan && planning.Preview.Length>0,"scanned-book-forms-preview-from-launcher-root: " + planning.Status);
                Check(planning.RecoveryStatus=="已自动保存","scanned-book-session-saved");
                var session=new SqlitePlanningSessionStore(root).Load();
                Check(session?.Request==planning.Request && session.References.Count==1,"scanned-book-durable-request-and-binding");
            }
            window.MainScroll.ScrollToVerticalOffset(600); window.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var file=File.Create(Path.Combine(evidence,restart ? "complete.png":"paused.png"))) encoder.Save(file);
            if(restart)
            {
                Click("RemoveMaterial"); await Wait(() => !workspace.Busy && workspace.Materials.Count==0);
                Check((await knowledge.CollectionsAsync(library.Id)).Single().Collection.ReadyCount==45,"remove-retains-complete-scan-cache");
                Check(File.Exists(path) && planning.LocalReferences.Count==0,"delete-retains-original-removes-binding");
                Check(!planning.CanSendPlan,"remove-invalidates-plan-preview");
                Check(new SqlitePlanningSessionStore(root).Load()?.References.Count==0,"remove-persists-empty-binding");
                var before = ocr.Calls;
                await workspace.ImportAsync(HostSelection.FromPlanFilePicker(path));
                Check(ocr.Calls==before && workspace.References.Count==1,"reimport-complete-zero-ocr");
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
                Click("RemoveMaterial"); await Wait(() => !workspace.Busy && workspace.Materials.Count==0);
                var partialPath=Path.Combine(root,"partial.pdf");
                var partialPdf=new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
                for(var page=0;page<8;page++) partialPdf.AddPage(160,210);
                await File.WriteAllBytesAsync(partialPath,partialPdf.Build());
                var importing=workspace.ImportAsync(HostSelection.FromPlanFilePicker(partialPath));
                await Wait(()=>workspace.ScanCompleted>=2);
                Check(Button("RemoveMaterial").IsEnabled,"delete-enabled-during-scan");
                Click("RemoveMaterial"); await importing;
                await Wait(()=>!workspace.Busy && workspace.Materials.Count==0);
                Check(workspace.Materials.Count==0 && workspace.References.Count==0,"delete-during-scan-no-late-binding");
                Check(File.Exists(path),"delete-during-scan-retains-original");
                var partial=workspace.Collections.Single(c=>c.Snapshot.Collection.Label=="partial.pdf").Snapshot.Collection;
                Check(partial.ReadyCount>0 && partial.ReadyCount<8,"remove-retains-partial-scan-cache");
                before=ocr.Calls;
                await workspace.ImportAsync(HostSelection.FromPlanFilePicker(partialPath));
                Check(ocr.Calls-before==8-partial.ReadyCount,"reimport-partial-only-missing-pages");
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ContextIdle);
                Click("RemoveMaterial"); await Wait(()=>!workspace.Busy && workspace.Materials.Count==0);
                File.WriteAllText(Path.Combine(evidence,"removed.marker"),"Both PDFs removed from draft; shared cache retained.");
            }
            File.WriteAllText(Path.Combine(evidence,restart ? "restart.json":"ui.json"),JsonSerializer.Serialize(new { checks,failed=0,realNetwork=false },Wire.Options));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch(Exception ex) { File.WriteAllText(Path.Combine(evidence,"failure.json"),JsonSerializer.Serialize(new { checks,error=ex.ToString() },Wire.Options)); System.Windows.Application.Current.Shutdown(1); }
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    { yield return root; for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) foreach(var child in Walk(VisualTreeHelper.GetChild(root,i))) yield return child; }
}
