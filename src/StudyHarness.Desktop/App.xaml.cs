using System.IO;
using System.Windows;
using StudyHarness.Application;
using StudyHarness.Infrastructure;
namespace StudyHarness.Desktop;
public partial class App : System.Windows.Application
{
    private Mutex? instance;
    private TrayLifetime? tray;
    internal bool TryHideToTray(Window window) => tray?.TryHide(window) == true;
    internal TrayLifetime Tray => tray ?? throw new InvalidOperationException("Tray not initialized");
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Composition root only; views never access the database or credentials.
        var locationCheck = e.Args.Contains("--p29-selfcheck");
        var locationEvidence = locationCheck ? e.Args[Array.IndexOf(e.Args, "--evidence") + 1] : null;
        var environmentRoot = Environment.GetEnvironmentVariable("STUDYHARNESS_DATA_ROOT");
        if (locationCheck && (environmentRoot is null || !File.Exists(Path.Combine(environmentRoot, "p29-synthetic.marker"))))
        { Shutdown(1); return; }
        var locations = new DataDirectorySettings(AppContext.BaseDirectory,
            locationCheck ? Path.Combine(locationEvidence!, "preferences") : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), environmentRoot);
        var root = "";
        Window? transferWindow = null;
        System.ComponentModel.CancelEventHandler blockTransferClose = (_, args) => args.Cancel = true;
        try
        {
            root = locations.Resolve();
            instance = new Mutex(true, DataDirectorySettings.MutexName(root), out var owns);
            if (!owns) { MessageBox.Show("此数据目录已有Study Harness在运行。", "Study Harness"); Shutdown(1); return; }
            if (locations.Read()?.CopyPending == true)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                transferWindow = new Window { Title = "Deep Study · 迁移数据", Width = 440, Height = 150,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize,
                    Content = new System.Windows.Controls.TextBlock { Text = "正在复制并校验学习数据，请稍候。\n原目录保留，完成后打开课程。", Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap } };
                transferWindow.Closing += blockTransferClose;
                transferWindow.Show();
            }
            await locations.ApplyPendingAsync();
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "knowledge"));
            var store = new SqliteSettingsStore(root);
            var webSources = new DeepSeekWebSources(store, DeepSeekWebSources.CreateClient());
            var knowledge = new KnowledgeStore(root, webSources);
            var service = new SettingsService(store, knowledge);
            var capabilities = new DeepSeekCapabilities(root, store, DeepSeekCapabilities.CreateClient());
            var vm = new ShellViewModel(service, new ModelEgress(new DeepSeekProvider(store, DeepSeekProvider.CreateClient())), new SafeLog(Path.Combine(root, "logs", "operations.jsonl")), Path.GetFullPath(root), locations, new CacheCatalog(root));
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
            var generation = new GenerationService(plans, learning, learning, knowledge, service, new ModelEgress(provider, fixture ? null : capabilities), new TheoryLessonParser(), e.Args.Contains("--p4-selfcheck"), new BookKnowledgeCache(root));
            var planning = new PlanningViewModel(new PlanningService(plans, knowledge, service,
                new ModelEgress(new DeepSeekProvider(store, DeepSeekProvider.CreateClient()), capabilities), new PlanParser(), learning, explicitSourcesOnly: true, knowledgeCache: new BookKnowledgeCache(root)), learning,
                new WebLearningService(webSources, knowledge, knowledge, Path.Combine(root, "knowledge"), new OiDirectory(OiDirectory.CreateClient())), new SqlitePlanningSessionStore(root));
            if (await knowledge.CurrentAsync() is null)
                await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(Path.Combine(root, "knowledge"))));
            var labs = new LabService(plans, learning, learning, knowledge, service,
                new ModelEgress(new DeepSeekProvider(store, DeepSeekProvider.CreateClient()), capabilities), new LabZipExporter());
            MainWindow = new MainWindow(vm, workspace, planning, generation, root, labs,
                () => new TutoringSession(plans, learning, learning, service, new ModelEgress(new DeepSeekProvider(store, DeepSeekProvider.CreateClient()))),
                new TutoringFiles(), new SqliteSubjectLifecycle(root));
            MainWindow.Show();
            if (transferWindow is not null) { transferWindow.Closing -= blockTransferClose; transferWindow.Close(); transferWindow = null; }
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            tray = new TrayLifetime(MainWindow, () => Shutdown());
            await ((MainWindow)MainWindow).Ready.Task;
            if (e.Args.Contains("--p35-selfcheck"))
            {
                await P35DesktopCheck.RunAsync(root, e.Args[Array.IndexOf(e.Args, "--evidence") + 1]); return;
            }
            if (e.Args.Contains("--p34-selfcheck"))
            {
                await P34DesktopCheck.RunAsync(root, e.Args[Array.IndexOf(e.Args, "--evidence") + 1]); return;
            }
            if (e.Args.Contains("--p33-selfcheck"))
            {
                await P33DesktopCheck.RunAsync(root, e.Args[Array.IndexOf(e.Args, "--evidence") + 1]); return;
            }
            if (e.Args.Contains("--p32-selfcheck"))
            {
                await P32DesktopCheck.RunAsync(root, e.Args[Array.IndexOf(e.Args, "--evidence") + 1]); return;
            }
            if (e.Args.Contains("--p31-selfcheck"))
            {
                await P31DesktopCheck.RunAsync(root, e.Args[Array.IndexOf(e.Args, "--evidence") + 1]); return;
            }
            if (e.Args.Contains("--p30-selfcheck"))
            {
                await P30DesktopCheck.RunAsync(root, e.Args[Array.IndexOf(e.Args, "--evidence") + 1]); return;
            }
            if (locationCheck)
            {
                await P29DesktopCheck.RunAsync(root, locationEvidence!, locations); return;
            }
            if (e.Args.Contains("--p23-selfcheck"))
            {
                await P23DesktopCheck.RunAsync(root, e.Args[Array.IndexOf(e.Args, "--evidence") + 1]); return;
            }
            if (e.Args.Contains("--p17-selfcheck"))
            {
                await P17DesktopCheck.RunAsync(root, e.Args[Array.IndexOf(e.Args, "--evidence") + 1]); return;
            }
            if (e.Args.Contains("--p16-selfcheck"))
            {
                await P16DesktopCheck.RunAsync(root, e.Args[Array.IndexOf(e.Args, "--evidence") + 1], e.Args.Contains("--baseline")); return;
            }
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
            if (locationCheck)
            { File.WriteAllText(Path.Combine(locationEvidence!, "startup-failure.txt"), ex.ToString()); Shutdown(1); return; }
            if (transferWindow is not null) { transferWindow.Closing -= blockTransferClose; transferWindow.Close(); }
            try
            {
                if (locations.Read()?.PreviousPath is { } previous && MessageBox.Show(
                    "所选数据目录未能打开或迁移未完成，原数据已保留。是否撤销目录更改，下次启动恢复原目录？",
                    "Deep Study · 数据目录", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                { locations.CancelChange(previous); Shutdown(1); return; }
            }
            catch (Exception) { /* Keep the original startup failure; never fall back to an empty database. */ }
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
    protected override void OnExit(ExitEventArgs e) { tray?.Dispose(); instance?.Dispose(); base.OnExit(e); }
}
