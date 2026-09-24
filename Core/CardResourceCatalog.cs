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
    public sealed class CardResourceCatalog
    {
        public string Udid { get; internal set; }
        public string Hash { get; internal set; }
        public string Description { get; internal set; }
        public List<string> LocalAssets { get; } = new List<string>();
        public IEnumerable<string> DeviceAssets { get { return LocalAssets.Concat(RemoteAssets.Keys).Where(IsArtworkPath).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal); } }
        public Dictionary<string, RemoteArtwork> RemoteAssets { get; } = new Dictionary<string, RemoteArtwork>(StringComparer.Ordinal);
        public string[] Assets { get { return DeviceAssets.ToArray(); } }
        public static bool IsArtworkPath(string path) { return IsResourcePath(path) && !path.EndsWith(".urls", StringComparison.OrdinalIgnoreCase); }
        public static bool IsResourcePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 180 || path.IndexOfAny(new[] { '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0 || path.Any(c => c < 32)) return false;
            foreach (string part in path.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ")) return false;
                if (Regex.IsMatch(part.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) return false;
            }
            string image = path.EndsWith(".urls", StringComparison.OrdinalIgnoreCase) ? path.Substring(0, path.Length - 5) : path;
            return new[] { ".png", ".jpg", ".jpeg", ".pdf", ".webp", ".heic", ".heif", ".bmp", ".gif", ".tif", ".tiff" }.Contains(Path.GetExtension(image).ToLowerInvariant());
        }
        static Dictionary<string, object> Json(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("资源清单大小无效。");
            var serializer = Storage.Json(); serializer.MaxJsonLength = 4 * 1024 * 1024; serializer.RecursionLimit = 16;
            Dictionary<string, object> parsed;
            try { parsed = serializer.DeserializeObject(new UTF8Encoding(false, true).GetString(bytes)) as Dictionary<string, object>; }
            catch (ArgumentException) { throw new InvalidDataException("资源清单不是有效的 UTF-8 JSON。"); }
            if (parsed == null || parsed.Count > 2048) throw new InvalidDataException("资源清单格式无效。");
            return parsed;
        }
        public static string[] ManifestAssets(byte[] bytes)
        {
            var names = Json(bytes).Keys.Where(IsResourcePath).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw new InvalidDataException("资源清单包含 Windows 无法区分的同名文件。");
            return names;
        }
        internal void AddLocal(IEnumerable<string> names)
        {
            foreach (string name in names.Where(IsResourcePath))
            {
                if (LocalAssets.Contains(name)) continue;
                if (LocalAssets.Concat(RemoteAssets.Keys).Contains(name, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("资源存在 Windows 无法区分的同名文件。");
                LocalAssets.Add(name);
            }
        }
        internal void AddRemote(RemoteArtwork[] resources)
        {
            // A .urls reference may also have a downloaded local copy. Keep both
            // sources selectable without treating the reference as proof of a file.
            foreach (var resource in resources)
            {
                if (Assets.Contains(resource.Name, StringComparer.OrdinalIgnoreCase) && !Assets.Contains(resource.Name))
                    throw new InvalidDataException("远程资源与本地文件名称冲突。");
                RemoteArtwork previous;
                if (RemoteAssets.TryGetValue(resource.Name, out previous) && (previous.Size != resource.Size || !string.Equals(previous.Sha1, resource.Sha1, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("远程索引对同名文件给出了不同的大小或 SHA-1。");
            }
            foreach (var resource in resources)
                if (!RemoteAssets.ContainsKey(resource.Name)) RemoteAssets.Add(resource.Name, resource);
        }
        public static RemoteArtwork[] ParseUrls(string descriptor, byte[] bytes)
        {
            if (!IsResourcePath(descriptor) || !descriptor.EndsWith(".urls", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("资源索引名称无效。");
            string folder = descriptor.Contains("/") ? descriptor.Substring(0, descriptor.LastIndexOf('/') + 1) : "";
            var result = new List<RemoteArtwork>();
            foreach (var entry in Json(bytes))
            {
                var fields = entry.Value as Dictionary<string, object>; object url, size, hash;
                if (fields == null || !fields.TryGetValue("url", out url) || !fields.TryGetValue("size", out size) || !fields.TryGetValue("sha1", out hash)) throw new InvalidDataException("远程资源缺少地址、大小或 SHA-1。");
                if (entry.Key.Contains("/") || !IsResourcePath(folder + entry.Key) || entry.Key.EndsWith(".urls", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("远程图片文件名无效。");
                long length;
                if (!long.TryParse(Convert.ToString(size, System.Globalization.CultureInfo.InvariantCulture), out length)) throw new InvalidDataException("远程图片大小无效。");
                result.Add(new RemoteArtwork(folder + entry.Key, url as string, length, hash as string));
            }
            if (result.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count) throw new InvalidDataException("远程资源存在重复名称。");
            return result.ToArray();
        }
    }
    public sealed class RemoteArtwork
    {
        public string Name { get; private set; }
        public long Size { get; private set; }
        public string Sha1 { get; private set; }
        readonly Uri uri;
        public RemoteArtwork(string name, string url, long size, string sha1)
        {
            if (!CardResourceCatalog.IsResourcePath(name) || name.EndsWith(".urls", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("远程图片名称无效。");
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !uri.DnsSafeHost.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("只支持 HTTPS Apple 资源地址。");
            if (size <= 0 || size > 32 * 1024 * 1024 || !Regex.IsMatch(sha1 ?? "", "^[a-fA-F0-9]{40}$")) throw new InvalidDataException("远程图片大小或 SHA-1 无效。");
            Name = name; Size = size; Sha1 = sha1;
        }
        public byte[] ReadVerified(Stream stream, CancellationToken cancellation = default(CancellationToken))
        {
            using (var output = new MemoryStream())
            {
                var buffer = new byte[65536];
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, Size - output.Length + 1));
                    if (n == 0) break;
                    if (output.Length + n > Size) throw new InvalidDataException("远程图片大小超过清单记录，未保存。");
                    output.Write(buffer, 0, n);
                }
                byte[] bytes = output.ToArray();
                if (bytes.LongLength != Size) throw new InvalidDataException("远程图片不完整，未保存。");
                using (var hash = SHA1.Create())
                    if (!string.Equals(BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""), Sha1, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("远程图片 SHA-1 校验失败，未保存。");
                cancellation.ThrowIfCancellationRequested(); return bytes;
            }
        }
        public byte[] Download(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.AllowAutoRedirect = false; request.Timeout = 15000; request.ReadWriteTimeout = 5000;
            request.UseDefaultCredentials = false; request.UserAgent = "AirCard";
            using (cancellation.Register(request.Abort))
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode != HttpStatusCode.OK || (response.ContentLength >= 0 && response.ContentLength != Size)) throw new InvalidDataException("远程服务器返回的状态或大小与清单不符，未保存。");
                    using (var stream = response.GetResponseStream()) return ReadVerified(stream, cancellation);
                }
            }
            catch (WebException error)
            {
                if (error.Response != null) error.Response.Dispose();
                cancellation.ThrowIfCancellationRequested();
                // Do not include signed URLs or query parameters in diagnostics.
                throw new IOException("Apple 远程图片下载失败（" + error.Status + "）。");
            }
            catch (IOException) when (cancellation.IsCancellationRequested)
            { throw new OperationCanceledException("已取消远程图片下载。", cancellation); }
        }
    }
}
