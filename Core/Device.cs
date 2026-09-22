using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;

namespace AirCard.Core
{
    public enum ConnectionMode { Auto, Usb, Wifi }
    public sealed class DeviceInfo
    {
        public string Udid { get; set; }
        public string Name { get; set; }
        public string Product { get; set; }
        public string Version { get; set; }
        public string Transport { get; set; }
        internal Dictionary<string, object> Properties;
        public override string ToString() { return Name + " · " + Product + " · iOS " + Version + " · " + Transport; }
    }
    public static class Devices
    {
        static byte[] ReadExactly(Stream s, int count)
        {
            byte[] data = new byte[count]; int offset = 0;
            while (offset < count) { int n = s.Read(data, offset, count - offset); if (n == 0) throw new EndOfStreamException("Apple 设备服务已断开。"); offset += n; }
            return data;
        }
        internal static List<DeviceInfo> Query()
        {
            using (var client = new TcpClient())
            {
                var connect = client.BeginConnect("127.0.0.1", 27015, null, null);
                using (connect.AsyncWaitHandle) { if (!connect.AsyncWaitHandle.WaitOne(2000)) throw new IOException("Apple Mobile Device Service 连接超时。"); client.EndConnect(connect); }
                client.ReceiveTimeout = 3000; client.SendTimeout = 3000;
                var stream = client.GetStream();
                byte[] body = Plist.Write(Plist.Dict("MessageType", "ListDevices", "ClientVersionString", "AirCard.NetFramework", "ProgName", "AirCard"));
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) { writer.Write(body.Length + 16); writer.Write(1); writer.Write(8); writer.Write(1); writer.Write(body); writer.Flush(); }
                var header = ReadExactly(stream, 16); int size = BitConverter.ToInt32(header, 0);
                if (size < 16 || size > 16 * 1024 * 1024 || BitConverter.ToInt32(header, 4) != 1 || BitConverter.ToInt32(header, 8) != 8)
                    throw new InvalidDataException("Invalid usbmux response.");
                var root = (Dictionary<string, object>)Plist.Read(ReadExactly(stream, size - 16));
                object list;
                if (!root.TryGetValue("DeviceList", out list)) throw new IOException("Apple 服务未返回设备列表。");
                var result = new List<DeviceInfo>();
                foreach (var raw in (object[])list)
                {
                    var row = raw as Dictionary<string, object>; object props;
                    if (row == null || !row.TryGetValue("Properties", out props)) continue;
                    var d = (Dictionary<string, object>)props;
                    string transport = Plist.Text(d, "ConnectionType", "USB");
                    if (transport != "USB" && transport != "Network") continue;
                    string udid = Plist.Text(d, "SerialNumber"); if (string.IsNullOrWhiteSpace(udid)) continue;
                    result.Add(new DeviceInfo { Udid = udid, Transport = transport == "Network" ? "WiFi" : "USB", Name = "iPhone", Product = "iPhone", Version = "未知", Properties = d });
                }
                return result;
            }
        }
        public static List<DeviceInfo> List()
        {
            Native.EnsureLoaded(); var entries = Query();
            foreach (var info in entries)
            {
                using (var props = Cf.From(info.Properties))
                using (var device = new Cf(Native.AMDeviceCreateFromProperties(props.Handle)))
                {
                    if (Native.AMDeviceConnect(device.Handle) != 0) continue;
                    try
                    {
                        Native.AMDeviceValidatePairing(device.Handle);
                        info.Name = Value(device.Handle, "DeviceName", "iPhone");
                        info.Product = Value(device.Handle, "ProductType", "iPhone");
                        info.Version = Value(device.Handle, "ProductVersion", "未知");
                    }
                    finally { Native.AMDeviceDisconnect(device.Handle); }
                }
            }
            return entries.GroupBy(d => d.Udid, StringComparer.OrdinalIgnoreCase).Select(g => {
                var d = g.OrderBy(x => x.Transport == "USB" ? 0 : 1).First(); d.Transport = string.Join(" + ", g.Select(x => x.Transport).Distinct()); return d;
            }).ToList();
        }
        static string Value(IntPtr device, string key, string fallback)
        {
            using (var k = Cf.String(key))
            {
                var v = Native.AMDeviceCopyValue(device, IntPtr.Zero, k.Handle); if (v == IntPtr.Zero) return fallback;
                using (var cf = new Cf(v)) return Native.Text(cf.Handle);
            }
        }
        public static string CheckSupport() { return Native.EnsureLoaded(); }
    }
    internal sealed class DeviceSession : IDisposable
    {
        IntPtr device; bool connected, started;
        internal string Udid, Transport;
        internal static DeviceSession Open(string udid, ConnectionMode mode)
        {
            Native.EnsureLoaded(); var errors = new List<string>();
            foreach (var e in Devices.Query().Where(e => string.Equals(e.Udid, udid, StringComparison.OrdinalIgnoreCase)
                && (mode == ConnectionMode.Auto || e.Transport == (mode == ConnectionMode.Usb ? "USB" : "WiFi"))).OrderBy(e => e.Transport == "USB" ? 0 : 1))
            {
                var s = new DeviceSession { Udid = e.Udid, Transport = e.Transport };
                try
                {
                    using (var props = Cf.From(e.Properties)) s.device = Native.AMDeviceCreateFromProperties(props.Handle);
                    if (s.device == IntPtr.Zero) throw new IOException("无法创建设备对象。");
                    Native.Check(Native.AMDeviceConnect(s.device), "连接设备"); s.connected = true;
                    if (Native.AMDeviceIsPaired(s.device) == 0 && e.Transport == "USB") Native.Check(Native.AMDevicePair(s.device), "配对（请在手机上信任此电脑）");
                    Native.Check(Native.AMDeviceValidatePairing(s.device), "验证配对（请解锁手机，WiFi 需先通过 USB 配对）");
                    Native.Check(Native.AMDeviceStartSession(s.device), "启动设备会话"); s.started = true; return s;
                }
                catch (Exception ex) { s.Dispose(); errors.Add(e.Transport + ": " + ex.Message); }
            }
            throw new IOException(errors.Count == 0 ? "指定的设备或连接方式不可用，请刷新设备列表。" : string.Join("; ", errors));
        }
        internal DeviceService Service(string name)
        {
            using (var n = Cf.String(name))
            {
                IntPtr service; Native.Check(Native.AMDeviceSecureStartService(device, n.Handle, IntPtr.Zero, out service), name);
                if (service == IntPtr.Zero) throw new IOException("Apple 返回空服务。");
                return new DeviceService(service);
            }
        }
        public void Dispose()
        {
            if (device == IntPtr.Zero) return;
            if (started) Native.AMDeviceStopSession(device);
            if (connected) Native.AMDeviceDisconnect(device);
            Native.CFRelease(device); device = IntPtr.Zero;
        }
    }
    internal sealed class DeviceService : IDisposable
    {
        internal IntPtr Handle { get; private set; }
        internal DeviceService(IntPtr handle) { Handle = handle; }
        internal void Timeout(int milliseconds)
        {
            int socket = Native.AMDServiceConnectionGetSocket(Handle);
            if (socket <= 0) throw new IOException("无效的 Apple 服务套接字。");
            if (Native.setsockopt((UIntPtr)(uint)socket, 0xffff, 0x1006, ref milliseconds, 4) != 0 ||
                Native.setsockopt((UIntPtr)(uint)socket, 0xffff, 0x1005, ref milliseconds, 4) != 0)
                throw new IOException("无法设置 Apple 服务超时。");
        }
        public void Dispose() { if (Handle != IntPtr.Zero) { Native.AMDServiceConnectionInvalidate(Handle); Handle = IntPtr.Zero; } }
    }
}
