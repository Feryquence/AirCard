using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AirCard.Core
{
    public static class AppleSyncRuntime
    {
        internal const string ITunesUrl = "https://www.apple.com/itunes/download/win64";
        static readonly object Gate = new object();
        static string prepared;
        internal static string PrepareRequired()
        {
            lock (Gate)
            {
                string message = Prepare();
                if (prepared == null) throw new AppleDriverException(message);
                return message;
            }
        }
        // CoreFP is installed separately from Mobile Device Support. Do not copy
        // arbitrary DLLs into the app or change the user's global PATH/registry.
        internal static string Prepare()
        {
            lock (Gate)
            {
                if (prepared != null) return prepared;
                Native.EnsureLoaded();
                var candidates = new List<string>();
                var problems = new List<string>();
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                        {
                            using (var core = machine.OpenSubKey(@"SOFTWARE\Apple Inc.\CoreFP"))
                                candidates.Add(core == null ? null : core.GetValue("LibraryPath") as string);
                            using (var itunes = machine.OpenSubKey(@"SOFTWARE\Apple Computer, Inc.\iTunes"))
                            {
                                string root = itunes == null ? null : itunes.GetValue("InstallDir") as string;
                                if (!string.IsNullOrWhiteSpace(root)) candidates.Add(Path.Combine(root, "CoreFP.dll"));
                            }
                            // Desktop installers can register an executable path even
                            // when the CoreFP-specific registration is missing.
                            using (var app = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\iTunes.exe"))
                                candidates.Add(FromITunesExecutable(app == null ? null : app.GetValue(null) as string));
                        }
                    }
                    catch (Exception ex) { problems.Add(view + ": " + ex.Message); }
                }
                candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "iTunes", "CoreFP.dll"));
                foreach (string path in CandidatePaths(candidates))
                {
                    if (!File.Exists(path)) { problems.Add(path + "：文件不存在"); continue; }
                    try
                    {
                        using (var stream = File.OpenRead(path))
                            if (!IsX64Library(stream)) { problems.Add(path + "：不是 64 位 DLL"); continue; }
                        Native.LoadSyncLibrary(path);
                        return prepared = "Apple 同步组件：CoreFP.dll " + FileVersionInfo.GetVersionInfo(path).FileVersion + "；路径：" + path;
                    }
                    catch (Exception ex) { problems.Add(path + "：" + ex.Message); }
                }
                return "Apple 同步组件：未能加载 64 位 CoreFP.dll。" + string.Join("；", problems)
                    + "。请安装或修复 Apple 官网的完整 64 位桌面版 iTunes，然后重新启动 Air Card。";
            }
        }
        public static string FromITunesExecutable(string executable)
        {
            if (string.IsNullOrWhiteSpace(executable)) return null;
            try
            {
                string path = Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"'));
                if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/')) return null;
                path = Path.GetFullPath(path);
                if (!string.Equals(Path.GetFileName(path), "iTunes.exe", StringComparison.OrdinalIgnoreCase)) return null;
                return Path.Combine(Path.GetDirectoryName(path), "CoreFP.dll");
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }
        public static string[] CandidatePaths(IEnumerable<string> candidates)
        {
            var result = new List<string>();
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    string path = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
                    if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/')) continue;
                    path = Path.GetFullPath(path);
                    if (!string.Equals(Path.GetFileName(path), "CoreFP.dll", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (PathTooLongException) { }
            }
            return result.ToArray();
        }
        public static bool IsX64Library(Stream stream)
        {
            using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
            {
                if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) return false;
                stream.Position = 0x3c; int offset = reader.ReadInt32();
                if (offset < 64 || offset > stream.Length - 26) return false;
                stream.Position = offset;
                if (reader.ReadUInt32() != 0x4550 || reader.ReadUInt16() != 0x8664) return false;
                stream.Position = offset + 22;
                if ((reader.ReadUInt16() & 0x2000) == 0) return false;
                return reader.ReadUInt16() == 0x20b;
            }
        }
        public static string GrappaState(uint session)
        {
            return session == 0 ? "AirTraffic：本机 Grappa 初始化未成功（会话为空）。" : "AirTraffic：本机 Grappa 会话已创建，等待手机确认。";
        }
    }
}
