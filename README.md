# AIHubRouter

为你的 AIHub Key 找到更值得的路由。

AIHubRouter 会在可用分组中自动权衡价格、首 Token 速度和稳定性，选出当前最合适的分组，并把选中的 Key 切过去。你不需要手动盯着倍率、延迟或供应商状态。

## 为什么选择 AIHubRouter

### 路由有依据，不靠拍脑袋

每次决策先过滤掉不可用候选：最新状态为“异常”、未启用、监测过期、平台不匹配、账号无权限或倍率异常的分组都不会参与竞争。“可用”和“警告”状态均可进入评估，6 小时可用率不影响路由。

剩下的候选使用同一套可解释的价格/速度模型：

```text
价格溢价 = (候选倍率 - 最低倍率) / 最低倍率
速度收益 = 最低倍率候选首 Token 延迟 / 候选首 Token 延迟 - 1
加权得分 = 速度权重 × 速度收益 - 价格权重 × 价格溢价
```

默认 `Balanced` 以 90% 权重看价格、10% 权重看速度；`Economy` 更省钱，`Speed` 更在意首 Token。当前分组仍然有效时，新分组还必须领先至少 `0.05` 分才会切换，避免网络瞬时波动造成来回跳转。每次结果都会告诉你为什么选它。

| 模式 | 价格权重 | 速度权重 | 适合谁 |
| --- | ---: | ---: | --- |
| `Economy` | 98% | 2% | 优先控制成本 |
| `Balanced` | 90% | 10% | 日常默认选择 |
| `Speed` | 75% | 25% | 更看重响应速度 |

### Windows、Linux、macOS 都能用

- 支持 Windows、Linux、macOS。
- 每个平台提供 x64 与 ARM64 自包含包，不要求额外安装 .NET。
- 同一套核心逻辑同时提供桌面端和 CLI：有界面时点选即可，无界面时交给脚本、容器或 systemd。

### 轻量，安静地工作

- API-only：不代理模型请求，不改 Codex 配置，不启动或自动化浏览器。
- CLI 不加载 Avalonia、Skia 等桌面组件，适合低配机器和后台任务。
- 复用 HTTP 连接，缓存变化不大的数据，`watch` 使用定时器而不是忙轮询。
- 只有目标分组真的变化时才更新 Key，减少请求和写入。

### 你的凭据仍由你掌控

默认只允许 HTTPS；凭据与普通设置分开保存。Windows 使用 DPAPI，Linux/macOS 无头模式可使用主密钥加密；没有安全存储时不会偷偷落盘明文。审计日志只记录决策依据，不记录密码、Token 或 Cookie。

## 三步开始

### 1. 下载

从 [Releases](https://github.com/OnRightPath/AIHubRouter/releases) 下载与你的系统和架构匹配的压缩包：

| 系统 | x64 | ARM64 |
| --- | --- | --- |
| Windows | `AIHubRouter-win-x64.zip` | `AIHubRouter-win-arm64.zip` |
| Linux | `AIHubRouter-linux-x64.zip` | `AIHubRouter-linux-arm64.zip` |
| macOS | `AIHubRouter-osx-x64.zip` | `AIHubRouter-osx-arm64.zip` |

### 2. 先看结果，再切换

桌面端：启动 `desktop` 目录中的程序，登录后选择策略，点击“模拟”即可预览。

CLI：

```bash
chmod +x cli/aihub-router
./cli/aihub-router auth login --email <email> --password-stdin
./cli/aihub-router route --once --dry-run --json
./cli/aihub-router route --once --json
```

### 3. 需要时持续自动路由

```bash
./cli/aihub-router watch --interval 60 --json
```

`watch` 会按间隔重新读取状态；没有更好的候选时保持当前分组，不制造无意义的切换。

## 你会得到什么

- **更少的手工判断**：价格、首 Token 延迟、最新状态和权限统一比较。
- **更稳定的选择**：加权得分和切换门槛共同抑制抖动。
- **更清楚的结果**：JSON、桌面界面和审计日志都能看到目标分组、价格溢价、延迟改善和原因。
- **更小的运行成本**：CLI 可单独运行，后台只做必要的 API 请求。

## 边界很清楚

AIHubRouter 只负责“把 Key 放到合适的分组”。它不会转发你的模型流量，不会改写模型名，不会修改本地 Codex 配置，也不会代替浏览器完成登录。你仍然使用原有的 AIHub 入口，路由器只在需要时更新 Key 的分组。

## CLI 命令

```text
aihub-router auth login --email <email> --password-stdin [--persist]
aihub-router auth import-token --stdin [--persist]
aihub-router route --once [--dry-run] [--json]
aihub-router watch [--interval <seconds>] [--dry-run] [--json]
aihub-router status [--json]
aihub-router config show
aihub-router config set [options]
```

密码和 Token 不接受普通命令行参数，可通过 stdin、安全凭据文件或环境变量提供。运行 `aihub-router --help` 查看完整选项。

<details>
<summary>后台运行（Linux systemd）</summary>

仓库提供可选的 systemd service 和 keepalive timer：

```bash
sudo ./scripts/install-systemd.sh artifacts/linux-x64/cli/aihub-router
sudo systemctl enable --now aihub-router.service
sudo systemctl enable --now aihub-router-keepalive.timer
```

服务使用独立系统用户运行，凭据通过受保护的环境文件和加密存储提供。完整日志与轮转配置见 `deploy/systemd`。

</details>

<details>
<summary>从源码构建</summary>

需要 .NET 10 SDK。NuGet 默认优先使用华为云镜像，官方源仅作为兜底：

```bash
dotnet restore AIHubRouter.slnx --configfile NuGet.Config \
  --source https://mirrors.huaweicloud.com/repository/nuget/v3/index.json \
  -p:NuGetAudit=false -m:1
dotnet build AIHubRouter.slnx -c Release --no-restore -m:1
dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj \
  -c Release --no-restore
```

发布脚本会生成 `win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64` 和 `osx-arm64` 六个目标：

```bash
./scripts/package-release.sh
```

</details>

## 设计与代码

```text
src/AIHubRouter.Core/       路由算法、API 客户端、认证、缓存和存储
src/AIHubRouter.Cli/        无图形命令行入口
src/AIHubRouter.Desktop/    Avalonia 桌面端
tests/                      无网络确定性测试
docs/                       跨平台与决策设计
```

想了解过滤条件、评分推导、状态机和安全边界，请阅读 [`docs/cross-platform-design.md`](docs/cross-platform-design.md)。
