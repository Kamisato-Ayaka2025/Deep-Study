using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace StudyHarness.Infrastructure;

internal sealed record PresentationSlide(int Number, string Title, string Text, bool Visual);

// Read package parts directly. Never resolve external relationships or execute embedded objects.
internal static class PresentationReader
{
    internal const string Version = "presentation-v1";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    internal static IReadOnlyList<PresentationSlide> Read(Stream stream, CancellationToken ct)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
        if (zip.Entries.Count > 50000 || zip.Entries.Sum(e => e.Length) > 1024L * 1024 * 1024)
            throw new InvalidDataException("Presentation package budget");
        var sharedParts = new Dictionary<string, XDocument>();
        XDocument Xml(string name)
        {
            ct.ThrowIfCancellationRequested();
            if (sharedParts.TryGetValue(name, out var cached)) return cached;
            var entry = zip.GetEntry(name) ?? throw new InvalidDataException("Missing presentation part");
            if (entry.Length > 16 * 1024 * 1024) throw new InvalidDataException("Presentation XML budget");
            using var input = entry.Open();
            using var reader = XmlReader.Create(input, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
            var document = XDocument.Load(reader);
            if (!name.StartsWith("ppt/slides/slide", StringComparison.Ordinal)) sharedParts[name] = document;
            return document;
        }
        Dictionary<string, (string Path, string Type)> Relationships(string part)
        {
            var slash = part.LastIndexOf('/'); var rel = part[..(slash + 1)] + "_rels/" + part[(slash + 1)..] + ".rels";
            if (zip.GetEntry(rel) is null) return [];
            return Xml(rel).Root!.Elements().Where(e => (string?)e.Attribute("TargetMode") != "External")
                .ToDictionary(e => (string)e.Attribute("Id")!, e =>
                {
                    var uri = new Uri(new Uri("https://package/" + part), (string)e.Attribute("Target")!);
                    if (uri.Host != "package") throw new InvalidDataException("Invalid package relationship");
                    return (Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')), (string)e.Attribute("Type")!);
                });
        }
        static string Paragraph(XElement paragraph) => string.Concat(paragraph.Descendants().Where(e => e.Name == A + "t" || e.Name == A + "br").Select(e => e.Name == A + "br" ? "\n" : e.Value));
        static bool Visual(XDocument document) => document.Descendants().Any(e =>
            e.Name == P + "pic" || e.Name == P + "cxnSp" || e.Name == A + "blip" || e.Name == A + "custGeom" ||
            e.Name.LocalName is "chart" or "relIds" or "oleObj" or "oMath" or "oMathPara" ||
            e.Name == A + "prstGeom" && (string?)e.Attribute("prst") is not ("rect" or "roundRect") ||
            e.Name == P + "sp" && !e.Descendants(A + "t").Any() && !e.Descendants(P + "ph").Any());
        var rels = Relationships("ppt/presentation.xml");
        var ids = Xml("ppt/presentation.xml").Descendants(P + "sldId").ToArray();
        if (ids.Length is < 1 or > 2000) throw new InvalidDataException("Presentation slide budget");
        var result = new List<PresentationSlide>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            var part = rels[(string)id.Attribute(R + "id")!].Path;
            var slide = Xml(part); var links = Relationships(part);
            var titleShape = slide.Descendants(P + "sp").FirstOrDefault(s => s.Descendants(P + "ph").Any(p => (string?)p.Attribute("type") is "title" or "ctrTitle"));
            var title = titleShape is null ? "幻灯片 " + (result.Count + 1) : string.Join(" ", titleShape.Descendants(A + "p").Select(Paragraph)).Trim();
            if (title.Length == 0) title = "幻灯片 " + (result.Count + 1);
            var body = new StringBuilder("# " + title + "\n\n");
            foreach (var element in slide.Descendants().Where(e => e.Name == A + "p" && !e.Ancestors(A + "tbl").Any() || e.Name == A + "tbl"))
            {
                if (element.Name == A + "tbl")
                {
                    var rows = element.Elements(A + "tr").Select(row => row.Elements(A + "tc").Select(cell => string.Join(" ", cell.Descendants(A + "p").Select(Paragraph)).Replace("|", "\\|")).ToArray()).ToArray();
                    for (var i = 0; i < rows.Length; i++)
                    {
                        body.AppendLine("| " + string.Join(" | ", rows[i]) + " |");
                        if (i == 0) body.AppendLine("| " + string.Join(" | ", rows[i].Select(_ => "---")) + " |");
                    }
                }
                else { var text = Paragraph(element); if (!string.IsNullOrWhiteSpace(text)) body.AppendLine(text); }
            }
            var notes = links.Values.FirstOrDefault(r => r.Type.EndsWith("/notesSlide", StringComparison.Ordinal));
            if (notes.Path is not null)
            {
                var paragraphs = Xml(notes.Path).Descendants(P + "sp")
                    .Where(s => !s.Descendants(P + "ph").Any(p => (string?)p.Attribute("type") is "sldImg" or "sldNum" or "hdr" or "ftr" or "dt"))
                    .SelectMany(s => s.Descendants(A + "p")).Select(Paragraph).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
                if (paragraphs.Length > 0) body.AppendLine("\n## 讲者备注\n" + string.Join("\n", paragraphs));
            }
            var visual = Visual(slide);
            // Background/layout images also carry information; text placeholders alone do not.
            var layout = links.Values.FirstOrDefault(r => r.Type.EndsWith("/slideLayout", StringComparison.Ordinal));
            if (layout.Path is not null)
            {
                visual |= Visual(Xml(layout.Path));
                var master = Relationships(layout.Path).Values.FirstOrDefault(r => r.Type.EndsWith("/slideMaster", StringComparison.Ordinal));
                if (master.Path is not null) visual |= Visual(Xml(master.Path));
            }
            if (body.Length > 65536) throw new InvalidDataException("Presentation slide text budget");
            result.Add(new(result.Count + 1, title, body.ToString(), visual));
        }
        return result;
    }
}
