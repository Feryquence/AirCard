using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AirCard.Core
{
    public static class WalletFaceArchive
    {
        public static byte[] Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("钱包显示缓存大小无效。");
            if (IsImage(bytes)) return bytes;
            // Observed FrontFace format: a 48-byte cache header, followed by a
            // binary NSKeyedArchiver property list. Decode data only; never invoke
            // NSKeyedUnarchiver or instantiate archive-specified classes.
            int offset = HasPlistHeader(bytes, 0) ? 0 : HasPlistHeader(bytes, 48) ? 48 : -1;
            if (offset < 0) throw new InvalidDataException("暂不支持这张卡的钱包缓存格式。");
            Dictionary<string, object> archive;
            try
            {
                using (var plist = Cf.FromBytes(bytes.Skip(offset).ToArray()))
                    archive = Plist.Read(Native.Serialize(plist.Handle)) as Dictionary<string, object>;
            }
            catch (Exception error) { throw new InvalidDataException("无法解析钱包卡面归档。", error); }
            return ReadFaceImage(archive);
        }
        static bool HasPlistHeader(byte[] bytes, int offset)
        {
            return bytes.Length >= offset + 8 && Encoding.ASCII.GetString(bytes, offset, 8) == "bplist00";
        }
        static bool IsImage(byte[] bytes)
        {
            try { WalletEngine.DisplayImageExtension(bytes); return true; }
            catch (InvalidDataException) { return false; }
        }
        public static byte[] ReadFaceImage(Dictionary<string, object> archive)
        {
            object archiver, rawObjects, rawTop;
            if (archive == null || !archive.TryGetValue("$archiver", out archiver) || !Equals(archiver, "NSKeyedArchiver") ||
                !archive.TryGetValue("$objects", out rawObjects) || !(rawObjects is object[]) ||
                !archive.TryGetValue("$top", out rawTop) || !(rawTop is Dictionary<string, object>))
                throw new InvalidDataException("钱包卡面归档结构无效。");
            var objects = (object[])rawObjects;
            var root = Resolve(Get((Dictionary<string, object>)rawTop, "root"), objects) as Dictionary<string, object>;
            var face = Resolve(Get(root, "faceImage"), objects) as Dictionary<string, object>;
            var data = Resolve(Get(face, "imageData"), objects);
            var wrapper = data as Dictionary<string, object>;
            byte[] image = (wrapper == null ? data : Get(wrapper, "NS.data")) as byte[];
            if (!IsImage(image)) throw new InvalidDataException("钱包归档的 faceImage 不是 PNG/JPEG 图片。");
            // Select faceImage explicitly. Another NSData may hold only the card
            // shadow; signature scanning / taking the largest blob is not reliable.
            return image;
        }
        static object Get(Dictionary<string, object> dictionary, string key)
        {
            object value;
            if (dictionary == null || !dictionary.TryGetValue(key, out value)) throw new InvalidDataException("钱包归档缺少 " + key + "。");
            return value;
        }
        static object Resolve(object value, object[] objects)
        {
            var reference = value as Dictionary<string, object>; object raw;
            if (reference == null || reference.Count != 1 || !reference.TryGetValue("CF$UID", out raw)) return value;
            if (!(raw is long) || (long)raw < 0 || (long)raw >= objects.Length) throw new InvalidDataException("钱包归档包含无效对象引用。");
            return objects[(int)(long)raw];
        }
    }
}
