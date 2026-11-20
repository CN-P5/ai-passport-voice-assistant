# 贡献指南

欢迎提 Issue 和 PR。这个项目很小，保持简单直接就好。

## 开发环境

| 项目 | 要求 |
|---|---|
| .NET SDK | 10.0 或更高 |
| 系统 | Windows 10 / 11 x64 |
| IDE | Visual Studio 2022+ / Rider / VS Code（可选） |
| 虚拟声卡 | [VB-CABLE](https://vb-audio.com/Cable/)（跑音频链路时需要） |

## 本地构建

```powershell
# 编译
dotnet build AiPassportVoice.sln

# 运行界面
dotnet run --project src/AiPassportVoice.App

# 跑自检（协议、状态机、配置往返，不需要音频设备）
dotnet run --project tests/AiPassportVoice.SelfTest

# 发布单文件
dotnet publish src/AiPassportVoice.App/AiPassportVoice.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o dist
```

## 代码结构

```
src/AiPassportVoice.Core/      协议、UDP 传输、转发状态机、ADPCM 解码（无 Windows 依赖）
src/AiPassportVoice.Interop/   winmm 音频输出、键盘注入、声卡探测（P/Invoke 集中在这里）
src/AiPassportVoice.App/       WPF 界面、托盘、配置、服务编排
tests/AiPassportVoice.SelfTest 无第三方测试框架的自检程序
```

约定：

- **平台相关的 P/Invoke 只放在 Interop 层**，Core 层保持纯逻辑，方便测试
- 音频写入路径上不要加重活；电平这类统计用原子写入，别抢队列锁
- 注入/音频线程不要碰 UI，状态通过事件回传，由 Dispatcher 更新
- 不引入 NuGet 依赖，保留"单文件、零依赖"的特性

## 提交规范

提交信息用中文，格式：

```
<type>: <简短描述>

<可选正文>
```

`type` 取值：`feat` / `fix` / `docs` / `refactor` / `perf` / `test` / `chore`

示例：

```
fix: 短按 UP 时两次点击间隔过短导致输入法忽略第二次

tap 模式补齐到 300ms，避免被识别成双击。
```

## PR 流程

1. Fork 并从 `main` 拉分支：`git checkout -b fix/xxx`
2. 改动后确保 `dotnet build` 和 `dotnet run --project tests/AiPassportVoice.SelfTest` 都通过
3. 提交 PR，说明**现象、原因、改法**，涉及界面改动的附截图
4. 如果修复的是设备协议相关的问题，附上相关日志片段（`logs/app-*.log`）

## 不要提交的内容

- `bin/`、`obj/`、`dist/`、`build/publish/`
- 运行日志 `logs/`
- 本机生成的 `voice-config.json`
- 任何包含个人路径、设备 IP、微信/邮箱等隐私信息的截图或日志

## 报告问题

提 Issue 前先看 [README 的常见问题](README.md#常见问题)。提 Issue 时请附：

- 系统版本、使用的输入法名称
- 是否安装 VB-CABLE、程序日志（`logs\` 目录下对应日期的文件）
- 复现步骤和期望行为

## 行为准则

保持技术讨论的礼貌与克制。不接受针对个人的攻击、无关的政治与宗教话题。
