using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using StudyHarness.Contracts;

namespace StudyHarness.Desktop;

public sealed class LessonWebView : WebView2
{
    public const string Origin = "https://reader.studyharness.invalid";
    public event Func<JsonElement, Task>? ReaderMessage;
    public bool ReaderReady { get; private set; }
    public int BlockedRequests { get; private set; }
    public async Task InitializeAsync(string root)
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "reader-profile"));
        await EnsureCoreWebView2Async(environment);
        var settings = CoreWebView2.Settings;
        settings.AreHostObjectsAllowed = false; settings.AreDevToolsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreDefaultContextMenusEnabled = false; settings.IsStatusBarEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false; settings.IsPasswordAutosaveEnabled = false; settings.IsGeneralAutofillEnabled = false;
        CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        CoreWebView2.DownloadStarting += (_, e) => e.Cancel = true;
        CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
        CoreWebView2.NavigationStarting += (_, e) => { if (e.Uri != Origin + "/index.html") e.Cancel = true; };
        CoreWebView2.FrameNavigationStarting += (_, e) => e.Cancel = true;
        var assets = Path.Combine(AppContext.BaseDirectory, "ReaderAssets");
        var files = Directory.GetFiles(assets, "*", SearchOption.AllDirectories).ToDictionary(
            p => Origin + "/" + Path.GetRelativePath(assets, p).Replace('\\', '/'), p => p, StringComparer.Ordinal);
        CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        CoreWebView2.WebResourceRequested += (_, e) =>
        {
            if (!files.TryGetValue(e.Request.Uri, out var file) || e.Request.Method != "GET")
            { BlockedRequests++; e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", ""); return; }
            var mime = Path.GetExtension(file) switch { ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css", ".woff2" => "font/woff2", ".woff" => "font/woff", ".ttf" => "font/ttf", _ => "application/octet-stream" };
            e.Response = environment.CreateWebResourceResponse(File.OpenRead(file), 200, "OK",
                "Content-Type: " + mime + "\r\nContent-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'; connect-src 'none'; img-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'\r\nX-Content-Type-Options: nosniff");
        };
        CoreWebView2.WebMessageReceived += async (_, e) =>
        {
            if (e.Source != Origin + "/index.html") return;
            try
            {
                if (e.WebMessageAsJson.Length > 4096) return;
                using var doc = JsonDocument.Parse(e.WebMessageAsJson); var message = doc.RootElement.Clone();
                if (message.GetProperty("action").GetString() == "ready") ReaderReady = true;
                if (ReaderMessage is { } handler) await handler(message);
            }
            catch (Exception) { /* Invalid/untrusted bridge payload cannot invoke other capabilities. */ }
        };
        CoreWebView2.Navigate(Origin + "/index.html");
    }
    public void ShowLesson(LessonReader reader) => CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "lesson", value = reader }, Wire.Options));
    public void ClearLesson() => CoreWebView2.PostWebMessageAsJson("{\"type\":\"clear\"}");
}
