# WinLinScp

轻量级 WinSCP 风格 **Windows ↔ Linux 文件管理器**（.NET 10 WPF）。复用系统 OpenSSH 与 `~/.ssh/config`（含跳板 ProxyJump、密钥登录），支持**通过登录脚本一键连接**，轻量发布、无外部依赖。

## 功能特性

- **双栏文件管理**：本地 / 远端并排，**可点击/可拖放的面包屑地址栏**、后退/前进/上级、列表顶部".."返回上一级、排序、显示隐藏文件
- **拖拽传输**：本地↔远端跨系统拖拽上传/下载（**多选一次拖入**），拖到**面包屑分块**即传到/移到该目录，本地面板内拖拽移动（Ctrl=复制），支持从资源管理器拖入
- **批量上传确认 + 打包**：多选上传弹窗确认（不打包 | tar 默认 | zip），打包 = 单个归档一次传输 → **远端自动解压**到目标目录并清理
- **执行自定义脚本**：右键「执行自定义脚本…」，本地默认 PowerShell、远端默认 bash（工作目录=当前目录），结果弹窗
- **本地 shell 工具**：右键「以终端打开」（所选文件夹或当前目录，Windows Terminal，回退 cmd）、「以任务管理器打开」
- **任务队列**：上传/下载（含文件夹递归 `scp -r`）、**文件操作任务卡**（压缩/删除/脚本等）、逐项取消、完成/总数统计、**实时速度 + 乐观进度条 + 预计剩余时间（ETA）**；卡片区可拖高、可收起
- **远端归档**：右键 `压缩为 .tar.gz / .zip`、`解压到当前目录`（远端 `tar`/`unzip`）
- **本地归档**：右键 `压缩为 .zip`（**可选压缩等级**：不压缩/最快/标准/最小体积，后台压缩不卡界面）、`解压到当前目录`（.NET 内置 ZipArchive，正确处理中文文件名）
- **内置查看器**：远端文本文件预览、编码切换（UTF-8/GBK/UTF-16/Latin1）、用默认程序打开、保存
- **连接配置持久化**：命名 profile 存 `profiles.json`（exe 所在目录，便携）。两种连接方式：**登录脚本**（自动提取 Target/WorkDir；脚本内部仍可经 `~/.ssh/config` 别名/跳板）、**IP + 用户名 + 密码直接连接**（经系统 ssh `SSH_ASKPASS` 免交互认证）
- **性能**：常驻 SSH 会话复用单条连接（列目录/增删改毫秒级）+ 目录列表缓存（返回/后退秒开）+ `find -printf` 单进程列目录
- 快捷键：F5 刷新 · F2 重命名 · Del 删除 · Enter 进入 · Backspace 上级 · Alt+←/→ 后退/前进 · Ctrl+L 连接 · Ctrl+D 下载 · Ctrl+U 上传

## 环境要求

| 项 | 要求 |
|---|---|
| 系统 | Windows 10/11 |
| 运行时 | .NET 10 Desktop Runtime（或装 SDK 构建） |
| 传输 | 系统 OpenSSH 客户端（`ssh.exe`/`scp.exe`），`~/.ssh/config` 已配置主机别名（含 ProxyJump/密钥） |
| 远端 | Linux（Ubuntu 等），`tar` 必备；`zip`/`unzip` 可选（压缩/解压 zip 时需要） |

## 构建与发布

```bash
# Debug 构建
dotnet build src/WinLinScp/WinLinScp.csproj -c Debug

# 无头自检（连真实主机逐项验证，退出码 0/1）
dist\WinLinScp.exe -selftest
# 或 Debug 版：
src/WinLinScp/bin/Debug/net10.0-windows/WinLinScp.exe -selftest

# 非单文件 Release 发布 → dist\（文件夹，启动略快）
build-dist.cmd        # 双击即可
```

`dist\` 为**框架依赖非单文件**（`WinLinScp.exe` + dll，共约 500KB，依赖本机 .NET 10 运行时）。要拷给无运行时机器，用 self-contained 发布（约 60MB）。

## 使用指南

1. 工具栏 **连接…** → 选保存的配置，或填**登录脚本**（如 `login_ubuntu.ps1`，自动提取 `$Target`/`$WorkDir`）/ 填 **IP/主机 + 用户名 + 密码**直接连接 → **连接** → 日志绿色"登录成功" → **确定并连接**
2. 左栏=本地、右栏=远端，双击进目录，选中后工具栏 **↓ 下载 / ↑ 上传**（目标路径显示在状态栏和传输队列）
3. 右键：远端可 tar/zip 压缩解压；本地可 zip 压缩解压
4. 传输队列在底部"传输队列"展开，可逐项取消

## 架构

```
src/WinLinScp/
├── App.xaml(.cs)        # 组合根 + -selftest 分发
├── MainWindow           # 主窗口：工具栏/双栏/传输队列/状态栏
├── Views/               # FilePaneView(复用双栏)、ConnectDialog、RemoteViewerWindow 等
├── ViewModels/          # Main、Local/RemotePane、TransferQueue、Connect、Viewer
├── Models/              # SshResult、RemoteFileInfo、TransferItem、ConnectionProfile…
└── Services/
    ├── ProcessRunner        # 唯一进程执行点：ArgumentList、并发读流、超时杀树
    ├── PersistentSshSession # 常驻 SSH 管道：一条连接复用，长度前缀帧协议
    ├── SshService           # 命令分发：常驻会话优先，故障回退一次性连接
    ├── ScpService           # 上传/下载（cwd 相对名防 C: 冒号误判）
    ├── RemoteFileListing    # find -printf 列目录（NUL 分隔，含 | 文件名也安全）
    ├── RemoteOps            # 远端增删改/归档 bash 构造（ShellQuote + base64）
    ├── SshConfigDiscovery / LoginScriptParser / ProfileStore
    ├── SelfTest             # -selftest 无头自检（13 项）
    └── …
```

**关键技术点**：
- 主机参数永远传 `~/.ssh/config` 的**别名**（如 `my-ubuntu`），不拼 `user@host` —— ProxyJump/密钥/known_hosts 全部依赖 config 块
- 远程命令整段 base64 后 `echo <b64> | base64 -d | bash`，彻底免引号问题
- 常驻会话的远程循环用 `bash -c "$(echo b64 | base64 -d)"`（脚本作参数），使循环 stdin 保持为 SSH 通道——**不能用管道**，否则循环立即 EOF
- scp 本地参数一律 cwd 相对名（`WorkingDirectory`），避免 Windows 盘符冒号被误判为远端

## 已知限制

- 进度为**乐观估计**（总量经并发预估，文件夹走 `du`/递归统计；完成前封顶 95%，不做实时字节级精确百分比）
- 无断点续传；scp 静默覆盖
- 单条常驻会话串行化所有远端命令（传输用独立 scp 进程，不受影响）
- 非 UTF-8 文件名仅显示有损；操作仍正确

## 版本

见 [CHANGELOG.md](CHANGELOG.md)。

## 许可

[MIT](LICENSE) — 自由使用/修改/再分发，需保留版权与许可声明。

## 开发辅助

- `tools/probe/`：无 UI 驱动完整 VM 栈的集成探针（连真实主机，验证传输/归档/会话速度）
- `-selftest`：服务层无头自检（13 项，含跳板连通、往返传输、归档、会话复用）
