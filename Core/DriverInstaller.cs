using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AirCard.Core
{
    internal static class DriverInstaller
    {
        internal const string ITunesUrl = "https://www.apple.com/itunes/download/win64";
        internal static string ScriptText()
        {
            using (var stream = typeof(DriverInstaller).Assembly.GetManifestResourceStream("AirCard.AppleDrivInstaller.ps1"))
            {
                if (stream == null) throw new IOException("内置驱动安装脚本缺失。");
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }
        internal static ProcessStartInfo StartInfo(string script)
        {
            // A literal -File argument avoids evaluating a path as PowerShell code.
            if (!Path.IsPathRooted(script) || script.IndexOf('"') >= 0) throw new ArgumentException("Invalid installer script path.");
            return new ProcessStartInfo {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"",
                WorkingDirectory = Path.GetDirectoryName(script), UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            };
        }
        internal static bool Install(Action<string> log)
        {
            string folder = Path.Combine(Storage.Root, "DriverInstaller", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string script = Path.Combine(folder, "AppleDrivInstaller.ps1"), logPath = Path.Combine(folder, "install.log");
            File.WriteAllText(script, ScriptText(), new UTF8Encoding(true));
            log("请求管理员权限，仅安装 Apple 设备支持、USB 和网络驱动。安装日志: " + logPath);
            // Keep the exact embedded script locked against modification/deletion
            // until the elevated process has finished reading it.
            using (var locked = new FileStream(script, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var process = Process.Start(StartInfo(script)))
            {
                if (process == null) throw new IOException("无法启动驱动安装程序。");
                int logged = 0;
                do { ReadProgress(logPath, log, ref logged); } while (!process.WaitForExit(500));
                ReadProgress(logPath, log, ref logged);
                if (process.ExitCode != 0 && process.ExitCode != 3010)
                    throw new IOException("驱动安装未完成（退出码 " + process.ExitCode + "）。已安装的组件会保留。\n日志: " + logPath);
                // Exit status alone is insufficient if an installer silently did
                // nothing. Air Card needs all three native frameworks.
                string support = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "Apple", "Mobile Device Support");
                foreach (string name in new[] { "MobileDevice.dll", "CoreFoundation.dll", "AirTrafficHost.dll" })
                    if (!File.Exists(Path.Combine(support, name))) throw new IOException("安装程序已结束，但仍缺少 " + name + "。请重启电脑后检查，或安装完整 iTunes。\n日志: " + logPath);
                return process.ExitCode == 3010;
            }
        }
        static void ReadProgress(string path, Action<string> log, ref int logged)
        {
            try
            {
                if (!File.Exists(path)) return;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string text = reader.ReadToEnd(); int complete = text.LastIndexOf('\n') + 1;
                    if (complete > logged) { log(text.Substring(logged, complete - logged).TrimEnd()); logged = complete; }
                }
            }
            catch (IOException) { } // A busy log file does not change installer status.
            catch (UnauthorizedAccessException) { }
        }
    }
}
