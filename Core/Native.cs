using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AirCard.Core
{
    public sealed class AppleDriverException : IOException
    {
        public AppleDriverException(string message, Exception inner = null) : base(message, inner) { }
    }
    internal static class Native
    {
        const string CF = "CoreFoundation.dll", MD = "MobileDevice.dll", AT = "AirTrafficHost.dll";
        static readonly object Gate = new object();
        static bool loaded;
        // Kept loaded for process lifetime: Apple's frameworks have global state.
        static readonly System.Collections.Generic.List<IntPtr> Modules = new System.Collections.Generic.List<IntPtr>();
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr AddDllDirectory(string path);
        public static string EnsureLoaded()
        {
            lock (Gate)
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "Apple", "Mobile Device Support");
                if (loaded) return path;
                if (!Environment.Is64BitProcess) throw new InvalidOperationException("请使用 x64 版本。");
                if (!new[] { CF, MD, AT }.All(n => File.Exists(Path.Combine(path, n))))
                    throw new AppleDriverException("未找到 64 位 Apple Mobile Device Support。请选择安装方式。");
                if (AddDllDirectory(path) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                foreach (string dll in new[] { CF, MD, AT })
                {
                    var h = LoadLibraryEx(Path.Combine(path, dll), IntPtr.Zero, 0x100 | 0x1000);
                    if (h == IntPtr.Zero) throw new AppleDriverException("Apple 驱动无法加载，请安装或修复设备支持组件后重新启动 Air Card。", new Win32Exception(Marshal.GetLastWin32Error(), "无法加载 " + dll));
                    Modules.Add(h);
                }
                loaded = true; return path;
            }
        }
        public static void Check(int code, string operation) { if (code != 0) throw new IOException(operation + " 失败 (0x" + code.ToString("X8") + ")。"); }
        internal static string DriverVersions()
        {
            string path = EnsureLoaded();
            return "Apple 驱动: " + string.Join("；", new[] { MD, AT }.Select(name => {
                try { return name + " " + System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(path, name)).FileVersion; }
                catch (Exception) { return name + " <版本不可用>"; }
            }));
        }
        [DllImport(CF, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr CFStringCreateWithCString(IntPtr allocator, byte[] text, uint encoding);
        [DllImport(CF, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr CFStringGetLength(IntPtr value);
        [DllImport(CF, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr CFStringGetMaximumSizeForEncoding(IntPtr length, uint encoding);
        [DllImport(CF, CallingConvention = CallingConvention.Cdecl)] internal static extern byte CFStringGetCString(IntPtr value, byte[] output, IntPtr length, uint encoding);
        [DllImport(CF, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr CFDataCreate(IntPtr allocator, byte[] data, IntPtr length);
        [DllImport(CF, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr CFDataGetBytePtr(IntPtr data);
        [DllImport(CF, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr CFDataGetLength(IntPtr data);
        [DllImport(CF, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr CFPropertyListCreateWithData(IntPtr allocator, IntPtr data, UIntPtr options, IntPtr format, IntPtr error);
        [DllImport(CF, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr CFPropertyListCreateData(IntPtr allocator, IntPtr value, IntPtr format, UIntPtr options, IntPtr error);
        [DllImport(CF, CallingConvention = CallingConvention.Cdecl)] internal static extern void CFRelease(IntPtr value);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr AMDeviceCreateFromProperties(IntPtr props);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AMDeviceConnect(IntPtr device);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AMDeviceDisconnect(IntPtr device);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AMDeviceIsPaired(IntPtr device);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AMDevicePair(IntPtr device);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AMDeviceValidatePairing(IntPtr device);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AMDeviceStartSession(IntPtr device);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AMDeviceStopSession(IntPtr device);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr AMDeviceCopyValue(IntPtr device, IntPtr domain, IntPtr key);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AMDeviceSecureStartService(IntPtr device, IntPtr name, IntPtr options, out IntPtr service);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AMDServiceConnectionGetSocket(IntPtr service);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr AMDServiceConnectionGetSecureIOContext(IntPtr service);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AMDServiceConnectionInvalidate(IntPtr service);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AMDServiceConnectionSend(IntPtr service, byte[] data, UIntPtr count);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AMDServiceConnectionReceive(IntPtr service, byte[] data, UIntPtr count);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AMDServiceConnectionSendMessage(IntPtr service, IntPtr message, IntPtr format);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AMDServiceConnectionReceiveMessage(IntPtr service, out IntPtr message, out IntPtr format);
        [DllImport("ws2_32", SetLastError = true)] internal static extern int setsockopt(UIntPtr socket, int level, int option, ref int value, int length);
        [DllImport("ws2_32")] internal static extern int WSAGetLastError();
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AFCConnectionOpen(int socket, uint flags, out IntPtr afc);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AFCConnectionClose(IntPtr afc);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AFCConnectionSetSecureContext(IntPtr afc, IntPtr ssl);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AFCConnectionSetDisposeSecureContextOnInvalidate(IntPtr afc, int dispose);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AFCConnectionSetIOTimeout(IntPtr afc, uint seconds);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AFCFileInfoOpen(IntPtr afc, byte[] path, out IntPtr info);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AFCKeyValueRead(IntPtr info, out IntPtr key, out IntPtr value);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AFCKeyValueClose(IntPtr info);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AFCFileRefOpen(IntPtr afc, byte[] path, ulong mode, out ulong file);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AFCFileRefRead(IntPtr afc, ulong file, byte[] buffer, ref IntPtr count);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AFCFileRefWrite(IntPtr afc, ulong file, byte[] buffer, IntPtr count);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AFCFileRefClose(IntPtr afc, ulong file);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AFCDirectoryCreate(IntPtr afc, byte[] path);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AFCRemovePath(IntPtr afc, byte[] path);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AFCDirectoryOpen(IntPtr afc, byte[] path, out IntPtr directory);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AFCDirectoryRead(IntPtr afc, IntPtr directory, out IntPtr entry);
        [DllImport(MD, CallingConvention = CallingConvention.Cdecl)] internal static extern int AFCDirectoryClose(IntPtr afc, IntPtr directory);
        [DllImport(AT, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr ATHostConnectionCreate(IntPtr udid);
        [DllImport(AT, CallingConvention = CallingConvention.Cdecl)] internal static extern void ATHostConnectionRelease(IntPtr connection);
        [DllImport(AT, CallingConvention = CallingConvention.Cdecl)] internal static extern void ATHostConnectionSendHostInfo(IntPtr connection, IntPtr info);
        [DllImport(AT, CallingConvention = CallingConvention.Cdecl)] internal static extern void ATHostConnectionSendSyncRequest(IntPtr connection, IntPtr classes, IntPtr anchors, IntPtr info);
        [DllImport(AT, CallingConvention = CallingConvention.Cdecl)] internal static extern void ATHostConnectionSendMetadataSyncFinished(IntPtr connection, IntPtr types, IntPtr anchors);
        [DllImport(AT, CallingConvention = CallingConvention.Cdecl)] internal static extern void ATHostConnectionSendAssetCompleted(IntPtr connection, IntPtr identifier, IntPtr dataClass, IntPtr destination);
        [DllImport(AT, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr ATHostConnectionReadMessage(IntPtr connection);
        [DllImport(AT, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr ATCFMessageGetName(IntPtr message);
        [DllImport(AT, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr ATCFMessageGetParam(IntPtr message, IntPtr key);

        internal static byte[] Utf8(string text)
        {
            if (text.IndexOf('\0') >= 0) throw new ArgumentException("NUL in native string.");
            return Encoding.UTF8.GetBytes(text + "\0");
        }
        internal static string CString(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return "";
            int n = 0; while (n < 65536 && Marshal.ReadByte(ptr, n) != 0) n++;
            if (n == 65536) throw new InvalidDataException("Native string exceeds limit.");
            var b = new byte[n]; Marshal.Copy(ptr, b, 0, n); return Encoding.UTF8.GetString(b);
        }
        internal static string Text(IntPtr value)
        {
            if (value == IntPtr.Zero) return "";
            long n = CFStringGetMaximumSizeForEncoding(CFStringGetLength(value), 0x08000100).ToInt64() + 1;
            if (n <= 0 || n > 1024 * 1024) throw new InvalidDataException("Invalid CF string length.");
            var b = new byte[(int)n]; if (CFStringGetCString(value, b, (IntPtr)n, 0x08000100) == 0) return "";
            int end = Array.IndexOf(b, (byte)0); return Encoding.UTF8.GetString(b, 0, end < 0 ? b.Length : end);
        }
        internal static byte[] Serialize(IntPtr value, int format = 100)
        {
            using (var data = new Cf(CFPropertyListCreateData(IntPtr.Zero, value, (IntPtr)format, UIntPtr.Zero, IntPtr.Zero)))
            {
                long size = CFDataGetLength(data.Handle).ToInt64();
                if (size < 0 || size > 32 * 1024 * 1024) throw new InvalidDataException("CF plist exceeds limit.");
                byte[] b = new byte[(int)size]; Marshal.Copy(CFDataGetBytePtr(data.Handle), b, 0, b.Length); return b;
            }
        }
    }
    internal sealed class Cf : IDisposable
    {
        internal IntPtr Handle { get; private set; }
        internal Cf(IntPtr handle) { if (handle == IntPtr.Zero) throw new IOException("Apple CoreFoundation 返回空对象。"); Handle = handle; }
        internal static Cf String(string text) { return new Cf(Native.CFStringCreateWithCString(IntPtr.Zero, Native.Utf8(text), 0x08000100)); }
        internal static Cf From(object value) { return FromBytes(Plist.Write(value)); }
        internal static Cf FromBytes(byte[] bytes)
        {
            using (var data = new Cf(Native.CFDataCreate(IntPtr.Zero, bytes, (IntPtr)bytes.Length)))
                return new Cf(Native.CFPropertyListCreateWithData(IntPtr.Zero, data.Handle, UIntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        }
        public void Dispose() { if (Handle != IntPtr.Zero) { Native.CFRelease(Handle); Handle = IntPtr.Zero; } }
    }
}
