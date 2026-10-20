using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class P0RegressionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-tests", Guid.NewGuid().ToString());

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static void CreateVersionOne(string path, bool conflict = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = Open(path);
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE settings(id INTEGER PRIMARY KEY, model TEXT NOT NULL, generation TEXT NOT NULL,
              consent TEXT NOT NULL, revision INTEGER NOT NULL, credential_ref TEXT);
            INSERT INTO settings VALUES(1,'preserve-model','Click','Each',7,NULL);
            CREATE TABLE credentials(id TEXT PRIMARY KEY, protected_value BLOB NOT NULL);
            PRAGMA user_version=1;
            """ + (conflict ? "CREATE TABLE requests(id TEXT PRIMARY KEY);" : "");
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task Migration_PathWithSemicolon_PreservesSettingsAndCreatesBackup()
    {
        var folder = Path.Combine(root, "资料;Data Source=other");
        var path = Path.Combine(folder, "app.db");
        CreateVersionOne(path);
        var settings = await new SqliteSettingsStore(folder).ReadAsync(default);
        Assert.Equal("preserve-model", settings.Model);
        Assert.Equal(7, settings.Revision);
        using var backup = Open(path + ".v1.bak");
        using var cmd = backup.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }

    [Fact]
    public void Migration_Failure_RollsBackAndRetainsReadableBackup()
    {
        var path = Path.Combine(root, "app.db");
        CreateVersionOne(path, conflict: true);
        Assert.Throws<SqliteException>(() => new SqliteSettingsStore(root));
        foreach (var file in new[] { path, path + ".v1.bak" })
        {
            using var db = Open(file);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            Assert.Equal(1L, cmd.ExecuteScalar());
            cmd.CommandText = "SELECT model FROM settings WHERE id=1";
            Assert.Equal("preserve-model", cmd.ExecuteScalar());
        }
    }

    [Fact]
    public async Task MissingPayload_ReturnsValidationErrorWithoutWriting()
    {
        var service = new SettingsService(new SqliteSettingsStore(root));
        var result = await service.SaveAsync(new(Guid.NewGuid(), 0, null!));
        Assert.Equal(ErrorCode.VALIDATION_ERROR, result.Error!.Code);
        Assert.Equal(0, (await service.GetAsync()).Revision);
    }

    [Fact]
    public async Task CredentialChange_ConflictDoesNotReplaceExistingSecret()
    {
        var service = new SettingsService(new SqliteSettingsStore(root));
        var saved = await service.SaveAsync(new(Guid.NewGuid(), 0, new("model", GenerationMode.Auto, ConsentMode.Once, "test-key-original")));
        Assert.True(saved.Ok);
        var rejected = await service.SaveAsync(new(Guid.NewGuid(), 0, new("other", GenerationMode.Click, ConsentMode.Each, "test-key-rejected")));
        Assert.Equal(ErrorCode.REVISION_CONFLICT, rejected.Error!.Code);
        var restarted = await new SettingsService(new SqliteSettingsStore(root)).GetAsync();
        Assert.Equal(saved.Value, restarted);
        using var db = Open(Path.Combine(root, "app.db"));
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT protected_value FROM credentials";
        var decrypted = System.Security.Cryptography.ProtectedData.Unprotect((byte[])cmd.ExecuteScalar()!, null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser);
        Assert.Equal("test-key-original", System.Text.Encoding.UTF8.GetString(decrypted));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
