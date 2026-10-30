namespace StudyHarness.HostProbe;
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Contains("--p36-performance")) return P36Performance.Run(Path.GetFullPath(Value(args, "--evidence")!), Value(args, "--mode")!).GetAwaiter().GetResult();
        if (args.Contains("--import-local-only"))
        {
            var root = Path.GetFullPath(Value(args, "--evidence")!);
            Directory.CreateDirectory(root);
            using (var pdf = UglyToad.PdfPig.PdfDocument.Open(Value(args, "--pdf")!)) Console.WriteLine($"PDF_PAGES={pdf.NumberOfPages}");
            var type = typeof(Infrastructure.KnowledgeStore).Assembly.GetType("StudyHarness.Infrastructure.CollectionStore")!;
            var store = Activator.CreateInstance(type, root, Guid.NewGuid())!;
            try
            {
                var result = type.GetMethod("Import")!.Invoke(store, [Value(args, "--pdf")!, CancellationToken.None, null]);
                File.WriteAllText(Path.Combine(root, "import.json"), System.Text.Json.JsonSerializer.Serialize(result, Contracts.Wire.Options));
                Console.WriteLine("LOCAL_IMPORT_OK"); return 0;
            }
            catch (System.Reflection.TargetInvocationException e)
            {
                File.WriteAllText(Path.Combine(root, "failure.txt"), e.InnerException!.ToString());
                Console.WriteLine(e.InnerException); return 2;
            }
        }
        if (args.Contains("--p30-context-replay")) return P30ContextReplay.Run(args);
        if (args.Contains("--p30-mapping-replay")) return P30MappingReplay.Run(args);
        if (args.Contains("--p31-real")) return P31RealHarness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--p31-feedback")) return P31RealHarness.Feedback(args).GetAwaiter().GetResult();
        if (args.Contains("--p30-book")) return P30Harness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--p25-preflight")) return P25Harness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--p24-benchmark")) return P24Harness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--p23-import")) return P23Harness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--p22-import")) return P22Harness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--book-plan-replay")) return BookPlanReliabilityHarness.Replay(args);
        if (args.Contains("--book-plan-reliability")) return BookPlanReliabilityHarness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--p14-lesson-preview")) return P14Harness.LessonPreview(args).GetAwaiter().GetResult();
        if (args.Contains("--p14-scan")) return P14Harness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--p14-plan")) return P14Harness.Plan(args).GetAwaiter().GetResult();
        if (args.Contains("--p14-inspect")) return P14Harness.Inspect(args).GetAwaiter().GetResult();
        if (args.Contains("--p9-source-audit")) return P9Harness.AuditSource(args);
        if (args.Contains("--p9-plan-audit")) return P9Harness.AuditPlanReply(args);
        if (args.Contains("--p9-fetch-audit")) return P9Harness.AuditFetch(args).GetAwaiter().GetResult();
        if (args.Contains("--p9-selection-audit")) return P9Harness.AuditSelection(args).GetAwaiter().GetResult();
        if (args.Contains("--p9-real")) return P9Harness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--plan-reliability")) return PlanReliabilityHarness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--p8-real")) return P8IntegrationHarness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--p8-directory"))
        {
            using var client = Infrastructure.OiDirectory.CreateClient();
            var directory = new Infrastructure.OiDirectory(client).ReadAsync(default).GetAwaiter().GetResult();
            var path = Value(args, "--evidence") ?? throw new ArgumentException("Evidence path required");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "oi-directory.json"), System.Text.Json.JsonSerializer.Serialize(directory, Contracts.Wire.Options));
            Console.WriteLine($"OI_DIRECTORY entries={directory.Entries.Count} sitemap={directory.SitemapCount} navigation={directory.NavigationCount}"); return 0;
        }
        if (args.Contains("--p8-benchmark")) return P8Benchmark.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--web-real")) return WebHarness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--p7-real")) return P7Harness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--p6-real")) return P6Harness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--credential-status")) return CredentialStatus.Run(Value(args, "--data-root"));
        if (args.Contains("--p5-real") || args.Contains("--p5-preview")) return P5Harness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--p4-seed") || args.Contains("--p4-real")) return P4Harness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--p3-seed") || args.Contains("--p3-real")) return P3Harness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--p2-seed") || args.Contains("--p2-real")) return P2Harness.Run(args).GetAwaiter().GetResult();
        Console.Error.WriteLine("USAGE_INVALID"); return 64;
    }
    private static string? Value(string[] args, string name)
    { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
}
