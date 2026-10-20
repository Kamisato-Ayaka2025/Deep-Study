using System.Security.Cryptography;
using StudyHarness.Application;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace StudyHarness.Infrastructure;

internal static class WindowsPdfRenderer
{
    public static async Task<IReadOnlyList<RenderedPage>> RenderAsync(string path, Guid collection, string fingerprint, IReadOnlyList<int> pages, CancellationToken ct)
    {
        using var source = SafeKnowledgeFiles.OpenRead(path, 512L * 1024 * 1024);
        if (Convert.ToHexString(await SHA256.HashDataAsync(source.Stream, ct)) != fingerprint) throw new InvalidDataException("Source changed; reimport before OCR");
        source.Stream.Position = 0; using var input = source.Stream.AsRandomAccessStream();
        var document = await PdfDocument.LoadFromStreamAsync(input).AsTask(ct);
        if (document.PageCount > 2000 || pages.Any(p => p < 1 || p > document.PageCount)) throw new InvalidDataException("Page range");
        var result = new List<RenderedPage>();
        foreach (var number in pages)
        {
            ct.ThrowIfCancellationRequested(); using var page = document.GetPage((uint)(number - 1));
            var size = page.Size; var scale = 2000 / Math.Max(size.Width, size.Height);
            var width = (uint)Math.Max(1, Math.Floor(size.Width * scale)); var height = (uint)Math.Max(1, Math.Floor(size.Height * scale));
            if ((long)width * height > 4000000) throw new InvalidDataException("Image pixel budget");
            using var output = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(output, new PdfPageRenderOptions { DestinationWidth = width, DestinationHeight = height, BitmapEncoderId = BitmapEncoder.PngEncoderId }).AsTask(ct);
            if (output.Size > 4194304) throw new InvalidDataException("PNG byte budget");
            output.Seek(0); using var stream = output.AsStreamForRead(); var bytes = new byte[(int)output.Size]; await stream.ReadExactlyAsync(bytes, ct);
            result.Add(new(collection, fingerprint, number, "windows-pdf-2000-v1", bytes, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        return result;
    }
}
