using System.Text;
using StudyHarness.Application;

namespace StudyHarness.Infrastructure;

public sealed class TutoringFiles : ITutoringFiles
{
    public string ReadSelected(HostSelection selection)
    {
        if (selection.IsDirectory || !new[] { ".c", ".h", ".cpp", ".hpp", ".md", ".txt" }.Contains(Path.GetExtension(selection.Path), StringComparer.OrdinalIgnoreCase))
            throw new IOException("请选择单个受支持的文本或代码文件。");
        var bytes = SafeKnowledgeFiles.ReadBytes(selection.Path);
        if (bytes.Length > 64000) throw new IOException("所选代码超过读取限额。");
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        if (text.Length > 16000 || text.Contains('\0')) throw new IOException("请选择16000字符以内的UTF-8文本。");
        return text;
    }
}
