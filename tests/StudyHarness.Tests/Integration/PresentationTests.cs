using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class PresentationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-presentation-" + Guid.NewGuid().ToString("N"));
    private async Task<(KnowledgeStore Store, Guid Library, string Path)> Setup()
    {
        var library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        var store = new KnowledgeStore(root);
        var id = (await store.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(library)))).Value!.Id;
        return (store, id, Path.Combine(root, "slides.pptx"));
    }
    private static void Fixture(string path, bool corrupt = false)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Part(string name, string xml) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(xml); }
        const string ns = "xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
        Part("ppt/presentation.xml", corrupt ? "<!DOCTYPE p [<!ENTITY x SYSTEM 'file:///C:/secret'>]><p>&x;</p>" : $"<p:presentation {ns} xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><p:sldIdLst><p:sldId r:id=\"r2\"/><p:sldId r:id=\"r1\"/><p:sldId r:id=\"r3\"/></p:sldIdLst></p:presentation>");
        Part("ppt/_rels/presentation.xml.rels", "<Relationships><Relationship Id=\"r1\" Target=\"slides/slide1.xml\" Type=\"slide\"/><Relationship Id=\"r2\" Target=\"slides/slide2.xml\" Type=\"slide\"/><Relationship Id=\"r3\" Target=\"slides/slide3.xml\" Type=\"slide\"/></Relationships>");
        string Shape(string title, string text) => $"<p:sp><p:nvSpPr><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>{title}</a:t></a:r></a:p></p:txBody></p:sp><p:sp><p:txBody><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp>";
        Part("ppt/slides/slide2.xml", $"<p:sld {ns}><p:cSld><p:spTree>{Shape("TCP reliability", "TCP retransmission starts after timeout.")}</p:spTree></p:cSld></p:sld>");
        Part("ppt/slides/slide1.xml", $"<p:sld {ns}><p:cSld><p:spTree>{Shape("TCP reliability", "The receiver acknowledges sequence numbers.")}<p:cxnSp/><a:tbl><a:tr><a:tc><a:txBody><a:p><a:r><a:t>Sequence</a:t></a:r></a:p></a:txBody></a:tc></a:tr><a:tr><a:tc><a:txBody><a:p><a:r><a:t>42</a:t></a:r></a:p></a:txBody></a:tc></a:tr></a:tbl></p:spTree></p:cSld></p:sld>");
        Part("ppt/slides/slide3.xml", $"<p:sld {ns}><p:cSld><p:spTree>{Shape("Wireless channels", "Wireless modulation uses radio frequencies.")}</p:spTree></p:cSld></p:sld>");
        Part("ppt/slides/_rels/slide2.xml.rels", "<Relationships><Relationship Id=\"notes\" Target=\"../notesSlides/notesSlide1.xml\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide\"/><Relationship Id=\"external\" Target=\"https://invalid.test/never-fetch\" TargetMode=\"External\" Type=\"image\"/></Relationships>");
        Part("ppt/notesSlides/notesSlide1.xml", $"<p:notes {ns}><p:sp><p:nvSpPr><p:nvPr><p:ph type=\"body\"/></p:nvPr></p:nvSpPr><a:p><a:r><a:t>Teacher note: distinguish congestion from corruption.</a:t></a:r></a:p></p:sp></p:notes>");
    }
    [Fact]
    public async Task ImportPreservesOrderTablesNotesAndOriginalAndReusesCache()
    {
        var (store, library, path) = await Setup(); Fixture(path);
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var imported = await store.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromPlanFilePicker(path)), library);
        Assert.True(imported.Ok); Assert.All(imported.Value!.Files, f => Assert.True(f.Ok));
        var snapshot = Assert.Single(await store.CollectionsAsync(library));
        Assert.Equal("presentation", snapshot.Collection.SourceKind); Assert.Equal(3, snapshot.Collection.EntryCount);
        Assert.Equal(2, snapshot.Collection.ReadyCount);
        var text = new List<string>();
        foreach (var reference in snapshot.References) text.Add((await store.ResolveAsync(reference)).Text!);
        Assert.Contains("retransmission", text[0]); Assert.Contains(text, t => t.Contains("Teacher note"));
        Assert.Contains(text, t => t.Contains("| Sequence |")); Assert.Contains(text, t => t.Contains("| 42 |"));
        var scan = await store.ScanStateAsync(library, snapshot.Collection.Id, default); Assert.Equal(new[] { 2 }, scan.PendingPages);
        var repeat = (await store.ImportCollectionAsync(HostSelection.FromFilePicker(path), library)).Value!;
        Assert.Equal(snapshot.Collection.Version, repeat.Collection.Version);
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
    }
    [Fact]
    public async Task FrozenLessonKeepsContinuationWithoutUnrelatedSlidesAndHonorsBudget()
    {
        var (store, library, path) = await Setup(); Fixture(path);
        var snapshot = (await store.ImportCollectionAsync(HostSelection.FromFilePicker(path), library)).Value!;
        var binding = new PlanCollection(library, snapshot.Manifest, snapshot.References, snapshot.Structure);
        var keys = snapshot.Manifest.Entries.Select(e => e.Key).ToArray();
        var result = await store.RetrieveFrozenAsync(binding, "TCP retransmission", keys, 12000);
        Assert.Contains(result, r => r.Text.Contains("acknowledges"));
        Assert.Contains(result, r => r.Text.Contains("Teacher note"));
        Assert.DoesNotContain(result, r => r.Text.Contains("Wireless"));
        var node = new PlannedNode("n1", "TCP retransmission", "theory", ["Explain TCP retransmission"], [], 30, null);
        var candidate = new PlanCandidate("Networks", "Beginner", "Understand TCP", 120, "Adaptive", [], [], [], [], [node], 2,
            Coverage: keys.Select(key => new PlanCoverage("d1/" + key, "included", ["n1"], null)).ToArray());
        var plan = new PlanDocument(candidate, [], [], "frozen-test-plan", null, Collections: [binding]);
        var selected = await CourseMaterials.SelectAsync(store, plan, node, default);
        Assert.Equal(result.Select(r => r.Reference), selected.Select(s => s.Reference));
        Assert.All(selected, source => Assert.True(CourseMaterials.Allowed(plan, node, source)));
        Assert.Equal(result, await new KnowledgeStore(root).RetrieveFrozenAsync(binding, "TCP retransmission", keys, 12000));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RetrieveFrozenAsync(binding, "TCP retransmission", keys, 20));
    }
    [Fact]
    public async Task RenderingCopyRemovesExternalActionsWithoutDanglingReferencesOrTextLoss()
    {
        var (_, _, path) = await Setup();
        const string owner = "ppt/slides/slide1.xml";
        const string rels = "ppt/slides/_rels/slide1.xml.rels";
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry(owner).Open()))
                writer.Write("""<a:p xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><a:r><a:rPr><a:hlinkClick r:id="external"/><a:hlinkHover r:id="external"/></a:rPr><a:t>Visible linked explanation</a:t></a:r><a:blip r:embed="internal" r:link="externalImage"/></a:p>""");
            using (var writer = new StreamWriter(zip.CreateEntry(rels).Open()))
                writer.Write("""<Relationships><Relationship Id="external" Target="https://invalid.test" TargetMode="External" Type="hyperlink"/><Relationship Id="externalImage" Target="https://invalid.test/image" TargetMode="External" Type="image"/><Relationship Id="internal" Target="../media/image1.png" Type="image"/></Relationships>""");
        }
        var copy = Path.Combine(root, "render-copy.pptx"); File.Copy(path, copy);
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        PowerPointBridge.RemoveExternalRelationships(copy);
        using var result = ZipFile.OpenRead(copy);
        using var input = result.GetEntry(owner)!.Open(); var xml = XDocument.Load(input);
        Assert.Contains("Visible linked explanation", xml.ToString());
        Assert.DoesNotContain(xml.Descendants(), e => e.Name.LocalName is "hlinkClick" or "hlinkHover");
        Assert.DoesNotContain(xml.Descendants().Attributes(), a => a.Value is "external" or "externalImage");
        Assert.Contains(xml.Descendants().Attributes(), a => a.Name.LocalName == "embed" && a.Value == "internal");
        using var relationships = result.GetEntry(rels)!.Open();
        Assert.Equal("internal", (string?)Assert.Single(XDocument.Load(relationships).Root!.Elements()).Attribute("Id"));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
    }
    [Fact]
    public async Task InvalidXmlDoesNotPublishCollection()
    {
        var (store, library, path) = await Setup(); Fixture(path, true);
        Assert.False((await store.ImportCollectionAsync(HostSelection.FromFilePicker(path), library)).Ok);
        Assert.Empty(await store.CollectionsAsync(library));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
