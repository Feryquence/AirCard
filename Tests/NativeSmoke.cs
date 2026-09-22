using AirCard.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

// Optional read-only host check. Does not discover, connect to, or write to devices.
class NativeSmoke
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    static int Main()
    {
        try
        {
            Devices.CheckSupport();
            var assembly = typeof(Devices).Assembly;
            var native = assembly.GetType("AirCard.Core.Native"); int count = 0;
            foreach (var method in native.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var import = method.GetCustomAttribute<DllImportAttribute>();
                if (import == null || !(import.Value == "CoreFoundation.dll" || import.Value == "MobileDevice.dll" || import.Value == "AirTrafficHost.dll")) continue;
                if (GetProcAddress(GetModuleHandle(import.Value), import.EntryPoint) == IntPtr.Zero) throw new Exception("Missing entry point: " + method.Name);
                count++;
            }
            var cf = assembly.GetType("AirCard.Core.Cf"); var flags = BindingFlags.Static | BindingFlags.NonPublic;
            string syncRuntime = (string)typeof(AppleSyncRuntime).GetMethod("Prepare", flags).Invoke(null, null);
            if (syncRuntime.Contains("；路径：") && GetModuleHandle("CoreFP.dll") == IntPtr.Zero) throw new Exception("Sync component was not loaded.");
            Console.WriteLine(syncRuntime);
            var value = cf.GetMethod("From", flags).Invoke(null, new object[] { Plist.Dict("name", "白色卡面", "data", new byte[] {0,1,255}, "number", 2) });
            byte[] binary;
            using ((IDisposable)value)
            {
                var handle = cf.GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(value);
                binary = (byte[])native.GetMethod("Serialize", flags).Invoke(null, new object[] {handle, 200});
                if (Encoding.ASCII.GetString(binary, 0, 8) != "bplist00") throw new Exception("Binary plist conversion failed.");
            }
            var decoded = cf.GetMethod("FromBytes", flags).Invoke(null, new object[] {binary});
            using ((IDisposable)decoded)
            {
                var handle = cf.GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(decoded);
                var xml = (byte[])native.GetMethod("Serialize", flags).Invoke(null, new object[] {handle, 100});
                var dict = (Dictionary<string, object>)Plist.Read(xml);
                if (!dict["name"].Equals("白色卡面") || !((byte[])dict["data"]).SequenceEqual(new byte[] {0,1,255})) throw new Exception("CF roundtrip data mismatch.");
            }
            var textValue = cf.GetMethod("String", flags).Invoke(null, new object[] {"卡面 / USB"});
            using ((IDisposable)textValue)
            {
                var handle = cf.GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(textValue);
                if (!native.GetMethod("Text", flags).Invoke(null, new[] {handle}).Equals("卡面 / USB")) throw new Exception("CF UTF-8 ABI mismatch.");
            }
            foreach (object scalar in new object[] { -1, 42, "同步拒绝" })
            {
                var diagnostic = cf.GetMethod("From", flags).Invoke(null, new[] { scalar });
                using ((IDisposable)diagnostic)
                {
                    var handle = cf.GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(diagnostic);
                    var xml = (byte[])native.GetMethod("Serialize", flags).Invoke(null, new object[] {handle, 100});
                    if (SyncDiagnostics.Scalar(xml) != scalar.ToString()) throw new Exception("CF diagnostic scalar mismatch.");
                }
            }
            Console.WriteLine("PASS: " + count + " Apple exports found; native binary/XML plist, diagnostic scalars and UTF-8 roundtrips passed. No device operations."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
