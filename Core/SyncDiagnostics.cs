using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace AirCard.Core
{
    public static class SyncDiagnostics
    {
        // Read only known error fields, never dump the complete sync manifest.
        static readonly string[] Keys = { "ErrorCode", "ErrorDomain", "ErrorDescription", "Error", "Reason" };
        public static string Scalar(byte[] plist)
        {
            using (var stream = new MemoryStream(plist))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 16384
            }))
            {
                var root = XDocument.Load(reader).Root;
                if (root == null || root.Name != "plist") return null;
                var value = root.Elements().SingleOrDefault();
                if (value == null || value.HasElements) return null;
                // Keep integer text: unsigned Apple error codes may exceed Int64.
                switch (value.Name.LocalName) {
                    case "integer": case "real": case "string": return value.Value;
                    case "true": return "true";
                    case "false": return "false";
                    default: return null;
                }
            }
        }
        public static IOException Failure(string expected, string terminal, Func<string, string> readParameter)
        {
            var details = new List<string>();
            foreach (string key in Keys)
            {
                try
                {
                    string value = readParameter(key);
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    value = new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
                    if (value.Length > 512) value = value.Substring(0, 512) + "…";
                    details.Add(key + "=" + value);
                }
                catch (Exception) { details.Add(key + "=<无法读取>"); }
            }
            return new IOException("AirTraffic 同步提前结束：等待 " + expected + " 时收到 " + terminal + "。" +
                "本次同步尚未发送卡面资源移动确认。" +
                (details.Count == 0 ? "设备未提供可读取的错误详情。" : "设备错误：" + string.Join("；", details)));
        }
    }
}
