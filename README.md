# Air Card

用于 Windows 的 iPhone 钱包卡面工具，基于 C#、WPF 和 .NET Framework 4.7.2。

![Air Card 界面](docs/images/air-card.png)

## 功能

- 扫描钱包中打开的卡片，识别到第一张后自动停止。
- 批量导出可读取的原始卡面和 Wallet 显示缓存。
- 导入 PNG、JPG、WebP、未加密的单页 PDF，预览处理后的卡面。
- 点击应用后识别卡面格式；不匹配时询问转换或取消。
- 缺少 Apple 驱动时，可下载完整 iTunes，或仅安装设备支持与驱动。
- 使用 dnSpy 派生的深色 WPF 主题。

这是实验性设备读写工具。读取使用 Apple 图书同步通道临时移动资源并归位，操作期间请保持手机连接、解锁。不同 iOS 版本和卡片资源的兼容性存在差异；不能保证所有卡面都能读取或替换。具体机制和限制见 [技术说明](docs/TECHNICAL.md)。

## 环境要求

- Windows 10 或更新版本，64 位。
- .NET Framework 4.7.2 或更新版本。
- 64 位 Apple Mobile Device Support。首次通过 USB 连接时，需要在 iPhone 上信任此电脑。

驱动提示中的“仅安装驱动”仍会下载 iTunes 安装包，从中提取设备支持组件，再从微软更新目录下载 USB 和网络驱动；不安装 iTunes 本体。安装需要联网和管理员权限，安装结束后按提示重启程序或电脑。

## 使用

1. 启动 `bin/Release/AirCard.exe`，连接并解锁 iPhone，点击“刷新设备”。
2. 点击“扫描卡片”，然后在 iPhone 的“钱包”App 中打开目标卡片。
3. **导出**：点击“导出全部卡面文件”，选择目标文件夹。结果保存在 `AirCard_output_<卡片Hash>` 子目录，保留原始文件名；缓存另存为 `Wallet 卡面缓存.png` 或 `.jpg`。未读取到的资源正常跳过。
4. **替换**：选择本地图片或 PDF。选择文件只加载预览；点击“应用到所选卡片”后才识别设备上的原始格式，不匹配时选择转换或取消。
5. 如果同一组主卡面同时包含 PNG 和 PDF，先选择要覆盖的格式。程序无法仅凭文件存在判断钱包实际使用哪一种，不会自动按导入格式写入。

PNG 输出居中裁切为 **1536 × 969**；图片转 PDF 后仍是位图内容。PDF 转 PNG 会栅格化；导入 PDF 并以 PDF 应用时保留原文件字节。PDF 仅支持未加密单页文件，大小不超过 32 MB。WebP 需要系统可用的解码器。

应用结束后退出并重新打开钱包查看结果。扫描标识只在重新扫描或刷新设备时清空，不保存扫描历史。WiFi 模式需要先通过 USB 配对并启用 WiFi 同步。

## 构建

安装 Visual Studio 2022 或 Build Tools，并包含：

- .NET 桌面开发工具。
- .NET Framework 4.7.2 Targeting Pack。
- Windows 10/11 SDK（PDF 渲染所需的 WinRT 引用）。

在 Windows PowerShell 中运行：

```powershell
.\Build.ps1
```

也可打开 `AirCard.sln`，选择 `Release | x64`。构建结果位于 `bin/Release`；源码仓库不提交程序二进制、Apple 驱动或本地设备数据。

若使用独立的 .NET 参考程序集目录，可指定：

```powershell
.\Build.ps1 -ReferenceAssemblies 'D:\ReferenceAssemblies\v4.7.2'
```

## 测试

```powershell
# 离线测试：不会连接手机、弹出 UAC 或安装驱动
.\Tests\Run.ps1 -Artifacts "$env:TEMP\AirCard-tests"

# 额外检查已安装的 Apple 原生接口；仍不会操作手机
.\Tests\Run.ps1 -Artifacts "$env:TEMP\AirCard-tests" -NativeBindings
```

最近一次本机检查通过 **219 项应用断言和 14 项驱动脚本断言**。驱动安装测试使用模拟进程和签名结果，完整安装过程尚未在无驱动电脑上验证。原始格式识别与转换流程的回归测试为离线检查，不代表所有卡片的真机兼容性。

## 源码结构

| 路径 | 用途 |
| --- | --- |
| `MainWindow.xaml`、`MainWindow.xaml.cs` | 主界面与操作流程 |
| `Core/` | 设备通信、卡片扫描、卡面处理和读写事务 |
| `Controls/`、`Themes/` | WPF 控件与主题 |
| `Tests/` | 离线回归测试和可选的原生接口检查 |
| `ThirdParty/` | 上游源码、固定版本和授权说明 |
| `docs/` | 机制、限制和界面截图 |

## 来源与授权

本组合版本按 **GPL-3.0-or-later** 提供，见 [LICENSE.txt](LICENSE.txt)。原始授权与来源说明保留在 [ThirdParty](ThirdParty)。

- [Lumid-Off/AirCard-Windows](https://github.com/Lumid-Off/AirCard-Windows)：设备引擎移植基线，MIT。
- [0xjohnnydev/airlift](https://github.com/0xjohnnydev/airlift)：间接读取参考，MIT。
- [dnSpy/dnSpy](https://github.com/dnSpy/dnSpy)：主题和控件，经 ConnectDeveloperTool 适配，GPL-3.0-or-later。
- [NelloKudo/Apple-Mobile-Drivers-Installer](https://github.com/NelloKudo/Apple-Mobile-Drivers-Installer)：内置驱动安装脚本的上游来源，GPL-3.0。

Apple 和 iTunes 是各自权利人的商标。本项目与 Apple 无隶属关系。
