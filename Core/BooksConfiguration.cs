using System;
using System.Collections.Generic;
using System.IO;

namespace AirCard.Core
{
    public static class BooksConfiguration
    {
        public static string[] Paths { get { return new[] { "Books/Books.plist", "Books/Sync/Books.plist", "Books/Sync/Upload.plist" }; } }

        public static void Restore(IDictionary<string, string> snapshot, Action<string, byte[]> write, Action<string> remove)
        {
            // atc keeps the SQLite database and its WAL/SHM open after a sync ends.
            // Copying old bytes over those files truncates live SQLite mappings.
            // Legacy journals may contain them: deliberately ignore those entries.
            var errors = new List<Exception>();
            foreach (string path in Paths)
            {
                try
                {
                    string saved;
                    if (snapshot == null || !snapshot.TryGetValue(path, out saved)) throw new IOException("操作记录缺少 " + path);
                    if (saved == null) remove(path);
                    else write(path, Convert.FromBase64String(saved));
                }
                catch (Exception error) { errors.Add(error); }
            }
            if (errors.Count != 0) throw new AggregateException("图书同步配置还原不完整，已保留操作记录。", errors);
        }
    }
}
