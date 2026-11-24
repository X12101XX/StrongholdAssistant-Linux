# StrongholdAssistant Linux 移植版 (Wayland / niri / Lutris + Proton)

本仓库基于上游 [LBEILC/StrongholdAssistant](https://github.com/LBEILC/StrongholdAssistant) **v1.3.8**（Apache-2.0），面向 Linux Wayland 环境进行原生移植。当前首版针对 **Wayland + niri + Lutris/Proton 明日方舟 PC 端** 完成适配。

## 架构概览

在不重写上游 100+ 核心识别、模拟与决策文件的前提下，采用跨平台 .NET 8 + 原生 Wayland/GTK 辅助组件方案：

| 模块 | 实现方式 | 说明 |
| --- | --- | --- |
| **核心引擎 (`ASPAssistant.Core`)** | `.NET 8` + `Maa.Framework` (ONNX CPU) + `OpenCvSharp4` (linux-x64 slim) + `Microsoft.ML.OnnxRuntime` + `SkiaSharp` | 完整保留开局/商店/场上干员（`sprite-model.onnx`）/盟约/敌人识别、站位模拟与阵容规划 |
| **窗口追踪 (`NiriIpc.cs`)** | 直连 `$NIRI_SOCKET` Unix Socket JSON IPC | 自动识别 Lutris/Proton 下的 `明日方舟` (`arknights.exe`) 窗口（自动排除鹰角启动器 `games.exe`）、多显示器坐标与 HiDPI 缩放、工作区/滚屏可见性 |
| **画面捕获 (`libstronghold_wayland.so`)** | 原生 `zwlr_screencopy_manager_v1` SHM（回退 `grim`） | ~3.5 ms 捕获 1080p 游戏区域，**不污染剪贴板、不抓鼠标光标、无黄框、无需门户弹窗** |
| **键鼠输入 (`libstronghold_wayland.so`)** | 原生 `zwlr_virtual_pointer_manager_v1` + `zwp_virtual_keyboard_manager_v1` | **无需 root / ydotool / uinput**，支持绝对定位与相对位移拖拽（适配物资调配页拖动与自动操作） |
| **透明点击穿透覆盖层 (`stronghold-overlay`)** | `GTK4` + `gtk4-layer-shell` + `Cairo` | 锚定游戏所在显示器，空输入区域 100% 穿透，支持商店卡框、推荐站位、待确认问号、升本提示、敌人路线 |
| **侧栏、阵容广场与森空岛 (`stronghold-webview` + `UiBridgeServer`)** | `GTK3` + `WebKit2GTK-4.1` + 本地 HTTP/WebSocket 桥接与广场反向代理 | 支持阵容广场浏览/发布/点赞/云同步与登录弹窗、森空岛账号登录/退出、攻略广场列表、单篇攻略导入与历史战绩同步 |

## 快速安装（Release 预编译包）

从 [Releases](https://github.com/X12101XX/StrongholdAssistant-Linux/releases) 下载 `StrongholdAssistant-<version>-linux-x64.tar.gz`（已内置自包含 .NET 8 运行时，**无需安装 `dotnet-sdk`**），安装图形与 Wayland 运行时依赖后解压即可运行：

```bash
# Arch / CachyOS 运行时依赖
sudo pacman -S gtk4 gtk4-layer-shell webkit2gtk-4.1 gtk3 wl-clipboard grim zenity fontconfig

tar -xzf StrongholdAssistant-1.3.8-linux.1-linux-x64.tar.gz
cd StrongholdAssistant-1.3.8-linux.1-linux-x64
./stronghold-assistant
```

## 从源码构建（Arch / CachyOS）

```bash
sudo pacman -S dotnet-sdk-8.0 gtk4 gtk4-layer-shell webkit2gtk-4.1 gtk3 \
               wayland wayland-protocols wl-clipboard grim zenity fontconfig nodejs npm gcc make
```

> 如未全局安装 `dotnet-sdk-8.0`，也可使用微软官方脚本安装到 `~/.dotnet`，`scripts/build-linux.sh`、`scripts/run-linux.sh` 与 `scripts/package-linux.sh` 会自动检测 `DOTNET_ROOT=$HOME/.dotnet`。

```bash
chmod +x scripts/build-linux.sh scripts/run-linux.sh scripts/package-linux.sh
./scripts/build-linux.sh
./scripts/run-linux.sh

# 打自包含 tar.gz 发布包（输出到 dist/）
./scripts/package-linux.sh 1.3.8-linux.1
```

常用命令行参数：

```bash
./scripts/run-linux.sh --help
./scripts/run-linux.sh --headless --port 15178   # 仅启动本地 HTTP/WS 服务，在浏览器打开侧栏
./scripts/run-linux.sh --no-overlay              # 关闭游戏透明覆盖层
./scripts/run-linux.sh --devtools                # 开启 WebKitGTK 调试器
./scripts/run-linux.sh --trigger toggle          # 向运行中的实例发送热键动作 (toggle|rescan|route|field|stop)
```

## niri 推荐配置

### 1. 窗口规则 (`~/.config/niri/cfg/rules.kdl`)

```kdl
// 明日方舟（Lutris + Proton / xwayland-satellite）关闭圆角裁剪，保证画面 1:1 对齐
window-rule {
    match app-id=r#"^arknights\.exe$"#
    match title=r#"^明日方舟$"#
    geometry-corner-radius 0
    clip-to-geometry false
}

// 助手侧栏：推荐浮窗或放在副屏
window-rule {
    match app-id=r#"^stronghold-assistant$"#
    open-floating true
    default-column-width { fixed 410; }
    default-window-height { fixed 920; }
}
```

### 2. 全局快捷键 (`~/.config/niri/cfg/keybinds.kdl`)

Wayland 出于安全策略不允许后台应用直接拦截全局按键，建议通过 niri 原生 `binds` 触发：

```kdl
binds {
    Ctrl+Shift+Slash { spawn "/data/projects/StrongholdAssistant-Linux/scripts/run-linux.sh" "--trigger" "toggle"; }
    Alt+R            { spawn "/data/projects/StrongholdAssistant-Linux/scripts/run-linux.sh" "--trigger" "route"; }
    Alt+F            { spawn "/data/projects/StrongholdAssistant-Linux/scripts/run-linux.sh" "--trigger" "field"; }
    Ctrl+Alt+Q       { spawn "/data/projects/StrongholdAssistant-Linux/scripts/run-linux.sh" "--trigger" "stop"; }
}
```

## Lutris + Proton 明日方舟匹配说明

- 默认自动识别标题为 `明日方舟` / `Arknights` 或 `app_id` 为 `arknights.exe` 的窗口，并自动忽略鹰角启动器（`鹰角启动器` / `games.exe`）。
- 如需自定义窗口匹配，可设置环境变量：
  - `STRONGHOLD_WINDOW_TITLE="明日方舟"`
  - `STRONGHOLD_WINDOW_APP_ID="arknights.exe"`
- 数据与日志保存在 `${XDG_DATA_HOME:-~/.local/share}/StrongholdAssistant/`。

## 致谢与许可

本项目遵循上游 **Apache License 2.0** 许可协议（详见 [LICENSE](LICENSE) 与 [NOTICE](NOTICE)），所有原始游戏数据、识别模型、前端界面与核心算法版权归原作者与上游贡献者所有。
