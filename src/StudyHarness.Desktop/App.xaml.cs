using System.IO;
using System.Windows;
using StudyHarness.Application;
using StudyHarness.Infrastructure;
namespace StudyHarness.Desktop;
public partial class App : System.Windows.Application
{
    private Mutex? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Composition root only; views never access the database or credentials.
        var root = Environment.GetEnvironmentVariable("STUDYHARNESS_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StudyHarness");
        try
        {
            root = Path.GetFullPath(root);
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "knowledge"));
            var mutexName = "Local\\StudyHarness-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())));
            instance = new Mutex(true, mutexName, out var owns);
            if (!owns) { MessageBox.Show("此数据目录已有Study Harness在运行。", "Study Harness"); Shutdown(1); return; }
            var store = new SqliteSettingsStore(root);
            var webSources = new DeepSeekWebSources(store, DeepSeekWebSources.CreateClient());
            var knowledge = new KnowledgeStore(root, webSources);
            var service = new SettingsService(store, knowledge);
            var capabilities = new DeepSeekCapabilities(root, store, DeepSeekCapabilities.CreateClient());
            var vm = new ShellViewModel(service, new ModelEgress(new DeepSeekProvider(store, DeepSeekProvider.CreateClient())), new SafeLog(Path.Combine(root, "logs", "operations.jsonl")), Path.GetFullPath(root));
            var workspace = new WorkspaceViewModel(new WorkspaceService(knowledge, service,
                new ModelEgress(new DeepSeekProvider(store, DeepSeekProvider.CreateClient()))),
                new OcrService(knowledge, service, capabilities, new DeepSeekOcrProvider(store, DeepSeekProvider.CreateClient())),
                new AutomaticPdfScanService(knowledge, service, capabilities, new DeepSeekOcrProvider(store, DeepSeekProvider.CreateClient())));
            var plans = new SqlitePlanningStore(root);
            var learning = new SqliteLearningStore(root, plans);
            await learning.RecoverAsync();
            await learning.RecoverLabsAsync();
            await learning.SetModeAsync(StudyHarness.Contracts.GenerationMode.Click);
            var fixture = e.Args.Contains("--p4-demo") || e.Args.Contains("--p4-selfcheck");
            if (fixture && !File.Exists(Path.Combine(root, "p4-synthetic.marker"))) throw new InvalidOperationException("Synthetic data root required");
            IModelProvider provider = fixture ? new TheoryFixtureProvider(e.Args.Contains("--restart-check") ? "重生成检查" : "") : new DeepSeekTheoryProvider(store, DeepSeekProvider.CreateClient());
            var generation = new GenerationService(plans, learning, learning, knowledge, service, new ModelEgress(provider, fixture ? null : capabilities), new TheoryLessonParser(), e.Args.Contains("--p4-selfcheck"));
            var planning = new PlanningViewModel(new PlanningService(plans, knowledge, service,
                new ModelEgress(new DeepSeekProvider(store, DeepSeekProvider.CreateClient()), capabilities), new PlanParser(), learning, explicitSourcesOnly: true), learning,
                new WebLearningService(webSources, knowledge, knowledge, Path.Combine(root, "knowledge"), new OiDirectory(OiDirectory.CreateClient())), new SqlitePlanningSessionStore(root));
            if (await knowledge.CurrentAsync() is null)
                await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(Path.Combine(root, "knowledge"))));
            var labs = new LabService(plans, learning, learning, knowledge, service,
                new ModelEgress(new DeepSeekProvider(store, DeepSeekProvider.CreateClient()), capabilities), new LabZipExporter());
            MainWindow = new MainWindow(vm, workspace, planning, generation, root, labs,
                () => new TutoringSession(plans, learning, learning, service, new ModelEgress(new DeepSeekProvider(store, DeepSeekProvider.CreateClient()))),
                new TutoringFiles(), new SqliteSubjectLifecycle(root));
            MainWindow.Show();
            await ((MainWindow)MainWindow).Ready.Task;
            if (e.Args.Contains("--p14-selfcheck"))
            {
                var evidence = e.Args[Array.IndexOf(e.Args,"--evidence")+1];
                await P14DesktopCheck.RunAsync(root,evidence); return;
            }
            if (e.Args.Contains("--p12-selfcheck"))
            {
                var index = Array.IndexOf(e.Args, "--evidence");
                if (index < 0 || index + 1 >= e.Args.Length) throw new ArgumentException("Evidence directory required");
                await Task.Delay(700);
                await P12DesktopCheck.RunAsync(root, e.Args[index + 1]); return;
            }
            if (e.Args.Contains("--hybrid-selfcheck"))
            {
                var index = Array.IndexOf(e.Args, "--evidence");
                if (index < 0 || index + 1 >= e.Args.Length) throw new ArgumentException("Evidence directory required");
                await HybridDesktopCheck.RunAsync(root, e.Args[index + 1]); return;
            }
            if (e.Args.Contains("--p11-selfcheck"))
            {
                var index = Array.IndexOf(e.Args, "--evidence");
                if (index < 0 || index + 1 >= e.Args.Length) throw new ArgumentException("Evidence directory required");
                await P11DesktopCheck.RunAsync(planning, (MainWindow)MainWindow, root, e.Args[index + 1], e.Args.Contains("--baseline")); return;
            }
            if (e.Args.Contains("--p10-selfcheck"))
            {
                var index = Array.IndexOf(e.Args, "--evidence");
                if (index < 0 || index + 1 >= e.Args.Length) throw new ArgumentException("Evidence directory required");
                await P10DesktopCheck.RunAsync(root, e.Args[index + 1]); return;
            }
            if (e.Args.Contains("--p9-selfcheck"))
            {
                var index = Array.IndexOf(e.Args, "--evidence");
                if (index < 0 || index + 1 >= e.Args.Length) throw new ArgumentException("Evidence directory required");
                await P9DesktopCheck.RunAsync(root, e.Args[index + 1]); return;
            }
            if (e.Args.Contains("--p8-selfcheck"))
            {
                var index = Array.IndexOf(e.Args, "--evidence");
                if (index < 0 || index + 1 >= e.Args.Length) throw new ArgumentException("Evidence directory required");
                await P8DesktopCheck.RunAsync(root, e.Args[index + 1]); return;
            }
            if (e.Args.Contains("--web-selfcheck"))
            {
                var index = Array.IndexOf(e.Args, "--evidence");
                if (index < 0 || index + 1 >= e.Args.Length) throw new ArgumentException("Evidence directory required");
                await WebDesktopCheck.RunAsync(root, e.Args[index + 1]);
                return;
            }
            if (e.Args.Contains("--p7-selfcheck"))
            {
                var index = Array.IndexOf(e.Args, "--evidence");
                if (index < 0 || index + 1 >= e.Args.Length) throw new ArgumentException("Evidence directory required");
                await P7DesktopCheck.RunAsync(root, e.Args[index + 1]);
                return;
            }
            if (e.Args.Contains("--p6-selfcheck"))
            {
                var index = Array.IndexOf(e.Args, "--evidence");
                if (index < 0 || index + 1 >= e.Args.Length) throw new ArgumentException("Evidence directory required");
                await P6DesktopCheck.RunAsync(root, e.Args[index + 1], e.Args.Contains("--restart-check"));
                return;
            }
            if (e.Args.Contains("--p5-selfcheck"))
            {
                var index = Array.IndexOf(e.Args, "--evidence");
                if (index < 0 || index + 1 >= e.Args.Length) throw new ArgumentException("Evidence directory required");
                await P5DesktopCheck.RunAsync(root, e.Args[index + 1]);
                return;
            }
            if (e.Args.Contains("--p4-selfcheck"))
            {
                var index = Array.IndexOf(e.Args, "--evidence");
                if (index < 0 || index + 1 >= e.Args.Length) throw new ArgumentException("Evidence directory required");
                await P4DesktopCheck.RunAsync(generation, root, e.Args[index + 1], e.Args.Contains("--restart-check"));
            }
        }
        catch (Exception ex)
        {
            if (e.Args.Contains("--p14-selfcheck") && File.Exists(Path.Combine(root,"p14-synthetic.marker")))
            {
                File.WriteAllText(Path.Combine(root,"startup-failure.txt"),ex.ToString()); Shutdown(1); return;
            }
            // Never display exception text, which can contain file paths or secret values.
            MessageBox.Show("IO_FAILURE：无法打开本地数据。请检查数据目录权限或数据库版本。未继续写入，请保留原数据后重试。",
                "Study Harness 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
