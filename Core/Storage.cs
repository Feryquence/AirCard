using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace AirCard.Core
{
    public static class Storage
    {
        public static string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirCard.NetFramework");
        public static string RecoveryRoot { get { return Path.Combine(Root, "Recovery"); } }
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 128 * 1024 * 1024, RecursionLimit = 128 }; }
        public static void AtomicWrite(string path, byte[] bytes)
        {
            string full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full));
            string temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var s = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { s.Write(bytes, 0, bytes.Length); s.Flush(true); }
                if (File.Exists(full)) File.Replace(temp, full, null); else File.Move(temp, full);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static void Save<T>(string path, T value) { AtomicWrite(path, Encoding.UTF8.GetBytes(Json().Serialize(value))); }
        public static T Load<T>(string path) { return Json().Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)); }
    }
}
