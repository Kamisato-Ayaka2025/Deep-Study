using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace StudyHarness.Infrastructure;
public sealed class SqliteSettingsStore : ISettingsStore, ICredentialReader
{
    private readonly string database;
    public SqliteSettingsStore(string root)
    {
        Directory.CreateDirectory(root);
        database = Path.Combine(root, "app.db");
        Initialize();
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Pooling = false, ForeignKeys = true, DefaultTimeout = 5
        }.ToString());
        connection.Open();
        return connection;
    }
    public Task<string?> ReadKeyAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT protected_value FROM credentials WHERE id='deepseek'";
        if (cmd.ExecuteScalar() is not byte[] encrypted) return Task.FromResult<string?>(null);
        var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try { return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private void Initialize()
    {
        using var db = Open();
        using var version = db.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var current = Convert.ToInt32(version.ExecuteScalar());
        if (current > 2) throw new InvalidDataException("Unsupported database version.");
        if (current > 0 && current < 2)
        {
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = $"{database}.v{current}.bak", Pooling = false
            }.ToString());
            backup.Open();
            db.BackupDatabase(backup);
        }
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        if (current == 0)
        {
            cmd.CommandText = """
                CREATE TABLE settings (id INTEGER PRIMARY KEY CHECK(id=1), model TEXT NOT NULL,
                  generation TEXT NOT NULL, consent TEXT NOT NULL, revision INTEGER NOT NULL, credential_ref TEXT);
                INSERT INTO settings VALUES(1,'deepseek-chat','Click','Each',0,NULL);
                CREATE TABLE credentials (id TEXT PRIMARY KEY, protected_value BLOB NOT NULL);
                PRAGMA user_version=1;
                """;
            cmd.ExecuteNonQuery();
        }
        if (current < 2)
        {
            cmd.CommandText = """
                CREATE TABLE requests (id TEXT PRIMARY KEY, payload_hash TEXT NOT NULL, result TEXT NOT NULL);
                PRAGMA user_version=2;
                """;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    private static SettingsDto Read(SqliteConnection db, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT model,generation,consent,credential_ref,revision FROM settings WHERE id=1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Settings missing.");
        return new(reader.GetString(0), Enum.Parse<GenerationMode>(reader.GetString(1)),
            Enum.Parse<ConsentMode>(reader.GetString(2)), !reader.IsDBNull(3), reader.GetInt64(4));
    }
    public Task<SettingsDto> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var db = Open();
        return Task.FromResult(Read(db));
    }
    public Task<Result<SettingsDto>> SaveAsync(Command<SaveSettings> command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        // Hash avoids storing secret request bodies in the idempotency table.
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, Wire.Options)));
        cmd.CommandText = "SELECT payload_hash,result FROM requests WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", command.RequestId.ToString());
        using (var previous = cmd.ExecuteReader())
        {
            if (previous.Read())
                return Task.FromResult(previous.GetString(0) == hash
                    ? JsonSerializer.Deserialize<Result<SettingsDto>>(previous.GetString(1), Wire.Options)!
                    : Result<SettingsDto>.Fail(ErrorCode.IDEMPOTENCY_CONFLICT, "请求标识已用于不同内容。"));
        }
        var current = Read(db, tx);
        if (current.Revision != command.ExpectedRevision)
            return Task.FromResult(Result<SettingsDto>.Fail(ErrorCode.REVISION_CONFLICT, "设置已更新，请重新载入后保存。"));
        if (command.Payload.NewApiKey is { } key)
        {
            var bytes = Encoding.UTF8.GetBytes(key);
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            cmd.CommandText = "INSERT INTO credentials VALUES('deepseek',$secret) ON CONFLICT(id) DO UPDATE SET protected_value=excluded.protected_value";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$secret", encrypted);
            cmd.ExecuteNonQuery();
        }
        cmd.CommandText = """
            UPDATE settings SET model=$model,generation=$generation,consent=$consent,revision=revision+1,
              credential_ref=CASE WHEN $newKey=1 THEN 'deepseek' ELSE credential_ref END WHERE id=1
            """;
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$model", command.Payload.Model);
        cmd.Parameters.AddWithValue("$generation", command.Payload.GenerationMode.ToString());
        cmd.Parameters.AddWithValue("$consent", command.Payload.ConsentMode.ToString());
        cmd.Parameters.AddWithValue("$newKey", command.Payload.NewApiKey is not null ? 1 : 0);
        cmd.ExecuteNonQuery();
        var result = Result<SettingsDto>.Success(Read(db, tx));
        cmd.CommandText = "INSERT INTO requests VALUES($id,$hash,$result)";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$id", command.RequestId.ToString());
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$result", JsonSerializer.Serialize(result, Wire.Options));
        cmd.ExecuteNonQuery();
        ct.ThrowIfCancellationRequested();
        tx.Commit();
        return Task.FromResult(result);
    }
}
