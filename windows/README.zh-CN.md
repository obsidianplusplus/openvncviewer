# OpenVNCViewer C# 中文客户端

面向 Windows 11 x64，使用 .NET 10 LTS、WPF 系统 Fluent 主题和 WriteableBitmap。GPL-3.0-or-later；继承仓库 LICENSE。

## 编译与下载

Actions → C# Windows 11 EXE → Run workflow。成功后下载 OpenVNCViewer-CSharp-Windows11-x64，解压运行 OpenVNCViewer.exe；无需安装 .NET。构建附带 SHA256SUMS.txt。本地编译：

```
dotnet run --project windows/ProtocolTests -c Release
dotnet publish windows/OpenVNCViewer -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts
```

## 使用

填写服务器和端口。Mac 屏幕共享填写系统账号；普通 VNC 的账号留空。剪贴板默认关闭，启用后只在远端视图获得焦点时向远端同步，远端同步可覆盖本地剪贴板。密码仅在内存中使用，不保存磁盘。

F11 切换全屏。支持鼠标、滚轮、键盘和 Alt → Command 映射。Windows 保留的系统快捷键（如 Alt+Tab）不会被全局拦截。

## 性能设计与边界

后台网络解码与 UI 分离；按显示刷新节奏消费最新帧，最高 120 次/秒，不为旧帧积压调度任务；鼠标移动合并、TCP_NODELAY、持续 ZRLE 解压流、直接 BGR32 缓冲。显示实际绘制 FPS，不伪造服务端 FPS。局域网低延迟模式优先 Raw，牺牲带宽降低解码开销；默认优先 ZRLE，支持 CopyRect、Raw、桌面尺寸变化。

没有承诺 120 FPS：服务器更新频率、屏幕刷新率、分辨率、带宽和全帧复制成本仍是限制。此版尚未实现 Tight/JPEG、自动重连、收藏服务器、密码保存、全局快捷键钩子和中文 IME 文本注入；Python 客户端保留用于兼容，不宣称功能完全等价。需要真实 Mac / VNC 环境做兼容与性能验收。

只支持 RFB None / VNC DES / Apple ARD type 30 认证，不支持 VeNCrypt。传统 VNC 密码只用前 8 字节，.NET 会拒绝 DES 弱密钥；遇到该错误请更换 VNC 密码。ARD 校验 DH 素数与边界，但没有可信服务端身份认证。没有 TLS，请只连可信服务器，跨不可信网络必须配合 VPN / SSH 隧道。构建为未签名程序。

唯一外部运行库为 SharpZipLib 1.4.2（MIT），用于 RFB 连续 zlib 流；不包含遥测、更新下载器、启动项或后台服务。
