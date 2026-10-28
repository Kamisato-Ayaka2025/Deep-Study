using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Xml.Linq;
using StudyHarness.Application;

namespace StudyHarness.Infrastructure;

// PowerPoint only converts a private copy and renders static slides. Macros are disabled.
internal static class PowerPointBridge
{
    internal static T WithPresentation<T>(Stream source, string extension, Func<dynamic, string, T> action, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetTempPath(), "StudyHarness-slides-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "source" + extension);
        try
        {
            using (var copy = SafeKnowledgeFiles.CreateNew(directory, path)) source.CopyTo(copy);
            if (extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase))
            {
                RemoveExternalRelationships(path);
            }
            T? result = default; Exception? failure = null;
            var thread = new Thread(() =>
            {
                object? app = null, presentations = null, deck = null;
                int? security = null, alerts = null;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var type = Type.GetTypeFromProgID("PowerPoint.Application") ?? throw new PdfPreparationException("PPT_RENDER：旧PPT转换与幻灯片读图需要安装Microsoft PowerPoint；PPTX纯文字可直接读取。");
                    app = Activator.CreateInstance(type)!;
                    dynamic application = app;
                    security = (int)application.AutomationSecurity;
                    alerts = (int)application.DisplayAlerts;
                    application.AutomationSecurity = 3; // msoAutomationSecurityForceDisable
                    application.DisplayAlerts = 1; // ppAlertsNone
                    presentations = application.Presentations;
                    try { deck = ((dynamic)presentations).Open(path, -1, 0, 0); }
                    catch (COMException e) { throw new PdfPreparationException($"PPT_OPEN：PowerPoint无法打开本地渲染副本（错误码{e.HResult:X8}）；本批尚未发送，已有扫描进度保留。"); }
                    ct.ThrowIfCancellationRequested();
                    result = action((dynamic)deck, directory);
                }
                catch (Exception e) { failure = e; }
                finally
                {
                    try
                    {
                        if (deck is not null) { try { ((dynamic)deck).Close(); } finally { Marshal.FinalReleaseComObject(deck); } }
                        if (app is not null)
                        {
                            if (security.HasValue) ((dynamic)app).AutomationSecurity = security.Value;
                            if (alerts.HasValue) ((dynamic)app).DisplayAlerts = alerts.Value;
                            if (presentations is not null && (int)((dynamic)presentations).Count == 0) ((dynamic)app).Quit();
                        }
                    }
                    catch (Exception e) { failure ??= e; }
                    finally
                    {
                        if (presentations is not null) Marshal.FinalReleaseComObject(presentations);
                        if (app is not null) Marshal.FinalReleaseComObject(app);
                    }
                }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return result!;
        }
        finally { Directory.Delete(directory, true); }
    }
    internal static void RemoveExternalRelationships(string path)
    {
        // A renderer must not follow linked images, linked workbooks or remote media.
        using var package = ZipFile.Open(path, ZipArchiveMode.Update);
        foreach (var entry in package.Entries.Where(e => e.FullName.EndsWith(".rels", StringComparison.Ordinal)).ToArray())
        {
            if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("Presentation relationship budget");
            XDocument relationships;
            using (var input = entry.Open())
            using (var xml = System.Xml.XmlReader.Create(input, new() { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null }))
                relationships = XDocument.Load(xml);
            var external = relationships.Root!.Elements().Where(e => (string?)e.Attribute("TargetMode") == "External").ToArray();
            if (external.Length == 0) continue;
            var ids = external.Select(e => (string?)e.Attribute("Id")).ToHashSet();
            var ownerName = entry.FullName.Replace("/_rels/", "/", StringComparison.Ordinal)[..^5];
            var owner = package.GetEntry(ownerName);
            if (owner is not null)
            {
                XDocument document;
                using (var input = owner.Open())
                using (var xml = System.Xml.XmlReader.Create(input, new() { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 }))
                    document = XDocument.Load(xml);
                XNamespace relationNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
                foreach (var reference in document.Descendants().Attributes().Where(a => a.Name.Namespace == relationNamespace && ids.Contains(a.Value)).ToArray())
                {
                    // Removing only the .rels entry leaves invalid dangling r:id values.
                    // Hyperlink actions are metadata inside runs; remove the action, never its visible text.
                    if (reference.Parent?.Name.LocalName is "hlinkClick" or "hlinkHover" or "externalData") reference.Parent.Remove();
                    else reference.Remove();
                }
                owner.Delete();
                using var ownerOutput = package.CreateEntry(ownerName).Open(); document.Save(ownerOutput);
            }
            foreach (var link in external) link.Remove();
            var name = entry.FullName; entry.Delete();
            using var output = package.CreateEntry(name).Open(); relationships.Save(output);
        }
    }

    internal static IReadOnlyList<PresentationSlide> ReadLegacy(Stream source, CancellationToken ct) =>
        WithPresentation<IReadOnlyList<PresentationSlide>>(source, ".ppt", (deck, directory) =>
        {
            var path = Path.Combine(directory, "converted.pptx");
            deck.SaveAs(path, 24, 0);
            using var converted = SafeKnowledgeFiles.OpenRead(path, 512L * 1024 * 1024);
            return PresentationReader.Read(converted.Stream, ct);
        }, ct);

    internal static async Task<IReadOnlyList<RenderedPage>> RenderAsync(string path, Guid collection, string fingerprint, IReadOnlyList<int> pages, CancellationToken ct)
    {
        var result = new List<RenderedPage>();
        await ProduceAsync(path, collection, fingerprint, pages, (page, _) => { result.Add(page); return Task.CompletedTask; }, ct);
        return result;
    }
    internal static Task ProduceAsync(string path, Guid collection, string fingerprint, IReadOnlyList<int> pages,
        Func<RenderedPage, CancellationToken, Task> publish, CancellationToken ct) => Task.Run(() =>
    {
        using var source = SafeKnowledgeFiles.OpenRead(path, 512L * 1024 * 1024);
        if (Convert.ToHexString(SHA256.HashData(source.Stream)) != fingerprint) throw new InvalidDataException("Presentation changed; reimport before OCR");
        source.Stream.Position = 0;
        WithPresentation<int>(source.Stream, Path.GetExtension(path), (deck, directory) =>
        {
            object slides = deck.Slides; object setup = deck.PageSetup;
            try
            {
                double width = ((dynamic)setup).SlideWidth, height = ((dynamic)setup).SlideHeight;
                var scale = 2000 / Math.Max(width, height);
                foreach (var page in pages)
                {
                    ct.ThrowIfCancellationRequested();
                    object slide = ((dynamic)slides).Item(page);
                    try
                    {
                        var image = Path.Combine(directory, "page-" + page + ".png");
                        ((dynamic)slide).Export(image, "PNG", (int)(width * scale), (int)(height * scale));
                        var bytes = SafeKnowledgeFiles.ReadBytes(image);
                        if (bytes.Length is 0 or > 4194304) throw new PdfPreparationException("PPT_IMAGE_LIMIT：幻灯片图像超过4 MiB，未发送。");
                        publish(new(collection, fingerprint, page, "powerpoint-slide-2000-v1", bytes, Convert.ToHexString(SHA256.HashData(bytes))), ct).GetAwaiter().GetResult();
                        File.Delete(image);
                    }
                    finally { Marshal.FinalReleaseComObject(slide); }
                }
            }
            finally { Marshal.FinalReleaseComObject(slides); Marshal.FinalReleaseComObject(setup); }
            return 0;
        }, ct);
    }, ct);
}
