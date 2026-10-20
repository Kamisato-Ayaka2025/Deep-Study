using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using StudyHarness.Application;

namespace StudyHarness.Desktop;

// Only native text elements are created. Model text is never parsed as XAML/HTML,
// and cannot load images, navigate links or execute code.
public sealed class TutorMarkdownView : RichTextBox
{
    public string Text { get; private set; } = "";
    public TutorMarkdownView()
    {
        IsReadOnly = true; IsDocumentEnabled = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Document = NewDocument();
    }
    private static FlowDocument NewDocument() => new() { PagePadding = new Thickness(8), FontSize = 14, FontFamily = new FontFamily("Microsoft YaHei UI") };
    public void Clear() { Text = ""; Document = NewDocument(); }
    public void ShowMessages(IReadOnlyList<TutorMessage> messages)
    {
        Text = string.Join("\n\n", messages.Select(m => (m.Role == "user" ? "你：" : "导师：") + m.Text));
        var document = NewDocument();
        foreach (var message in messages)
        {
            document.Blocks.Add(new Paragraph(new Bold(new Run(message.Role == "user" ? "你" : "导师"))) { Margin = new Thickness(0, 8, 0, 4) });
            if (message.Role == "user") document.Blocks.Add(new Paragraph(new Run(message.Text)));
            else Render(document, message.Text);
        }
        Document = document;
        ScrollToEnd();
    }
    private static Match Match(string text, string pattern) => Regex.Match(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static void Render(FlowDocument document, string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fence = Match(line, @"^\s{0,3}(`{3,}|~{3,})(.*)$");
            if (fence.Success)
            {
                var marker = fence.Groups[1].Value; var code = new StringBuilder();
                while (++i < lines.Length)
                {
                    var end = lines[i].Trim();
                    if (end.Length >= marker.Length && end.All(c => c == marker[0])) break;
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i]);
                }
                document.Blocks.Add(new Paragraph(new Run(code.ToString()))
                {
                    FontFamily = new FontFamily("Consolas"), FontSize = 13,
                    Background = Brushes.AliceBlue, Padding = new Thickness(8), Margin = new Thickness(0, 4, 0, 8)
                });
                continue;
            }
            var heading = Match(line, @"^ {0,3}(#{1,6})\s+(.+?)\s*#*\s*$");
            var bullet = Match(line, @"^\s*([-+*]|\d+[.)])\s+(.+)$");
            var quote = Match(line, @"^\s*>\s?(.*)$");
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
            if (heading.Success)
            {
                paragraph.FontWeight = FontWeights.Bold;
                paragraph.FontSize = 22 - heading.Groups[1].Length;
                InlineText(paragraph.Inlines, heading.Groups[2].Value);
            }
            else if (bullet.Success)
            {
                paragraph.Margin = new Thickness(12, 0, 0, 4);
                paragraph.Inlines.Add(new Run(char.IsDigit(bullet.Groups[1].Value[0]) ? bullet.Groups[1].Value + " " : "• "));
                InlineText(paragraph.Inlines, bullet.Groups[2].Value);
            }
            else if (quote.Success)
            {
                paragraph.BorderBrush = Brushes.LightSlateGray; paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
                paragraph.Padding = new Thickness(8, 0, 0, 0);
                InlineText(paragraph.Inlines, quote.Groups[1].Value);
            }
            else if (Match(line, @"^\s*([-*_])(?:\s*\1){2,}\s*$").Success)
            { paragraph.BorderBrush = Brushes.LightGray; paragraph.BorderThickness = new Thickness(0, 0, 0, 1); }
            else InlineText(paragraph.Inlines, line);
            document.Blocks.Add(paragraph);
        }
    }
    private static void InlineText(InlineCollection inlines, string text, int depth = 0)
    {
        if (depth > 8) { inlines.Add(new Run(text)); return; }
        // Backtick runs are consumed before emphasis, so code characters stay literal.
        var pattern = @"(`+)(.+?)\1|\*\*(.+?)\*\*|__(.+?)__|(?<!\*)\*([^*]+)\*(?!\*)|(?<!\w)_([^_]+)_(?!\w)";
        var position = 0;
        foreach (Match match in Regex.Matches(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            inlines.Add(new Run(text[position..match.Index]));
            if (match.Groups[1].Success)
                inlines.Add(new Run(match.Groups[2].Value) { FontFamily = new FontFamily("Consolas"), Background = Brushes.AliceBlue });
            else
            {
                Span span = match.Groups[3].Success || match.Groups[4].Success ? new Bold() : new Italic();
                var content = match.Groups.Cast<Group>().Skip(3).First(g => g.Success).Value;
                InlineText(span.Inlines, content, depth + 1); inlines.Add(span);
            }
            position = match.Index + match.Length;
        }
        inlines.Add(new Run(text[position..]));
    }
}
