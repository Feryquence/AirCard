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
        CancellationTokenSource operationCancellation;
        CancellationTokenSource libraryCancellation, libraryPreviewCancellation;
        IList<CommunityCard> libraryCards = new List<CommunityCard>();
        CommunityCard librarySelected;
        byte[] librarySelectedBytes;
        bool libraryLoaded;
        SavedCard currentCard;
        PreparedSkin skin;
        CardArtworkFormat cardFormat;
        public MainWindow()
        {
            InitializeComponent(); initialized = true; UpdateLibraryPreviewCorners();
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
            SubmitCardButton.IsEnabled = true;
            LibraryUseButton.IsEnabled = idle && librarySelected != null && librarySelectedBytes != null;
            ExportButton.IsEnabled = idle && device && hash;
            CaptureExportLog.IsEnabled = idle;
            CancelOperationButton.IsEnabled = busy && operationCancellation != null && !operationCancellation.IsCancellationRequested;
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
            return new Controls.NoticeWindow("安装完整 iTunes", message + "\n\n请安装或修复 Apple 官方完整 64 位桌面版 iTunes，以提供设备驱动和卡面读写所需的同步组件。安装完成后重新启动 Air Card。\n\n点击下载将打开 Apple 官方下载地址。",
                "下载完整 iTunes", true);
        }
        void PromptDriver(AppleDriverException error)
        {
            DriverInfo.Text = "Apple 设备驱动或同步组件未安装或不可用。";
            var dialog = (Controls.NoticeWindow)CreateDriverNotice(error.Message); dialog.Owner = this;
            if (dialog.ShowDialog() != true) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppleSyncRuntime.ITunesUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Log(ex.ToString()); Notice("无法打开浏览器", "请手动打开：" + AppleSyncRuntime.ITunesUrl); }
        }
        async Task<bool> Run(string action, Func<Task> operation, Func<string> completedMessage = null, bool notify = false, bool cancellable = false)
        {
            if (busy || scanning != null) return false;
            operationCancellation = cancellable ? new CancellationTokenSource() : null;
            busy = true; UpdateState(); StatusText.Text = action; Log(action);
            try { await operation(); if (operationCancellation != null && operationCancellation.IsCancellationRequested) throw new OperationCanceledException("操作已取消，已完成的修改或导出文件会保留。"); StatusText.Text = completedMessage == null ? action + "：完成" : completedMessage(); if (notify) Notice("操作完成", StatusText.Text); return true; }
            catch (AppleDriverException ex) { StatusText.Text = action + "：Apple 环境不完整"; Log(ex.ToString()); PromptDriver(ex); return false; }
            catch (CardAppliedException ex) { StatusText.Text = ex.Message; Log(ex.ToString()); Notice("卡面已写入，后续处理未完成", ex.Message); return false; }
            catch (OperationCanceledException ex) { StatusText.Text = ex.Message; Log(ex.Message); return false; }
            catch (Exception ex) { StatusText.Text = action + "：失败，详见日志"; Log(ex.ToString()); if (action == "应用卡面" || action == "从卡面库载入卡面") Notice("操作未完成", ex.Message + "\n详细原因见操作日志。"); return false; }
            finally { if (operationCancellation != null) operationCancellation.Dispose(); operationCancellation = null; busy = false; UpdateState(); }
        }
        void CancelOperation_Click(object sender, RoutedEventArgs e)
        {
            if (operationCancellation == null || operationCancellation.IsCancellationRequested) return;
            operationCancellation.Cancel();
            StatusText.Text = "正在取消，等待当前资源归位和同步清理…";
            Log("已请求取消，不再处理后续资源；正在进行的同步和原文件归位完成后停止。");
            UpdateState();
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
                DeviceSelect.ItemsSource = null;
                var list = await Task.Run(() => { Devices.CheckSupport(); Log(AppleSyncRuntime.PrepareRequired()); return Devices.List(); });
                DriverInfo.Text = "Apple 设备驱动和同步组件已加载。";
                DeviceSelect.ItemsSource = list; DeviceSelect.SelectedItem = list.FirstOrDefault(d => d.Udid == old) ?? list.FirstOrDefault();
                Log(list.Count == 0 ? "未发现设备。请连接并解锁 iPhone，信任此电脑。" : "发现 " + list.Count + " 台设备。");
                foreach (var device in list) Log("设备环境: " + device.Product + " · iOS " + device.Version + " · " + device.Transport);
                if (list.Count > 0) Log(Native.DriverVersions());
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
                var engine = new WalletEngine(Log, operationCancellation.Token);
                try
                {
                    cardFormat = await Task.Run(() => engine.DetectCardFormat(udid, mode, hash));
                    operationCancellation.Token.ThrowIfCancellationRequested();
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
                }
                catch (OperationCanceledException) { await Task.Run(() => engine.FinishCancellation(udid, mode)); throw; }
            }, () => result.Summary, notify: true, cancellable: true);
        }
        async void Export_Click(object sender, RoutedEventArgs e)
        {
            if (Selected == null || currentCard == null) return; string udid = Selected.Udid, hash = currentCard.Hash; var mode = Mode;
            bool captureLog = CaptureExportLog.IsChecked == true;
            // Diagnostic capture must cover discovery too, since it uses the same
            // device sync channel as exporting individual files.
            string folder = captureLog ? Controls.FolderPicker.Select(this) : null;
            if (captureLog && folder == null) return;
            WalletBatchExportResult result = null;
            await Run("读取卡片资源清单", async () => {
                var engine = new WalletEngine(Log, operationCancellation.Token);
                DeviceLogCapture capture = null;
                string trace = captureLog ? Path.Combine(folder, "aircard-device-sync-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log") : null;
                try
                {
                    if (captureLog)
                    {
                        Log("开始采集手机同步日志: " + trace);
                        capture = await DeviceLogCapture.StartAsync(udid, mode, trace);
                    }
                    var catalog = await Task.Run(() => engine.DiscoverResources(udid, mode, hash));
                    operationCancellation.Token.ThrowIfCancellationRequested();
                    var selection = new Controls.ExportSelection(catalog); selection.Window.Owner = this;
                    if (selection.Window.ShowDialog() != true) throw new OperationCanceledException("已取消导出。");
                    bool includeCache = selection.IncludeCache; string[] selectedAssets = selection.OriginalAssets;
                    string[] remoteAssets = selection.RemoteAssets;
                    if (folder == null) folder = Controls.FolderPicker.Select(this);
                    if (folder == null) throw new OperationCanceledException("已取消导出。");
                    StatusText.Text = "批量导出当前卡面"; Log(StatusText.Text);
                    result = await Task.Run(() => engine.ExportFolder(udid, mode, hash, folder, includeCache, selectedAssets, catalog, remoteAssets));
                }
                catch (OperationCanceledException) { operationCancellation.Cancel(); await Task.Run(() => engine.FinishCancellation(udid, mode)); throw; }
                finally
                {
                    if (capture != null)
                    {
                        // Await the reader before allowing a new operation or closing its native handles.
                        await Task.Delay(750); // Allow the device to flush the final failure messages.
                        await capture.StopAsync();
                        Log("手机同步日志已保存: " + trace + "（" + capture.Lines + " 行）");
                        if (capture.Truncated) Log("诊断日志达到 2 MiB 上限，后续内容未保存。");
                        if (capture.Error != null) Log("系统日志采集异常: " + capture.Error);
                        else if (capture.Lines == 0) Log("手机未输出匹配的同步日志；不能据此判断同步正常。");
                    }
                }
            }, () => result.Summary, notify: true, cancellable: true);
        }
        void SubmitCard_Click(object sender, RoutedEventArgs e)
        {
            OpenCommunityPage("https://github.com/Feryquence/AirCard/issues/new?template=card-submission.yml");
        }
        void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source == Tabs && IsLoaded && Tabs.SelectedItem == CardLibraryTab && !libraryLoaded) _ = RefreshLibrary();
        }
        async void LibraryRefresh_Click(object sender, RoutedEventArgs e) { await RefreshLibrary(); }
        async Task RefreshLibrary()
        {
            libraryLoaded = true;
            if (libraryCancellation != null) libraryCancellation.Cancel();
            var cancel = new CancellationTokenSource(); libraryCancellation = cancel;
            LibraryRefreshButton.IsEnabled = false; LibraryStatus.Text = "正在读取卡面库…";
            try
            {
                var cards = await Task.Run(() => CommunityCards.Fetch(cancel.Token));
                if (cancel.IsCancellationRequested) return;
                libraryCards = cards;
                LibrarySearch_Changed(null, null);
                LibraryStatus.Text = cards.Count == 0 ? "卡面库暂无已收录卡面。" : "已加载 " + cards.Count + " 张卡面。";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!cancel.IsCancellationRequested) { LibraryStatus.Text = "读取失败，请检查网络后点击“刷新卡面库”。"; Log("卡面库读取失败: " + ex.Message); } }
            finally { if (libraryCancellation == cancel) { LibraryRefreshButton.IsEnabled = true; libraryCancellation = null; } cancel.Dispose(); }
        }
        void LibrarySearch_Changed(object sender, TextChangedEventArgs e)
        {
            if (LibraryList == null) return;
            string query = LibrarySearch == null ? "" : LibrarySearch.Text.Trim();
            LibraryList.ItemsSource = libraryCards.Where(card => query.Length == 0 ||
                card.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                card.Uploader.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                card.Description.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }
        void LibraryRoundedPreview_Changed(object sender, RoutedEventArgs e) { UpdateLibraryPreviewCorners(); }
        void UpdateLibraryPreviewCorners()
        {
            if (LibraryPreview == null || LibraryRoundedPreviewCheck == null) return;
            // Preview the requested 3.18 mm radius relative to an 85.6 mm wide card.
            double radius = LibraryRoundedPreviewCheck.IsChecked == true ? LibraryPreview.Width * 3.18 / 85.6 : 0;
            LibraryPreview.RadiusX = LibraryPreview.RadiusY = radius;
        }
        async void LibraryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (libraryPreviewCancellation != null) libraryPreviewCancellation.Cancel();
            var card = LibraryList.SelectedItem as CommunityCard;
            librarySelected = card; librarySelectedBytes = null; LibraryPreviewBrush.ImageSource = null; LibraryDownloadButton.IsEnabled = false; UpdateState();
            LibraryName.Text = card == null ? "尚未选择卡面" : card.Name;
            LibraryMeta.Text = card == null ? "" : card.Detail;
            LibraryDescription.Text = card == null ? "" : card.Description;
            LibraryPreviewHint.Text = card == null ? "选择左侧卡面查看预览" : "正在下载预览…";
            LibraryPreviewHint.Visibility = Visibility.Visible;
            if (card == null) return;
            var cancel = new CancellationTokenSource(); libraryPreviewCancellation = cancel;
            try
            {
                var bytes = await Task.Run(() => CommunityCards.Download(card, cancel.Token));
                if (cancel.IsCancellationRequested || librarySelected != card) return;
                librarySelectedBytes = bytes; LibraryDownloadButton.IsEnabled = true; UpdateState();
                LibraryStatus.Text = "已校验文件，可立即使用或下载原文件。";
                try
                {
                    var preview = await Task.Run(() => card.Type == "pdf" ? PdfArtwork.Render(bytes) : bytes);
                    if (cancel.IsCancellationRequested || librarySelected != card) return;
                    var image = Preview(preview);
                    LibraryPreview.Width = image.PixelWidth; LibraryPreview.Height = image.PixelHeight;
                    LibraryPreviewBrush.ImageSource = image; UpdateLibraryPreviewCorners();
                    LibraryPreviewHint.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex) { if (!cancel.IsCancellationRequested && librarySelected == card) { LibraryPreviewHint.Text = "无法预览，原文件仍可下载。"; Log("卡面库预览失败: " + ex.Message); } }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!cancel.IsCancellationRequested && librarySelected == card) { LibraryPreviewHint.Text = "无法读取此卡面"; LibraryStatus.Text = "文件下载失败，请重试或刷新卡面库。"; Log("卡面库文件下载失败: " + ex.Message); } }
            finally { if (libraryPreviewCancellation == cancel) libraryPreviewCancellation = null; cancel.Dispose(); }
        }
        async void LibraryUse_Click(object sender, RoutedEventArgs e)
        {
            var card = librarySelected; var bytes = librarySelectedBytes;
            if (card == null || bytes == null) return;
            await Run("从卡面库载入卡面", async () => {
                var token = operationCancellation.Token;
                string path = await Task.Run(() => CommunityCards.Cache(card, bytes));
                token.ThrowIfCancellationRequested();
                Log("卡面库文件已缓存: " + path);
                var prepared = await Task.Run(() => PreparedSkin.LoadPreview(path));
                token.ThrowIfCancellationRequested();
                skin = prepared;
                SkinPreview.Source = Preview(prepared.Png); EmptyPreview.Visibility = Visibility.Collapsed;
                SkinInfo.Text = card.Name + " · 已从卡面库载入";
                Tabs.SelectedIndex = 0;
            }, () => "已载入卡面。扫描目标卡片后，点击“应用到所选卡片”。", notify: true, cancellable: true);
        }
        void LibraryDownload_Click(object sender, RoutedEventArgs e)
        {
            if (librarySelected == null || librarySelectedBytes == null) return;
            var card = librarySelected; var bytes = librarySelectedBytes;
            string safeName = new string(card.Name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim().TrimEnd('.');
            if (safeName.Length == 0) safeName = "AirCard";
            var dialog = new SaveFileDialog { Title = "保存卡面原文件", Filter = card.Type.ToUpperInvariant() + " 文件|*." + card.Type,
                FileName = safeName + "." + card.Type, DefaultExt = "." + card.Type, AddExtension = true };
            if (dialog.ShowDialog(this) != true) return;
            try { Storage.AtomicWrite(dialog.FileName, bytes); LibraryStatus.Text = "已保存到 " + dialog.FileName; Log("已下载卡面库文件: " + dialog.FileName); }
            catch (Exception ex) { LibraryStatus.Text = "保存失败，详见操作日志。"; Log("保存卡面库文件失败: " + ex); }
        }
        void OpenCommunityPage(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log(ex.Message); Notice("无法打开网页", "请手动打开：" + url); }
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
            if (!e.Cancel) { if (libraryCancellation != null) libraryCancellation.Cancel(); if (libraryPreviewCancellation != null) libraryPreviewCancellation.Cancel(); }
        }
    }
}
