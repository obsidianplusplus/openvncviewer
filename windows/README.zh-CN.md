# OpenVNCViewer C# 中文客户端 1.1

Windows 11 x64，.NET 10 LTS + WPF 系统 Fluent 主题。GPL-3.0-or-later，继承仓库 LICENSE。

## 编译与下载

仓库 Actions → C# Windows 11 EXE → Run workflow；通过协议测试、中文界面和设置保存测试后，下载 `OpenVNCViewer-CSharp-Windows11-x64`，解压运行 `OpenVNCViewer.exe`，无需另装 .NET。构建包含 SHA256 校验值；UI Preview 产物提供模拟数据的界面截图。

```powershell
dotnet run --project windows/ProtocolTests -c Release
dotnet run --project windows/UiTests -c Release
dotnet publish windows/OpenVNCViewer -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts
```

## 连接与保存

左侧输入名称、主机、端口、账号及密码，点击连接。Mac 屏幕共享填写系统账号；标准 VNC 账号留空。保存连接会保存该服务器的专属选项。支持新建、选择、删除和双击连接，最多 50 条。

设置存储于 `%LOCALAPPDATA%/OpenVNCViewer/settings.json`，原子替换写入。只有勾选“使用 Windows 加密保存密码”并点击保存连接时才会保存密码；使用 Windows DPAPI CurrentUser 加密，无明文回退。取消勾选会立即撤销已保存密码。DPAPI 不防御同一 Windows 登录账号下的恶意程序。

## 设置

| 页面 | 选项 |
| --- | --- |
| 画面与性能 | 省流量/均衡/流畅预设，5–120 FPS 请求上限，ZRLE/Raw 编码，24 位颜色/16 位 RGB565，CopyRect，本地适应窗口/原始大小/25–200%缩放，平滑缩放 |
| 键鼠与剪贴板 | 仅查看，Alt→Command，Ctrl/Alt 交换，1–10倍滚轮及反向，剪贴板关闭/单向/双向，仅在画面获焦时发送，远端提示音 |
| 连接与重连 | 3–60秒连接/认证超时，共享会话，TCP保活，断线自动重连，间隔与次数 |
| 窗口 | 连接后全屏，全屏隐藏侧栏，性能统计，记住大小和最大化状态 |

画面、输入与帧率立即生效；编码、色深、CopyRect、连接超时、保活与共享会话需重连。设置在“应用并保存”时持久化，有选中的已保存服务器时同步更新该配置。自动重连只在会话曾成功连接后启用，认证错误或主动断开会停止重试。

工具栏提供强制刷新、PNG截图、Ctrl+Alt+Del、文本发送（含本地中文输入法输入的文本，是否接收 Unicode 取决于服务器）和快速缩放。F11 切换全屏；Ctrl+Alt+Shift+S 打开设置。Windows 保留的全局系统快捷键不被拦截。

## 带宽和画面

默认均衡：ZRLE、全彩、30 FPS。高分辨率或网络拥堵优先用“省流量”：ZRLE、RGB565、15 FPS。16位色会降低颜色精度，也能减少像素数据量。Raw 仅作为明确标注的排查选项，1920×1080 全帧 32位像素在30 FPS时约需249 MB/s，不能因为是局域网就假定带宽足够。

解码在后台执行，保留连续 ZRLE 解压状态，合并鼠标移动，按帧率消费最新画面，按累计变化区域更新显示。状态栏显示实际编码、色深、MB/s和绘制FPS。FPS配置是请求上限，不保证服务器输出同样帧率。本地缩放不改变远端传输分辨率。

## 当前协议范围

支持 RFB 3.3/3.7/3.8，None / VNC DES / Apple ARD type 30 认证，Raw / ZRLE / CopyRect / DesktopSize。16位色需服务器遵守 SetPixelFormat。尚不支持 Tight/JPEG、VeNCrypt/TLS、文件传输、远端分辨率设置或全局键盘钩子。中文IME直接透传未实现，可使用工具栏文本发送。

VNC DES 仅使用前8字节密码，.NET会拒绝弱DES密钥；ARD校验DH素数和边界，但不提供可信服务端身份验证。仅连接可信服务器，跨不可信网络使用VPN/SSH隧道。构建为未签名程序。

唯一外部运行依赖为 SharpZipLib 1.4.2（MIT）。没有遥测、自动下载执行、开机启动或后台服务。测试使用本地模拟服务器及临时设置，不连接真实远端；真实Mac兼容性和帧率仍需目标设备验收。
