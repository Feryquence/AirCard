using AirCard;
using AirCard.Core;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Card hashes below are deterministic SHA-1 fixtures generated from AirCard synthetic test labels.
class Smoke
{
    static int checks;
    static string artifacts;
    static void Assert(bool condition, string name) { checks++; if (!condition) throw new Exception("FAIL: " + name); }
    static void Throws(Action action, string name) { bool threw = false; try { action(); } catch { threw = true; } Assert(threw, name); }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            artifacts = args[0]; Storage.Root = Path.Combine(artifacts, "state");
            TestPlist(); TestSyncDiagnostics(); TestSyncRuntime(); TestSyncLogFilter(); TestZip(); TestArtwork(); TestPdfImport(); TestCardFormatMatching(); TestStagingProbe(); TestWalletFace(); TestBatchExport(); TestResourceCatalog(); TestCommunityCards(); TestScanner(); TestSingleCardScan(); TestRecovery(); TestBooksConfiguration(); TestCacheResults(); TestStorage(); TestPendingDevices(); TestWindow();
            string result = "PASS: " + checks + " assertions; no iPhone operations performed.";
            Console.WriteLine(result); File.WriteAllText(Path.Combine(artifacts, "results.txt"), result); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(artifacts, "results.txt"), ex.ToString()); return 1; }
    }
    static void TestPlist()
    {
        var input = Plist.Dict("name", "钱包 & <卡片>", "on", true, "off", false, "count", 42, "raw", new byte[] {0, 255, 16}, "items", new object[] { "USB", Plist.Dict("x", "y") });
        var output = (Dictionary<string, object>)Plist.Read(Plist.Write(input));
        Assert((string)output["name"] == (string)input["name"], "plist unicode/xml escaping");
        Assert((bool)output["on"] && !(bool)output["off"] && (long)output["count"] == 42, "plist scalars");
        Assert(((byte[])output["raw"]).SequenceEqual((byte[])input["raw"]), "plist bytes");
        Assert(((Dictionary<string, object>)((object[])output["items"])[1])["x"].Equals("y"), "plist nesting");
        Throws(() => Plist.Read(Encoding.UTF8.GetBytes("<plist><dict><key>x</key></dict></plist>")), "malformed plist rejected");
        Throws(() => Plist.Read(Encoding.UTF8.GetBytes("<!DOCTYPE plist [<!ENTITY evil SYSTEM 'file:///secret'>]><plist><string>&evil;</string></plist>")), "external entity rejected");
    }
    static void TestSyncDiagnostics()
    {
        Assert(SyncDiagnostics.Scalar(Encoding.UTF8.GetBytes("<plist><integer>18446744073709551615</integer></plist>")) == "18446744073709551615", "sync unsigned error code preserved");
        Assert(SyncDiagnostics.Scalar(Plist.Write(-1)) == "-1", "sync negative error code preserved");
        Assert(SyncDiagnostics.Scalar(Plist.Write("同步拒绝")) == "同步拒绝", "sync unicode reason");
        Assert(SyncDiagnostics.Scalar(Plist.Write(Plist.Dict("private", "manifest"))) == null, "sync nested data not dumped");
        Assert(SyncDiagnostics.Scalar(Plist.Write(new byte[] { 1, 2 })) == null, "sync binary data not dumped");
        Throws(() => SyncDiagnostics.Scalar(Encoding.UTF8.GetBytes("<!DOCTYPE plist [<!ENTITY evil SYSTEM 'file:///secret'>]><plist><string>&evil;</string></plist>")), "sync external entity not expanded");
        var queried = new List<string>();
        var failure = SyncDiagnostics.Failure("ReadyForSync", "SyncFailed", key => {
            queried.Add(key);
            if (key == "ErrorCode") return "18446744073709551615";
            if (key == "Reason") return "first\nsecond\tthird";
            return null;
        });
        Assert(failure.Message.Contains("ReadyForSync") && failure.Message.Contains("SyncFailed"), "sync reports exact failed stage");
        Assert(failure.Message.Contains("ErrorCode=18446744073709551615"), "sync displays full error code");
        Assert(failure.Message.Contains("Reason=first second third"), "sync detail cannot inject log lines");
        Assert(queried.SequenceEqual(new[] { "ErrorCode", "ErrorDomain", "ErrorDescription", "Error", "Reason" }), "sync only reads known diagnostic keys");
        Assert(SyncDiagnostics.Failure("SyncAllowed", "SyncFinished", key => null).Message.Contains("设备未提供"), "sync missing details explicit");
        var broken = SyncDiagnostics.Failure("AssetManifest", "SyncFailed", key => { throw new IOException("bad plist"); });
        Assert(broken.Message.Contains("SyncFailed") && broken.Message.Contains("ErrorCode=<无法读取>"), "bad diagnostic field preserves original sync failure");
        string longDetail = SyncDiagnostics.Failure("ReadyForSync", "SyncFailed", key => key == "Reason" ? new string('x', 600) : null).Message;
        Assert(longDetail.Contains(new string('x', 512) + "…") && !longDetail.Contains(new string('x', 513)), "sync reason length bounded");
    }
    static void TestSyncRuntime()
    {
        Assert(typeof(WalletEngine).Assembly.GetName().Version.ToString() == "1.3.0.0", "release assembly version is 1.3");
        Assert(!typeof(WalletEngine).Assembly.GetManifestResourceNames().Any(name => name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)), "release no longer embeds driver installer scripts");
        Assert(AppleSyncRuntime.FromITunesExecutable("\"D:\\Apple Tools\\iTunes.exe\"") == @"D:\Apple Tools\CoreFP.dll", "sync component locates custom desktop iTunes directory");
        Assert(AppleSyncRuntime.FromITunesExecutable(@"\\host\share\iTunes.exe") == null, "sync component rejects network executable registration");
        Assert(AppleSyncRuntime.FromITunesExecutable(@"C:\iTunes.exe --argument") == null, "sync component rejects executable commands");
        Assert(AppleSyncRuntime.FromITunesExecutable("iTunes.exe") == null, "sync component rejects relative executable registration");
        var paths = AppleSyncRuntime.CandidatePaths(new[] { null, "", "CoreFP.dll", @"C:CoreFP.dll", @"\\server\share\CoreFP.dll", @"C:\Apple\different.dll", @"C:\Apple\CoreFP.dll", @"c:\apple\COREFP.dll", "\"D:\\iTunes\\CoreFP.dll\"" });
        Assert(paths.SequenceEqual(new[] { @"C:\Apple\CoreFP.dll", @"D:\iTunes\CoreFP.dll" }), "sync component candidates preserve preference and reject relative/network/unrelated paths");
        var image = new byte[256];
        using (var stream = new MemoryStream(image)) using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0x5a4d); stream.Position = 0x3c; writer.Write(0x80);
            stream.Position = 0x80; writer.Write(0x4550); writer.Write((ushort)0x8664);
            stream.Position = 0x96; writer.Write((ushort)0x2000); writer.Write((ushort)0x20b);
        }
        using (var stream = new MemoryStream(image)) Assert(AppleSyncRuntime.IsX64Library(stream), "sync component accepts x64 PE32+ DLL header");
        var x86 = (byte[])image.Clone(); x86[0x84] = 0x4c; x86[0x85] = 0x01;
        using (var stream = new MemoryStream(x86)) Assert(!AppleSyncRuntime.IsX64Library(stream), "sync component rejects 32-bit runtime");
        var executable = (byte[])image.Clone(); executable[0x97] = 0;
        using (var stream = new MemoryStream(executable)) Assert(!AppleSyncRuntime.IsX64Library(stream), "sync component rejects executable instead of DLL");
        var badOffset = (byte[])image.Clone(); badOffset[0x3f] = 0x7f;
        using (var stream = new MemoryStream(badOffset)) Assert(!AppleSyncRuntime.IsX64Library(stream), "sync component rejects PE header beyond file");
        using (var stream = new MemoryStream(new byte[4])) Assert(!AppleSyncRuntime.IsX64Library(stream), "sync component truncated file rejected");
        Assert(AppleSyncRuntime.GrappaState(0).Contains("未成功"), "zero Grappa session is not reported as success");
        Assert(AppleSyncRuntime.GrappaState(123).Contains("等待手机确认"), "host Grappa creation does not claim device confirmation");
    }
    static void TestSyncLogFilter()
    {
        var lines = new List<string>(); var filter = new SyncLogFilter(lines.Add);
        byte[] bytes = Encoding.UTF8.GetBytes("Sep 22 19:00:00 phone atc(Books)[1]: 同步失败\r\nSep 22 19:00:00 phone other[2]: private unrelated text\0atc[1]: ErrorCode=4\0");
        for (int i = 0; i < bytes.Length; i++) filter.Feed(new[] { bytes[i] }, 1);
        Assert(lines.Count == 2 && lines[0].EndsWith("同步失败") && lines[1].EndsWith("ErrorCode=4"), "device log frames split UTF8 and null/newline records");
        Assert(filter.Lines == 2 && !filter.Truncated, "device log counts only matching records");
        Assert(!lines.Any(x => x.Contains("private unrelated")), "device log excludes unrelated processes");
        var small = new SyncLogFilter(lines.Add, 12);
        byte[] record = Encoding.UTF8.GetBytes("atc: ignored\natc[1]: text too long\n"); small.Feed(record, record.Length);
        Assert(small.Truncated && small.Lines == 0, "device log enforces byte budget");
        var longLine = new SyncLogFilter(lines.Add);
        byte[] oversized = Encoding.UTF8.GetBytes("atc[1]: " + new string('x', 65536) + "\natc[1]: recovered\n");
        longLine.Feed(oversized, oversized.Length);
        Assert(longLine.Lines == 1 && lines.Last() == "atc[1]: recovered", "oversized log record discarded without poisoning next record");
        Throws(() => filter.Feed(new byte[1], 2), "device log invalid native length rejected");
        var subsystem = new SyncLogFilter(lines.Add);
        byte[] sub = Encoding.UTF8.GetBytes("process(AirTrafficDevice)[1]: failed\n"); subsystem.Feed(sub, sub.Length);
        Assert(subsystem.Lines == 1, "device log includes named AirTraffic subsystem");
        byte[] continuation = Encoding.UTF8.GetBytes("atc[1]: failure {\n    ErrorCode = 4;\n}\nother[2]: next record\n    unrelated continuation\n");
        int previous = lines.Count; subsystem.Feed(continuation, continuation.Length);
        Assert(lines.Count == previous + 3 && lines.Contains("    ErrorCode = 4;"), "device log retains error continuations without unrelated record continuations");
    }
    static void TestZip()
    {
        Assert(StreamingZip.Crc32(Encoding.ASCII.GetBytes("123456789")) == 0xcbf43926, "standard ZIP CRC vector");
        byte[] data = StreamingZip.Build("/var/mobile/Library/Passes/Cards/test.pkpass", new[] { new byte[] { 1, 2, 3 }, new byte[0] });
        using (var stream = new MemoryStream(data)) using (var zip = new ZipArchive(stream))
        {
            Assert(zip.GetEntry("payload_0").Length == 3 && zip.GetEntry("payload_1").Length == 0, "ZIP payload/zero-length marker");
            using (var reader = new StreamReader(zip.GetEntry("p0/p1/p2/link").Open())) Assert(reader.ReadToEnd() == "../../../var/mobile/Library/Passes/Cards/test.pkpass", "symlink target");
            Assert(((uint)zip.GetEntry("p0/p1/p2/link").ExternalAttributes >> 16) == 0xa1ff, "symlink Unix mode");
            using (var reader = new BinaryReader(zip.GetEntry("payload_0").Open())) Assert(reader.ReadBytes(3).SequenceEqual(new byte[] {1,2,3}), "ZIP extracts exact payload");
        }
        Throws(() => StreamingZip.Build("/var/mobile/../root", new byte[0][]), "traversal target rejected");
        Throws(() => StreamingZip.Build("/etc", new byte[0][]), "non-mobile target rejected");
        File.WriteAllBytes(Path.Combine(artifacts, "staging-test.zip"), data);
    }
    static void TestArtwork()
    {
        string image = Path.Combine(artifacts, "input.png");
        using (var b = new Bitmap(600, 200)) using (var g = Graphics.FromImage(b))
        { g.Clear(System.Drawing.Color.Red); g.FillRectangle(System.Drawing.Brushes.Blue, 140, 0, 320, 200); b.Save(image, System.Drawing.Imaging.ImageFormat.Png); }
        var skin = PreparedSkin.Load(image); File.WriteAllBytes(Path.Combine(artifacts, "prepared.png"), skin.Png); File.WriteAllBytes(Path.Combine(artifacts, "prepared.pdf"), skin.Pdf);
        using (var s = new MemoryStream(skin.Png)) using (var b = new Bitmap(s))
        { Assert(b.Width == 1536 && b.Height == 969, "PNG dimensions"); Assert(b.GetPixel(768, 484).B > 240, "center crop preserves center"); Assert(b.GetPixel(10, 484).B > 220, "crop removes red sides"); }
        string pdf = Encoding.ASCII.GetString(skin.Pdf);
        Assert(pdf.StartsWith("%PDF-1.4") && pdf.Contains("/MediaBox [0 0 1536 969]"), "PDF page geometry");
        int start = pdf.IndexOf("5 0 obj", StringComparison.Ordinal); int payload = pdf.IndexOf("stream\n", start, StringComparison.Ordinal) + 7;
        int payloadEnd = pdf.IndexOf("\nendstream", payload, StringComparison.Ordinal);
        using (var s = new MemoryStream(skin.Pdf, payload + 2, payloadEnd - payload - 6)) using (var deflate = new DeflateStream(s, CompressionMode.Decompress)) using (var rgb = new MemoryStream())
        {
            deflate.CopyTo(rgb); Assert(rgb.Length == 1536 * 969 * 3, "PDF image decompresses to full RGB pixels");
            var pixels = rgb.ToArray(); int i = (484 * 1536 + 768) * 3; Assert(pixels[i + 2] > 240 && pixels[i] < 10, "PDF RGB channels");
        }
        string xrefText = pdf.Substring(pdf.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10).Split('\n')[0];
        Assert(pdf.Substring(int.Parse(xrefText)).StartsWith("xref\n"), "PDF xref byte offset");
        WalletEngine.ValidateArtwork(skin.Png, "cardBackgroundCombined@3x.png"); WalletEngine.ValidateArtwork(skin.Pdf, "cardBackgroundCombined.pdf");
        Throws(() => WalletEngine.ValidateArtwork(Encoding.UTF8.GetBytes("corrupted"), "cardBackgroundCombined@2x.png"), "invalid device artwork rejected");
    }
    static byte[] VectorPdf(int pages)
    {
        string commands = "1 0 0 rg 0 0 600 200 re f\n0 0 1 rg 140 0 320 200 re f\n";
        var objects = new[] {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R" + (pages == 2 ? " 5 0 R" : "") + "] /Count " + pages + " >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 200] /Resources << >> /Contents 4 0 R >>",
            "<< /Length " + commands.Length + " >>\nstream\n" + commands + "endstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 200] /Resources << >> /Contents 4 0 R >>"
        };
        var text = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (int i = 0; i < (pages == 2 ? 5 : 4); i++) { offsets.Add(text.Length); text.Append((i + 1) + " 0 obj\n" + objects[i] + "\nendobj\n"); }
        int xref = text.Length;
        text.Append("xref\n0 " + (offsets.Count + 1) + "\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append(offset.ToString("D10") + " 00000 n \n");
        text.Append("trailer\n<< /Size " + (offsets.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
    static void TestPdfImport()
    {
        var existing = PreparedSkin.Load(Path.Combine(artifacts, "prepared.pdf"));
        Assert(existing.IsPdf && existing.Pdf.SequenceEqual(File.ReadAllBytes(Path.Combine(artifacts, "prepared.pdf"))), "PDF import preserves original bytes exactly");
        string path = Path.Combine(artifacts, "vector.pdf"); byte[] original = VectorPdf(1); File.WriteAllBytes(path, original);
        var skin = PreparedSkin.Load(path);
        Assert(skin.Pdf.SequenceEqual(original), "vector PDF stays vector and keeps its original page geometry");
        using (var s = new MemoryStream(skin.Png)) using (var bitmap = new Bitmap(s))
        {
            Assert(bitmap.Width == 1536 && bitmap.Height == 969, "PDF renderer creates full-size PNG");
            Assert(bitmap.GetPixel(768, 484).B > 240 && bitmap.GetPixel(10, 484).B > 220 && bitmap.GetPixel(1525, 484).B > 220,
                "PDF PNG is centered and cropped rather than stretched or padded");
        }
        File.WriteAllBytes(Path.Combine(artifacts, "pdf-import-preview.png"), skin.Png);
        string multiple = Path.Combine(artifacts, "multipage.pdf"); File.WriteAllBytes(multiple, VectorPdf(2));
        Throws(() => PreparedSkin.Load(multiple), "multi-page PDF cannot silently become a single card");
        string invalid = Path.Combine(artifacts, "invalid.pdf"); File.WriteAllText(invalid, "%PDF-1.4\nnot a document");
        Throws(() => PreparedSkin.Load(invalid), "malformed PDF rejected before device operations");
    }
    static void TestCardFormatMatching()
    {
        byte[] png = File.ReadAllBytes(Path.Combine(artifacts, "prepared.png"));
        byte[] pdf = File.ReadAllBytes(Path.Combine(artifacts, "prepared.pdf"));
        Assert(CardFormatMatching.FromAssets(new[] { "FrontFace", "Wallet 卡面缓存.png", "Wallet 卡面缓存.jpg" }) == CardArtworkFormat.Unknown, "display cache cannot identify original artwork format");
        Assert(CardFormatMatching.Detect(leaf => null) == CardArtworkFormat.Unknown, "unreadable originals do not default to PNG");
        Assert(CardFormatMatching.Detect(leaf => leaf == "background.pdf" ? pdf : null) == CardArtworkFormat.Pdf, "PDF background is detected without combined assets");
        Assert(CardFormatMatching.Detect(leaf => leaf == "strip@2x.png" ? png : null) == CardArtworkFormat.Png, "PNG strip is detected without combined assets");
        Assert(CardFormatMatching.Detect(leaf => leaf.EndsWith(".pdf") ? pdf : png) == CardArtworkFormat.Both, "coexisting original formats are reported without inventing an active renderer");
        int reads = 0;
        Throws(() => CardFormatMatching.Detect(leaf => { reads++; throw new IOException("device disconnected"); }), "transport failures cannot identify a format");
        Assert(reads == 1, "format detection stops after a device failure");
        Throws(() => CardFormatMatching.Detect(leaf => pdf), "PDF content under a PNG resource name cannot identify PNG");
        int confirmations = 0; string message = null;
        Func<string, bool> cancel = text => { confirmations++; message = text; return false; };
        Assert(CardFormatMatching.Approve(CardArtworkFormat.Png, ImportedArtworkFormat.Png, cancel) && confirmations == 0, "matching PNG imports without conversion prompt");
        Assert(CardFormatMatching.Approve(CardArtworkFormat.Pdf, ImportedArtworkFormat.Pdf, cancel) && confirmations == 0, "matching PDF imports without conversion prompt");
        Assert(!CardFormatMatching.Approve(CardArtworkFormat.Png, ImportedArtworkFormat.Pdf, cancel) && confirmations == 1 && message.Contains("转换为 PNG"), "PDF-to-PNG requires explicit conversion and cancel rejects import");
        Assert(!CardFormatMatching.Approve(CardArtworkFormat.Pdf, ImportedArtworkFormat.Png, cancel) && confirmations == 2 && message.Contains("转换为 PDF"), "PNG-to-PDF requires explicit conversion and cancel rejects import");
        Assert(CardFormatMatching.Approve(CardArtworkFormat.Pdf, ImportedArtworkFormat.Png, text => true), "accept permits the requested conversion");
        Assert(!CardFormatMatching.Approve(CardArtworkFormat.Png, ImportedArtworkFormat.OtherImage, cancel), "JPG/WebP-to-PNG also requests conversion");
        Throws(() => CardFormatMatching.Target(CardArtworkFormat.Both, ImportedArtworkFormat.Png), "PNG import must not silently select PNG when the primary background also has PDF");
        Throws(() => CardFormatMatching.Target(CardArtworkFormat.Both, ImportedArtworkFormat.Pdf), "PDF import must not silently select PDF when the primary background also has PNG");
        Throws(() => CardFormatMatching.Approve(CardArtworkFormat.Both, ImportedArtworkFormat.Png, text => true), "ambiguous primary format requires a choice before conversion approval");
        Assert(CardFormatMatching.FromAssets(new[] { "cardBackgroundCombined.pdf", "diffuse@3x.png", "background@2x.png", "strip@2x.png" }) == CardArtworkFormat.Pdf,
            "auxiliary PNG files do not turn a combined PDF card into a mixed card");
        Assert(CardFormatMatching.FromAssets(new[] { "cardBackgroundCombined@3x.png", "background.pdf" }) == CardArtworkFormat.Png,
            "auxiliary PDF does not change the combined PNG background format");
        Assert(CardFormatMatching.FromAssets(new[] { "diffuse@3x.png", "FrontFace" }) == CardArtworkFormat.Unknown,
            "diffuse texture and display cache are not proof of primary background format");
        var primaryReads = new List<string>();
        var primaryPdf = CardFormatMatching.Detect(leaf => {
            primaryReads.Add(leaf);
            if (!leaf.StartsWith("cardBackgroundCombined")) throw new IOException("unrelated probe should never run");
            return leaf.EndsWith(".pdf") ? pdf : null;
        });
        Assert(primaryPdf == CardArtworkFormat.Pdf && primaryReads.Count == 4,
            "confirmed combined PDF stops detection before unrelated auxiliary resources");
        string mismatch = null;
        Assert(!CardFormatMatching.Approve(primaryPdf, ImportedArtworkFormat.Png, text => { mismatch = text; return false; }) && mismatch.Contains("转换为 PDF"),
            "PNG on the confirmed PDF card prompts conversion and cancellation prevents application");
        Throws(() => CardFormatMatching.Approve(CardArtworkFormat.Unknown, ImportedArtworkFormat.Png, text => true), "unknown card format cannot be accepted or converted blindly");
        string raster = Path.Combine(artifacts, "prepared.png"), vector = Path.Combine(artifacts, "vector.pdf");
        Assert(CardFormatMatching.ReadInputFormat(raster) == ImportedArtworkFormat.Png && CardFormatMatching.ReadInputFormat(vector) == ImportedArtworkFormat.Pdf, "input format comes from content");
        var pngSkin = PreparedSkin.Load(vector, CardArtworkFormat.Png);
        Assert(pngSkin.TargetFormat == CardArtworkFormat.Png && pngSkin.Pdf == null && WalletEngine.ReplacementAssets(pngSkin).All(item => item.Key.EndsWith(".png")), "approved PDF-to-PNG writes PNG assets only");
        var pdfSkin = PreparedSkin.Load(raster, CardArtworkFormat.Pdf);
        Assert(pdfSkin.TargetFormat == CardArtworkFormat.Pdf && WalletEngine.ReplacementAssets(pdfSkin).Single().Key == "cardBackgroundCombined.pdf", "approved PNG-to-PDF writes a single PDF only");
        var original = PreparedSkin.Load(vector, CardArtworkFormat.Pdf);
        Assert(WalletEngine.ReplacementAssets(original).Single().Value.SequenceEqual(File.ReadAllBytes(vector)), "matching PDF stays byte-for-byte unchanged when written");
        Throws(() => WalletEngine.ReplacementAssets(PreparedSkin.Load(raster)), "legacy dual-format preparation cannot bypass format selection");
        var preview = PreparedSkin.LoadPreview(raster);
        Assert(preview.InputFormat == ImportedArtworkFormat.Png && preview.Pdf == null, "loading a local PNG preview does not convert it to PDF");
        var converted = preview.ForTarget(CardArtworkFormat.Pdf);
        Assert(converted.Pdf != null && preview.Pdf == null && preview.TargetFormat == CardArtworkFormat.Png, "apply-time conversion does not change the imported preview");
        var pdfPreview = PreparedSkin.LoadPreview(vector);
        Assert(pdfPreview.ForTarget(CardArtworkFormat.Pdf).Pdf.SequenceEqual(File.ReadAllBytes(vector)), "applying matching PDF preserves the loaded original");
        Assert(pdfPreview.ForTarget(CardArtworkFormat.Png).Pdf == null && pdfPreview.Pdf != null, "PDF-to-PNG application keeps original PDF available for later applications");
    }
    static void TestStagingProbe()
    {
        var assembly = typeof(WalletEngine).Assembly;
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var probe = assembly.GetType("AirCard.Core.Afc").GetMethod("ProbeExportStaging", flags);
        string name = "airlift-recovered-" + new string('a', 32);
        Func<int, Exception> error = code => (Exception)Activator.CreateInstance(assembly.GetType("AirCard.Core.AfcException"),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new object[] { code, name }, null);
        Func<string, Func<bool>, Func<string[]>, bool> check = (path, stat, list) => (bool)probe.Invoke(null, new object[] { path, stat, list });
        int lists = 0;
        Assert(!check(name, () => false, () => { lists++; return new string[0]; }) && lists == 0, "known missing staging file needs no extra listing");
        Assert(check(name, () => true, () => { lists++; return new string[0]; }) && lists == 0, "present staging file must be returned");
        Assert(!check(name, () => { throw error(4); }, () => new[] { "Books", "other-file" }), "read-error stat is skipped only after directory proves staging file absent");
        Throws(() => check(name, () => { throw error(4); }, () => new[] { name }), "present but unreadable staging file still fails");
        Throws(() => check(name, () => { throw error(4); }, () => { throw new IOException("disconnect"); }), "directory failure cannot be mistaken for missing file");
        Throws(() => check(name, () => { throw error(4); }, () => null), "missing directory result is not absence confirmation");
        Throws(() => check(name, () => { throw error(7); }, () => new string[0]), "other transport errors are never ignored");
        Throws(() => check("../" + name, () => false, () => new string[0]), "probe cannot check a protected or nested path");
    }
    static void TestWalletFace()
    {
        var face = File.ReadAllBytes(Path.Combine(artifacts, "prepared.png"));
        var shadow = new byte[] {137,80,78,71,13,10,26,10,0};
        Func<long, object> uid = i => Plist.Dict("CF$UID", i);
        var root = Plist.Dict("faceImage", uid(3), "faceShadowImage", uid(1));
        var archive = Plist.Dict("$archiver", "NSKeyedArchiver", "$top", Plist.Dict("root", uid(2)), "$objects", new object[] {
            "$null", Plist.Dict("imageData", shadow), root, Plist.Dict("imageData", uid(4)), Plist.Dict("NS.data", face)
        });
        Assert(ReferenceEquals(WalletFaceArchive.ReadFaceImage(archive), face), "archive selects faceImage references instead of first PNG or shadow");
        var artwork = new CardArtwork("FrontFace", WalletFaceArchive.ReadFaceImage(archive));
        Assert(artwork.IsRendered && artwork.Extension == ".png" && artwork.Bytes.SequenceEqual(face), "rendered face retains original image bytes and format");
        Assert(WalletFaceArchive.Decode(face).SequenceEqual(face), "direct PNG cache also supported without plist or drivers");
        var jpeg = new byte[] {255,216,255,224,0};
        Assert(new CardArtwork("FrontFace", jpeg).Extension == ".jpg", "JPEG cache uses JPEG extension");
        root["faceImage"] = uid(999);
        Throws(() => WalletFaceArchive.ReadFaceImage(archive), "out of bounds archive references rejected");
        root["faceImage"] = uid(-1);
        Throws(() => WalletFaceArchive.ReadFaceImage(archive), "negative archive reference rejected");
        root["faceImage"] = uid(2);
        Throws(() => WalletFaceArchive.ReadFaceImage(archive), "cyclic archive does not loop or select unrelated image");
        root.Remove("faceImage");
        Throws(() => WalletFaceArchive.ReadFaceImage(archive), "missing face cannot export a shadow instead");
        Throws(() => WalletFaceArchive.Decode(Encoding.ASCII.GetBytes("corrupted")), "invalid cache marker cannot become a PNG");
        Throws(() => WalletFaceArchive.ReadFaceImage(Plist.Dict()), "invalid archive structure rejected");
        const string target = "/var/mobile/Library/Passes/Cards/6Ecf-_8mdRJ7iATfwxEw1WlP240=";
        Assert(WalletEngine.IsExportTarget(target + ".cache", "FrontFace") && WalletEngine.IsExportTarget(target + ".pkcache", "FrontFace"), "display cache recovery accepts both known directories");
        Assert(!WalletEngine.IsExportTarget(target + ".pkpass", "FrontFace") && !WalletEngine.IsExportTarget(target + ".cache", "pass.json"), "display reads cannot expand to arbitrary pass metadata");
        Assert(!WalletEngine.IsExportTarget("/var/mobile/Library/Passes/Cards/../other.pkpass", "cardBackgroundCombined.pdf"), "invalid journal target rejected before any return");
    }
    static void TestScanner()
    {
        const string hash = "wyVm8b8yDSaLAEijQXQR56Io9UQ=";
        Assert(CardScanner.ValidHash(hash), "valid card hash");
        Assert(!CardScanner.ValidHash("../" + hash) && !CardScanner.ValidHash(hash.Replace(hash[0],'/')), "card path separators rejected");
        Assert(CardScanner.Parse("unrelated " + hash) == null, "non-wallet log ignored");
        var card = CardScanner.Parse("passd: /var/mobile/Library/Passes/Cards/" + hash + ".pkpass description='测试卡'");
        Assert(card != null && card.Hash == hash && card.Name == "测试卡", "wallet path with padded hash parsed");
        Assert(CardScanner.Parse("wallet: card_hash='wyVm8b8yDSaLAEijQXQR56Io9UQ'").Hash == hash.TrimEnd('='), "unpadded card identifier is preserved");
        Assert(CardScanner.Parse("wallet: request digest=" + hash) == null, "unlabelled Wallet digest is not a card");
        Assert(CardScanner.Parse("passd: unique_id='" + hash + "'").Hash == hash, "legacy Wallet unique_id format restored");
        Assert(CardScanner.Parse("wallet: /unrelated/" + hash + ".cache") == null, "unrelated cache path is not a card");
        string unpadded = "6Ecf-_8mdRJ7iATfwxEw1WlP240";
        Assert(CardScanner.Parse("passd: /var/mobile/Library/Passes/Cards/" + unpadded + ".pkpass").Hash == unpadded, "exact filesystem hash keeps hyphen and omits absent padding");
        Assert(CardScanner.Parse("passd: /var/mobile/Library/Passes/Cards/" + unpadded + "=.pkpass").Hash == unpadded + "=", "padding retained when actually present in path");
        foreach (string line in new[] {
            "passd: card_id='" + hash + "'", "wallet: passID=\"" + hash + "\"",
            "nanopassd: opened <" + hash + ">", "passd: opened /Cards/" + hash + ".pkpass)",
            "passd: opened /Passes/Cards/" + hash + ",", "wallet: /Cards/" + hash + "/cardBackgroundCombined@2x.png",
            "wallet: opened /private/card/" + hash + ".pkpass]" })
            Assert(CardScanner.Parse(line).Hash == hash, "legacy Wallet log accepted: " + line);
        Assert(CardScanner.Parse("wallet: checksum='" + hash + "'") == null, "labelled checksum is not a bare card hash");
        Assert(CardScanner.Parse("unrelated: card_id='" + hash + "'") == null, "card id outside wallet context ignored");
        Assert(CardScanner.Parse("passd: hwAtAmHKYwsQrJbT5cTNDsaxVME=") == null && !CardScanner.ValidHash("hwAtAmHKYwsQrJbT5cTNDsaxVME"), "dummy card rejected with and without padding");
        Assert(!CardScanner.ValidHash(new string('A', 27) + "="), "non-random placeholder is not a card hash");
        Assert(CardScanner.Parse("wallet: card_hash='hwAtAmHKYwsQrJbT5cTNDsaxVME=' card_hash='" + hash + "'").Hash == hash, "invalid first match does not hide valid card later in same line");
        Assert(CardScanner.Parse("wallet: nonce=" + hash + " /Cards/" + unpadded + ".pkpass").Hash == unpadded, "explicit card path wins over bare hashes");
        string history = Path.Combine(Storage.Root, "cards.json"); Storage.AtomicWrite(history, Encoding.UTF8.GetBytes("old history"));
        CardScanner.DeleteLegacyHistory();
        Assert(!File.Exists(history), "legacy scanned card history is removed");
    }
    static void TestSingleCardScan()
    {
        const string first = "wyVm8b8yDSaLAEijQXQR56Io9UQ=", second = "PVzJWbfaMQgJ2L--H5uOt0ZLl6U=";
        byte[] data = Encoding.UTF8.GetBytes("wallet card_hash='" + first + "'\nwallet card_hash='" + second + "'\n"); int reads = 0;
        var found = CardScanner.ReadFirst(buffer => { reads++; if (reads != 1) throw new Exception("Unexpected second receive after card detection."); Buffer.BlockCopy(data,0,buffer,0,data.Length); return data.Length; }, CancellationToken.None);
        Assert(found.Hash == first && reads == 1, "scan returns first card immediately even with two cards in the same receive");
        var chunks = new Queue<byte[]>(new[] {Encoding.UTF8.GetBytes("not a wallet event\nwallet card_hash='" + first.Substring(0, 7)), Encoding.UTF8.GetBytes(first.Substring(7) + "'\0")});
        found = CardScanner.ReadFirst(buffer => { var chunk = chunks.Dequeue(); Buffer.BlockCopy(chunk,0,buffer,0,chunk.Length); return chunk.Length; }, CancellationToken.None);
        Assert(found.Hash == first && chunks.Count == 0, "scan preserves partial card log across receives");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); reads = 0;
            Assert(CardScanner.ReadFirst(buffer => { reads++; return 0; }, cancel.Token) == null && reads == 0, "manual cancellation stops before another receive");
        }
        Throws(() => CardScanner.ReadFirst(buffer => 0, CancellationToken.None), "device disconnect still reported");
    }
    sealed class ExportFake : IExportFiles
    {
        public bool Exists = true, ReadFails, BackupFails, ReturnFails, KeepRecovered, PhaseFails;
        public int Returned; public string Phase; public bool BackedUp;
        public bool RecoveredExists() { return Exists; }
        public byte[] ReadRecovered() { if (ReadFails) throw new IOException("read failed"); return new byte[] {1,2,3}; }
        public void SaveBackup(byte[] data) { if (BackupFails) throw new IOException("disk full"); BackedUp = true; }
        public void SaveRecoveryPhase(string phase) { if (PhaseFails) throw new IOException("journal disk full"); Phase = phase; }
        public void ReturnOriginal() { Returned++; if (ReturnFails) throw new IOException("disconnected"); if (!KeepRecovered) Exists = false; }
    }
    static void TestRecovery()
    {
        var record = new RecoveryRecord { Token = "5574834bc09a46f49a18d88119d0b7fe", Leaf = WalletEngine.ArtworkAssets[0] };
        int journalWrites = 0;
        string firstReturn = ExportRecovery.BeginReturnAttempt(record, r => { journalWrites++; Assert(r.ReturnTokens.Count == 1, "return link recorded before staging"); });
        string retryReturn = ExportRecovery.BeginReturnAttempt(record, r => journalWrites++);
        var alreadySynced = new HashSet<string> { "../../airlift-src-" + record.Token + "/p0/p1/p2/link", "../../airlift-src-" + firstReturn + "/p0/p1/p2/link" };
        Assert(firstReturn != record.Token && !alreadySynced.Contains("../../airlift-src-" + retryReturn + "/p0/p1/p2/link"), "return and retry never reuse completed link asset IDs");
        Assert(record.ReturnTokens.SequenceEqual(new[] {firstReturn, retryReturn}) && journalWrites == 2, "all return channels retained for crash cleanup");
        Assert(record.Token == "5574834bc09a46f49a18d88119d0b7fe" && record.Leaf == WalletEngine.ArtworkAssets[0], "fresh return channel never changes original source or destination leaf");
        Throws(() => ExportRecovery.BeginReturnAttempt(record, r => { throw new IOException("disk full"); }), "no unjournaled return attempt when persistence fails");
        var attempted = new List<string>();
        string chosen = WalletEngine.TryExportCandidates(WalletEngine.AutoPng, leaf => { attempted.Add(leaf); return leaf == WalletEngine.ArtworkAssets[1]; });
        Assert(chosen == WalletEngine.ArtworkAssets[1] && attempted.SequenceEqual(WalletEngine.ArtworkAssets.Take(2)), "unread 3x does not block a successful 2x export");
        attempted.Clear();
        Assert(WalletEngine.TryExportCandidates(WalletEngine.AutoPng, leaf => { attempted.Add(leaf); return false; }) == null && attempted.Count == 3, "all unread candidates never report success");
        attempted.Clear();
        Throws(() => WalletEngine.TryExportCandidates(WalletEngine.AutoPng, leaf => { attempted.Add(leaf); throw new IOException("restore failed"); }), "restore failure stops fallback");
        Assert(attempted.Count == 1, "no next card resource before restoration completes");
        Assert(WalletEngine.ExportCandidates(WalletEngine.ArtworkAssets[2]).Single() == WalletEngine.ArtworkAssets[2], "explicit PDF never falls back to PNG");
        attempted.Clear();
        chosen = WalletEngine.TryExportCandidates(WalletEngine.AutoArtwork, leaf => { attempted.Add(leaf); return leaf.EndsWith(".pdf"); });
        Assert(chosen == "cardBackgroundCombined.pdf" && attempted.Count == 4, "PDF-only cards are included in automatic export after PNG candidates");
        attempted.Clear();
        Assert(WalletEngine.TryExportCandidates(WalletEngine.AutoArtwork, leaf => { attempted.Add(leaf); return false; }) == null && attempted.Count == 4, "automatic format never fabricates success when no file arrives");
        attempted.Clear();
        Throws(() => WalletEngine.TryExportCandidates(WalletEngine.AutoArtwork, leaf => { attempted.Add(leaf); throw new IOException("return not confirmed"); }), "automatic format propagates incomplete return");
        Assert(attempted.Count == 1, "automatic format cannot continue after an incomplete return");
        var pdfArtwork = new CardArtwork("cardBackgroundCombined.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n"));
        Assert(pdfArtwork.Extension == ".pdf" && Encoding.ASCII.GetString(pdfArtwork.Bytes).StartsWith("%PDF-"), "PDF retains its original extension and bytes");
        Throws(() => new CardArtwork("cardBackgroundCombined@3x.png", pdfArtwork.Bytes), "PDF bytes cannot be saved as a claimed PNG resource");
        Throws(() => new CardArtwork("../secret.png", new byte[0]), "export result cannot reference non-artwork files");
        Throws(() => new CardArtwork("cardBackgroundCombined.pdf", null), "empty returned artwork rejected");
        Throws(() => new WalletEngine(null).ExportCard("offline", ConnectionMode.Auto, "6Ecf-_8mdRJ7iATfwxEw1WlP240=", WalletEngine.ArtworkAssets[2], Path.Combine(artifacts, "wrong.png")), "wrong save extension rejected before connecting to device");
        int cleaned = 0, watched = 0;
        ExportRecovery.WatchUnread(() => false, () => cleaned++, () => watched++);
        Assert(cleaned == 1 && watched == 1, "unread request restores Books then retains a watcher without protected metadata queries");
        cleaned = watched = 0;
        Throws(() => ExportRecovery.WatchUnread(() => true, () => cleaned++, () => watched++), "existing original must be returned before cleanup");
        Assert(cleaned == 0 && watched == 0, "staged original never cleaned or marked as empty");
        int checks = 0;
        Throws(() => ExportRecovery.WatchUnread(() => ++checks == 2, () => cleaned++, () => watched++), "late arrival during cleanup remains an active recovery");
        Assert(watched == 0, "late arrival never marked as an empty watcher");
        Throws(() => ExportRecovery.WatchUnread(() => { throw new IOException("AFC unavailable"); }, () => cleaned++, () => watched++), "AFC disconnect still blocks cleanup");
        Throws(() => ExportRecovery.WatchUnread(() => false, () => { throw new IOException("Books failed"); }, () => watched++), "Books cleanup failure is not hidden");
        Assert(watched == 0, "failed cleanup preserves active journal");
        Throws(() => ExportRecovery.WatchUnread(() => false, () => {}, () => { throw new IOException("journal full"); }), "watcher persistence failure is reported");
        var f = new ExportFake(); Assert(ExportRecovery.ReadAndReturn(f).SequenceEqual(new byte[] {1,2,3}) && f.Returned == 1 && !f.Exists && f.BackedUp && f.Phase == "Returned", "export success returns exact bytes and restores original");
        f = new ExportFake {ReadFails = true}; Throws(() => ExportRecovery.ReadAndReturn(f), "read failure reported"); Assert(f.Returned == 1 && !f.Exists, "read failure still restores original");
        f = new ExportFake {BackupFails = true}; Throws(() => ExportRecovery.ReadAndReturn(f), "disk full reported"); Assert(f.Returned == 1 && !f.Exists, "disk full still restores original");
        f = new ExportFake {ReturnFails = true}; Throws(() => ExportRecovery.ReadAndReturn(f), "restore failure reported"); Assert(f.Exists && f.Phase == "ReturnRequested", "restore failure retains original and recovery phase");
        f = new ExportFake {KeepRecovered = true}; Throws(() => ExportRecovery.ReadAndReturn(f), "unconfirmed restore cannot succeed"); Assert(f.Phase != "Returned", "no success phase before source disappears");
        f = new ExportFake {Exists = false}; Throws(() => ExportRecovery.ReadAndReturn(f), "missing file never succeeds"); Assert(f.Returned == 0, "missing source not blindly restored");
        f = new ExportFake {PhaseFails = true}; Throws(() => ExportRecovery.ReadAndReturn(f), "journal error reported"); Assert(f.Returned == 1 && !f.Exists, "journal failure must still restore original");
    }
    static void TestBooksConfiguration()
    {
        var snapshot = new Dictionary<string, string> {
            { "Books/Books.plist", Convert.ToBase64String(new byte[] {1,2}) },
            { "Books/Sync/Books.plist", null },
            { "Books/Sync/Upload.plist", Convert.ToBase64String(new byte[] {3,4}) },
            { "Books/Sync/Database/OutstandingAssets_4.sqlite", Convert.ToBase64String(new byte[] {5,6}) },
            { "Books/Sync/Database/OutstandingAssets_4.sqlite-shm", null },
            { "Books/Sync/Database/OutstandingAssets_4.sqlite-wal", "invalid legacy snapshot" }
        };
        var written = new Dictionary<string, byte[]>(); var removed = new List<string>();
        BooksConfiguration.Restore(snapshot, (path, bytes) => written.Add(path, bytes), removed.Add);
        Assert(written.Count == 2 && written["Books/Books.plist"].SequenceEqual(new byte[] {1,2}), "Books configuration bytes restored exactly");
        Assert(removed.SequenceEqual(new[] { "Books/Sync/Books.plist" }), "only previously absent configuration is removed");
        Assert(!written.Keys.Concat(removed).Any(p => p.Contains("Database")), "legacy SQLite, WAL and SHM are never overwritten or deleted");
        Assert(BooksConfiguration.Paths.All(p => p.EndsWith(".plist")), "new snapshots exclude live databases");
        int attempted = 0;
        Throws(() => BooksConfiguration.Restore(snapshot, (path, bytes) => { attempted++; throw new IOException("disconnected"); }, path => attempted++), "configuration restore failure remains visible");
        Assert(attempted == 3, "failed configuration write does not skip other configuration cleanup");
        Throws(() => BooksConfiguration.Restore(new Dictionary<string, string>(), (path, bytes) => {}, path => {}), "incomplete configuration snapshots cannot silently succeed");
    }
    static void TestBatchExport()
    {
        const string hash = "6Ecf-_8mdRJ7iATfwxEw1WlP240=";
        byte[] png = File.ReadAllBytes(Path.Combine(artifacts, "prepared.png")), pdf = File.ReadAllBytes(Path.Combine(artifacts, "prepared.pdf"));
        string parent = Path.Combine(artifacts, "batch-complete");
        var attempted = new List<string>();
        var result = WalletBatchExport.Export(parent, hash, leaf => {
            attempted.Add(leaf);
            return leaf == "cardBackgroundCombined@3x.png" ? png : leaf == "background.pdf" ? pdf : null;
        }, () => new CardArtwork("FrontFace", png));
        Assert(result.DirectoryPath == Path.Combine(parent, "AirCard_output_" + hash), "batch folder includes exact hash with its padding");
        Assert(attempted.SequenceEqual(WalletEngine.BatchArtworkAssets) && attempted.Count == 11, "batch attempts all eleven original resources exactly once");
        Assert(result.SavedFiles.SequenceEqual(new[] { "Wallet 卡面缓存.png", "cardBackgroundCombined@3x.png", "background.pdf" }), "cache has separate filename and originals keep exact names");
        Assert(result.UnavailableFiles.Count == 9 && result.Summary.Contains("已导出 3 个文件"), "batch reports saved and unavailable counts separately");
        Assert(File.ReadAllBytes(Path.Combine(result.DirectoryPath, "cardBackgroundCombined@3x.png")).SequenceEqual(png) &&
            File.ReadAllBytes(Path.Combine(result.DirectoryPath, "background.pdf")).SequenceEqual(pdf), "batch preserves PNG and PDF bytes without conversion");
        Assert(Directory.GetFiles(result.DirectoryPath).Length == 3, "batch output contains no probe, placeholder or manifest files");
        File.WriteAllText(Path.Combine(result.DirectoryPath, "keep.txt"), "user file");
        var repeated = WalletBatchExport.Export(parent, hash, leaf => leaf == "cardBackgroundCombined@3x.png" ? png : null);
        Assert(repeated.SavedFiles.Count == 1 && File.Exists(Path.Combine(result.DirectoryPath, "background.pdf")) &&
            File.ReadAllText(Path.Combine(result.DirectoryPath, "keep.txt")) == "user file", "repeat export preserves unavailable old resources and unrelated files");
        string failedParent = Path.Combine(artifacts, "batch-interrupted"); int readCalls = 0;
        Throws(() => WalletBatchExport.Export(failedParent, hash, leaf => { readCalls++; if (readCalls == 1) return png; throw new IOException("return not confirmed"); }), "critical device failure interrupts batch");
        Assert(readCalls == 2 && File.Exists(Path.Combine(WalletBatchExport.OutputDirectory(failedParent, hash), "cardBackgroundCombined@3x.png")), "interrupted batch retains completed files and never starts next device read");
        int emptyReads = 0;
        var empty = WalletBatchExport.Export(Path.Combine(artifacts, "batch-empty"), hash, leaf => { emptyReads++; return null; }, () => null);
        Assert(empty.SavedFiles.Count == 0 && empty.UnavailableFiles.Count == 12 && empty.Summary.Contains("未读取到可导出的卡面文件") && Directory.GetFiles(empty.DirectoryPath).Length == 0,
            "all unavailable resources give an informational empty result without an error or placeholder files");
        Assert(emptyReads == 11, "an unavailable file does not prevent remaining resource attempts");
        string blocked = Path.Combine(artifacts, "batch-blocked"); File.WriteAllText(blocked, "not a directory"); readCalls = 0;
        Throws(() => WalletBatchExport.Export(blocked, hash, leaf => { readCalls++; return png; }), "invalid destination fails preflight");
        Assert(readCalls == 0, "destination failure does not start a device operation");
        Throws(() => WalletBatchExport.OutputDirectory(parent, "../" + hash), "batch hash cannot escape selected folder");
        byte[] jpeg = {255,216,255,224,0};
        var cacheOnly = WalletBatchExport.Export(Path.Combine(artifacts, "batch-cache-only"), hash, leaf => null, () => new CardArtwork("FrontFace", jpeg));
        Assert(cacheOnly.SavedFiles.Single() == "Wallet 卡面缓存.jpg" && cacheOnly.UnavailableFiles.Count == 11, "JPEG cache-only result remains a successful JPEG export");
        byte[] unknown = Encoding.ASCII.GetBytes("synthetic unknown original bytes");
        attempted.Clear(); var warnings = new List<string>();
        var raw = WalletBatchExport.Export(Path.Combine(artifacts, "batch-raw"), hash, leaf => {
            attempted.Add(leaf);
            return leaf == "cardBackgroundCombined@2x.png" ? unknown : leaf == "diffuse@2x.png" ? pdf : leaf == "strip.pdf" ? pdf : null;
        }, null, warnings.Add);
        Assert(attempted.SequenceEqual(WalletEngine.BatchArtworkAssets) && raw.SavedFiles.Count == 3, "unrecognized original does not stop later raw resource exports");
        Assert(File.ReadAllBytes(Path.Combine(raw.DirectoryPath, "cardBackgroundCombined@2x.png")).SequenceEqual(unknown) &&
            File.ReadAllBytes(Path.Combine(raw.DirectoryPath, "diffuse@2x.png")).SequenceEqual(pdf), "unknown or mismatched encoding preserves exact original names and bytes");
        Assert(raw.UnrecognizedFiles.SequenceEqual(new[] { "cardBackgroundCombined@2x.png", "diffuse@2x.png" }) && raw.Summary.Contains("2 个原始文件未通过格式校验"), "raw export summary identifies unrecognized content without claiming conversion");
        Assert(warnings.Count(line => line.Contains("文件头未通过格式校验")) == 2, "raw export logs each nonfatal signature mismatch");
        Throws(() => new CardArtwork("cardBackgroundCombined@2x.png", unknown), "image consumers still reject unknown originals");
        readCalls = 0;
        Throws(() => WalletBatchExport.Export(Path.Combine(artifacts, "batch-raw-interrupted"), hash, leaf => {
            readCalls++; if (readCalls == 1) return unknown; throw new IOException("return not confirmed");
        }), "raw export never ignores later return failure");
        Assert(readCalls == 2, "raw export stops before another device read after return failure");
        attempted.Clear();
        var subset = WalletBatchExport.Export(Path.Combine(artifacts, "batch-subset"), hash, leaf => { attempted.Add(leaf); return png; },
            selectedOriginalAssets: new[] { "strip@2x.png", "diffuse@3x.png", "strip@2x.png" });
        Assert(attempted.SequenceEqual(new[] { "diffuse@3x.png", "strip@2x.png" }) && subset.SavedFiles.Count == 2 && subset.UnavailableFiles.Count == 0, "selection reads only checked resources once in canonical order");
        readCalls = 0;
        var selectedCache = WalletBatchExport.Export(Path.Combine(artifacts, "batch-selected-cache"), hash, leaf => { readCalls++; return png; },
            () => new CardArtwork("FrontFace", png), selectedOriginalAssets: new string[0]);
        Assert(readCalls == 0 && selectedCache.SavedFiles.Single() == "Wallet 卡面缓存.png", "cache-only selection never reads original resources");
        string invalidSelection = Path.Combine(artifacts, "batch-invalid-selection");
        Throws(() => WalletBatchExport.Export(invalidSelection, hash, leaf => { readCalls++; return png; }, selectedOriginalAssets: new string[0]), "empty selection is rejected");
        Throws(() => WalletBatchExport.Export(invalidSelection, hash, leaf => { readCalls++; return png; }, selectedOriginalAssets: new[] { "../unknown.png" }), "unknown selection is rejected");
        Assert(!Directory.Exists(invalidSelection) && readCalls == 0, "invalid selection creates no output and starts no reads");
        using (var cancel = new CancellationTokenSource())
        {
            int cancelledReads = 0;
            string cancelledParent = Path.Combine(artifacts, "batch-cancelled");
            try
            {
                WalletBatchExport.Export(cancelledParent, hash, leaf => { cancelledReads++; cancel.Cancel(); return png; }, cancellation: cancel.Token);
                throw new Exception("Cancelled batch reported success");
            }
            catch (OperationCanceledException e) { Assert(e.Message.Contains("保留已保存的 1 个文件"), "cancelled export reports partial output"); }
            Assert(cancelledReads == 1 && File.ReadAllBytes(Path.Combine(WalletBatchExport.OutputDirectory(cancelledParent, hash), "cardBackgroundCombined@3x.png")).SequenceEqual(png), "cancel saves completed current resource and prevents next read");
            string beforeStart = Path.Combine(artifacts, "batch-pre-cancelled");
            try { WalletBatchExport.Export(beforeStart, hash, leaf => { cancelledReads++; return png; }, cancellation: cancel.Token); throw new Exception("Pre-cancelled batch reported success"); }
            catch (OperationCanceledException) { }
            Assert(!Directory.Exists(beforeStart) && cancelledReads == 1, "pre-cancel prevents filesystem and device work");
        }
        using (var cancel = new CancellationTokenSource())
        {
            try { WalletBatchExport.Export(Path.Combine(artifacts, "batch-cancel-failure"), hash, leaf => { cancel.Cancel(); throw new IOException("return failed"); }, cancellation: cancel.Token); throw new Exception("Return failure ignored"); }
            catch (IOException e) { Assert(e.InnerException.Message == "return failed", "device return failure is not hidden by concurrent cancellation"); }
        }
    }
    static void TestResourceCatalog()
    {
        Func<string, byte[]> utf8 = Encoding.UTF8.GetBytes;
        var names = CardResourceCatalog.ManifestAssets(utf8("{\"pass.json\":\"x\",\"en.lproj/pass.strings\":\"x\",\"art/custom.png\":\"x\",\"background.pdf.urls\":\"x\",\"../outside.png\":\"x\"}"));
        Assert(names.SequenceEqual(new[] { "art/custom.png", "background.pdf.urls" }), "manifest discovers arbitrary artwork and descriptors without exporting unrelated metadata");
        foreach (string path in new[] { "../x.png", "art/../../x.png", "/x.png", "C:/x.png", "x\\y.png", "aux.png", "folder./x.png", "x.png:stream", "x\n.png", "pass.json" })
            Assert(!CardResourceCatalog.IsResourcePath(path), "unsafe or unrelated resource rejected: " + path);
        Assert(CardResourceCatalog.IsResourcePath("zh.lproj/custom image.jpg"), "safe nested image accepted");
        Throws(() => CardResourceCatalog.ManifestAssets(utf8("{\"art.png\":\"x\",\"ART.PNG\":\"y\"}")), "case insensitive output collisions rejected");
        Throws(() => CardResourceCatalog.ManifestAssets(utf8("invalid")), "malformed manifest rejected");
        Throws(() => CardResourceCatalog.ManifestAssets(new byte[] { 0xff }), "invalid UTF8 rejected");
        const string digest = "a9993e364706816aba3e25717850c26c9cd0d89d";
        string json = "{\"custom.png\":{\"url\":\"https://assets.apple.com/image\",\"size\":3,\"sha1\":\"" + digest + "\"}}";
        var remote = CardResourceCatalog.ParseUrls("art/custom.png.urls", utf8(json)).Single();
        Assert(remote.Name == "art/custom.png" && remote.Size == 3 && remote.Sha1 == digest, "remote descriptor preserves folder, size and digest");
        Assert(remote.ReadVerified(new MemoryStream(utf8("abc"))).SequenceEqual(utf8("abc")), "remote bytes pass exact size and SHA1 checks without transcoding");
        Throws(() => remote.ReadVerified(new MemoryStream(utf8("ab"))), "truncated download rejected");
        Throws(() => remote.ReadVerified(new MemoryStream(utf8("abcd"))), "oversized download rejected");
        Throws(() => remote.ReadVerified(new MemoryStream(utf8("xyz"))), "same-size altered download rejected");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { remote.ReadVerified(new MemoryStream(utf8("abc")), cancelled.Token); throw new Exception("cancel ignored"); }
            catch (OperationCanceledException) { Assert(true, "remote validation obeys cancellation"); }
        }
        foreach (string url in new[] { "http://assets.apple.com/x", "https://apple.com.evil.test/x", "https://assets.apple.com:444/x", "https://u:p@assets.apple.com/x", "file:///c:/secret", "https://127.0.0.1/x", "https://assets.apple.com/x#fragment" })
            Throws(() => new RemoteArtwork("custom.png", url, 3, digest), "unapproved remote URL rejected");
        Throws(() => new RemoteArtwork("custom.png", "https://assets.apple.com/x", 0, digest), "invalid declared size rejected");
        Throws(() => new RemoteArtwork("custom.png", "https://assets.apple.com/x", 3, "invalid"), "invalid declared digest rejected");
        Throws(() => CardResourceCatalog.ParseUrls("custom.png.urls", utf8("{\"x.png\":{\"url\":\"https://assets.apple.com/x\"}}")), "missing integrity metadata prevents download");
        var catalog = new CardResourceCatalog(); catalog.LocalAssets.Add(remote.Name); catalog.RemoteAssets.Add(remote.Name, remote);
        catalog.LocalAssets.Add("art/custom.png.urls");
        Assert(catalog.Assets.SequenceEqual(new[] { remote.Name }), "local and remote sources share a single output name");
        string hash = "A0nuz33fwMffEsnDw-PwGDgbY9I=";
        // Any valid fixture hash works; it is never sent to a phone.
        var attempted = new List<string>();
        byte[] pdf = utf8("%PDF-1.7\nsynthetic export fixture");
        var result = WalletBatchExport.Export(Path.Combine(artifacts, "dynamic-resources"), hash, leaf => { attempted.Add(leaf); return pdf; },
            selectedOriginalAssets: new[] { "art/custom.pdf" }, availableAssets: new[] { "art/custom.pdf", "other.pdf" });
        Assert(attempted.SequenceEqual(new[] { "art/custom.pdf" }) && result.UnrecognizedFiles.Count == 0, "only selected dynamic artwork is read");
        Assert(File.ReadAllBytes(Path.Combine(result.DirectoryPath, "art/custom.pdf")).SequenceEqual(pdf), "nested dynamic resource saves exact bytes");
        Throws(() => WalletBatchExport.Export(Path.Combine(artifacts, "dynamic-index"), hash, leaf => { throw new Exception("must not read descriptor"); }, availableAssets: new[] { "art/custom.pdf.urls" }), "indexes are not exportable resources");
        Throws(() => WalletBatchExport.Export(Path.Combine(artifacts, "dynamic-unsafe"), hash, leaf => null, availableAssets: new[] { "../x.png" }), "dynamic export rejects traversal before reads");
        Throws(() => WalletBatchExport.Export(Path.Combine(artifacts, "dynamic-unknown"), hash, leaf => null, availableAssets: new[] { "custom.png" }, selectedOriginalAssets: new[] { "unlisted.png" }), "selection must belong to discovered catalog");
    }
    static void TestStorage()
    {
        string path = Path.Combine(artifacts, "atomic.dat"); Storage.AtomicWrite(path, new byte[] {1,2}); Storage.AtomicWrite(path, new byte[] {3,4});
        Assert(File.ReadAllBytes(path).SequenceEqual(new byte[] {3,4}), "atomic overwrite");
        Assert(!Directory.GetFiles(artifacts, "*.tmp").Any(), "no leftover atomic temp files");
    }
    static void TestCacheResults()
    {
        int cleaned = 0;
        var complete = BatchWriteResult.Finish(new string[0], false, () => cleaned++);
        Assert(!complete.HasWarnings && cleaned == 1, "complete required batch cleans up successfully");
        cleaned = 0;
        Throws(() => BatchWriteResult.Finish(new[] { "cardBackgroundCombined.pdf" }, false, () => cleaned++), "incomplete actual artwork still fails");
        Assert(cleaned == 0, "required artwork failure retains staging and journal");
        Throws(() => BatchWriteResult.Finish(new[] { "en-2-ABC--white.png" }, false, () => cleaned++), "incomplete passcode theme still fails");
        var cache = BatchWriteResult.Finish(new[] { "FrontFace", "Preview" }, true, () => cleaned++);
        Assert(cache.HasWarnings && cleaned == 1 && cache.PendingAssets.SequenceEqual(new[] { "FrontFace", "Preview" }), "optional partial cache reports exact files after cleanup");
        var apply = new CardApplyResult(); apply.CacheWarnings.Add(".pkcache: Preview");
        Assert(apply.Summary.Contains("卡面已写入") && apply.Summary.Contains("部分缓存未刷新"), "reported real-world case preserves artwork success in UI summary");
        Throws(() => BatchWriteResult.Finish(new[] { "Preview" }, true, () => { throw new IOException("Books restore failed"); }), "optional cache never hides Books cleanup failure");
        var partialError = new CardAppliedException(new IOException("disconnected"));
        Assert(partialError.Message.Contains("卡面已写入") && partialError.Message.Contains("恢复"), "device disconnect during cache stage accurately retains artwork success");
        const string root = "/var/mobile/Library/Passes/Cards/PVzJWbfaMQgJ2L--H5uOt0ZLl6U=";
        Assert(WalletEngine.IsWalletCacheTarget(root + ".cache") && WalletEngine.IsWalletCacheTarget(root + ".pkcache"), "both actual wallet cache layouts classified");
        Assert(!WalletEngine.IsWalletCacheTarget(root + ".pkpass"), "actual card artwork cannot become optional");
        Assert(!WalletEngine.IsWalletCacheTarget("/var/mobile/Library/Caches/TelephonyUI-10") && !WalletEngine.IsWalletCacheTarget(root + "/../x.cache"), "theme and traversal targets cannot become optional");
    }
    static void TestCommunityCards()
    {
        Assert(CommunityCards.Parse(Encoding.UTF8.GetBytes("{\"cards\":[]}")).Count == 0, "empty public card library is a valid index");
        var file = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3 };
        string digest;
        using (var sha = System.Security.Cryptography.SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        string json = "{\"cards\":[{\"name\":\"测试卡面\",\"uploader\":\"作者\",\"description\":\"说明\",\"fanmade\":true,\"type\":\"png\",\"source\":\"https://raw.githubusercontent.com/Feryquence/AirCard/cards/files/" + digest + ".png\"}]}";
        string api = "{\"type\":\"file\",\"name\":\"cards.json\",\"path\":\"cards.json\",\"encoding\":\"base64\",\"content\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) + "\"}";
        Assert(CommunityCards.Parse(CommunityCards.DecodeIndexResponse(Encoding.UTF8.GetBytes(api))).Count == 1, "GitHub Contents API index decodes into a current card listing");
        Throws(() => CommunityCards.DecodeIndexResponse(Encoding.UTF8.GetBytes(api.Replace("\"encoding\":\"base64\"", "\"encoding\":\"none\""))), "index response without file bytes is rejected");
        Throws(() => CommunityCards.DecodeIndexResponse(Encoding.UTF8.GetBytes(api.Replace("\"path\":\"cards.json\"", "\"path\":\"other.json\""))), "index response must identify cards.json");
        var cards = CommunityCards.Parse(Encoding.UTF8.GetBytes(json));
        Assert(cards.Count == 1 && cards[0].Name == "测试卡面" && cards[0].Detail.Contains("二创"), "library parses card metadata for in-app display");
        CommunityCards.Verify(cards[0], file); Assert(true, "downloaded original matches the content-addressed source");
        string cached = CommunityCards.Cache(cards[0], file);
        Assert(cached == Path.Combine(Storage.Root, "CardLibraryCache", digest + ".png") && File.ReadAllBytes(cached).SequenceEqual(file), "immediate use caches the original bytes under their verified digest");
        Throws(() => CommunityCards.Cache(cards[0], new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 4 }), "corrupted library card cannot replace the cache");
        Assert(File.ReadAllBytes(cached).SequenceEqual(file), "failed cache validation preserves the previous file");
        Throws(() => CommunityCards.Verify(cards[0], new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 4 }), "changed original is rejected by SHA-256");
        Throws(() => CommunityCards.Parse(Encoding.UTF8.GetBytes(json.Replace("raw.githubusercontent.com/Feryquence/AirCard", "example.com"))), "untrusted library source is rejected");
        Throws(() => CommunityCards.Parse(Encoding.UTF8.GetBytes(json.Replace(".png\"", ".pdf\""))), "source extension must match declared type");
    }
    static void TestWindow()
    {
        var app = new App(); app.InitializeComponent(); var window = new MainWindow();
        var root = (FrameworkElement)window.Content; var tabs = (TabControl)window.FindName("Tabs");
        Assert(tabs.Items.Count == 3 && tabs.Items[1] == window.FindName("CardLibraryTab") && window.FindName("CurrentCardText") == null && window.FindName("ChooseThemeButton") == null, "wallet, card library and help tabs exclude old card selection and keypad controls");
        Assert(window.FindName("LibraryList") is ListBox && window.FindName("LibraryPreview") is System.Windows.Shapes.Rectangle && ((CheckBox)window.FindName("LibraryRoundedPreviewCheck")).IsChecked == true && window.FindName("LibraryUseButton") is Button && window.FindName("LibraryDownloadButton") is Button && window.FindName("BrowseCardsButton") == null,
            "community cards are browsed, previewed and downloaded within the application");
        for (int i = 0; i < tabs.Items.Count; i++)
        {
            tabs.SelectedIndex = i; root.Width = 1120; root.Height = 800; root.Measure(new System.Windows.Size(1120,800)); root.Arrange(new Rect(0,0,1120,800)); root.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1120, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var s = File.Create(Path.Combine(artifacts, "ui-" + i + ".png"))) encoder.Save(s);
        }
        var export = (Button)window.FindName("ExportButton"); Assert(!export.IsEnabled, "export disabled without device");
        string[] requestedAssets = {
            "cardBackgroundCombined@3x.png", "cardBackgroundCombined@2x.png", "cardBackgroundCombined.pdf",
            "diffuse@3x.png", "diffuse@2x.png", "background@3x.png", "background@2x.png", "background.pdf",
            "strip@3x.png", "strip@2x.png", "strip.pdf"
        };
        Assert(window.FindName("ExportAsset") == null && WalletEngine.BatchArtworkAssets.SequenceEqual(requestedAssets),
            "folder export replaces individual selection and covers all eleven requested resources");
        foreach (string resource in requestedAssets)
            Assert(WalletEngine.ExportCandidates(resource).Single() == resource &&
                WalletEngine.IsExportTarget("/var/mobile/Library/Passes/Cards/6Ecf-_8mdRJ7iATfwxEw1WlP240=.pkpass", resource),
                "selected artwork can be read and returned to the same exact leaf: " + resource);
        var selected = (ComboBox)window.FindName("DeviceSelect"); selected.ItemsSource = new[] { new DeviceInfo { Udid = "offline-test", Name = "Test", Transport = "USB" } }; selected.SelectedIndex = 0;
        ((TextBox)window.FindName("HashBox")).Text = "wyVm8b8yDSaLAEijQXQR56Io9UQ=";
        Assert(!export.IsEnabled, "hash alone cannot bypass a fresh scan");
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var setCard = typeof(MainWindow).GetMethod("SetCurrentCard", flags);
        var scanned = new SavedCard { Udid = "offline-test", Hash = "wyVm8b8yDSaLAEijQXQR56Io9UQ=", Name = "Test card" };
        setCard.Invoke(window, new object[] { scanned });
        Assert(export.IsEnabled, "export available with a fresh scanned card independently of imported image");
        Assert(!((Button)window.FindName("ApplySkinButton")).IsEnabled, "apply disabled without image");
        var pngSkin = PreparedSkin.Load(Path.Combine(artifacts, "prepared.png"), CardArtworkFormat.Png);
        typeof(MainWindow).GetField("skin", flags).SetValue(window, pngSkin);
        typeof(MainWindow).GetMethod("UpdateState", flags).Invoke(window, null);
        Assert(((Button)window.FindName("ApplySkinButton")).IsEnabled && (CardArtworkFormat)typeof(MainWindow).GetField("cardFormat", flags).GetValue(window) == CardArtworkFormat.Unknown,
            "apply is available before format identification when a local preview and scanned card exist");
        var run = typeof(MainWindow).GetMethod("Run", flags);
        var task = (System.Threading.Tasks.Task<bool>)run.Invoke(window, new object[] { "offline export", new Func<System.Threading.Tasks.Task>(() => System.Threading.Tasks.Task.CompletedTask), null, false, false });
        Assert(task.GetAwaiter().GetResult() && export.IsEnabled && ((TextBox)window.FindName("HashBox")).Text == scanned.Hash, "completed operation retains scanned card for another operation");
        task = (System.Threading.Tasks.Task<bool>)run.Invoke(window, new object[] { "offline export failure", new Func<System.Threading.Tasks.Task>(() => { throw new IOException("offline failure"); }), null, false, false });
        Assert(!task.GetAwaiter().GetResult() && export.IsEnabled && ((TextBox)window.FindName("HashBox")).Text == scanned.Hash, "failed operation also retains the scanned card");
        task = (System.Threading.Tasks.Task<bool>)run.Invoke(window, new object[] { "offline cancel", new Func<System.Threading.Tasks.Task>(() => {
            var cancelButton = (Button)window.FindName("CancelOperationButton");
            Assert(cancelButton.IsEnabled, "cancellable operation enables cancellation control");
            typeof(MainWindow).GetMethod("CancelOperation_Click", flags).Invoke(window, new object[] { null, new RoutedEventArgs() });
            Assert(!cancelButton.IsEnabled && ((TextBlock)window.FindName("StatusText")).Text.Contains("正在取消"), "cancel immediately updates UI and prevents repeated clicks");
            return System.Threading.Tasks.Task.CompletedTask;
        }), null, false, true });
        Assert(!task.GetAwaiter().GetResult() && !((Button)window.FindName("CancelOperationButton")).IsEnabled && export.IsEnabled, "cancellation finishes without success and restores idle controls");
        selected.ItemsSource = new[] { new DeviceInfo { Udid = "other-device", Name = "Other", Transport = "USB" }, new DeviceInfo { Udid = scanned.Udid, Name = "Test", Transport = "USB" } }; selected.SelectedIndex = 0;
        Assert(!export.IsEnabled && ((TextBox)window.FindName("HashBox")).Text == scanned.Hash, "retained identifier cannot operate on another device");
        Assert(!((Button)window.FindName("ApplySkinButton")).IsEnabled && ((Button)window.FindName("ChooseSkinButton")).IsEnabled, "another device cannot use the old scan but can still load a local preview");
        selected.SelectedIndex = 1;
        Assert(export.IsEnabled, "returning to the scanned device retains usable identifier");
        setCard.Invoke(window, new object[] { scanned });
        Assert(((Button)window.FindName("ApplySkinButton")).IsEnabled && (CardArtworkFormat)typeof(MainWindow).GetField("cardFormat", flags).GetValue(window) == CardArtworkFormat.Unknown,
            "rescanning preserves the local preview and defers fresh format identification to application");
        Assert(!File.Exists(Path.Combine(Storage.Root, "cards.json")), "scanning and operations never save card history");
        ((TextBox)window.FindName("HashBox")).Text = "../invalid"; Assert(!export.IsEnabled, "UI rejects invalid hash");
        var notice = (Window)typeof(MainWindow).GetMethod("CreateDriverNotice", flags).Invoke(window, new object[] { "未找到 64 位 Apple Mobile Device Support。" });
        var noticeGrid = (Grid)((Border)notice.Content).Child;
        var buttonPanel = ((StackPanel)noticeGrid.Children[1]).Children.OfType<StackPanel>().Single();
        Assert(buttonPanel.Children.OfType<Button>().Select(button => (string)button.Content).SequenceEqual(new[] { "下载完整 iTunes", "取消" }),
            "missing environment offers only full iTunes download and cancel");
        Assert(buttonPanel.Children.OfType<Button>().Last().IsCancel && !(bool)notice.GetType().GetProperty("SecondarySelected", flags).GetValue(notice),
            "cancel/close cannot choose driver installation by default");
        var noticeContent = (FrameworkElement)notice.Content;
        noticeContent.Measure(new System.Windows.Size(notice.Width, double.PositiveInfinity)); var size = noticeContent.DesiredSize;
        noticeContent.Arrange(new Rect(0, 0, size.Width, size.Height)); noticeContent.UpdateLayout();
        var noticeBitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32); noticeBitmap.Render(noticeContent);
        var noticeEncoder = new PngBitmapEncoder(); noticeEncoder.Frames.Add(BitmapFrame.Create(noticeBitmap));
        using (var stream = File.Create(Path.Combine(artifacts, "driver-notice.png"))) noticeEncoder.Save(stream);
        var selectionType = typeof(MainWindow).Assembly.GetType("AirCard.Controls.ExportSelection");
        var catalog = new CardResourceCatalog();
        catalog.LocalAssets.Add("custom-art.png"); catalog.LocalAssets.Add("cardBackgroundCombined.png.urls");
        catalog.RemoteAssets.Add("remote-art.png", new RemoteArtwork("remote-art.png", "https://assets.apple.com/art", 3, "a9993e364706816aba3e25717850c26c9cd0d89d"));
        var selection = Activator.CreateInstance(selectionType, flags, null, new object[] { catalog }, null);
        var choices = (List<CheckBox>)selectionType.GetField("choices", flags).GetValue(selection);
        var selectionWindow = (Window)selectionType.GetProperty("Window", flags).GetValue(selection);
        var accept = (Button)selectionWindow.GetType().GetProperty("AcceptButton", flags).GetValue(selectionWindow);
        Assert(choices.Count == 4 && choices.Take(3).All(c => c.IsChecked == true) && choices.Last().IsChecked == false && accept.IsEnabled, "dynamic list selects device resources but never remote downloads by default");
        Assert(choices.All(c => c.Tag == null || !((string)c.Tag).EndsWith(".urls")), "resource indexes never appear in export selection");
        Assert(((string[])selectionType.GetProperty("RemoteAssets", flags).GetValue(selection)).Length == 0, "no remote downloads selected by default");
        foreach (var choice in choices) choice.IsChecked = false;
        Assert(!accept.IsEnabled, "empty checkbox selection disables continuation");
        choices[0].IsChecked = true;
        Assert(accept.IsEnabled && (bool)selectionType.GetProperty("IncludeCache", flags).GetValue(selection) && ((string[])selectionType.GetProperty("OriginalAssets", flags).GetValue(selection)).Length == 0, "checkbox list supports cache-only selection");
        choices[0].IsChecked = false; choices[1].IsChecked = true;
        Assert(!(bool)selectionType.GetProperty("IncludeCache", flags).GetValue(selection) && ((string[])selectionType.GetProperty("OriginalAssets", flags).GetValue(selection)).Single() == "custom-art.png", "checkbox list keeps arbitrary original selection separate from cache");
        choices[1].IsChecked = false; choices.Last().IsChecked = true;
        Assert(((string[])selectionType.GetProperty("OriginalAssets", flags).GetValue(selection)).Length == 0 && ((string[])selectionType.GetProperty("RemoteAssets", flags).GetValue(selection)).Single() == "remote-art.png", "remote choice cannot silently select device reads");
        foreach (var choice in choices) choice.IsChecked = true;
        var selectionContent = (FrameworkElement)selectionWindow.Content;
        selectionContent.Measure(new System.Windows.Size(selectionWindow.Width, double.PositiveInfinity)); var selectionSize = selectionContent.DesiredSize;
        selectionContent.Arrange(new Rect(0, 0, selectionSize.Width, selectionSize.Height)); selectionContent.UpdateLayout();
        var selectionBitmap = new RenderTargetBitmap((int)Math.Ceiling(selectionSize.Width), (int)Math.Ceiling(selectionSize.Height), 96, 96, PixelFormats.Pbgra32); selectionBitmap.Render(selectionContent);
        var selectionEncoder = new PngBitmapEncoder(); selectionEncoder.Frames.Add(BitmapFrame.Create(selectionBitmap));
        using (var stream = File.Create(Path.Combine(artifacts, "export-selection.png"))) selectionEncoder.Save(stream);
        window.Close(); app.Shutdown();
    }
    static void TestPendingDevices()
    {
        string originalRoot = Storage.Root;
        try
        {
            Storage.Root = Path.Combine(artifacts, "pending-device-tests");
            Storage.Save(Path.Combine(Storage.RecoveryRoot, "phone-a.json"), new RecoveryRecord { Udid = "PHONE-A", Token = "a" });
            Storage.Save(Path.Combine(Storage.RecoveryRoot, "phone-b.json"), new RecoveryRecord { Udid = "phone-b", Token = "b" });
            File.WriteAllText(Path.Combine(Storage.RecoveryRoot, "malformed-old.json"), "invalid old journal");
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var select = typeof(WalletEngine).GetMethod("CurrentRecordsForDevice", flags);
            var engine = new WalletEngine(null);
            Func<WalletEngine, string, RecoveryRecord[]> records = (e, device) => (RecoveryRecord[])select.Invoke(e, new object[] { device });
            Assert(records(engine, "phone-a").Length == 0, "new operation never loads old journals including malformed ones");
            var current = (List<RecoveryRecord>)typeof(WalletEngine).GetField("currentRecords", flags).GetValue(engine);
            current.Add(new RecoveryRecord { Udid = "PHONE-A", Token = "current-a", Phase = "WatchingExport" });
            current.Add(new RecoveryRecord { Udid = "phone-b", Token = "current-b", Phase = "WatchingExport" });
            Assert(records(engine, "phone-a").Single().Token == "current-a", "late replies in current operation still match device case-insensitively");
            Assert(records(engine, "phone-b").Single().Token == "current-b" && records(engine, "phone-c").Length == 0, "current cleanup cannot touch another device");
            Assert(records(new WalletEngine(null), "phone-a").Length == 0, "retry or restart cannot inherit previous operation records");
            Assert(Directory.GetFiles(Storage.RecoveryRoot, "*.json").Length == 3 && File.ReadAllText(Path.Combine(Storage.RecoveryRoot, "malformed-old.json")) == "invalid old journal", "historical journals are preserved without reading or deleting them");
        }
        finally { Storage.Root = originalRoot; }
    }
}
