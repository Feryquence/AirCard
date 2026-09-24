using System;
using System.Collections.Generic;
using System.IO;

namespace AirCard.Core
{
    public sealed class BatchWriteResult
    {
        public string[] PendingAssets { get; private set; }
        public bool HasWarnings { get { return PendingAssets.Length != 0; } }

        public static BatchWriteResult Finish(string[] pendingAssets, bool optionalCache, Action cleanup)
        {
            // Required artwork/theme payloads still fail strictly. Only optional cache
            // invalidation can produce a warning, and only AFTER restoring Books state.
            if (pendingAssets.Length != 0 && !optionalCache)
                throw new IOException("同步资源未全部移动: " + string.Join(", ", pendingAssets) + "。保留恢复记录。");
            cleanup(); // A cleanup failure must propagate and keep the recovery journal.
            return new BatchWriteResult { PendingAssets = (string[])pendingAssets.Clone() };
        }
    }
    public sealed class CardApplyResult
    {
        public List<string> CacheWarnings { get; } = new List<string>();
        public string Summary { get { return CacheWarnings.Count == 0 ? "卡面已写入，缓存刷新完成；请重新打开钱包。" : "卡面已写入；部分缓存未刷新，请退出并重新打开钱包。"; } }
    }
    public sealed class CardAppliedException : IOException
    {
        public CardAppliedException(Exception inner)
            : base("卡面已写入；后续缓存刷新或同步状态恢复未完成，请重连同一台手机后重试。", inner) { }
    }
}
