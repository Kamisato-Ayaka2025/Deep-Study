using System.IO.Compression;
using System.Text;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

public sealed class GeneralPracticeExporter
{
    public async Task<Result<string>> ExportAsync(GeneralPractice material, IReadOnlyList<string> sources, string path, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !GeneralPracticeRules.Validate(material, sources).Ok)
            return Result<string>.Fail(ErrorCode.VALIDATION_ERROR, "请选择新的ZIP路径并提供有效通用材料。");
        try
        {
            ct.ThrowIfCancellationRequested();
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                var entry = zip.CreateEntry("README.md");
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                await writer.WriteAsync(GeneralPracticeRules.Markdown(material, sources).AsMemory(), ct);
            }
            ct.ThrowIfCancellationRequested(); buffer.Position = 0;
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await buffer.CopyToAsync(output, CancellationToken.None);
            return Result<string>.Success(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Result<string>.Fail(ErrorCode.IO_FAILURE, "无法创建导出文件，请选择新的文件名；已有文件不覆盖。"); }
    }
}
