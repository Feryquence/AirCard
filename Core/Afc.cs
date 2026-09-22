using System;
using System.Collections.Generic;
using System.IO;

namespace AirCard.Core
{
    internal sealed class AfcException : IOException
    {
        internal int Code { get; private set; }
        internal AfcException(int code, string path) : base("读取文件信息 " + path + " 失败 (0x" + code.ToString("X8") + ")。") { Code = code; }
    }
    // All paths are relative to /var/mobile/Media. Never recursively follow links.
    internal sealed class Afc : IDisposable
    {
        IntPtr connection; readonly DeviceService service;
        internal Afc(DeviceSession session)
        {
            service = session.Service("com.apple.afc");
            try
            {
                Native.Check(Native.AFCConnectionOpen(Native.AMDServiceConnectionGetSocket(service.Handle), 0, out connection), "打开 AFC");
                if (connection == IntPtr.Zero) throw new IOException("空 AFC 连接。");
                var ssl = Native.AMDServiceConnectionGetSecureIOContext(service.Handle);
                if (ssl != IntPtr.Zero)
                {
                    Native.Check(Native.AFCConnectionSetSecureContext(connection, ssl), "AFC SSL");
                    Native.Check(Native.AFCConnectionSetDisposeSecureContextOnInvalidate(connection, 0), "AFC SSL ownership");
                }
                Native.Check(Native.AFCConnectionSetIOTimeout(connection, 30), "AFC timeout");
            }
            catch { Dispose(); throw; }
        }
        internal Dictionary<string, string> Info(string path)
        {
            IntPtr info; int code = Native.AFCFileInfoOpen(connection, Native.Utf8(path), out info);
            if (code != 0) { if (info != IntPtr.Zero) Native.AFCKeyValueClose(info); if (code == 8) return null; throw new AfcException(code, path); }
            if (info == IntPtr.Zero) throw new IOException("AFC returned empty metadata: " + path);
            try
            {
                var result = new Dictionary<string, string>(); IntPtr key, value;
                while (true)
                {
                    Native.Check(Native.AFCKeyValueRead(info, out key, out value), "读取 AFC 属性");
                    if (key == IntPtr.Zero || value == IntPtr.Zero) break;
                    result[Native.CString(key)] = Native.CString(value);
                }
                return result;
            }
            finally { Native.AFCKeyValueClose(info); }
        }
        internal bool Exists(string path) { return Info(path) != null; }
        internal bool ExportStagingExists(string path) { return ProbeExportStaging(path, () => Exists(path), () => List(".")); }
        internal static bool ProbeExportStaging(string path, Func<bool> stat, Func<string[]> listRoot)
        {
            // A failed stat is not proof of absence. For our own root-level staging
            // name only, a successful directory listing can confirm it is missing.
            if (!System.Text.RegularExpressions.Regex.IsMatch(path ?? "", "^airlift-recovered-[a-f0-9]{32}$"))
                throw new ArgumentException("Invalid export staging path.");
            try { return stat(); }
            catch (AfcException error) when (error.Code == 4)
            {
                string[] names = listRoot();
                if (names == null) throw new IOException("无法确认设备暂存目录状态。", error);
                if (Array.Exists(names, name => string.Equals(name, path, StringComparison.OrdinalIgnoreCase))) throw;
                return false;
            }
        }
        internal byte[] Read(string path, int max = 32 * 1024 * 1024)
        {
            var info = Info(path); string raw; long size;
            if (info == null) throw new FileNotFoundException("设备文件不存在: " + path);
            if (!info.TryGetValue("st_size", out raw) || !long.TryParse(raw, out size) || size < 0 || size > max) throw new IOException("设备文件大小异常: " + path);
            ulong file; Native.Check(Native.AFCFileRefOpen(connection, Native.Utf8(path), 1, out file), "打开 " + path);
            try
            {
                using (var output = new MemoryStream((int)size))
                {
                    byte[] buffer = new byte[65536];
                    while (output.Length < size)
                    {
                        int requested = (int)Math.Min(buffer.Length, size - output.Length); IntPtr n = (IntPtr)requested;
                        Native.Check(Native.AFCFileRefRead(connection, file, buffer, ref n), "读取 " + path);
                        if (n.ToInt64() <= 0 || n.ToInt64() > requested) throw new EndOfStreamException("AFC 文件读取不完整。");
                        output.Write(buffer, 0, n.ToInt32());
                    }
                    return output.ToArray();
                }
            }
            finally { Native.AFCFileRefClose(connection, file); }
        }
        internal void Write(string path, byte[] data)
        {
            ulong file; Native.Check(Native.AFCFileRefOpen(connection, Native.Utf8(path), 3, out file), "创建 " + path);
            try
            {
                for (int i = 0; i < data.Length; i += 65536)
                {
                    int length = Math.Min(65536, data.Length - i); var chunk = new byte[length]; Buffer.BlockCopy(data, i, chunk, 0, length);
                    Native.Check(Native.AFCFileRefWrite(connection, file, chunk, (IntPtr)length), "写入 " + path);
                }
            }
            finally { Native.AFCFileRefClose(connection, file); }
        }
        internal void Mkdir(string path)
        {
            string current = "";
            foreach (string part in path.Split('/')) { if (part.Length == 0) continue; current = current.Length == 0 ? part : current + "/" + part; if (!Exists(current)) Native.Check(Native.AFCDirectoryCreate(connection, Native.Utf8(current)), "创建目录 " + current); }
        }
        internal void Remove(string path) { if (Exists(path)) Native.Check(Native.AFCRemovePath(connection, Native.Utf8(path)), "清理 " + path); }
        internal string[] List(string path)
        {
            IntPtr directory; Native.Check(Native.AFCDirectoryOpen(connection, Native.Utf8(path), out directory), "列举目录 " + path);
            var names = new List<string>();
            try
            {
                while (true)
                {
                    IntPtr entry; Native.Check(Native.AFCDirectoryRead(connection, directory, out entry), "读取目录 " + path);
                    if (entry == IntPtr.Zero) break;
                    string name = Native.CString(entry); if (name.Length == 0) break;
                    if (name == "." || name == "..") continue;
                    if (name.IndexOfAny(new[] { '/', '\\' }) >= 0) throw new IOException("Unsafe directory entry.");
                    names.Add(name);
                }
            }
            finally { Native.AFCDirectoryClose(connection, directory); }
            return names.ToArray();
        }
        internal void RemoveStagingTree(string path, int depth = 0)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"^airlift-src-[a-f0-9]{32}(?:/[^.][^\x00]*)?$")) throw new IOException("Refusing to remove non-staging path.");
            if (depth > 32) throw new IOException("Staging tree exceeds depth limit.");
            var info = Info(path); if (info == null) return;
            string type;
            if (!info.TryGetValue("st_ifmt", out type)) throw new IOException("Unknown staging node type.");
            if (type == "S_IFDIR")
            {
                IntPtr directory; Native.Check(Native.AFCDirectoryOpen(connection, Native.Utf8(path), out directory), "列举暂存目录");
                var names = new List<string>();
                try
                {
                    while (true)
                    {
                        IntPtr entry; Native.Check(Native.AFCDirectoryRead(connection, directory, out entry), "读取暂存目录");
                        if (entry == IntPtr.Zero) break;
                        string name = Native.CString(entry); if (name.Length == 0) break;
                        if (name == "." || name == "..") continue;
                        if (name.IndexOfAny(new[] {'/', '\\'}) >= 0) throw new IOException("Unsafe staging entry.");
                        names.Add(name);
                    }
                }
                finally { Native.AFCDirectoryClose(connection, directory); }
                foreach (string name in names) RemoveStagingTree(path + "/" + name, depth + 1);
            }
            Remove(path);
        }
        public void Dispose() { if (connection != IntPtr.Zero) { Native.AFCConnectionClose(connection); connection = IntPtr.Zero; } service.Dispose(); }
    }
}
