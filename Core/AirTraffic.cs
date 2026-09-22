using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace AirCard.Core
{
    internal sealed class AssetMove
    {
        internal string Identifier, Destination;
        internal AssetMove(string identifier, string destination) { Identifier = identifier; Destination = destination; }
    }
    internal static class AirTraffic
    {
        internal static byte[] BuildArchive(string target, IList<byte[]> payloads)
        {
            using (var metadata = Cf.From(Plist.Dict("Version", 2))) return StreamingZip.Build(target, payloads, Native.Serialize(metadata.Handle, 200));
        }
        internal static void Stage(DeviceSession session, string source, byte[] archive)
        {
            using (var service = session.Service("com.apple.streaming_zip_conduit"))
            {
                service.Timeout(30000);
                using (var message = Cf.From(Plist.Dict("MediaSubdir", source))) Native.Check(Native.AMDServiceConnectionSendMessage(service.Handle, message.Handle, (IntPtr)200), "发送 StreamingZip 元数据");
                int sent = 0;
                while (sent < archive.Length)
                {
                    int count = Math.Min(65536, archive.Length - sent); var b = new byte[count]; Buffer.BlockCopy(archive, sent, b, 0, count);
                    int n = Native.AMDServiceConnectionSend(service.Handle, b, (UIntPtr)(uint)count);
                    if (n <= 0 || n > count) throw new IOException("StreamingZip 传输失败。"); sent += n;
                }
                IntPtr result, format; Native.Check(Native.AMDServiceConnectionReceiveMessage(service.Handle, out result, out format), "StreamingZip 响应");
                if (result != IntPtr.Zero)
                {
                    using (var response = new Cf(result))
                    {
                        var dict = Plist.Read(Native.Serialize(response.Handle)) as Dictionary<string, object>;
                        if (dict != null && dict.ContainsKey("Error")) throw new IOException("StreamingZip: " + Plist.Text(dict, "Error"));
                    }
                }
            }
        }
        internal static void Sync(DeviceSession session, IList<AssetMove> assets, Action<string> log, int assetDelayMilliseconds = 0)
        {
            if (!Devices.Query().Any(d => d.Udid == session.Udid && d.Transport == session.Transport)) throw new IOException("同步前设备连接已断开。");
            using (var udid = Cf.String(session.Udid))
            {
                var connection = Native.ATHostConnectionCreate(udid.Handle);
                if (connection == IntPtr.Zero) throw new IOException("无法连接 AirTraffic 服务。");
                try
                {
                    log("等待手机允许同步，请保持解锁；必要时先打开一次 Apple 图书。");
                    log("设备会话连接: " + session.Transport + "；AirTraffic 通道由 Apple 驱动选择。");
                    WaitFor(connection, "SyncAllowed", 30, log);
                    var info = Plist.Dict("Type", "iTunes", "Version", "13.7.0.161", "MacOSVersion", "Windows NT 10.0", "SyncHostName", "airlift",
                        "LibraryID", Guid.NewGuid().ToString(), "SyncedDataclasses", new[] {"Book"}, "SyncedAssetTypes", new[] {"Book"}, "Wakeable", false);
                    using (var host = Cf.From(info)) using (var classes = Cf.From(new[] { "Book" })) using (var anchors = Cf.From(Plist.Dict())) using (var types = Cf.From(Plist.Dict("Book", 1)))
                    {
                        Native.ATHostConnectionSendHostInfo(connection, host.Handle); Thread.Sleep(200);
                        Native.ATHostConnectionSendSyncRequest(connection, classes.Handle, anchors.Handle, host.Handle);
                        WaitFor(connection, "ReadyForSync", 40, log);
                        Native.ATHostConnectionSendMetadataSyncFinished(connection, types.Handle, anchors.Handle);
                        var manifest = WaitFor(connection, "AssetManifest", 60, log);
                        object books;
                        if (manifest == null || !manifest.TryGetValue("Book", out books)) throw new IOException("同步清单没有 Book 项。");
                        var available = new HashSet<string>(((object[])books).OfType<Dictionary<string, object>>().Where(d => d.ContainsKey("IsDownload") && Equals(d["IsDownload"], true)).Select(d => Plist.Text(d, "AssetID")));
                        foreach (var asset in assets) if (!available.Contains(asset.Identifier)) throw new IOException("同步清单缺少目标资源: " + asset.Identifier);
                        using (var dataClass = Cf.String("Book"))
                        {
                            for (int i = 0; i < assets.Count; i++)
                            {
                                using (var id = Cf.String(assets[i].Identifier)) using (var dest = Cf.String(assets[i].Destination))
                                    Native.ATHostConnectionSendAssetCompleted(connection, id.Handle, dataClass.Handle, dest.Handle);
                                Thread.Sleep(assetDelayMilliseconds > 0 ? assetDelayMilliseconds : i == 0 ? 400 : 60);
                            }
                        }
                    }
                    // Completion is checked by callers through the AFC staging objects.
                    Thread.Sleep(2000);
                }
                finally { Native.ATHostConnectionRelease(connection); }
            }
        }
        static Dictionary<string, object> WaitFor(IntPtr connection, string expected, int seconds, Action<string> log)
        {
            log("AirTraffic：等待 " + expected + "…");
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                // This proprietary call may block inside the driver. Never abandon it and
                // concurrently clean up device files; the UI stays busy until it returns.
                IntPtr raw = Native.ATHostConnectionReadMessage(connection);
                if (raw == IntPtr.Zero) { Thread.Sleep(150); continue; }
                using (var message = new Cf(raw))
                {
                    string name = Native.Text(Native.ATCFMessageGetName(message.Handle));
                    if (name == "SyncFailed" || name == "SyncFinished")
                    {
                        var error = SyncDiagnostics.Failure(expected, name, parameter => {
                            using (var key = Cf.String(parameter))
                            {
                                IntPtr value = Native.ATCFMessageGetParam(message.Handle, key.Handle);
                                return value == IntPtr.Zero ? null : SyncDiagnostics.Scalar(Native.Serialize(value));
                            }
                        });
                        log(error.Message); throw error;
                    }
                    if (name != expected) continue;
                    log("AirTraffic：已收到 " + expected + "。");
                    if (expected != "AssetManifest") return null;
                    using (var key = Cf.String("AssetManifest"))
                    {
                        var value = Native.ATCFMessageGetParam(message.Handle, key.Handle);
                        if (value == IntPtr.Zero) throw new IOException("空同步清单。");
                        return (Dictionary<string, object>)Plist.Read(Native.Serialize(value));
                    }
                }
            }
            throw new TimeoutException("AirTraffic 等待 " + expected + " 超时，本次同步尚未发送卡面资源移动确认。");
        }
    }
}
