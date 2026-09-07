# 投音通 BluetoothCast

把 Windows 电脑变成一台蓝牙音箱：手机 / 平板 / 手表通过蓝牙连接到电脑，媒体声音从电脑扬声器播放。

基于 WinUI 3（Fluent UI）与 Windows App SDK 1.8，unpackaged 自包含部署，无需安装运行时。

---

## 运行要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 2004（内部版本 19041）或更高，推荐 Windows 11 |
| 蓝牙硬件 | 支持 A2DP Sink 的适配器（Intel AX200/AX201、Realtek RTL8822 等实测可用） |
| 运行时 | 已随程序自带 Windows App SDK 运行时，无需额外安装 |

首次使用必须先在 **Windows 设置 → 蓝牙和其他设备** 中完成手机与电脑的配对，应用只负责建立音频连接。

---

## 使用方式

1. 打开 Windows 蓝牙设置，把手机与电脑配对。
2. 启动「投音通」，在设备列表中点击手机即可开始接收音频。
3. 手机端播放音乐，声音会从电脑的默认播放设备输出。
4. 在「声音输出」中可切换出声位置。

托盘图标常驻后台，右键菜单可快速连接、断开、打开系统设置。

### 命令行参数

| 参数 | 说明 |
| --- | --- |
| `--silent` | 静默启动，不弹出主窗口（开机自启时使用） |

设置项中的「启动时最小化」会隐式按静默方式启动。

---

## 技术实现

核心是 Windows 内置的 **A2DP Sink** 能力，通过公开 WinRT API 调用：

```
Windows.Media.Audio.AudioPlaybackConnection
```

- 引入版本：Windows 10 2004（10.0.19041.0）
- 关键成员：`GetDeviceSelector()`、`TryCreateFromId(id)`、`StartAsync()`、`OpenAsync()`、`Close()`、`StateChanged`
- 编解码、AVDTP 协商、缓冲与音频路由全部由系统完成，应用只管理连接生命周期

```
手机 / 手表（A2DP Source）
        │  蓝牙
        ▼
Windows 蓝牙协议栈（A2DP Sink + SBC 解码）   ← 系统实现，零开发量
        │
        ▼
AudioPlaybackConnection                      ← 本应用调用
        │
        ▼
系统默认播放设备 → 扬声器
```

### 项目结构

```
BtAudioSink/
├── App.xaml(.cs)                  应用入口、托盘菜单编排
├── Services/
│   ├── AudioPlaybackService.cs    A2DP Sink 连接管理（核心）
│   ├── AudioEndpointService.cs    播放设备枚举与默认端点切换
│   ├── TrayHost.cs                Win32 通知区域图标（后台 STA 线程）
│   ├── StartupService.cs          开机自启（HKCU Run）
│   └── Log.cs                     文件日志
├── Models/
│   ├── AppSettings.cs             设置持久化
│   └── BtDeviceItem.cs            设备列表项
├── Native/Win32.cs                P/Invoke 与 IPolicyConfig 声明
└── Views/MainWindow.xaml(.cs)     Fluent UI 主窗口
```

日志位置：`%LOCALAPPDATA%\BtAudioSink\logs\app.log`
设置位置：`%LOCALAPPDATA%\BtAudioSink\settings.json`

---

## 已知限制

这些限制来自 Windows 平台本身，不是应用缺陷：

1. **编解码固定为 SBC**。aptX、AAC、LDAC 无法在应用层指定。
2. **没有 AVRCP**。系统未开放 AVCTP/AVRCP Controller 接口，因此无法在电脑上播放/暂停/切歌，也读不到曲目信息。
3. **不支持 HFP/HSP**。不能用作免提通话设备。
4. **延迟约 150–300 毫秒**，观看视频时可能出现音画不同步。
5. **音频固定送往系统默认播放设备**。切换默认端点依赖未文档化的 `IPolicyConfig` 接口，失败时会降级为提示用户手动切换。
6. 部分廉价 USB 蓝牙适配器不实现 A2DP Sink，表现是能配对但无法建立音频连接。

---

## 构建

```powershell
dotnet build -c Release
```

产物位于 `bin\x64\Release\net8.0-windows10.0.22621.0\win-x64\BluetoothCast.exe`。

发布单文件：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

> 程序未做代码签名，首次运行可能触发 SmartScreen 提示，选择「更多信息 → 仍要运行」即可。

---

## 许可证

本项目以 **MIT License** 开源，完整文本见仓库根目录的 [LICENSE](LICENSE) 文件。

简而言之：你可以自由使用、修改、再发布（含闭源商业用途），只需在分发时保留版权与许可声明；软件按"原样"提供，不附带任何担保。

### 第三方依赖许可

本项目基于以下主要依赖构建，其许可均与本项目（MIT）兼容：

| 依赖 | 用途 | 许可 |
| --- | --- | --- |
| Windows App SDK（WinUI 3） | UI 框架与运行时 | MIT |
| .NET 8 Runtime | 运行环境 | MIT |
| Segoe MDL2 Assets（系统字体） | 应用图标所用的蓝牙字形（E702），运行时由系统提供，不随程序分发 | 微软系统字体授权 |

应用图标中的蓝牙字形取自系统字体，未包含任何第三方字体文件的再分发。
