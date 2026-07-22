# AIHubRouter

CLI 路由全新版本核弹来袭，奥特曼瘫坐在椅子不知所措。

## 我们在做什么？

每分钟轮询平台数据，依据你的策略偏好（省钱 vs 高速）自动切换分组。默认间隔为 60 秒，也可以按需调整。

## 为什么选择这个项目？

1. 🐦 像巨人一样自由的连续型算法：倍率和首 Token 速度进入同一条线性加权评分公式，按你的省钱、均衡或极速偏好自动切换。经验参数来自真实监测数据上的粒子群优化，超参数少、自由度高但不过拟合。

2. ❤️ 超敏感体质：使用实时状态作为可用性的硬约束。相比滞后的五/六小时可用率，最新监测状态优先决定一个分组能否进入候选。

3. 💩 狗屎风格粘性分组：rank 分相差不大时优先保持分组稳定，守护缓存命中；分差悬殊时果断切换，展示你的铁公鸡本性。

4. 🐶 像村里大黄一样忠诚的全平台兼容性：路由不嫌弃你的硬件架构，Windows、Linux、macOS 的 x64 与 ARM64 全面支持；GUI 对人类友好，CLI 对智能体友好，双插头都能用。

5. 😎 不再讨好，做独立大男/女主：算法太垃圾，总给你路由到不想见到的分组？好办。把恶心人的分组拉进黑名单，永远不再见。天才程序员永远阳光明媚。

## 路由有依据，不靠拍脑袋

每次决策先过滤掉不可用候选：最新状态为“异常”、未启用、监测过期、平台不匹配、账号无权限、被加入黑名单或倍率异常的分组都不会参与竞争。“可用”和“警告”状态均可进入评估，6 小时可用率不影响路由。

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

可通过桌面端的“黑名单分组”勾选要排除的分组，也可以使用 CLI 保存分组 ID：

```bash
./cli/aihub-router config set --blacklisted-groups 12,18
```

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
