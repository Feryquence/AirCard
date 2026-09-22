using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media.Imaging;

namespace AirCard.Core
{
    public sealed class PreparedSkin
    {
        public const int Width = 1536, Height = 969;
        public byte[] Png { get; private set; }
        public byte[] Pdf { get; private set; }
        public int SourceWidth { get; private set; }
        public int SourceHeight { get; private set; }
        public bool IsPdf { get; private set; }
        public CardArtworkFormat TargetFormat { get; private set; }
        public ImportedArtworkFormat InputFormat { get; private set; }
        public static PreparedSkin LoadPreview(string path)
        {
            return Load(path, CardFormatMatching.ReadInputFormat(path) == ImportedArtworkFormat.Pdf ? CardArtworkFormat.Pdf : CardArtworkFormat.Png);
        }
        public PreparedSkin ForTarget(CardArtworkFormat target)
        {
            if (target != CardArtworkFormat.Png && target != CardArtworkFormat.Pdf) throw new ArgumentException("请选择 PNG 或 PDF 格式。");
            byte[] pdf = null;
            if (target == CardArtworkFormat.Pdf)
            {
                pdf = Pdf;
                if (pdf == null)
                    using (var stream = new MemoryStream(Png)) using (var bitmap = new Bitmap(stream)) pdf = ToPdf(bitmap);
            }
            return new PreparedSkin { Png = Png, Pdf = pdf, IsPdf = IsPdf, InputFormat = InputFormat,
                SourceWidth = SourceWidth, SourceHeight = SourceHeight, TargetFormat = target };
        }
        public static PreparedSkin Load(string path)
        {
            return Load(path, CardArtworkFormat.Both);
        }
        public static PreparedSkin Load(string path, CardArtworkFormat target)
        {
            if (target != CardArtworkFormat.Png && target != CardArtworkFormat.Pdf && target != CardArtworkFormat.Both) throw new ArgumentException("未知输出格式。");
            var inputFormat = CardFormatMatching.ReadInputFormat(path);
            if (inputFormat == ImportedArtworkFormat.Pdf)
            {
                if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new IOException("PDF 不能超过 32 MB。");
                byte[] pdf = File.ReadAllBytes(path);
                var rendered = PdfArtwork.Render(pdf);
                return new PreparedSkin { Pdf = target == CardArtworkFormat.Png ? null : pdf, Png = rendered, IsPdf = true, InputFormat = inputFormat, TargetFormat = target, SourceWidth = Width, SourceHeight = Height };
            }
            byte[] bytes = File.ReadAllBytes(path);
            if (Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (var s = new MemoryStream(bytes))
                    {
                        var decoder = BitmapDecoder.Create(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(decoder.Frames[0]);
                        using (var output = new MemoryStream()) { encoder.Save(output); bytes = output.ToArray(); }
                    }
                }
                catch (Exception e) { throw new IOException("此 Windows 没有可用的 WebP 图像解码器，请转换为 PNG/JPG 后导入。", e); }
            }
            using (var s = new MemoryStream(bytes)) using (var source = Image.FromStream(s, true, true))
            {
                if ((long)source.Width * source.Height > 100000000) throw new IOException("图片尺寸过大，请缩小后再试。");
                var result = new PreparedSkin { SourceWidth = source.Width, SourceHeight = source.Height, InputFormat = inputFormat, TargetFormat = target };
                double scale = Math.Max((double)Width / source.Width, (double)Height / source.Height);
                float cropWidth = (float)(Width / scale), cropHeight = (float)(Height / scale);
                using (var image = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(image))
                    using (var attributes = new ImageAttributes())
                    {
                        g.CompositingMode = CompositingMode.SourceCopy; g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        attributes.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(source, new Rectangle(0, 0, Width, Height), (source.Width - cropWidth) / 2, (source.Height - cropHeight) / 2, cropWidth, cropHeight, GraphicsUnit.Pixel, attributes);
                    }
                    using (var output = new MemoryStream()) { image.Save(output, ImageFormat.Png); result.Png = output.ToArray(); }
                    if (target != CardArtworkFormat.Png) result.Pdf = ToPdf(image);
                }
                return result;
            }
        }
        static byte[] ToPdf(Bitmap bitmap)
        {
            int width = bitmap.Width, height = bitmap.Height;
            byte[] rgb = new byte[width * height * 3];
            using (var flat = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(flat)) { g.Clear(Color.White); g.DrawImageUnscaled(bitmap, 0, 0); }
                var locked = flat.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var row = new byte[Math.Abs(locked.Stride)];
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(locked.Scan0, y * locked.Stride), row, 0, row.Length);
                        for (int x = 0; x < width; x++) { int i = (y * width + x) * 3; rgb[i] = row[x * 3 + 2]; rgb[i + 1] = row[x * 3 + 1]; rgb[i + 2] = row[x * 3]; }
                    }
                }
                finally { flat.UnlockBits(locked); }
            }
            byte[] compressed;
            using (var stream = new MemoryStream())
            {
                stream.WriteByte(0x78); stream.WriteByte(0x9c);
                using (var deflate = new DeflateStream(stream, CompressionLevel.Optimal, true)) deflate.Write(rgb, 0, rgb.Length);
                uint a = 1, b = 0; foreach (byte v in rgb) { a = (a + v) % 65521; b = (b + a) % 65521; }
                uint adler = (b << 16) | a; for (int shift = 24; shift >= 0; shift -= 8) stream.WriteByte((byte)(adler >> shift));
                compressed = stream.ToArray();
            }
            using (var output = new MemoryStream())
            {
                Action<string> write = text => { byte[] data = Encoding.ASCII.GetBytes(text); output.Write(data, 0, data.Length); };
                var offsets = new List<long>();
                Action<int, string> obj = (id, text) => { offsets.Add(output.Position); write(id + " 0 obj\n" + text + "\nendobj\n"); };
                write("%PDF-1.4\n");
                obj(1, "<< /Type /Catalog /Pages 2 0 R >>"); obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
                obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + width + " " + height + "] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>");
                string content = "q\n" + width + " 0 0 " + height + " 0 0 cm\n/Im0 Do\nQ\n";
                obj(4, "<< /Length " + content.Length + " >>\nstream\n" + content + "endstream");
                offsets.Add(output.Position);
                write("5 0 obj\n<< /Type /XObject /Subtype /Image /Width " + width + " /Height " + height + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length " + compressed.Length + " >>\nstream\n");
                output.Write(compressed, 0, compressed.Length); write("\nendstream\nendobj\n");
                long xref = output.Position; write("xref\n0 6\n0000000000 65535 f \n");
                foreach (long off in offsets) write(off.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
                write("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n" + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n"); return output.ToArray();
            }
        }
    }
}
