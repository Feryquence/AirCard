using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AirCard.Core
{
    [Flags]
    public enum CardArtworkFormat { Unknown = 0, Png = 1, Pdf = 2, Both = Png | Pdf }
    public enum ImportedArtworkFormat { Png, Pdf, OtherImage }

    public static class CardFormatMatching
    {
        // Select one artwork family. Auxiliary diffuse/strip/background files
        // must not change the format of an available combined card background.
        static readonly string[][] Families = {
            new[] { "cardBackgroundCombined@3x.png", "cardBackgroundCombined@2x.png", "cardBackgroundCombined.png", "cardBackgroundCombined.pdf" },
            new[] { "background@3x.png", "background@2x.png", "background.pdf" },
            new[] { "strip@3x.png", "strip@2x.png", "strip.pdf" }
        };
        static CardArtworkFormat AssetFormat(string asset) { return asset.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? CardArtworkFormat.Pdf : CardArtworkFormat.Png; }
        public static CardArtworkFormat FromAssets(IEnumerable<string> assets)
        {
            var names = new HashSet<string>(assets, StringComparer.Ordinal);
            foreach (var family in Families)
            {
                var format = CardArtworkFormat.Unknown;
                foreach (string asset in family) if (names.Contains(asset)) format |= AssetFormat(asset);
                if (format != CardArtworkFormat.Unknown) return format;
            }
            return CardArtworkFormat.Unknown;
        }
        public static CardArtworkFormat Detect(Func<string, byte[]> read)
        {
            foreach (var family in Families)
            {
                var format = CardArtworkFormat.Unknown;
                foreach (string asset in family)
                {
                    var candidate = AssetFormat(asset);
                    if ((format & candidate) != 0) continue;
                    byte[] bytes = read(asset);
                    if (bytes == null) continue;
                    WalletEngine.ValidateArtwork(bytes, asset);
                    format |= candidate;
                    if (format == CardArtworkFormat.Both) break;
                }
                if (format != CardArtworkFormat.Unknown) return format;
            }
            return CardArtworkFormat.Unknown;
        }
        public static string Label(CardArtworkFormat format)
        {
            switch (format) {
                case CardArtworkFormat.Png: return "PNG";
                case CardArtworkFormat.Pdf: return "PDF";
                case CardArtworkFormat.Both: return "PNG / PDF";
                default: return "未识别";
            }
        }
        public static ImportedArtworkFormat ReadInputFormat(string path)
        {
            var bytes = new byte[12]; int count;
            using (var stream = File.OpenRead(path)) count = stream.Read(bytes, 0, bytes.Length);
            if (count >= 5 && System.Text.Encoding.ASCII.GetString(bytes, 0, 5) == "%PDF-") return ImportedArtworkFormat.Pdf;
            if (count >= 8 && bytes.Take(8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) return ImportedArtworkFormat.Png;
            if (count >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255) return ImportedArtworkFormat.OtherImage;
            if (count == 12 && System.Text.Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP") return ImportedArtworkFormat.OtherImage;
            throw new InvalidDataException("无法识别导入文件，请选择有效的 PNG、JPG、WebP 或单页 PDF。");
        }
        public static CardArtworkFormat Target(CardArtworkFormat card, ImportedArtworkFormat input)
        {
            if (card == CardArtworkFormat.Unknown) throw new InvalidOperationException("未读到卡片的原始卡面，无法判断 PNG/PDF 格式。缓存图片不能用于判断，请重新扫描后重试。");
            if (card == CardArtworkFormat.Both) throw new InvalidOperationException("主卡面同时存在 PNG 和 PDF，必须先选择要覆盖的格式。");
            if (card != CardArtworkFormat.Png && card != CardArtworkFormat.Pdf) throw new ArgumentException("未知卡面格式。");
            return card;
        }
        public static bool NeedsConversion(ImportedArtworkFormat input, CardArtworkFormat target)
        {
            return !(input == ImportedArtworkFormat.Png && target == CardArtworkFormat.Png || input == ImportedArtworkFormat.Pdf && target == CardArtworkFormat.Pdf);
        }
        public static bool Approve(CardArtworkFormat card, ImportedArtworkFormat input, Func<string, bool> confirm)
        {
            var target = Target(card, input);
            if (!NeedsConversion(input, target)) return true;
            string source = input == ImportedArtworkFormat.OtherImage ? "JPG / WebP 图片" : input == ImportedArtworkFormat.Pdf ? "PDF" : "PNG";
            return confirm("已识别的原始卡面格式：" + Label(card) + "。\n导入文件格式：" + source + "。\n\n是否转换为 " + Label(target) + " 后应用？" +
                (target == CardArtworkFormat.Png ? "\n将生成 1536 × 969 PNG。" + (input == ImportedArtworkFormat.Pdf ? "PDF 的矢量内容将栅格化。" : "") : "\n将图片封装为 PDF，不会变成矢量图。"));
        }
    }
}
