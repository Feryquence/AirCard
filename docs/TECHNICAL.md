# 设备读写与限制

## 批量导出

程序尝试读取以下 11 种原始资源：

- `cardBackgroundCombined@3x.png`、`cardBackgroundCombined@2x.png`、`cardBackgroundCombined.pdf`
- `diffuse@3x.png`、`diffuse@2x.png`
- `background@3x.png`、`background@2x.png`、`background.pdf`
- `strip@3x.png`、`strip@2x.png`、`strip.pdf`

原始文件保持原字节和文件名。显示缓存读取 `.cache/FrontFace`，必要时尝试 `.pkcache/FrontFace`，从归档的 `faceImage → imageData → NS.data` 提取 PNG/JPEG。缓存可能包含圆角和合成文字，尺寸不等于原始卡面。

未读取到的资源正常跳过；全部未读到时显示无可导出文件。暂存文件查询遇到 AFC `0x04` 时，仅在成功列举 Media 根目录、确认该暂存文件没有出现后按未收到处理。文件存在但无法读取、连接中断、无法确认归位或本地保存失败，仍会停止。未读取到不能证明受保护目录里的原文件不存在。

重复导出覆盖本次读取到的同名本地文件，保留未读取到的旧文件和其他文件。

## 格式识别与写入

识别在点击应用之后执行。优先检查 `cardBackgroundCombined` 这一组主卡面；没有读到时依次尝试 `background`、`strip`。不会把不同组的格式混合起来，`diffuse` 和显示缓存不能单独用于判断主卡面格式。

同一组资源同时有 PNG 和 PDF 时，需要用户明确选择覆盖格式，再检查导入文件是否需要转换。不能仅凭资源存在判断钱包实际采用哪种格式。无法读取原始资源时取消应用，保留本地预览。

写入集合只包含本次确认的格式：

- PNG：`cardBackgroundCombined@3x.png`、`cardBackgroundCombined@2x.png`。
- PDF：`cardBackgroundCombined.pdf`。

不删除另一格式的已有文件。写入后尝试刷新 `.cache` 和 `.pkcache` 中的 `FrontFace`、`PlaceHolder`、`Preview`。可选缓存刷新不完整会报告警告；同步完成并不保证钱包立即采用新资源。

## 同步与中断

“图书同步”指 Apple 驱动提供的 AirTraffic 文件同步通道。间接读取需要临时将资源移到 Media，通过 AFC 读取，再把本次读取的原始字节通过独立通道放回原位置。归位使用新的资源标识，确认后才清理暂存文件。不能仅凭同步通知或旧暂存文件消失就判定成功。

程序保存并还原三个配置文件：`Books/Books.plist`、`Books/Sync/Books.plist`、`Books/Sync/Upload.plist`。

不快照、覆盖或删除同步服务持有的 SQLite、WAL、SHM 文件，历史记录中的数据库快照也不会回写。服务生成的非空数据库目录保留。

操作记录和读取备份位于 `%LOCALAPPDATA%\AirCard.NetFramework\Recovery`。重试按设备匹配未完成记录；没有收到文件的请求保留延迟检查。设备暂存原文件不可用时保留记录并停止，不自动拿历史备份覆盖当前卡面。

缓存解码发生在原文件归位之后。移动确认不等于对受保护目录进行独立字节回读；此机制仍有兼容性与中断风险。

## 同步失败诊断

日志记录设备型号、iOS 版本、设备会话连接方式及 Apple 驱动版本。AirTraffic 实际通道由 Apple 驱动选择。同步握手依次等待 `SyncAllowed`、`ReadyForSync`、`AssetManifest`，这些步骤完成后才发送资源移动确认。

收到 `SyncFailed` 或提前收到 `SyncFinished` 时，报告等待阶段及设备提供的标量错误字段；不输出整个同步清单。等待 `ReadyForSync` 时被拒绝不代表卡面文件不存在，也不能单凭这一条错误确定是 iOS 或驱动不兼容。缺少文件可以跳过，同步失败仍会中断操作并执行已有的清理检查。排查时请提供版本、连接方式、图书是否已安装并打开，以及失败阶段和错误详情；分享日志前遮盖个人设备和卡片标识。


## 网络与本地数据

- 不上传卡面，不保存扫描历史。操作日志、恢复记录和原始备份可能包含设备或卡片标识，请勿直接提交到公共仓库。
- USB 设备发现连接本机 Apple 服务；WiFi 模式连接已配对设备。
- PDF 使用本机 Windows 渲染器。
- 只有用户选择驱动下载或安装时才启动对应流程。“仅安装驱动”连接 Apple 和微软下载服务，Windows 可能访问证书验证服务。
- 安装脚本内置于程序，不会在运行时下载并执行 GitHub 上的最新脚本。检查 Apple EXE/MSI 签名、安装退出码、重启要求及所需 DLL。失败不自动卸载已安装的组件。
- 安装日志位于 `%LOCALAPPDATA%\AirCard.NetFramework\DriverInstaller\<操作编号>`，下载的临时安装包在结束时尝试清理。

## 固定上游版本

| 来源 | 版本 |
| --- | --- |
| AirCard-Windows | `b4d07e55fb21cf8be5414c35f5b29614a93135ad` |
| airlift | `c684cd41ca0ded2d1ab780c15f6ead05509ce062` |
| dnSpy | `2b6dcfaf602fb8ca6462b8b6237fdfc0c74ad994` |
| 驱动脚本 | 见 `ThirdParty/Apple-Mobile-Drivers-Installer/NOTICE.md` 的原文件 SHA-256 |

主题的原始副本、哈希与许可证保留于 `ThirdParty/dnSpy`。运行和构建不依赖任何开发者的个人磁盘路径。
