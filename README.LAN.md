# AudioPlaybackConnector LAN

基于 [ysc3839/AudioPlaybackConnector](https://github.com/ysc3839/AudioPlaybackConnector) 的局域网扩展。
目标：SUPERU 播放的声音通过家庭网络在另一台 Windows 电脑的耳机 / 音箱上播放。

## 运行

两台电脑解压同一个 `dist/AudioPlaybackConnector-LAN-win-x64` 发行包，运行
`AudioPlaybackConnectorLAN.exe`。两边默认使用同一个连接码 `APCL-HOME-2026`，发送端只需填写接收电脑的 IP；换过连接码时才需要复制、粘贴连接信息。
便携版本包含 .NET 8 运行环境；无需安装虚拟声卡或额外音频工具。
详细步骤见 [快速开始](docs/快速开始.txt)。首次启动不会自动传输或修改网络配置。

## 实现

- 发送：WASAPI loopback，共享模式自动转换到 48 kHz / 16-bit / stereo。
- 传输：APCL v1 UDP，每个数据报最大 1176 字节；AES-256-GCM 验证并加密音频。
- 接收：根据样本位置重排，丢包补静音，缓冲有上限，线性插值以 ±0.2% 的读速率补偿两台声卡时钟漂移。
- 播放：WASAPI 共享模式，可选输出设备及接收音量。
- 首版单向、单发送会话。接收端监听时在 UDP 29865 广播自己的名字、地址和音频端口，发送端列出后选择，不再手填 IPv4；本机 IPv4 自发自收在产品界面禁止，测试程序只在 loopback 地址做验证。
- 不实现虚拟播放设备、不抑制发送端的本地播放，不兼容 VBAN，不承诺蓝牙电脑互连问题已修复。

头部为 little-endian：magic[4]、version[1]、channels[1]、bits[1]、headerSize[1]、sampleRate[4]、
sequence[4]、sessionNonce[12]、framePosition[8]、payloadLength[4]；接密文和 GCM tag[16]。
全部头部作为 AAD。每次发送启动生成随机 96-bit 会话 nonce，最后 32 位与序号异或作为该包 nonce；
序号耗尽前停止，避免同一会话重用 nonce。两边默认连接码为 `APCL-HOME-2026`，改过的连接码保存在当前用户本机设置，日志不包含连接码或原始音频。

## 与原项目的关系

原蓝牙 C++ 源码保留，托盘菜单增加 `局域网音频 / LAN Audio` 启动旁边的局域网模块。
首版蓝牙分发文件仍是未经修改的原版 1.3.1；局域网程序的“蓝牙模式”按钮可以启动它。
本机未安装 Visual Studio C++ 工具链，因此 C++ 菜单补丁只完成源码修改，未编译验证。
已编译、可独立运行的功能是 .NET 局域网模块；重新构建 C++ 项目后才会获得原托盘中的新菜单。

## 构建与测试

需要 Windows 和 .NET 8 SDK；项目目录 `.tooling/dotnet` 中的工具链优先，否则使用 PATH 中的 dotnet。

```powershell
.\scripts\Test-Lan.ps1
.\scripts\Test-Lan.ps1 -Audio
.\scripts\Build-Lan.ps1 -Portable -BluetoothExe '原版 AudioPlaybackConnector64.exe 的绝对路径'
```

普通测试覆盖协议边界、加密鉴别、篡改、随机输入、乱序、丢包、重复与重放、缓冲上限、
音量、时钟漂移模拟以及真实本机 UDP 收发。`-Audio` 额外初始化真实 WASAPI 播放与采集，
只渲染静音测试数据，采集数据发送到本机测试接收器，不保存原始音频。
自动测试不能代替两台物理电脑的实际 Wi-Fi / 网线联调。

运行程序不需要管理员。可选网络配置脚本默认只显示预览，显式 `-Apply` 并以管理员运行才会修改：
将指定网卡设为专用网络，并仅向本地子网放行该可执行文件的指定 UDP 端口。

## 目录

- `LanAudio/`：WinForms 界面、WASAPI 采集与播放、加密协议、接收缓冲。
- `LanAudio.Tests/`：可执行测试程序，无额外测试框架依赖。
- `scripts/`：构建、测试与可选网络配置。
- `docs/`：双机操作说明与第三方许可。
- 原根目录 C++ 源码：上游蓝牙接收工具及 LAN 启动菜单补丁。

许可：MIT。上游版本基点 `f1975592ef346cad2a2d8384efe41c50ea194d2f`。
