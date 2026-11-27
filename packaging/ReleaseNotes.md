把 Steam Deck 的内置手柄通过局域网用作 Windows 的 Xbox 360 手柄。请从下方 **Assets** 下载两个平台各自的安装包。

## Windows x64

1. 升级前先从系统托盘退出 Deck Remote。运行 `DeckRemote-*-win-x64-setup.exe`，接受 Windows 的管理员权限提示，以便安装器写入程序目录并添加专用网络防火墙规则。
2. 从开始菜单打开 **Deck Remote**。如果提示驱动不可用，安装 [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0)，回到 Deck Remote 重新检测。

## SteamOS（Steam Deck）

1. 切换到桌面模式，解压 `DeckRemote-*-steamos-x64.tar.gz`。
2. 在解压目录打开 Konsole，以当前桌面用户运行 `bash install.sh`。安装完成后，从应用菜单打开 **Deck Remote**。
3. 如需在游戏模式使用，在 Steam 桌面客户端将 `~/.local/opt/deck-remote/current/DeckRemote` 添加为非 Steam 游戏，关闭强制使用兼容性工具，填写启动选项 `--game-mode`。回到游戏模式，从 Steam 库启动。

## 首次连接

将 PC 和 Steam Deck 连到同一个可信的 IPv4 子网，并将 PC 上对应的 Windows 网络配置文件设为 **专用网络**。在 Windows 端点击 **配对设备**，在 Steam Deck 端选择这台 PC，核对两端显示的验证信息，再分别确认。完成配对后，两端再次启动时会自动重连。

`SHA256SUMS.txt` 列出了两个安装包的 SHA-256。Windows 可用 `Get-FileHash <文件名>` 查看，SteamOS 可在下载目录执行 `sha256sum -c SHA256SUMS.txt`。
