using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AirCard.Core
{
    public sealed class SavedCard
    {
        public string Udid { get; set; }
        public string Hash { get; set; }
        public string Name { get; set; }
        public override string ToString() { return (string.IsNullOrWhiteSpace(Name) ? "卡片" : Name) + " · " + Hash; }
    }
    public static class CardScanner
    {
        static readonly Regex HashPattern = new Regex(@"^(?:[A-Za-z0-9+_-]{27}=?|[A-Za-z0-9+_-]{43}=?)$", RegexOptions.Compiled);
        static readonly Regex[] Patterns = {
            new Regex(@"/(?:Library/Passes/|Passes/)?Cards/([A-Za-z0-9+_-]{27}=?|[A-Za-z0-9+_-]{43}=?)(?=\.(?:pkpass|cache|pkcache)\b|[/\s'""<>)\],;:]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"/([A-Za-z0-9+_-]{27}=?|[A-Za-z0-9+_-]{43}=?)\.pkpass\b", RegexOptions.Compiled),
            new Regex("\\b(?:card[_\\s]?(?:hash|id)|pass[_\\s]?(?:hash|id)|unique[_\\s]?id)\\s*[:=]\\s*['\"]?([A-Za-z0-9+=_-]{27,44})(?=$|[^A-Za-z0-9+=_/-])", RegexOptions.IgnoreCase | RegexOptions.Compiled)
        };
        static readonly Regex BareHash = new Regex(@"(?<![A-Za-z0-9+/_=-])([A-Za-z0-9+_-]{27}=|[A-Za-z0-9+_-]{43}=)(?![A-Za-z0-9+/_=-])", RegexOptions.Compiled);
        static readonly Regex OtherHashLabel = new Regex(@"\b(?:digest|checksum|signature|request[_\s]?id|transaction[_\s]?id)\s*[:=]\s*['""<>]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly string[] Keywords = { "passd", "passbook", "passkit", "stockholm", "nanopassd", "npkcompanion", "wallet", "/cards/", "/passes/" };
        static readonly Regex NamePattern = new Regex("(?:description|localizedDescription|passName|title)\\s*[:=]\\s*['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase);
        public static bool ValidHash(string hash)
        {
            if (hash == null || !HashPattern.IsMatch(hash)) return false;
            // A hash is one path component. Reject '/' even if supplied as Base64.
            if (new[] { "M6nDwZrkYbFlsodLgCbvyFZQ1cc", "kJL-D0rr-SZhbj2c8nK-OQ9hCMY", "hwAtAmHKYwsQrJbT5cTNDsaxVME" }.Contains(hash.TrimEnd('='))) return false;
            try
            {
                // Decode only for validation. Never replace the original path spelling.
                string encoded = hash.Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
                var bytes = Convert.FromBase64String(encoded);
                return (bytes.Length == 20 || bytes.Length == 32) && bytes.Distinct().Count() >= 12;
            }
            catch (FormatException) { return false; }
        }
        public static SavedCard Parse(string line)
        {
            if (!Keywords.Any(k => line.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) return null;
            foreach (var re in Patterns)
            {
                foreach (Match match in re.Matches(line))
                    if (ValidHash(match.Groups[1].Value)) return FromMatch(line, match);
            }
            // Some iOS versions log only a padded identifier with the Wallet event.
            // Keep the legacy format, but exclude clearly labelled non-card digests.
            foreach (Match match in BareHash.Matches(line))
                if (ValidHash(match.Groups[1].Value) && !OtherHashLabel.IsMatch(line.Substring(0, match.Index))) return FromMatch(line, match);
            return null;
        }
        static SavedCard FromMatch(string line, Match match)
        {
            var name = NamePattern.Match(line);
            string label = name.Success && !name.Groups[1].Value.Contains("<private>") ? name.Groups[1].Value : "卡片";
            return new SavedCard { Hash = match.Groups[1].Value, Name = label };
        }
        public static void DeleteLegacyHistory() { string path = Path.Combine(Storage.Root, "cards.json"); if (File.Exists(path)) File.Delete(path); }
        public static SavedCard Scan(string udid, ConnectionMode mode, CancellationToken stop, Action<string> log)
        {
            SavedCard card;
            using (var session = DeviceSession.Open(udid, mode))
            using (var service = session.Service("com.apple.syslog_relay"))
            {
                service.Timeout(500); log("扫描已开始。在 iPhone 钱包中点开目标卡片。");
                card = ReadFirst(buffer => {
                    int n = Native.AMDServiceConnectionReceive(service.Handle, buffer, (UIntPtr)(uint)buffer.Length);
                    if (n < 0) { int error = Native.WSAGetLastError(); if (error != 0 && error != 10060 && error != 10035 && error != 10004) throw new IOException("日志连接断开，错误 " + error); }
                    return n;
                }, stop);
                if (card != null) card.Udid = udid;
            }
            // Both native handles are disposed before returning the first card to the UI.
            log(card == null ? "卡片扫描已停止。" : "已识别到第一张卡片，扫描已自动停止。");
            return card;
        }
        public static SavedCard ReadFirst(Func<byte[], int> receive, CancellationToken stop)
        {
            byte[] buffer = new byte[8192]; var line = new List<byte>();
            while (!stop.IsCancellationRequested)
            {
                int n = receive(buffer);
                if (stop.IsCancellationRequested) return null;
                if (n == 0) throw new IOException("手机已关闭扫描连接。");
                if (n < 0) { stop.WaitHandle.WaitOne(50); continue; }
                if (n > buffer.Length) throw new IOException("Invalid syslog length.");
                for (int i = 0; i < n && !stop.IsCancellationRequested; i++)
                {
                    byte b = buffer[i];
                    if (b == 0 || b == 10)
                    {
                        var card = Parse(Encoding.UTF8.GetString(line.ToArray())); line.Clear();
                        if (card != null) return card;
                    }
                    else if (b != 13) { if (line.Count < 65536) line.Add(b); else line.Clear(); }
                }
            }
            return null;
        }
    }
}
