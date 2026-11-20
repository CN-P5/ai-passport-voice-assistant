# AI Passport 语音 · Windows 原生客户端（.NET 10 / WPF）

将 AI Passport 设备（ESP32-C3）当作电脑的**无线麦克风 + 语音快捷键**。
设备麦克风 → Wi-Fi UDP → 本程序 → 虚拟声卡(CABLE Input) → 输入法；
设备按键 → 本程序 → 系统热键注入（SendInput）。

本目录是 `folo-ai-passport-voice/windows`（Python 版）的 C# 重写，**线协议与设备固件完全兼容**，设备端不需要任何改动。

## 依赖策略：零 NuGet

| 能力 | 实现 |
| --- | --- |
| GUI | WPF（框架内置） |
| 托盘 | WinForms `NotifyIcon`（`UseWindowsForms`，框架内置） |
| 键盘注入 | `user32!SendInput` / `keybd_event` / `OpenDesktopW` |
| 音频输出 | `winmm!waveOut`（16kHz / 单声道 / 16bit） |
| UDP | `System.Net.Sockets` + `NetworkInterface` |
| 配置 | `System.Text.Json` |
| 测试 | 自写断言控制台（无 xunit/NUnit） |

全仓库**没有任何 `PackageReference`**。

## 目录结构

```
AiPassportVoice/
├── AiPassportVoice.sln / .slnx
├── Directory.Build.props          # 统一 Nullable / LangVersion
├── src/
│   ├── AiPassportVoice.Core/      # net10.0，纯逻辑，可跨平台
│   │   ├── Adpcm/AdpcmDecoder.cs  # IMA ADPCM 解码（对齐固件）
│   │   ├── Protocol/VoiceProtocol.cs
│   │   ├── Input/KeySpec.cs       # 键位表 + 组合键解析
│   │   ├── Config/AppConfig.cs
│   │   ├── Transport/UdpTransport.cs   # beacon / PING / 判活
│   │   └── Forwarding/Forwarder.cs     # 状态机
│   ├── AiPassportVoice.Interop/   # net10.0-windows，Win32 互操作
│   │   ├── Input/NativeKeyInjector.cs
│   │   └── Audio/WaveOutAudioSink.cs, AudioDeviceProbe.cs
│   └── AiPassportVoice.App/       # net10.0-windows，WPF
│       ├── App.xaml(.cs)          # 单实例 + 托盘 + 生命周期
│       ├── MainWindow.xaml(.cs)
│       ├── ViewModels/MainViewModel.cs
│       ├── Services/VoiceService.cs
│       ├── Tray/TrayIcon.cs
│       └── Themes/Dark.xaml, Light.xaml, Styles.xaml
├── tests/AiPassportVoice.SelfTest/
└── build/publish.ps1
```

## 构建 / 运行 / 测试 / 发布

```powershell
# 构建
dotnet build AiPassportVoice.sln

# 运行
dotnet run --project src/AiPassportVoice.App

# 自检（18 项：键位解析 / ADPCM / 状态机 / 配置）
dotnet run --project tests/AiPassportVoice.SelfTest

# 单文件发布（自包含，无需目标机装 .NET）
pwsh build/publish.ps1
```

产物：`build/publish/win-x64/AiPassportVoice.exe`。

## 与 Python 版的模块映射

| Python | C# |
| --- | --- |
| `adpcm.py` | `Core/Adpcm/AdpcmDecoder.cs` |
| `udp_transport.py` | `Core/Transport/UdpTransport.cs` |
| `hotkey_forwarder.py` `Forwarder` | `Core/Forwarding/Forwarder.cs` |
| `hotkey_forwarder.py` `KeyInjector` | `Interop/Input/NativeKeyInjector.cs` |
| `hotkey_forwarder.py` `AudioSink` | `Interop/Audio/WaveOutAudioSink.cs` |
| `KEY_MAP` / `resolve_key_spec` | `Core/Input/KeySpec.cs` |
| `voice-config.json` | `Core/Config/AppConfig.cs`（字段名保持一致，可直接迁移旧配置） |
| `diagnose_voice.py` | 主界面「诊断」按钮 + `AudioDeviceProbe` |

## 线协议（不可随意改）

UDP 33333 双向同端口，报文 `[1B type][payload]`：

| type | 方向 | 载荷 |
| --- | --- | --- |
| 0x01 AUDIO | 设备→PC | `[seq][0x80]` + 804B IMA ADPCM |
| 0x02 EVENT | 双向 | UTF-8 JSON 行（含 `\n`） |
| 0x03 BEACON | PC→设备 | `{"v":1,"role":"pc","port":33333}` |
| 0x04 PING | 设备→PC | 空，每 2s 判活 |

PC 侧角色：bind `0.0.0.0:33333`，每 1s 向各网段广播 beacon，从 recvfrom 来源学习设备地址。

## 关键行为（等价于 Python 版，勿删）

- **热键卡死防护**：`voice.end` 丢失或音频静默 > 2.5s 时，`CheckHealth` 强制抬键。
- **下键对账**：收到 `key.action=enter` 若仍处于语音态，先结束语音再回车，避免 Shift+Enter。
- **音频节奏**：3 个 waveOut 缓冲轮转，队列容量 5 丢最旧，空闲补静音，结束前 `Drain(800ms)`。
- **会话桌面**：注入线程 `OpenDesktopW("Default") + SetThreadDesktop`，穿透沙箱。
- **重连**：设备静默 > 12s 只告警不拆 socket，beacon 持续发送，设备回来自动恢复。

## CABLE 策略

启动时 `AudioDeviceProbe.ProbeCable()` 三态检测：

- `Ready`：CABLE Input 可打开 + CABLE Output 存在
- `NotInstalled`：无 CABLE 端点 → 主界面告警条 + 「下载」跳官网
- `Unusable`：端点存在但 16kHz 打不开 / 缺 CABLE Output

**不内置 CABLE 安装包**（再分发需 VB-Audio 授权）。用户点「下载」→ `https://vb-audio.com/Cable/`，装完点「重新检测」。

## 已知限制 / 待办

- [ ] 热键录制（当前是文本框输入键名，未做按键捕获）
- [ ] 设备 IP / 端口改值后需重启程序生效（设置页尚未接回 `VoiceService` 重启）
- [ ] 托盘图标为系统默认图标，待替换
- [ ] 未接入开机自启的「启动后最小化」联动
- [ ] 音频后端预留 NAudio(WASAPI) 升级点：只需替换 `WaveOutAudioSink`，接口 `IAudioSink` 不变
- [ ] 单文件发布首次需联网还原 runtime pack
