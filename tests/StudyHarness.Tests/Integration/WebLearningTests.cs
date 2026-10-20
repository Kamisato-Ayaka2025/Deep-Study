using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class WebLearningTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-web", Guid.NewGuid().ToString());
    private string DirectoryAt(string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    [Theory]
    [InlineData("file:///C:/secret")]
    [InlineData("http://oi-wiki.org")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://[::1]")]
    [InlineData("https://10.0.0.1")]
    [InlineData("https://user:password@oi-wiki.org")]
    [InlineData("https://oi-wiki.org:444")]
    [InlineData("https://router.local")]
    [InlineData("https://localhost")]
    public void UnsafeTargetsRejected(string url) => Assert.Throws<InvalidOperationException>(() => WebAddress.Parse(url));

    [Fact]
    public async Task EmptyLibrary_SearchThenBody_PersistRestartImmutableAndDeduplicate()
    {
        var knowledge = new KnowledgeStore(DirectoryAt("app")); var fake = new FakeWeb();
        var service = new WebLearningService(fake, knowledge, knowledge, DirectoryAt("library"));
        var first = await service.AcquireAsync("二分查找", "oi-wiki.org", true, null, default);
        Assert.Equal(1, fake.Searches); Assert.Equal(1, fake.Fetches); Assert.Contains("site:oi-wiki.org", fake.Query);
        var reference = Assert.Single(first.References);
        Assert.Equal("binary body is a search explanation.", (await knowledge.ResolveAsync(reference)).Text);
        var again = await service.AcquireAsync("二分查找", "oi-wiki.org", true, null, default);
        Assert.Equal(reference, Assert.Single(again.References));
        fake.Content = "changed body is a search explanation.";
        var changed = await service.AcquireAsync("二分查找", "oi-wiki.org", true, null, default);
        Assert.NotEqual(reference.VersionId, Assert.Single(changed.References).VersionId);
        var restarted = new KnowledgeStore(Path.Combine(root, "app"));
        Assert.Equal("binary body is a search explanation.", (await restarted.ResolveAsync(reference)).Text);
        Assert.Equal("changed body is a search explanation.", (await restarted.ResolveAsync(changed.References[0])).Text);
        Assert.Contains("最终重定向URL", changed.Report);
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "library", "knowledge.db")};Mode=ReadOnly;Pooling=False");
        db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version";
        Assert.Equal(2L, command.ExecuteScalar());
        command.CommandText = "SELECT json FROM state"; var state = (string)command.ExecuteScalar()!;
        Assert.Contains("webSnapshots", state); Assert.Contains("retrievedAt", state); Assert.Contains("finalUrl", state);
    }
    [Fact]
    public async Task DeniedCancelledAndEmptyBody_DoNotCreateLibrary()
    {
        var knowledge = new KnowledgeStore(DirectoryAt("app")); var fake = new FakeWeb();
        var service = new WebLearningService(fake, knowledge, knowledge, DirectoryAt("library"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcquireAsync("算法", "", false, null, default));
        Assert.Equal(0, fake.Searches);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AcquireAsync("算法", "", true, null, cancellation.Token));
        fake.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcquireAsync("算法", "", true, null, default));
        Assert.Null(await knowledge.CurrentAsync());
    }
    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(302)]
    [InlineData(503)]
    public async Task ProviderErrors_NoRetry_NoErrorBodyLeak(int status)
    {
        var handler = new Handler(status, "secret-provider-body"); using var client = new HttpClient(handler);
        var web = new DeepSeekWebSources(new Key(), client);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => web.SearchAsync("算法", default));
        Assert.DoesNotContain("secret-provider-body", error.Message); Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task Provider_FixedDestination_MinimalPayloadAndBodyLimits()
    {
        var handler = new Handler(200, "{\"results\":[{\"title\":\"title\",\"url\":\"https://oi-wiki.org/basic/binary/\"}]}");
        using var client = new HttpClient(handler); var web = new DeepSeekWebSources(new Key(), client);
        Assert.Single(await web.SearchAsync("二分查找", default));
        Assert.Equal("https://api.deepseek.com/v1/tools/search", handler.Url);
        Assert.DoesNotContain("fixture-key", handler.Body); Assert.Contains("\"need_content\":false", handler.Body);
        handler.Response = "{\"title\":\"t\",\"content\":\"fixture-key\"}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => web.FetchAsync("https://oi-wiki.org", default));
        handler.Response = "{\"title\":\"t\",\"content\":\"" + new string('a', 60001) + "\"}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => web.FetchAsync("https://oi-wiki.org", default));
        handler.Response = new string('a', 512 * 1024 + 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => web.SearchAsync("算法", default));
    }
    private sealed class Key : ICredentialReader { public Task<string?> ReadKeyAsync(CancellationToken ct) => Task.FromResult<string?>("fixture-key"); }
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"results\":[{}]}")]
    public async Task MalformedSearchIsRecoverable(string response)
    {
        using var client = new HttpClient(new Handler(200, response));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DeepSeekWebSources(new Key(), client).SearchAsync("算法", default));
    }
    private sealed class Handler(int status, string response) : HttpMessageHandler
    {
        public string Response = response; public int Calls; public string Url = "", Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Url = request.RequestUri!.AbsoluteUri; Body = await request.Content!.ReadAsStringAsync(ct);
            return new((HttpStatusCode)status) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class FakeWeb : IWebSources
    {
        public int Searches, Fetches; public string Query = "", Content = "binary body is a search explanation."; public bool Fail;
        public Task<IReadOnlyList<WebHit>> SearchAsync(string query, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Searches++; Query = query;
            return Task.FromResult<IReadOnlyList<WebHit>>([new("binary", "https://oi-wiki.org/basic/binary/"), new("duplicate", "https://oi-wiki.org/basic/binary/"), new("private", "https://127.0.0.1"), new("outside", "https://example.com")]);
        }
        public Task<WebPage> FetchAsync(string url, CancellationToken ct)
        {
            Fetches++; if (Fail) throw new InvalidOperationException("unavailable");
            return Task.FromResult(new WebPage(url, "二分查找", Content, DateTimeOffset.UtcNow));
        }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
