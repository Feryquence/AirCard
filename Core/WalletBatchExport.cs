using System;
using System.Collections.Generic;
using System.IO;

namespace AirCard.Core
{
    public sealed class WalletBatchExportResult
    {
        public string DirectoryPath { get; internal set; }
        public List<string> SavedFiles { get; private set; } = new List<string>();
        public List<string> UnavailableFiles { get; private set; } = new List<string>();
        public List<string> UnrecognizedFiles { get; private set; } = new List<string>();
        public string Summary { get { return (SavedFiles.Count == 0 ? "未读取到可导出的卡面文件。" : "已导出 " + SavedFiles.Count + " 个文件。") +
            (UnavailableFiles.Count == 0 ? "" : "已跳过 " + UnavailableFiles.Count + " 项未读取到的资源。") +
            (UnrecognizedFiles.Count == 0 ? "" : "其中 " + UnrecognizedFiles.Count + " 个原始文件未通过格式校验，已原样保存，未转换。") + "\n" + DirectoryPath; } }
    }
    public static class WalletBatchExport
    {
        public static string OutputDirectory(string parent, string hash)
        {
            if (!CardScanner.ValidHash(hash)) throw new ArgumentException("卡片标识无效。");
            if (string.IsNullOrWhiteSpace(parent) || !Path.IsPathRooted(parent)) throw new ArgumentException("请选择有效的导出文件夹。");
            return Path.Combine(Path.GetFullPath(parent), "AirCard_output_" + hash);
        }
        public static WalletBatchExportResult Export(string parent, string hash, Func<string, byte[]> readOriginal,
            Func<CardArtwork> readCache = null, Action<string> log = null)
        {
            if (readOriginal == null) throw new ArgumentNullException(nameof(readOriginal));
            log = log ?? (_ => { });
            var result = new WalletBatchExportResult { DirectoryPath = OutputDirectory(parent, hash) };
            Directory.CreateDirectory(result.DirectoryPath);
            // Fail before starting any device operation if the selected location is
            // unwritable. Each completed read has already returned the device file.
            string probe = Path.Combine(result.DirectoryPath, ".aircard-probe-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) stream.WriteByte(0);
            Action<string, byte[]> save = (name, bytes) => {
                Storage.AtomicWrite(Path.Combine(result.DirectoryPath, name), bytes);
                result.SavedFiles.Add(name); log("已保存: " + name);
            };
            log("导出目录: " + result.DirectoryPath);
            try
            {
                if (readCache != null)
                {
                    var cache = readCache();
                    if (cache == null) { result.UnavailableFiles.Add("Wallet 卡面缓存"); log("Wallet 卡面缓存：未读取到，跳过。"); }
                    else
                    {
                        if (!cache.IsRendered) throw new InvalidDataException("返回的资源不是钱包显示缓存。");
                        save("Wallet 卡面缓存" + cache.Extension, cache.Bytes);
                    }
                }
                string[] assets = WalletEngine.BatchArtworkAssets;
                for (int i = 0; i < assets.Length; i++)
                {
                    string leaf = assets[i]; log("[" + (i + 1) + "/" + assets.Length + "] 读取 " + leaf);
                    byte[] bytes = readOriginal(leaf);
                    if (bytes == null) { result.UnavailableFiles.Add(leaf); log(leaf + "：未读取到，跳过。"); continue; }
                    // Original-resource export is a byte-preserving copy, not an
                    // image import. Only signature mismatch is non-fatal here;
                    // read/return/save errors still stop the batch.
                    bool recognized = true;
                    try { WalletEngine.ValidateArtwork(bytes, leaf); }
                    catch (InvalidDataException) { recognized = false; }
                    save(leaf, bytes);
                    if (!recognized)
                    {
                        result.UnrecognizedFiles.Add(leaf);
                        log(leaf + "：文件头未通过格式校验，已保留原文件名和原始字节（" + bytes.Length + " 字节），未转换。继续读取其他资源。");
                    }
                }
            }
            catch (Exception error)
            {
                // Never continue to the next file after a transport/return/save
                // failure. Already exported local files remain available.
                throw new IOException("导出中断，已保存 " + result.SavedFiles.Count + " 个文件。目录: " + result.DirectoryPath, error);
            }
            return result;
        }
    }
}
