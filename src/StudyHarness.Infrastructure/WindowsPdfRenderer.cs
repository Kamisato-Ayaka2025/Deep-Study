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
            var size = page.Size;
            if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
                throw new PdfPreparationException($"PDF_PAGE_SIZE：第{number}页尺寸无效，未发送该页。");
            byte[]? bytes = null; var edgeUsed = 2000; var normalized = false;
            foreach (var edge in new[] { 2000, 1600, 1280, 1024 })
            {
                ct.ThrowIfCancellationRequested();
                var scale = edge / Math.Max(size.Width, size.Height);
                var width = (uint)Math.Max(1, Math.Floor(size.Width * scale)); var height = (uint)Math.Max(1, Math.Floor(size.Height * scale));
                using var output = new InMemoryRandomAccessStream();
                try { await page.RenderToStreamAsync(output, new PdfPageRenderOptions { DestinationWidth = width, DestinationHeight = height, BitmapEncoderId = BitmapEncoder.PngEncoderId }).AsTask(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) when (e is not OutOfMemoryException)
                { throw new PdfPreparationException($"PDF_RENDER：第{number}页无法生成扫描图像（错误码{e.HResult:X8}），未发送该页；请检查PDF能否正常打开。"); }
                if (output.Size == 0) continue;
                // Windows may encode high bit-depth PNGs. Normalize to bounded 8-bit pixels
                // before reducing resolution; pixel dimensions alone do not bound PNG bytes.
                output.Seek(0);
                var decoder = await BitmapDecoder.CreateAsync(output).AsTask(ct);
                if ((long)decoder.PixelWidth * decoder.PixelHeight > 64000000) throw new PdfPreparationException($"PDF_PIXEL_LIMIT：第{number}页渲染尺寸超过安全限制，未发送该页。");
                using var compact = new InMemoryRandomAccessStream();
                var encoded = output;
                if (output.Size > 4194304 || (long)decoder.PixelWidth * decoder.PixelHeight > 4000000)
                {
                    var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                        new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant },
                        ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, compact).AsTask(ct);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels.DetachPixelData());
                    await encoder.FlushAsync().AsTask(ct); encoded = compact; normalized = true;
                }
                if (encoded.Size is 0 or > 4194304) continue;
                encoded.Seek(0); using var stream = encoded.AsStreamForRead(); bytes = new byte[(int)encoded.Size]; await stream.ReadExactlyAsync(bytes, ct);
                edgeUsed = edge; break;
            }
            if (bytes is null) throw new PdfPreparationException($"PDF_IMAGE_LIMIT：第{number}页自动调整后仍超过4 MiB页图限制，未发送该页。");
            // Preserve old cache identity when the original 2000px rendering still fits.
            result.Add(new(collection, fingerprint, number, edgeUsed == 2000 && !normalized ? "windows-pdf-2000-v1" : $"windows-pdf-{edgeUsed}-v2", bytes, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        return result;
    }
}
