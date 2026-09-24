using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AirCard.Core
{
    public sealed class CommunityCard
    {
        public string Name { get; internal set; }
        public string Uploader { get; internal set; }
        public string Description { get; internal set; }
        public bool Fanmade { get; internal set; }
        public string Type { get; internal set; }
        public string Source { get; internal set; }
        internal string Digest { get; set; }
        public string Detail { get { return Type.ToUpperInvariant() + " · " + Uploader + (Fanmade ? " · 二创" : ""); } }
    }

    public static class CommunityCards
    {
        public const string IndexUrl = "https://raw.githubusercontent.com/Feryquence/AirCard/cards/cards.json";
        const string AssetPrefix = "https://raw.githubusercontent.com/Feryquence/AirCard/cards/files/";
        const int IndexLimit = 2 * 1024 * 1024;
        const int ImageLimit = 10 * 1024 * 1024;
        const int PdfLimit = 25 * 1024 * 1024;
        static readonly Regex AssetName = new Regex("^[0-9a-f]{64}\\.(png|jpg|pdf)$", RegexOptions.CultureInvariant);

        public static IList<CommunityCard> Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length > IndexLimit) throw new InvalidDataException("卡面库索引过大或无效。");
            Dictionary<string, object> root;
            try { root = Storage.Json().DeserializeObject(new UTF8Encoding(false, true).GetString(bytes)) as Dictionary<string, object>; }
            catch (Exception e) { throw new InvalidDataException("卡面库索引不是有效的 JSON。", e); }
            object value;
            if (root == null || !root.TryGetValue("cards", out value) || !(value is object[])) throw new InvalidDataException("卡面库索引缺少 cards 列表。");
            var entries = (object[])value;
            if (entries.Length > 10000) throw new InvalidDataException("卡面库条目过多。");
            var result = new List<CommunityCard>(entries.Length);
            foreach (object entry in entries)
            {
                var row = entry as Dictionary<string, object>;
                if (row == null) throw new InvalidDataException("卡面库条目格式无效。");
                var card = new CommunityCard {
                    Name = Field(row, "name", 120), Uploader = Field(row, "uploader", 120),
                    Description = Field(row, "description", 2000, true),
                    Type = Field(row, "type", 3), Source = Field(row, "source", 200)
                };
                if (!row.TryGetValue("fanmade", out value) || !(value is bool)) throw new InvalidDataException("卡面库二创字段无效。");
                card.Fanmade = (bool)value;
                if (!card.Source.StartsWith(AssetPrefix, StringComparison.Ordinal)) throw new InvalidDataException("卡面库文件地址无效。");
                string file = card.Source.Substring(AssetPrefix.Length);
                if (!AssetName.IsMatch(file) || !file.EndsWith("." + card.Type, StringComparison.Ordinal)) throw new InvalidDataException("卡面库文件地址或格式无效。");
                card.Digest = file.Substring(0, 64);
                result.Add(card);
            }
            return result;
        }

        static string Field(Dictionary<string, object> row, string key, int limit, bool empty = false)
        {
            object value;
            if (!row.TryGetValue(key, out value) || !(value is string)) throw new InvalidDataException("卡面库字段无效: " + key);
            string text = (string)value;
            if (text.Length > limit || (!empty && string.IsNullOrWhiteSpace(text)) || text.Any(char.IsControl)) throw new InvalidDataException("卡面库字段无效: " + key);
            return text;
        }

        public static IList<CommunityCard> Fetch(CancellationToken cancellation)
        {
            return Parse(Read(IndexUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(), IndexLimit, cancellation));
        }

        public static byte[] Download(CommunityCard card, CancellationToken cancellation)
        {
            if (card == null || card.Digest == null || !card.Source.Equals(AssetPrefix + card.Digest + "." + card.Type, StringComparison.Ordinal) || !AssetName.IsMatch(card.Digest + "." + card.Type))
                throw new InvalidDataException("卡面库文件地址无效。");
            byte[] bytes = Read(card.Source, card.Type == "pdf" ? PdfLimit : ImageLimit, cancellation);
            Verify(card, bytes);
            return bytes;
        }

        public static void Verify(CommunityCard card, byte[] bytes)
        {
            if (card == null || bytes == null || card.Digest == null) throw new InvalidDataException("卡面库文件无效。");
            int limit = card.Type == "pdf" ? PdfLimit : ImageLimit;
            if (bytes.Length == 0 || bytes.Length > limit) throw new InvalidDataException("卡面库文件大小无效。");
            bool signature = card.Type == "png" && bytes.Length >= 8 && bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || card.Type == "jpg" && bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255
                || card.Type == "pdf" && bytes.Length >= 5 && Encoding.ASCII.GetString(bytes, 0, 5) == "%PDF-";
            if (!signature) throw new InvalidDataException("下载的文件格式与索引不符。");
            using (var sha = SHA256.Create())
            {
                string digest = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                if (!digest.Equals(card.Digest, StringComparison.Ordinal)) throw new InvalidDataException("下载的卡面文件校验失败。");
            }
        }

        static byte[] Read(string url, int limit, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET"; request.UserAgent = "AirCard/1.3"; request.Accept = "application/json, image/png, image/jpeg, application/pdf";
            request.AllowAutoRedirect = false; request.Timeout = 15000; request.ReadWriteTimeout = 15000;
            request.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            using (cancellation.Register(() => request.Abort()))
            {
                try
                {
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var input = response.GetResponseStream())
                    using (var output = new MemoryStream())
                    {
                        if (response.StatusCode != HttpStatusCode.OK || response.ContentLength > limit) throw new InvalidDataException("卡面库文件过大或响应无效。");
                        byte[] buffer = new byte[8192]; int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            if (output.Length + read > limit) throw new InvalidDataException("卡面库文件超过大小限制。");
                            output.Write(buffer, 0, read);
                        }
                        return output.ToArray();
                    }
                }
                catch (WebException) when (cancellation.IsCancellationRequested) { throw new OperationCanceledException(cancellation); }
            }
        }
    }
}
