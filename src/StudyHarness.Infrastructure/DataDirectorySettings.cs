using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

public sealed record DataDirectoryChoice(string Path, string? PreviousPath, bool CopyPending, Guid OperationId);

/// <summary>Bootstrap location preference, independent of the databases it selects.</summary>
public sealed class DataDirectorySettings(string applicationDirectory, string localAppData, string? environmentRoot)
{
    private string PortableRoot => System.IO.Path.Combine(applicationDirectory, "data");
    private string LegacyRoot => System.IO.Path.Combine(localAppData, "StudyHarness");
    private string PreferenceFile => System.IO.Path.Combine(localAppData, "StudyHarnessLocations",
        string.IsNullOrWhiteSpace(environmentRoot) ? "default.json" :
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(environmentRoot).ToUpperInvariant()))) + ".json");
    public static string Normalize(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    public static string MutexName(string path) => "Local\\StudyHarness-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(path).ToUpperInvariant())));
    public DataDirectoryChoice? Read() => File.Exists(PreferenceFile)
        ? JsonSerializer.Deserialize<DataDirectoryChoice>(File.ReadAllText(PreferenceFile), Wire.Options)
            ?? throw new InvalidDataException("Invalid data location preference") : null;
    public string Resolve()
    {
        if (Read() is { } choice) return Normalize(choice.Path);
        if (!string.IsNullOrWhiteSpace(environmentRoot)) return Normalize(environmentRoot);
        if (HasData(PortableRoot)) return Normalize(PortableRoot);
        // Existing installations retain their old default. Never make an upgrade appear empty.
        return Normalize(HasData(LegacyRoot) ? LegacyRoot : PortableRoot);
    }
    public static bool HasData(string root) => new[] { "app.db", "planning.db", "learning.db", "libraries.db" }
        .Any(name => File.Exists(System.IO.Path.Combine(root, name)));
    private static bool Contains(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(parent + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public void Schedule(string current, string target, bool copy)
    {
        current = Normalize(current); target = Normalize(target);
        if (Contains(current, target) || Contains(target, current)) throw new IOException("请选择与当前目录互不包含的独立目录。");
        Directory.CreateDirectory(target);
        using (SafeKnowledgeFiles.PinDirectory(target)) { }
        if (copy && Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException("迁移目标必须为空目录，以免覆盖已有数据。");
        if (!copy && !HasData(target)) throw new IOException("此目录没有现有学习数据。新目录请使用“迁移当前数据”。");
        var probe = System.IO.Path.Combine(target, ".write-check-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        Save(new(target, current, copy, Guid.NewGuid()));
    }
    public void CancelChange(string current) => Save(new(Normalize(current), null, false, Guid.NewGuid()));
    private void Save(DataDirectoryChoice choice)
    {
        var directory = System.IO.Path.GetDirectoryName(PreferenceFile)!;
        Directory.CreateDirectory(directory);
        using (SafeKnowledgeFiles.PinDirectory(directory)) { }
        var temporary = PreferenceFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(choice, Wire.Options));
            File.Move(temporary, PreferenceFile, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    // The caller holds the target data-directory mutex. Copy only after the previous process has exited.
    public async Task ApplyPendingAsync(CancellationToken ct = default)
    {
        var choice = Read();
        if (choice is null) return;
        if (!choice.CopyPending)
        {
            if (!HasData(choice.Path)) throw new IOException("所选数据目录已不可用，未新建空库。");
            return;
        }
        if (choice.PreviousPath is null) throw new InvalidDataException("Missing transfer source");
        await Task.Run(() =>
        {
            using var sourceLock = new Mutex(true, MutexName(choice.PreviousPath), out var acquired);
            if (!acquired) throw new IOException("原数据目录仍在使用。请从托盘退出旧程序后重试。");
            try { CopyOffline(choice, ct); }
            finally { sourceLock.ReleaseMutex(); }
        }, ct);
        Save(choice with { CopyPending = false });
    }
    private static void CopyOffline(DataDirectoryChoice choice, CancellationToken ct)
    {
        var source = Normalize(choice.PreviousPath!); var target = Normalize(choice.Path);
        if (Contains(source, target) || Contains(target, source) || !HasData(source)) throw new IOException("原数据目录无效。");
        var marker = System.IO.Path.Combine(target, ".studyharness-transfer.json");
        if (File.Exists(marker))
        {
            using (SafeKnowledgeFiles.PinDirectory(target)) { }
            using var file = SafeKnowledgeFiles.PinExistingFile(marker);
            var completed = JsonSerializer.Deserialize<DataDirectoryChoice>(File.ReadAllText(marker), Wire.Options);
            if (completed == choice && HasData(target)) return; // Resume after the rename but before preference commit.
        }
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException("目标目录已非空，未覆盖任何数据。");
        var parent = System.IO.Path.GetDirectoryName(target) ?? throw new IOException("不能使用磁盘根目录。");
        Directory.CreateDirectory(parent);
        using (SafeKnowledgeFiles.PinDirectory(parent)) { }
        using (SafeKnowledgeFiles.PinDirectory(source)) { }
        var staging = System.IO.Path.Combine(parent, ".studyharness-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            CopyDirectory(source, staging, true, ct);
            RebaseLibraries(staging, source, target);
            File.WriteAllText(System.IO.Path.Combine(staging, ".studyharness-transfer.json"), JsonSerializer.Serialize(choice, Wire.Options));
            ct.ThrowIfCancellationRequested();
            // Delete only an empty selected folder, never an existing database or nonempty directory.
            if (Directory.Exists(target)) { using (SafeKnowledgeFiles.PinDirectory(target)) { } Directory.Delete(target, false); }
            Directory.Move(staging, target);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    private static void CopyDirectory(string source, string target, bool top, CancellationToken ct)
    {
        using var pin = SafeKnowledgeFiles.PinDirectory(source);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            ct.ThrowIfCancellationRequested();
            var name = System.IO.Path.GetFileName(entry);
            if (top && (name.Equals("reader-profile", StringComparison.OrdinalIgnoreCase) || name == ".studyharness-transfer.json")) continue;
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("数据目录包含链接，未自动复制。");
            var destination = System.IO.Path.Combine(target, name);
            if (Directory.Exists(entry)) { Directory.CreateDirectory(destination); CopyDirectory(entry, destination, false, ct); }
            else
            {
                using var input = SafeKnowledgeFiles.OpenRead(entry, long.MaxValue);
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.Stream.CopyTo(output);
                input.Stream.Position = 0;
                using var verify = File.OpenRead(destination);
                if (!SHA256.HashData(input.Stream).SequenceEqual(SHA256.HashData(verify))) throw new IOException("复制校验失败，原数据保持不变。");
            }
        }
    }
    private static void RebaseLibraries(string staging, string source, string target)
    {
        var database = System.IO.Path.Combine(staging, "libraries.db");
        if (!File.Exists(database)) return;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString());
        db.Open(); using var tx = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT json FROM state WHERE id=1";
        if (command.ExecuteScalar() is not string json) return;
        var registry = JsonNode.Parse(json) ?? throw new InvalidDataException("Invalid library registry");
        void Rebase(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["root"] is JsonValue value && value.TryGetValue<string>(out var path) && Contains(source, Normalize(path)))
                    obj["root"] = System.IO.Path.Combine(target, System.IO.Path.GetRelativePath(source, Normalize(path)));
                foreach (var pair in obj.ToArray()) Rebase(pair.Value);
            }
            else if (node is JsonArray array) foreach (var child in array) Rebase(child);
        }
        Rebase(registry);
        // Cached library-switch responses also contain a Root; retain request IDs and payload hashes.
        if (registry["requests"] is JsonObject requests)
            foreach (var request in requests.Select(p => p.Value).OfType<JsonObject>())
                if (request["json"]?.GetValue<string>() is { } saved) { var response = JsonNode.Parse(saved); Rebase(response); request["json"] = response?.ToJsonString(); }
        command.CommandText = "UPDATE state SET json=$json WHERE id=1"; command.Parameters.AddWithValue("$json", registry.ToJsonString()); command.ExecuteNonQuery(); tx.Commit();
    }
}
