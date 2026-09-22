using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace AirCard.Core
{
    // Uses the local Windows PDF renderer; no external application or network.
    internal static class PdfArtwork
    {
        internal static byte[] Render(byte[] bytes)
        {
            try { return RenderAsync(bytes).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { throw; }
            catch (Exception error) { throw new IOException("无法读取 PDF，请使用未加密的单页 PDF。PDF 导入需要 Windows 10 或更新版本。", error); }
        }
        static async Task<byte[]> RenderAsync(byte[] bytes)
        {
            using (var input = new MemoryStream(bytes))
            using (var source = input.AsRandomAccessStream())
            {
                var document = await PdfDocument.LoadFromStreamAsync(source).AsTask().ConfigureAwait(false);
                if (document.IsPasswordProtected) throw new InvalidDataException("不支持加密 PDF，请解密后再导入。");
                if (document.PageCount != 1) throw new InvalidDataException("卡面 PDF 只能有一页，请将需要的页面另存为单页 PDF。");
                using (var page = document.GetPage(0))
                using (var output = new InMemoryRandomAccessStream())
                {
                    double width = page.Size.Width, height = page.Size.Height;
                    if (double.IsNaN(width) || double.IsNaN(height) || double.IsInfinity(width) || double.IsInfinity(height) || width <= 0 || height <= 0)
                        throw new InvalidDataException("PDF 页面尺寸无效。");
                    double scale = Math.Max(PreparedSkin.Width / width, PreparedSkin.Height / height);
                    double cropWidth = PreparedSkin.Width / scale, cropHeight = PreparedSkin.Height / scale;
                    var options = new PdfPageRenderOptions {
                        SourceRect = new Rect((width - cropWidth) / 2, (height - cropHeight) / 2, cropWidth, cropHeight),
                        DestinationWidth = PreparedSkin.Width, DestinationHeight = PreparedSkin.Height
                    };
                    await page.RenderToStreamAsync(output, options).AsTask().ConfigureAwait(false);
                    output.Seek(0);
                    using (var stream = output.AsStreamForRead())
                    using (var png = new MemoryStream())
                    {
                        await stream.CopyToAsync(png).ConfigureAwait(false);
                        return png.ToArray();
                    }
                }
            }
        }
    }
}
