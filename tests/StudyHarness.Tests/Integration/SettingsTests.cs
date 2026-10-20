using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Desktop;
using StudyHarness.Infrastructure;
using Xunit;
namespace StudyHarness.Tests.Integration;
public sealed class SettingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-tests", Guid.NewGuid().ToString());
    private SqliteSettingsStore Store() => new(root);
    private static Command<SaveSettings> Save(long revision = 0, string? key = null) =>
        new(Guid.NewGuid(), revision, new("deepseek-test", GenerationMode.Auto, ConsentMode.Each, key));
    [Fact]
    public async Task EmptyDatabase_SaveAndRestart_PreservesSettingsAndEncryptedCredential()
    {
        var service = new SettingsService(Store());
        var initial = await service.GetAsync();
        Assert.False(initial.HasApiKey);
        Assert.Equal(0, initial.Revision);
        var secret = "fake-key-" + Guid.NewGuid();
        var result = await service.SaveAsync(Save(key: secret));
        Assert.True(result.Ok);
        var restarted = await new SettingsService(Store()).GetAsync();
        Assert.Equal(result.Value, restarted);
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "app.db")};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT protected_value FROM credentials WHERE id='deepseek'";
        var encrypted = (byte[])cmd.ExecuteScalar()!;
        Assert.Equal(secret, Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser)));
        db.Close();
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "app.db"))));
        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(restarted, Wire.Options));
        Assert.True((await service.SaveAsync(Save(1))).Value!.HasApiKey);
    }
    [Fact]
    public async Task Requests_RepeatIsIdempotent_ChangedBodyAndStaleRevisionAreRejected()
    {
        var service = new SettingsService(Store());
        var command = Save();
        var first = await service.SaveAsync(command);
        Assert.Equal(first, await new SettingsService(Store()).SaveAsync(command));
        var changed = command with { Payload = command.Payload with { Model = "another" } };
        Assert.Equal(ErrorCode.IDEMPOTENCY_CONFLICT, (await service.SaveAsync(changed)).Error!.Code);
        Assert.Equal(ErrorCode.REVISION_CONFLICT, (await service.SaveAsync(Save())).Error!.Code);
        Assert.Equal(1, (await service.GetAsync()).Revision);
    }
    [Fact]
    public async Task InvalidCommand_AndCancellation_DoNotWrite()
    {
        var service = new SettingsService(Store());
        var bad = Save() with { Payload = new("", GenerationMode.Click, ConsentMode.Each, null) };
        Assert.Equal(ErrorCode.VALIDATION_ERROR, (await service.SaveAsync(bad)).Error!.Code);
        Assert.Equal(ErrorCode.VALIDATION_ERROR, (await service.SaveAsync(Save() with { SchemaVersion = 2 })).Error!.Code);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(Save(), cts.Token));
        Assert.Equal(0, (await service.GetAsync()).Revision);
    }
    [Fact]
    public async Task VersionOne_MigratesAndBacksUp_FutureVersionRejected()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "app.db");
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE settings(id INTEGER PRIMARY KEY, model TEXT NOT NULL, generation TEXT NOT NULL, consent TEXT NOT NULL, revision INTEGER NOT NULL, credential_ref TEXT);
                INSERT INTO settings VALUES(1,'old-model','Click','Each',4,NULL);
                CREATE TABLE credentials(id TEXT PRIMARY KEY,protected_value BLOB NOT NULL);
                PRAGMA user_version=1;
                """;
            cmd.ExecuteNonQuery();
        }
        var migrated = await Store().ReadAsync(default);
        Assert.Equal("old-model", migrated.Model);
        Assert.Equal(4, migrated.Revision);
        Assert.True(File.Exists(path + ".v1.bak"));
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA user_version=99";
            cmd.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => Store());
        Assert.Equal(before, File.ReadAllBytes(path));
    }
    [Fact]
    public async Task ViewModel_ValidationErrorIsVisible_LogsContainOnlyAllowlistedFields()
    {
        var vm = new ShellViewModel(new SettingsService(Store()), new ModelEgress(new FakeProvider()),
            new SafeLog(Path.Combine(root, "operations.jsonl")));
        await vm.LoadAsync();
        vm.Model = "invalid secret-looking value";
        await vm.SaveAsync("fake-api-key");
        Assert.Contains("VALIDATION_ERROR", vm.Status);
        Assert.False(vm.Busy);
        var log = File.ReadAllText(Path.Combine(root, "operations.jsonl"));
        Assert.DoesNotContain("fake-api-key", log);
        Assert.DoesNotContain("secret-looking", log);
        using var json = System.Text.Json.JsonDocument.Parse(log.Trim());
        Assert.Equal(4, json.RootElement.EnumerateObject().Count());
    }
    [Fact]
    public async Task RestartedViewModel_BlankSavesAndValidationFailure_PreserveReusableCredential()
    {
        var secret = "synthetic-persistent-" + Guid.NewGuid();
        ShellViewModel View() => new(new SettingsService(Store()), new ModelEgress(new FakeProvider()), new SafeLog(Path.Combine(root, "operations.jsonl")), root);
        var first = View(); await first.LoadAsync(); first.Model = "deepseek-flash";
        Assert.True(await first.SaveAsync(secret));
        for (var i = 0; i < 3; i++)
        {
            var restarted = View(); await restarted.LoadAsync();
            Assert.True(restarted.HasApiKey);
            Assert.Contains("无需重复输入", restarted.CredentialStatus);
            Assert.Contains(root, restarted.DataLocation);
            Assert.True(await restarted.SaveAsync(null));
            Assert.Equal(secret, await Store().ReadKeyAsync(default));
            restarted.Model = "bad model";
            Assert.False(await restarted.SaveAsync("replacement-must-not-save"));
            Assert.Equal(secret, await Store().ReadKeyAsync(default));
        }
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(root, "operations.jsonl")));
    }

    [Fact]
    public async Task PendingCredential_BlocksNavigationUntilExplicitDiscard_AndNeverWritesImplicitly()
    {
        var service = new SettingsService(Store());
        await service.SaveAsync(Save(key: "synthetic-original"));
        var vm = new ShellViewModel(service, new ModelEgress(new FakeProvider()), new SafeLog(Path.Combine(root, "operations.jsonl")));
        await vm.LoadAsync(); vm.SetPendingApiKey(true);
        Assert.Contains("新密钥尚未保存", vm.CredentialStatus);
        Assert.False(vm.CanLeaveSettings()); Assert.Contains("已保留输入", vm.Status);
        Assert.Equal("synthetic-original", await Store().ReadKeyAsync(default));
        vm.Model = "invalid model"; Assert.False(await vm.SaveAsync("synthetic-new"));
        Assert.False(vm.CanLeaveSettings());
        vm.DiscardPendingApiKey(); Assert.True(vm.CanLeaveSettings());
        Assert.Equal("synthetic-original", await Store().ReadKeyAsync(default));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
