using Microsoft.Data.Sqlite;

namespace StudyHarness.Infrastructure;

internal static class DatabaseMigrationBackup
{
    // SQLite backup includes committed WAL content; copying the main file does not.
    // A fresh name preserves previous upgrade attempts and never overwrites a backup.
    public static void Create(SqliteConnection source, string database, int version)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(database))!;
        using var directoryPin = SafeKnowledgeFiles.PinDirectory(root);
        using var sourcePin = SafeKnowledgeFiles.PinExistingFile(database);
        var target = database + $".v{version}-{Guid.NewGuid():N}.bak";
        using (SafeKnowledgeFiles.CreateNew(root, target)) { }
        using (var targetPin = SafeKnowledgeFiles.PinExistingFile(target))
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = target, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            backup.Open();
            source.BackupDatabase(backup);
            using var check = backup.CreateCommand();
            check.CommandText = "PRAGMA integrity_check";
            if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
                throw new InvalidDataException("Migration backup integrity check failed.");
        }
        // Only marked backups have completed and passed integrity_check. A failed
        // copy remains diagnostic evidence but must not be offered for recovery.
        using var marker = SafeKnowledgeFiles.CreateNew(root, target + ".verified");
    }
}
