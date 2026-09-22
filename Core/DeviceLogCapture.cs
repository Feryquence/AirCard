using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AirCard.Core
{
    // Frame bytes before decoding: UTF-8 characters may straddle native reads.
    public sealed class SyncLogFilter
    {
        static readonly Regex Relevant = new Regex(@"\batc(?:\s|\[|\()|\((?:AirTraffic|AirTrafficDevice|ATFoundation|Books)\)", RegexOptions.IgnoreCase);
        readonly List<byte> pending = new List<byte>();
        readonly Action<string> write;
        readonly int limit;
        int size;
        bool discard, relevantRecord;
        public int Lines { get; private set; }
        public bool Truncated { get; private set; }
        public SyncLogFilter(Action<string> write, int limit = 2 * 1024 * 1024) { this.write = write; this.limit = limit; }
        public void Feed(byte[] bytes, int count)
        {
            if (count < 0 || count > bytes.Length) throw new ArgumentOutOfRangeException(nameof(count));
            for (int i = 0; i < count; i++)
            {
                byte b = bytes[i];
                if (b == 0 || b == 10)
                {
                    if (!discard && pending.Count > 0) Emit();
                    pending.Clear(); discard = false;
                }
                else if (!discard && b != 13)
                {
                    if (pending.Count >= 65536) { pending.Clear(); discard = true; relevantRecord = false; }
                    else pending.Add(b);
                }
            }
        }
        void Emit()
        {
            string line = Encoding.UTF8.GetString(pending.ToArray());
            // Apple messages may contain indented NSError/plist continuation lines.
            relevantRecord = Relevant.IsMatch(line) || (relevantRecord && (char.IsWhiteSpace(line[0]) || line == "}" || line == "}]"));
            if (!relevantRecord) return;
            int length = Encoding.UTF8.GetByteCount(line) + 2;
            if (Truncated || length > limit - size) { Truncated = true; return; }
            size += length; Lines++; write(line);
        }
    }

    public sealed class DeviceLogCapture
    {
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        readonly TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task worker;
        public string Error { get; private set; }
        public int Lines { get; private set; }
        public bool Truncated { get; private set; }
        public static async Task<DeviceLogCapture> StartAsync(string udid, ConnectionMode mode, string path)
        {
            var capture = new DeviceLogCapture();
            capture.worker = Task.Run(() => capture.Read(udid, mode, path));
            try { await capture.ready.Task.ConfigureAwait(false); return capture; }
            catch { await capture.worker.ConfigureAwait(false); capture.stop.Dispose(); throw; }
        }
        void Read(string udid, ConnectionMode mode, string path)
        {
            try
            {
                using (var file = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)))
                using (var session = DeviceSession.Open(udid, mode))
                using (var service = session.Service("com.apple.syslog_relay"))
                {
                    file.AutoFlush = true;
                    file.WriteLine("Air Card — filtered iPhone sync log; started " + DateTimeOffset.Now.ToString("o"));
                    service.Timeout(500);
                    var filter = new SyncLogFilter(file.WriteLine);
                    ready.TrySetResult(true);
                    byte[] buffer = new byte[8192];
                    try
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            int n = Native.AMDServiceConnectionReceive(service.Handle, buffer, (UIntPtr)(uint)buffer.Length);
                            if (n == 0) throw new IOException("手机已关闭系统日志连接。");
                            if (n < 0)
                            {
                                int error = Native.WSAGetLastError();
                                if (error != 0 && error != 10060 && error != 10035 && error != 10004) throw new IOException("系统日志连接错误 " + error);
                                stop.Token.WaitHandle.WaitOne(50); continue;
                            }
                            filter.Feed(buffer, n);
                        }
                    }
                    catch (Exception ex) { Error = ex.Message; file.WriteLine("Capture error: " + ex.Message); }
                    finally
                    {
                        Lines = filter.Lines; Truncated = filter.Truncated;
                        file.WriteLine("Capture ended " + DateTimeOffset.Now.ToString("o") + "; matching lines=" + Lines + "; size limit reached=" + Truncated);
                    }
                }
            }
            catch (Exception ex) { Error = ex.Message; ready.TrySetException(ex); }
        }
        public async Task StopAsync()
        {
            stop.Cancel();
            await worker.ConfigureAwait(false);
            stop.Dispose();
        }
    }
}
