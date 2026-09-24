using System.Windows;
using System.Threading;
namespace AirCard
{
    public partial class App : Application
    {
        Mutex instance;
        protected override void OnStartup(StartupEventArgs e)
        {
            bool created;
            instance = new Mutex(true, @"Local\AirCard.NetFramework.DeviceOperations", out created);
            if (!created) { new Controls.NoticeWindow("Air Card", "Air Card 已在运行，请使用已有窗口。") { WindowStartupLocation = WindowStartupLocation.CenterScreen }.ShowDialog(); instance.Dispose(); instance = null; Shutdown(); return; }
            base.OnStartup(e);
        }
        protected override void OnExit(ExitEventArgs e)
        {
            if (instance != null) { instance.ReleaseMutex(); instance.Dispose(); instance = null; }
            base.OnExit(e);
        }
    }
}
