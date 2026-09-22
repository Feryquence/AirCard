using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AirCard.Core
{
    public sealed class RecoveryRecord
    {
        public string Token { get; set; }
        public string Udid { get; set; }
        public string Target { get; set; }
        public string Leaf { get; set; }
        public string Phase { get; set; }
        public bool Export { get; set; }
        public Dictionary<string, string> Books { get; set; }
        public List<string> CreatedDirectories { get; set; }
        public List<string> ReturnTokens { get; set; }
    }
    // Kept separate from Apple interop so interrupted exports can be tested offline.
    public interface IExportFiles
    {
        bool RecoveredExists();
        byte[] ReadRecovered();
        void ReturnOriginal();
        void SaveRecoveryPhase(string phase);
        void SaveBackup(byte[] data);
    }
    public static class ExportRecovery
    {
        public static string BeginReturnAttempt(RecoveryRecord record, Action<RecoveryRecord> save)
        {
            // Asset IDs already completed in the read sync may be omitted from the next
            // manifest. Every return and retry needs a new link ID, persisted first.
            string token = Guid.NewGuid().ToString("N");
            if (record.ReturnTokens == null) record.ReturnTokens = new List<string>();
            record.ReturnTokens.Add(token); save(record); return token;
        }
        public static void WatchUnread(Func<bool> stagedExists, Action restoreAndClean, Action saveWatch)
        {
            if (stagedExists()) throw new IOException("暂存卡面已出现，必须先归位。");
            restoreAndClean();
            if (stagedExists()) throw new IOException("清理期间收到延迟卡面，请重试以自动归位。");
            saveWatch();
        }
        public static byte[] ReadAndReturn(IExportFiles files)
        {
            if (!files.RecoveredExists()) throw new IOException("尚未找到暂存卡面，已保留操作记录。请重新连接同一台手机后重试。");
            byte[] bytes = null; var errors = new List<Exception>();
            try { bytes = files.ReadRecovered(); files.SaveBackup(bytes); }
            catch (Exception ex) { errors.Add(ex); }
            // Return the original even if local disk is full or decoding/saving failed.
            try { files.SaveRecoveryPhase("ReturnRequested"); }
            catch (Exception ex) { errors.Add(ex); }
            try { files.ReturnOriginal(); }
            catch (Exception ex) { errors.Add(ex); throw new IOException("卡面尚未确认放回手机。请保留操作记录和备份；程序不会自动接续此操作。", new AggregateException(errors)); }
            if (files.RecoveredExists()) throw new IOException("卡面仍在设备暂存目录，恢复未完成。");
            try { files.SaveRecoveryPhase("Returned"); }
            catch (Exception ex) { errors.Add(ex); }
            if (errors.Count != 0) throw new IOException("卡面已放回，但本地读取、备份或记录保存失败。", new AggregateException(errors));
            return bytes;
        }
    }
    public sealed class CardArtwork
    {
        public string AssetName { get; private set; }
        public byte[] Bytes { get; private set; }
        public bool IsRendered { get { return AssetName == "FrontFace"; } }
        public string Extension { get { return IsRendered ? WalletEngine.DisplayImageExtension(Bytes) : Path.GetExtension(AssetName); } }
        public CardArtwork(string assetName, byte[] bytes)
        {
            if (!WalletEngine.ArtworkAssets.Contains(assetName) && assetName != "FrontFace") throw new ArgumentException("未知卡面资源。");
            WalletEngine.ValidateArtwork(bytes, assetName);
            AssetName = assetName; Bytes = bytes;
        }
    }
    public sealed class WalletEngine
    {
        public static readonly string[] ArtworkAssets = {
            "cardBackgroundCombined@3x.png", "cardBackgroundCombined@2x.png", "cardBackgroundCombined.pdf", "cardBackgroundCombined.png",
            "diffuse@3x.png", "diffuse@2x.png",
            "background@3x.png", "background@2x.png", "background.pdf",
            "strip@3x.png", "strip@2x.png", "strip.pdf"
        };
        public const string AutoPng = "auto-png";
        public const string AutoArtwork = "auto-artwork";
        public static string[] BatchArtworkAssets { get { return ArtworkAssets.Where(leaf => leaf != "cardBackgroundCombined.png").ToArray(); } }
        public static string[] ExportCandidates(string requested)
        {
            if (requested == AutoArtwork) return new[] { ArtworkAssets[0], ArtworkAssets[1], ArtworkAssets[3], ArtworkAssets[2] };
            if (requested == AutoPng) return new[] { ArtworkAssets[0], ArtworkAssets[1], ArtworkAssets[3] };
            if (!ArtworkAssets.Contains(requested)) throw new ArgumentException("只允许导出卡面资源。");
            return new[] { requested };
        }
        public static string TryExportCandidates(string requested, Func<string, bool> attempt)
        {
            foreach (string candidate in ExportCandidates(requested)) if (attempt(candidate)) return candidate;
            return null;
        }
        internal static readonly string[] BooksPaths = BooksConfiguration.Paths;
        readonly Action<string> log;
        readonly List<RecoveryRecord> currentRecords = new List<RecoveryRecord>();
        bool syncRuntimePrepared;
        public WalletEngine(Action<string> log) { this.log = log ?? (_ => { }); }
        static string RecordPath(RecoveryRecord r) { return Path.Combine(Storage.RecoveryRoot, r.Token + ".json"); }
        static string Source(RecoveryRecord r) { return "airlift-src-" + r.Token; }
        static string Link(RecoveryRecord r) { return "airlift-link-" + r.Token; }
        static string Recovered(RecoveryRecord r) { return "airlift-recovered-" + r.Token; }
        static void Persist(RecoveryRecord r) { Storage.Save(RecordPath(r), r); }
        void PrepareOperation(string udid, ConnectionMode mode)
        {
            if (!syncRuntimePrepared) { log(AppleSyncRuntime.PrepareRequired()); syncRuntimePrepared = true; }
            // Each UI action owns a new engine. Only finish late replies from this
            // action; never load historical journals when retrying or restarting.
            if (currentRecords.Count != 0) CompletePending(udid, mode);
        }
        static string CardTarget(string hash)
        {
            if (!CardScanner.ValidHash(hash)) throw new ArgumentException("卡片标识无效。请扫描或填写有效的卡片 hash（不能包含路径分隔符）。");
            return "/var/mobile/Library/Passes/Cards/" + hash + ".pkpass";
        }
        RecoveryRecord Snapshot(Afc afc, string udid, string target, string leaf, bool export)
        {
            var r = new RecoveryRecord { Token = Guid.NewGuid().ToString("N"), Udid = udid, Target = target, Leaf = leaf, Export = export, Phase = "Snapshot", Books = new Dictionary<string, string>(), CreatedDirectories = new List<string>() };
            CaptureBooks(afc, r);
            Persist(r); currentRecords.Add(r); return r;
        }
        static void CaptureBooks(Afc afc, RecoveryRecord r)
        {
            // A delayed original must be restored against today's Books state, not the
            // old snapshot that was already restored when the empty attempt finished.
            var books = new Dictionary<string, string>(); var directories = new List<string>();
            foreach (string dir in new[] { "Books", "Books/Sync" }) if (!afc.Exists(dir)) directories.Add(dir);
            foreach (string path in BooksPaths) books[path] = afc.Exists(path) ? Convert.ToBase64String(afc.Read(path, 64 * 1024 * 1024)) : null;
            r.Books = books; r.CreatedDirectories = directories;
        }
        static void PrepareBooks(Afc afc, IList<AssetMove> moves)
        {
            object[] books = moves.Select((m, i) => (object)Plist.Dict("Persistent ID", m.Identifier, "Item ID", (i + 1).ToString(), "DSID", "1")).ToArray();
            afc.Mkdir("Books/Sync");
            using (var plist = Cf.From(Plist.Dict("Books", books))) afc.Write("Books/Sync/Books.plist", Native.Serialize(plist.Handle, 200));
        }
        static void WaitUntil(Func<bool> condition, string error)
        {
            for (int i = 0; i < 40; i++) { if (condition()) return; Thread.Sleep(250); }
            throw new IOException(error);
        }
        void RestoreBooks(Afc afc, RecoveryRecord r)
        {
            BooksConfiguration.Restore(r.Books, (path, bytes) => {
                if (afc.Exists(path) && afc.Read(path, 64 * 1024 * 1024).SequenceEqual(bytes)) return;
                afc.Mkdir(path.Substring(0, path.LastIndexOf('/'))); afc.Write(path, bytes);
                if (!afc.Read(path, 64 * 1024 * 1024).SequenceEqual(bytes)) throw new IOException("图书配置验证失败: " + path);
            }, afc.Remove);
            foreach (string dir in r.CreatedDirectories.AsEnumerable().Reverse())
            {
                // Keep service-owned database directories and any directory that the
                // service has populated. Only empty directories we created are removed.
                if (dir != "Books/Sync/Database" && afc.Exists(dir) && afc.List(dir).Length == 0) afc.Remove(dir);
            }
        }
        void Cleanup(Afc afc, RecoveryRecord r, bool keepRecord = false)
        {
            // Never remove recovered original artwork. It must have been returned first.
            if (r.Export && afc.ExportStagingExists(Recovered(r))) throw new IOException("暂存卡面尚未归位，拒绝清理。");
            RestoreBooks(afc, r);
            afc.Remove(Link(r)); afc.RemoveStagingTree(Source(r));
            foreach (string token in r.ReturnTokens ?? new List<string>())
            {
                afc.Remove("airlift-link-" + token); afc.RemoveStagingTree("airlift-src-" + token);
            }
            if (!keepRecord) { File.Delete(RecordPath(r)); currentRecords.Remove(r); }
        }
        public CardApplyResult FlashCard(string udid, ConnectionMode mode, string hash, PreparedSkin skin)
        {
            string target = CardTarget(hash);
            var items = ReplacementAssets(skin);
            log("[1/3] 写入钱包卡面 " + CardFormatMatching.Label(skin.TargetFormat) + "…");
            WriteBatch(udid, mode, target, items);
            log("卡面资源移动完成，图书同步配置已还原。开始刷新显示缓存。");
            var result = new CardApplyResult(); int step = 2;
            foreach (string extension in new[] { ".cache", ".pkcache" })
            {
                log("[" + step++ + "/3] 刷新可选缓存 " + extension + "…");
                try
                {
                    var cache = WriteBatch(udid, mode, target.Substring(0, target.Length - 7) + extension,
                        new[] { "FrontFace", "PlaceHolder", "Preview" }.Select(n => new KeyValuePair<string, byte[]>(n, System.Text.Encoding.ASCII.GetBytes("corrupted"))).ToArray(), true);
                    if (cache.HasWarnings) result.CacheWarnings.Add(extension + ": " + string.Join(", ", cache.PendingAssets));
                }
                catch (Exception error) { throw new CardAppliedException(error); }
            }
            log(result.Summary); return result;
        }
        public static IList<KeyValuePair<string, byte[]>> ReplacementAssets(PreparedSkin skin)
        {
            if (skin == null) throw new ArgumentNullException(nameof(skin));
            if (skin.TargetFormat == CardArtworkFormat.Png)
            {
                ValidateArtwork(skin.Png, ArtworkAssets[0]);
                return new[] { new KeyValuePair<string, byte[]>(ArtworkAssets[0], skin.Png), new KeyValuePair<string, byte[]>(ArtworkAssets[1], skin.Png) };
            }
            if (skin.TargetFormat == CardArtworkFormat.Pdf)
            {
                ValidateArtwork(skin.Pdf, ArtworkAssets[2]);
                return new[] { new KeyValuePair<string, byte[]>(ArtworkAssets[2], skin.Pdf) };
            }
            throw new InvalidOperationException("请先识别所选卡片的格式并确认导入，不能同时写入两种格式。");
        }
        public CardArtworkFormat DetectCardFormat(string udid, ConnectionMode mode, string hash)
        {
            string target = CardTarget(hash);
            log("识别卡面原始格式，请保持手机连接…");
            var format = CardFormatMatching.Detect(leaf => TryReadCard(udid, mode, target, leaf));
            PrepareOperation(udid, mode);
            log("已读取的原始资源格式: " + CardFormatMatching.Label(format));
            return format;
        }
        public BatchWriteResult WriteBatch(string udid, ConnectionMode mode, string target, IList<KeyValuePair<string, byte[]>> items, bool optionalCache = false)
        {
            if (items.Count == 0) throw new ArgumentException("没有可写入的资源。");
            if (optionalCache && !IsWalletCacheTarget(target)) throw new ArgumentException("只有钱包显示缓存允许以警告结束。");
            foreach (var item in items) if (item.Key.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0 || item.Key == "." || item.Key == "..") throw new ArgumentException("Unsafe asset leaf.");
            PrepareOperation(udid, mode);
            using (var session = DeviceSession.Open(udid, mode)) using (var afc = new Afc(session))
            {
                byte[] archive = AirTraffic.BuildArchive(target, items.Select(i => i.Value).ToList());
                var r = Snapshot(afc, udid, target, "", false);
                try
                {
                    log("暂存 " + items.Count + " 个资源 → " + target.Substring(target.LastIndexOf('/') + 1) + "…"); AirTraffic.Stage(session, Source(r), archive);
                    var moves = new List<AssetMove> { new AssetMove("../../" + Source(r) + "/p0/p1/p2/link", Link(r)) };
                    moves.AddRange(items.Select((item, index) => new AssetMove("../../" + Source(r) + "/payload_" + index, Link(r) + "/" + item.Key)));
                    PrepareBooks(afc, moves); r.Phase = "WriteRequested"; Persist(r); AirTraffic.Sync(session, moves, log);
                    string[] pendingAssets = null;
                    for (int poll = 0; poll <= 40; poll++)
                    {
                        pendingAssets = items.Select((item, index) => new { item.Key, Path = Source(r) + "/payload_" + index }).Where(item => afc.Exists(item.Path)).Select(item => item.Key).ToArray();
                        if (pendingAssets.Length == 0 || poll == 40) break;
                        Thread.Sleep(250);
                    }
                    var result = BatchWriteResult.Finish(pendingAssets, optionalCache, () => {
                        r.Phase = pendingAssets.Length == 0 ? "Written" : "CacheIncomplete"; Persist(r); Cleanup(afc, r);
                    });
                    if (result.HasWarnings) log("缓存未全部刷新（" + string.Join(", ", result.PendingAssets) + "）。图书同步配置已还原；已写入的卡面保留。");
                    return result;
                }
                catch (Exception e)
                {
                    // A failed native operation may still be completing. Preserve staging
                    // and the on-disk Books snapshot for explicit recovery on reconnect.
                    throw new IOException("写入未完整结束。请保留操作记录；本次操作结束后不会自动接续。记录: " + RecordPath(r), e);
                }
            }
        }
        public static bool IsWalletCacheTarget(string target)
        {
            const string prefix = "/var/mobile/Library/Passes/Cards/";
            if (target == null || !target.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string leaf = target.Substring(prefix.Length);
            string suffix = leaf.EndsWith(".pkcache", StringComparison.Ordinal) ? ".pkcache" : leaf.EndsWith(".cache", StringComparison.Ordinal) ? ".cache" : null;
            return suffix != null && CardScanner.ValidHash(leaf.Substring(0, leaf.Length - suffix.Length));
        }
        public void ExportCard(string udid, ConnectionMode mode, string hash, string leaf, string destination)
        {
            // The UI reads first and then offers the matching file format. This API
            // remains available for callers explicitly choosing PNG or PDF paths.
            var candidates = ExportCandidates(leaf);
            if (candidates.Any(c => !string.Equals(Path.GetExtension(c), Path.GetExtension(destination), StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("输出扩展名必须与卡面格式一致；自动识别格式请使用 ReadCard。");
            string parent = Path.GetDirectoryName(Path.GetFullPath(destination)); Directory.CreateDirectory(parent);
            string probe = Path.Combine(parent, ".aircard-probe-" + Guid.NewGuid().ToString("N"));
            using (var s = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) s.WriteByte(0);
            var artwork = ReadCard(udid, mode, hash, leaf);
            Storage.AtomicWrite(destination, artwork.Bytes); log("已保存卡面原文件: " + destination);
        }
        public WalletBatchExportResult ExportFolder(string udid, ConnectionMode mode, string hash, string parent, bool includeCache)
        {
            string target = CardTarget(hash);
            var result = WalletBatchExport.Export(parent, hash,
                leaf => TryReadRawCard(udid, mode, target, leaf),
                includeCache ? (Func<CardArtwork>)(() => TryReadDisplayedCard(udid, mode, hash)) : null, log);
            // A candidate that did not arrive immediately may arrive later. Check
            // our own staging files before reporting the whole batch complete.
            PrepareOperation(udid, mode);
            log(result.Summary);
            return result;
        }
        public CardArtwork ReadCard(string udid, ConnectionMode mode, string hash, string requested = AutoArtwork)
        {
            string target = CardTarget(hash);
            ExportCandidates(requested);
            log("读取卡片目录: " + target);
            if (requested == AutoArtwork)
            {
                var displayed = TryReadDisplayedCard(udid, mode, hash);
                if (displayed != null) return displayed;
                log("显示缓存未提供支持的图片，继续读取原始 PNG / PDF 资源。");
            }
            CardArtwork artwork = null;
            string exported = TryExportCandidates(requested, candidate => {
                byte[] bytes = TryReadCard(udid, mode, target, candidate);
                if (bytes == null) return false;
                artwork = new CardArtwork(candidate, bytes); return true;
            });
            // Recheck older empty attempts before reporting completion, in case an
            // earlier candidate arrived while a later one was being processed.
            PrepareOperation(udid, mode);
            if (exported == null) throw new IOException("设备没有返回卡面文件。已尝试 " + string.Join("、", ExportCandidates(requested)) + "；图书同步配置已还原。读取是否受系统限制尚未确认，不能据此认定卡面不存在。");
            log("已读取原始卡面 " + artwork.AssetName + "（" + artwork.Bytes.Length + " 字节）并完成归位。");
            return artwork;
        }
        public CardArtwork ReadDisplayedCard(string udid, ConnectionMode mode, string hash)
        {
            return TryReadDisplayedCard(udid, mode, hash) ?? throw new IOException("设备没有返回可读取的钱包显示缓存。");
        }
        CardArtwork TryReadDisplayedCard(string udid, ConnectionMode mode, string hash)
        {
            string target = CardTarget(hash);
            foreach (string suffix in new[] { ".cache", ".pkcache" })
            {
                string cache = target.Substring(0, target.Length - ".pkpass".Length) + suffix;
                log("读取钱包显示缓存: " + cache + "/FrontFace");
                byte[] bytes = TryReadCard(udid, mode, cache, "FrontFace");
                if (bytes != null)
                {
                    // The raw cache has already been returned before any decoding.
                    try
                    {
                        var artwork = new CardArtwork("FrontFace", WalletFaceArchive.Decode(bytes));
                        log("已读取钱包当前显示卡面（" + artwork.Bytes.Length + " 字节），缓存原文件已放回。");
                        return artwork;
                    }
                    catch (InvalidDataException error) { log(error.Message + " 继续尝试其他卡面来源。"); }
                }
            }
            return null;
        }
        byte[] TryReadCard(string udid, ConnectionMode mode, string target, string leaf)
        {
            byte[] bytes = TryReadRawCard(udid, mode, target, leaf);
            if (bytes != null && leaf != "FrontFace") ValidateArtwork(bytes, leaf);
            return bytes;
        }
        // Raw export preserves the device file regardless of its image encoding.
        // Return and cleanup must finish before callers may save these bytes.
        byte[] TryReadRawCard(string udid, ConnectionMode mode, string target, string leaf)
        {
            PrepareOperation(udid, mode);
            using (var session = DeviceSession.Open(udid, mode)) using (var afc = new Afc(session))
            {
                // Each candidate has its own token, snapshot and recovered filename.
                // A delayed reply for @3x can never be mistaken for an @2x reply.
                var r = Snapshot(afc, udid, target, leaf, true); byte[] bytes = null; Exception failure = null;
                try
                {
                    log("尝试读取: " + leaf);
                    AirTraffic.Stage(session, Source(r), AirTraffic.BuildArchive(target, new byte[0][]));
                    r.Phase = "Staged"; Persist(r);
                    var moves = new[] {
                        new AssetMove("../../" + Source(r) + "/p0/p1/p2/link", Link(r)),
                        new AssetMove("../../../" + target.Substring("/var/mobile/".Length) + "/" + leaf, Recovered(r)) };
                    PrepareBooks(afc, moves); r.Phase = "MoveRequested"; Persist(r);
                    AirTraffic.Sync(session, moves, log, 900);
                    bool received = false;
                    for (int poll = 0; poll <= 40; poll++)
                    {
                        if (afc.ExportStagingExists(Recovered(r))) { received = true; break; }
                        if (poll < 40) Thread.Sleep(250);
                    }
                    if (!received) { WatchUnreadExport(afc, r); return null; }
                }
                catch (Exception e) { failure = e; }
                try
                {
                    if (afc.ExportStagingExists(Recovered(r))) bytes = ExportRecovery.ReadAndReturn(new ExportFiles(this, session, afc, r));
                    else if (r.Phase == "MoveRequested") WatchUnreadExport(afc, r);
                    if (r.Phase == "Returned" || r.Phase == "Snapshot" || r.Phase == "Staged") Cleanup(afc, r);
                }
                catch (Exception e) { failure = failure == null ? e : new AggregateException(failure, e); }
                if (failure != null) throw new IOException(File.Exists(RecordPath(r)) ? "导出未完成。操作记录: " + RecordPath(r) : "导出未完成，图书同步配置已还原。", failure);
                if (bytes == null) throw new IOException("未读取到卡面。");
                return bytes;
            }
        }
        public static void ValidateArtwork(byte[] bytes, string leaf)
        {
            if (bytes == null) throw new InvalidDataException("设备返回的卡面为空。");
            if (leaf == "FrontFace")
            {
                DisplayImageExtension(bytes); return;
            }
            if (leaf.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                byte[] png = {137,80,78,71,13,10,26,10};
                if (bytes.Length < 8 || !bytes.Take(8).SequenceEqual(png)) throw new InvalidDataException("设备返回的文件不是 PNG；原文件备份已保留。");
            }
            else if (bytes.Length < 5 || System.Text.Encoding.ASCII.GetString(bytes, 0, 5) != "%PDF-") throw new InvalidDataException("设备返回的文件不是 PDF；原文件备份已保留。");
        }
        public static string DisplayImageExtension(byte[] bytes)
        {
            if (bytes != null && bytes.Length >= 8 && bytes.Take(8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) return ".png";
            if (bytes != null && bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255) return ".jpg";
            throw new InvalidDataException("钱包显示缓存不是可直接导出的 PNG/JPEG。原文件已放回，未将未知格式当成图片。");
        }
        public static bool IsExportTarget(string target, string leaf)
        {
            if (IsWalletCacheTarget(target)) return leaf == "FrontFace";
            const string prefix = "/var/mobile/Library/Passes/Cards/";
            return target != null && target.StartsWith(prefix, StringComparison.Ordinal) && target.EndsWith(".pkpass", StringComparison.Ordinal)
                && CardScanner.ValidHash(target.Substring(prefix.Length, target.Length - prefix.Length - ".pkpass".Length)) && ArtworkAssets.Contains(leaf);
        }
        void CompletePending(string udid, ConnectionMode mode)
        {
            var records = CurrentRecordsForDevice(udid);
            if (records.Length == 0) return;
            if (records.Any(r => r.Phase != "WatchingExport")) log("正在完成本次操作的同步清理…");
            using (var session = DeviceSession.Open(udid, mode)) using (var afc = new Afc(session))
            foreach (var r in records)
            {
                ValidateRecord(r);
                if (r.Export && r.Phase == "WatchingExport")
                {
                    // No protected-directory stat is needed to check our own Media file.
                    // Leave the watcher intact and let a new explicit operation proceed.
                    if (!afc.ExportStagingExists(Recovered(r))) continue;
                    log("发现本次操作延迟返回的卡面，先将原文件归位…");
                    CaptureBooks(afc, r); r.Phase = "ReturnRequested"; Persist(r);
                }
                if (!r.Export && IsWalletCacheTarget(r.Target)) log("恢复缓存刷新后的同步状态；不会撤销已经应用的卡面。");
                if (r.Export && afc.ExportStagingExists(Recovered(r))) ExportRecovery.ReadAndReturn(new ExportFiles(this, session, afc, r));
                else if (r.Export && r.Phase == "MoveRequested")
                {
                    WatchUnreadExport(afc, r); continue;
                }
                else if (r.Export && r.Phase == "ReturnRequested")
                {
                    // Disappearance alone is not proof of a successful return: Books
                    // may have removed the old downloaded asset before the return failed.
                    new ExportFiles(this, session, afc, r).ReturnOriginal();
                    r.Phase = "Returned"; Persist(r);
                }
                Cleanup(afc, r); log("本次操作的同步清理已完成。");
            }
        }
        void WatchUnreadExport(Afc afc, RecoveryRecord r)
        {
            ExportRecovery.WatchUnread(() => afc.ExportStagingExists(Recovered(r)), () => Cleanup(afc, r, true), () => {
                r.Phase = "WatchingExport"; Persist(r);
            });
            log(r.Leaf + "：未收到卡面，已还原图书同步配置。本次操作内继续检查延迟文件；结束后保留记录，不自动接续。");
        }
        RecoveryRecord[] CurrentRecordsForDevice(string udid)
        {
            return currentRecords.Where(r => string.Equals(r.Udid, udid, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        static void ValidateRecord(RecoveryRecord r)
        {
            if (r == null || !System.Text.RegularExpressions.Regex.IsMatch(r.Token ?? "", "^[a-f0-9]{32}$") || r.Books == null || r.CreatedDirectories == null ||
                !BooksPaths.All(p => r.Books.ContainsKey(p)) || r.CreatedDirectories.Any(p => p != "Books" && p != "Books/Sync" && p != "Books/Sync/Database")) throw new IOException("恢复记录无效。");
            if (r.Export && !IsExportTarget(r.Target, r.Leaf)) throw new IOException("恢复卡面路径无效。");
            if (r.Target.Contains("..") || r.Target.Contains("\\") || r.Target.Contains("\0")) throw new IOException("恢复路径无效。");
            if (r.ReturnTokens != null && r.ReturnTokens.Any(t => !System.Text.RegularExpressions.Regex.IsMatch(t ?? "", "^[a-f0-9]{32}$"))) throw new IOException("归位通道记录无效。");
        }
        sealed class ExportFiles : IExportFiles
        {
            readonly WalletEngine engine; readonly DeviceSession session; readonly Afc afc; readonly RecoveryRecord record;
            byte[] originalBytes;
            internal ExportFiles(WalletEngine engine, DeviceSession session, Afc afc, RecoveryRecord record) { this.engine = engine; this.session = session; this.afc = afc; this.record = record; }
            public bool RecoveredExists() { return afc.ExportStagingExists(Recovered(record)); }
            public byte[] ReadRecovered() { originalBytes = afc.Read(Recovered(record)); return originalBytes; }
            public void SaveBackup(byte[] data) { Storage.AtomicWrite(Path.Combine(Storage.RecoveryRoot, record.Token + "-" + record.Leaf), data); }
            public void SaveRecoveryPhase(string phase) { record.Phase = phase; Persist(record); }
            public void ReturnOriginal()
            {
                engine.log("正在将原卡面放回钱包…");
                ValidateRecord(record);
                // The preceding sync can delete its old downloaded Book asset while
                // preparing the return manifest. Return a fresh, journaled byte-for-byte
                // copy rather than depending on that old asset surviving another sync.
                if (originalBytes == null)
                {
                    if (RecoveredExists()) originalBytes = afc.Read(Recovered(record));
                    else
                    {
                        throw new IOException("设备暂存原文件已不可用，无法确认上次归位。已保留操作记录；不会自动使用历史备份覆盖当前卡面。");
                    }
                }
                string token = ExportRecovery.BeginReturnAttempt(record, Persist);
                string source = "airlift-src-" + token, link = "airlift-link-" + token;
                AirTraffic.Stage(session, source, AirTraffic.BuildArchive(record.Target, new[] { originalBytes }));
                var moves = new[] {
                    new AssetMove("../../" + source + "/p0/p1/p2/link", link),
                    new AssetMove("../../" + source + "/payload_0", link + "/" + record.Leaf) };
                PrepareBooks(afc, moves); AirTraffic.Sync(session, moves, engine.log, 900);
                WaitUntil(() => !afc.Exists(source + "/payload_0"), "原卡面副本尚未写回钱包。");
                afc.Remove(Recovered(record));
            }
        }
    }
}
