using AirCard.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AirCard
{
    public partial class MainWindow : Window
    {
        bool initialized, busy, closeAfterScan;
        CancellationTokenSource scanning;
        SavedCard currentCard;
        PreparedSkin skin;
        CardArtworkFormat cardFormat;
        public MainWindow()
        {
            InitializeComponent(); initialized = true;
            try { CardScanner.DeleteLegacyHistory(); } catch (Exception ex) { Log("旧版卡片历史清理失败（不会读取或使用）: " + ex.Message); }
            UpdateState();
        }
        void Log(string text)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action<string>(Log), text); return; }
            LogBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine);
            if (LogBox.Text.Length > 180000) LogBox.Text = LogBox.Text.Substring(LogBox.Text.Length - 120000);
            LogBox.ScrollToEnd();
        }
        void UpdateState()
        {
            if (!initialized) return;
            bool idle = !busy && scanning == null, device = DeviceSelect.SelectedItem is DeviceInfo;
            bool hash = currentCard != null && device && currentCard.Udid == Selected.Udid && currentCard.Hash == HashBox.Text && CardScanner.ValidHash(currentCard.Hash);
            DeviceSelect.IsEnabled = ModeSelect.IsEnabled = RefreshButton.IsEnabled = idle;
            HashBox.IsEnabled = idle;
            ScanButton.IsEnabled = scanning != null || (idle && device); ScanButton.Content = scanning != null ? "停止扫描" : "扫描卡片";
            ChooseSkinButton.IsEnabled = idle; SaveSkinButton.IsEnabled = idle && skin != null;
            ExportButton.IsEnabled = idle && device && hash;
            ApplySkinButton.IsEnabled = idle && device && hash && skin != null;
            Progress.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        }
        Window CreateNotice(string title, string message, string accept, bool cancel)
        {
            return new Controls.NoticeWindow(title, message, accept, cancel);
        }
        bool Notice(string title, string message, string accept = "确定", bool cancel = false)
        {
            var dialog = CreateNotice(title, message, accept, cancel); dialog.Owner = this; return dialog.ShowDialog() == true;
        }
        Window CreateCardFormatNotice()
        {
            return new Controls.NoticeWindow("选择卡面格式", "主卡面同时存在 PNG 和 PDF，无法仅凭文件存在判断钱包当前使用哪一种。\n请选择要覆盖的格式；如果这是一张 PDF 卡，请选择 PDF。", "PDF", true, "PNG");
        }
        Window CreateDriverNotice(string message)
        {
            return new Controls.NoticeWindow("安装 Apple 驱动", message + "\n\n下载完整 iTunes：打开 Apple 官方下载页面。\n仅安装驱动：下载 iTunes 安装包并提取设备支持组件，再从微软下载 USB 和网络驱动；不安装 iTunes。需要联网和管理员权限。",
                "下载完整 iTunes", true, "仅安装驱动");
        }
        async Task PromptDriver(AppleDriverException error)
        {
            DriverInfo.Text = "Apple 驱动未安装或不可用。";
            var dialog = (Controls.NoticeWindow)CreateDriverNotice(error.Message); dialog.Owner = this;
            if (dialog.ShowDialog() != true) return;
            if (!dialog.SecondarySelected)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DriverInstaller.ITunesUrl) { UseShellExecute = true }); }
                catch (Exception ex) { Log(ex.ToString()); Notice("无法打开浏览器", "请手动打开：" + DriverInstaller.ITunesUrl); }
                return;
            }
            try
            {
                StatusText.Text = "正在下载并安装 Apple 驱动，请稍候…";
                bool reboot = await Task.Run(() => DriverInstaller.Install(Log));
                StatusText.Text = reboot ? "驱动已安装，需要重启电脑。" : "驱动已安装，请重新启动 Air Card。";
                DriverInfo.Text = StatusText.Text; Notice("驱动安装完成", StatusText.Text);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { StatusText.Text = "已取消驱动安装。"; Log(StatusText.Text); }
            catch (Exception ex) { StatusText.Text = "驱动安装未完成。"; Log(ex.ToString()); Notice("驱动安装未完成", ex.Message); }
        }
        async Task<bool> Run(string action, Func<Task> operation, Func<string> completedMessage = null, bool notify = false)
        {
            if (busy || scanning != null) return false;
            busy = true; UpdateState(); StatusText.Text = action; Log(action);
            try { await operation(); StatusText.Text = completedMessage == null ? action + "：完成" : completedMessage(); if (notify) Notice("操作完成", StatusText.Text); return true; }
            catch (AppleDriverException ex) { StatusText.Text = action + "：Apple 驱动不可用"; Log(ex.ToString()); await PromptDriver(ex); return false; }
            catch (CardAppliedException ex) { StatusText.Text = ex.Message; Log(ex.ToString()); Notice("卡面已写入，后续处理未完成", ex.Message); return false; }
            catch (OperationCanceledException ex) { StatusText.Text = ex.Message; Log(ex.Message); return false; }
            catch (Exception ex) { StatusText.Text = action + "：失败，详见日志"; Log(ex.ToString()); if (action == "应用卡面") Notice("应用未完成", ex.Message + "\n详细原因见操作日志。"); return false; }
            finally { busy = false; UpdateState(); }
        }
        DeviceInfo Selected { get { return DeviceSelect.SelectedItem as DeviceInfo; } }
        ConnectionMode Mode { get { return (ConnectionMode)Math.Max(0, ModeSelect.SelectedIndex); } }
        async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Offline UI smoke tests explicitly skip any driver/device access.
            if (Environment.GetCommandLineArgs().Contains("--offline")) { DriverInfo.Text = "离线界面预览"; return; }
            await RefreshDevices();
        }
        async Task RefreshDevices()
        {
            string old = Selected == null ? null : Selected.Udid;
            await Run("刷新设备", async () => {
                ClearCurrentCard();
                var list = await Task.Run(() => { Devices.CheckSupport(); return Devices.List(); });
                DriverInfo.Text = "Apple 驱动已加载。";
                DeviceSelect.ItemsSource = list; DeviceSelect.SelectedItem = list.FirstOrDefault(d => d.Udid == old) ?? list.FirstOrDefault();
                Log(list.Count == 0 ? "未发现设备。请连接并解锁 iPhone，信任此电脑。" : "发现 " + list.Count + " 台设备。");
            });
        }
        async void Refresh_Click(object sender, RoutedEventArgs e) { await RefreshDevices(); }
        void Device_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!initialized) return;
            UpdateState();
        }
        void ResetCardFormat() { cardFormat = CardArtworkFormat.Unknown; CardFormatInfo.Text = "原始卡面格式：应用时识别"; }
        void ClearCurrentCard() { currentCard = null; ResetCardFormat(); HashBox.Text = ""; }
        void SetCurrentCard(SavedCard card) { currentCard = card; ResetCardFormat(); HashBox.Text = card.Hash; UpdateState(); }
        void Hash_Changed(object sender, TextChangedEventArgs e) { UpdateState(); }
        async void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (scanning != null) { scanning.Cancel(); ScanButton.IsEnabled = false; StatusText.Text = "正在停止扫描…"; return; }
            if (busy || Selected == null) return;
            ClearCurrentCard(); string udid = Selected.Udid; var mode = Mode; scanning = new CancellationTokenSource(); var cancel = scanning;
            bool identified = false;
            UpdateState(); StatusText.Text = "扫描中 · 请在手机钱包里点开卡片";
            try
            {
                var card = await Task.Run(() => CardScanner.Scan(udid, mode, cancel.Token, Log));
                if (card != null)
                {
                    identified = true;
                    SetCurrentCard(card);
                    Log("已识别卡片: " + card.Name + " · " + card.Hash);
                }
            }
            catch (Exception ex) { Log(ex.ToString()); }
            finally { scanning = null; cancel.Dispose(); StatusText.Text = identified ? "已识别卡片，扫描已自动停止" : "扫描已停止"; UpdateState(); if (closeAfterScan) Close(); }
        }
        public static BitmapImage Preview(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes)) { var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image; }
        }
        async void ChooseSkin_Click(object sender, RoutedEventArgs e)
        {
            if (busy || scanning != null) return;
            var dialog = new OpenFileDialog { Filter = "卡面文件|*.png;*.jpg;*.jpeg;*.webp;*.pdf|图片|*.png;*.jpg;*.jpeg;*.webp|单页 PDF|*.pdf", Title = "选择替换卡面" }; if (dialog.ShowDialog(this) != true) return;
            await Run("处理卡面", async () => {
                var prepared = await Task.Run(() => PreparedSkin.LoadPreview(dialog.FileName)); skin = prepared;
                SkinPreview.Source = Preview(skin.Png); EmptyPreview.Visibility = Visibility.Collapsed;
                SkinInfo.Text = Path.GetFileName(dialog.FileName) + " · 已加载预览";
            }, () => "已导入卡面。点击“应用到所选卡片”后识别格式。", notify: true);
        }
        async void SaveSkin_Click(object sender, RoutedEventArgs e)
        {
            if (skin == null) return; var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = "aircard-prepared.png", AddExtension = true };
            if (dialog.ShowDialog(this) == true) await Run("保存处理后的卡面", () => Task.Run(() => Storage.AtomicWrite(dialog.FileName, skin.Png)), notify: true);
        }
        async void ApplySkin_Click(object sender, RoutedEventArgs e)
        {
            if (Selected == null || currentCard == null || skin == null || Selected.Udid != currentCard.Udid || HashBox.Text != currentCard.Hash) return; string udid = Selected.Udid, hash = currentCard.Hash; var mode = Mode; var selectedSkin = skin;
            CardApplyResult result = null;
            await Run("应用卡面", async () => {
                StatusText.Text = "正在识别卡面格式…";
                var engine = new WalletEngine(Log);
                cardFormat = await Task.Run(() => engine.DetectCardFormat(udid, mode, hash));
                CardFormatInfo.Text = "已读取的原始格式：" + CardFormatMatching.Label(cardFormat);
                if (cardFormat == CardArtworkFormat.Unknown)
                {
                    Notice("无法识别卡面格式", "未读到卡片的原始 PNG/PDF 资源，不能根据缓存图片判断格式。\n本次未应用，已保留导入的卡面。");
                    throw new OperationCanceledException("未识别原始格式，已取消应用。");
                }
                var selectedFormat = cardFormat;
                if (selectedFormat == CardArtworkFormat.Both)
                {
                    Log("主卡面同时存在 PNG 和 PDF，需要选择要覆盖的格式。");
                    var choice = (Controls.NoticeWindow)CreateCardFormatNotice();
                    choice.Owner = this;
                    if (choice.ShowDialog() != true) throw new OperationCanceledException("已取消应用，保留导入的卡面。");
                    selectedFormat = choice.SecondarySelected ? CardArtworkFormat.Png : CardArtworkFormat.Pdf;
                    Log("本次选择覆盖格式: " + CardFormatMatching.Label(selectedFormat));
                }
                if (!CardFormatMatching.Approve(selectedFormat, selectedSkin.InputFormat, message => Notice("卡面格式不匹配", message, "转换", true)))
                    throw new OperationCanceledException("已取消应用，保留导入的卡面。");
                var target = CardFormatMatching.Target(selectedFormat, selectedSkin.InputFormat);
                Log("本次确认写入格式: " + CardFormatMatching.Label(target));
                var prepared = await Task.Run(() => selectedSkin.ForTarget(target));
                StatusText.Text = "正在应用卡面…";
                result = await Task.Run(() => engine.FlashCard(udid, mode, hash, prepared));
            }, () => result.Summary, notify: true);
        }
        async void Export_Click(object sender, RoutedEventArgs e)
        {
            if (Selected == null || currentCard == null) return; string udid = Selected.Udid, hash = currentCard.Hash; var mode = Mode;
            string folder = Controls.FolderPicker.Select(this); if (folder == null) return;
            WalletBatchExportResult result = null;
            await Run("批量导出当前卡面", async () => {
                result = await Task.Run(() => new WalletEngine(Log).ExportFolder(udid, mode, hash, folder, true));
            }, () => result.Summary, notify: true);
        }
        void SaveLog_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog { Filter = "日志|*.log", FileName = "aircard-diagnostics.log" };
            if (dialog.ShowDialog(this) != true) return;
            try { Storage.AtomicWrite(dialog.FileName, System.Text.Encoding.UTF8.GetBytes(LogBox.Text)); } catch (Exception ex) { Log(ex.Message); }
        }
        void ClearLog_Click(object sender, RoutedEventArgs e) { LogBox.Clear(); }
        void Minimize_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        void Maximize_Click(object sender, RoutedEventArgs e) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
        void Close_Click(object sender, RoutedEventArgs e) { Close(); }
        void Window_Closing(object sender, CancelEventArgs e)
        {
            if (busy) { e.Cancel = true; StatusText.Text = "操作进行中，请等待完成后关闭。"; return; }
            if (scanning != null) { e.Cancel = true; closeAfterScan = true; scanning.Cancel(); }
        }
    }
}
