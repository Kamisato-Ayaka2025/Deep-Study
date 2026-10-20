using System.IO.Compression;
using System.Text;
using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

public sealed class LabZipExporter : ILabExporter
{
    public async Task<Result<string>> ExportAsync(LabPackage package, string newZipPath, CancellationToken ct = default)
    {
        if (package.Practice is { } practice)
            return await new GeneralPracticeExporter().ExportAsync(practice, package.Sources.Select(s => s.Key).ToArray(), newZipPath, ct);
        if (!Path.IsPathFullyQualified(newZipPath) || !newZipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            !LabMaterialRules.Validate(package.Material, package.Sources.Select(s => s.Key).ToArray()).Ok)
            return Result<string>.Fail(ErrorCode.VALIDATION_ERROR, "请选择新的ZIP文件路径。");
        try
        {
            var exportFiles = package.Material.SourceFiles.Concat(package.Material.TestFiles)
                .Select(f => f.Path.EndsWith(".txt", StringComparison.Ordinal) ? f with { Path = f.Path + ".md" } : f).ToArray();
            var exportNames = exportFiles.Select(f => f.Path).ToArray();
            if (exportNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != exportNames.Length ||
                exportNames.Any(a => exportNames.Any(b => b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase))))
                return Result<string>.Fail(ErrorCode.VALIDATION_ERROR, "旧材料Markdown文件名冲突，不能导出。");
            // No extraction, shell opening or execution. CreateNew also rejects existing links/files.
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                foreach (var file in exportFiles)
                {
                    var entry = zip.CreateEntry("files/" + file.Path); entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); await writer.WriteAsync(file.Text.AsMemory(), ct);
                }
                using (var writer = new StreamWriter(zip.CreateEntry("README.md").Open(), new UTF8Encoding(false)))
                    await writer.WriteAsync(("# 实验材料\n\n" + LabMaterialRules.Notice + "\n\n源文件和测试文件位于 files/ 目录。旧版.txt说明导出时追加.md后缀，内容保留。先解压到你选择的新目录，再自行阅读文件；方案中的相对文件名以 files/ 为根。\n\n" + LabMaterialRules.AssessmentOverview(package.Material) + "\n\n" + package.Material.Instructions + "\n\n## 测评方案\n" + package.Material.Assessment + "\n\n## 安全注意\n" + package.Material.SafetyNotes + "\n\n材料摘要：" + package.Hash).AsMemory(), ct);
                using var casesWriter = new StreamWriter(zip.CreateEntry("expected-cases.json").Open(), new UTF8Encoding(false));
                await casesWriter.WriteAsync(JsonSerializer.Serialize(package.Material.Cases, Wire.Options).AsMemory(), ct);
            }
            ct.ThrowIfCancellationRequested(); buffer.Position = 0;
            // ZIP is bounded and assembled before creating destination; no overwrite of user work.
            using var output = new FileStream(newZipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await buffer.CopyToAsync(output, CancellationToken.None);
            return Result<string>.Success(newZipPath);
        }
        catch (IOException) { return Result<string>.Fail(ErrorCode.IO_FAILURE, "导出未完成：目标已存在或不可写。请选择新文件名；已有文件未覆盖。"); }
        catch (UnauthorizedAccessException) { return Result<string>.Fail(ErrorCode.IO_FAILURE, "目标位置不可写。"); }
    }
}
