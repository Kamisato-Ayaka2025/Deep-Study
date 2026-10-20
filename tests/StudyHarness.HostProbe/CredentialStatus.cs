using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StudyHarness.HostProbe;

// Read-only local diagnosis: no credential text, hashes, lengths or provider request.
internal static class CredentialStatus
{
    internal static int Run(string? root)
    {
        if (root is null || !File.Exists(Path.Combine(root, "app.db"))) return 2;
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, "app.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT protected_value FROM credentials WHERE id='deepseek'";
            var saved = cmd.ExecuteScalar() is byte[];
            var readable = false;
            if (cmd.ExecuteScalar() is byte[] encrypted)
            {
                var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                try { readable = !string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(plain)); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            Console.WriteLine(JsonSerializer.Serialize(new { saved, readable, apiCalled = false }));
            return saved && readable ? 0 : 1;
        }
        catch (Exception e) when (e is SqliteException or CryptographicException or IOException)
        { Console.WriteLine("CREDENTIAL_READ_FAILED"); return 1; }
    }
}
