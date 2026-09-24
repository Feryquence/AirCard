using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AirCard.Core
{
    // XML plist on the host; Apple's CoreFoundation converts binary service messages.
    public static class Plist
    {
        public static Dictionary<string, object> Dict(params object[] pairs)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2) d.Add((string)pairs[i], pairs[i + 1]);
            return d;
        }
        public static byte[] Write(object value)
        {
            var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
                new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
                new XElement("plist", new XAttribute("version", "1.0"), Encode(value)));
            using (var s = new MemoryStream()) { doc.Save(s); return s.ToArray(); }
        }
        static XElement Encode(object value)
        {
            if (value is string) return new XElement("string", value);
            if (value is bool) return new XElement((bool)value ? "true" : "false");
            if (value is byte[]) return new XElement("data", Convert.ToBase64String((byte[])value));
            var dict = value as IDictionary<string, object>;
            if (dict != null) return new XElement("dict", dict.SelectMany(p => new[] { new XElement("key", p.Key), Encode(p.Value) }));
            var items = value as IEnumerable;
            if (items != null) return new XElement("array", items.Cast<object>().Select(Encode));
            if (value == null) throw new InvalidDataException("Plist does not support null.");
            return new XElement("integer", Convert.ToString(value, CultureInfo.InvariantCulture));
        }
        public static object Read(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 32 * 1024 * 1024 }))
            {
                var root = XDocument.Load(reader).Root;
                if (root == null || root.Name != "plist") throw new InvalidDataException("Invalid plist root.");
                return Decode(root.Elements().Single());
            }
        }
        static object Decode(XElement e)
        {
            switch (e.Name.LocalName)
            {
                case "dict":
                    var d = new Dictionary<string, object>(); var children = e.Elements().ToArray();
                    if (children.Length % 2 != 0) throw new InvalidDataException("Unpaired plist key.");
                    for (int i = 0; i < children.Length; i += 2) { if (children[i].Name != "key") throw new InvalidDataException("Invalid plist key."); d.Add(children[i].Value, Decode(children[i + 1])); }
                    return d;
                case "array": return e.Elements().Select(Decode).ToArray();
                case "true": return true;
                case "false": return false;
                case "integer": return long.Parse(e.Value, CultureInfo.InvariantCulture);
                case "real": return double.Parse(e.Value, CultureInfo.InvariantCulture);
                case "data": return Convert.FromBase64String(e.Value);
                case "string": case "date": return e.Value;
                default: throw new InvalidDataException("Unsupported plist element: " + e.Name);
            }
        }
        public static string Text(IDictionary<string, object> d, string key, string fallback = "") { object v; return d.TryGetValue(key, out v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : fallback; }
    }
}
