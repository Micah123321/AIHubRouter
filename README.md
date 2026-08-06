# AIHubRouter

CLI 路由全新版本核弹来袭，奥特曼瘫坐在椅子不知所措。

## 我们在做什么？

每分钟从 `https://aihub.top/api/v1/public/groups/usage-stats` 读取真实用户请求数据，依据你的策略偏好（省钱 vs 高速）自动切换分组。默认间隔为 60 秒，也可以按需调整。桌面端、Web 控制台和 CLI 共用同一套路由核心。

## 为什么选择这个项目？

1. 🐦 像巨人一样自由的连续型算法：倍率和真实用户请求的首 Token 速度进入同一条线性加权评分公式，按你的省钱、均衡或极速偏好自动切换。每组读取最近 100 条样本，新鲜度使用连续衰减，越新的体验影响越大。

2. ❤️ 超敏感体质：最后样本时间和样本量共同形成 `0..1` 置信度。低置信度的低延迟会被保守修正，最后成功样本超过 15 分钟的分组不会冒充当前可用。

3. 💩 狗屎风格粘性分组：rank 分相差不大时优先保持分组稳定，守护缓存命中；分差悬殊时果断切换，展示你的铁公鸡本性。

4. 🐶 像村里大黄一样忠诚的全平台兼容性：路由不嫌弃你的硬件架构，Windows、Linux、macOS 的 x64 与 ARM64 全面支持；GUI 对人类友好，CLI 对智能体友好，双插头都能用。

5. 😎 不再讨好，做独立大男/女主：算法太垃圾，总给你路由到不想见到的分组？好办。把恶心人的分组拉进黑名单，永远不再见。天才程序员永远阳光明媚。

## 路由有依据，不靠拍脑袋

每次决策先过滤掉不可用候选：没有有效实际样本、最后样本过期、置信度低于最低门槛、平台不匹配、账号无权限、被加入黑名单或倍率异常的分组都不会参与竞争。置信度只由样本新鲜度和有效样本量计算。

默认最低置信度为 `0.90`，低于门槛的候选直接排除。保守延迟使用 `实测延迟 × (1 + 置信度影响 × (1 - 置信度))`，置信度影响默认 `1.0`。Web 与桌面端的“置信度高级设置”，以及 CLI 的 `--confidence-impact` 和 `--min-confidence` 参数可以调整这两个值。

剩下的候选使用同一套可解释的价格/速度模型：

```text
价格溢价 = (候选倍率 - 最低倍率) / 最低倍率
速度收益 = 最低倍率候选首 Token 延迟 / 候选首 Token 延迟 - 1
加权得分 = 速度权重 × 速度收益 - 价格权重 × 价格溢价
```

默认 `Balanced` 对价格与首 Token 速度各占 50% 权重；`Economy` 以价格为主，`Speed` 以首 Token 速度为主。当前分组仍然有效时，新分组还必须超过“分组粘性”才会切换；默认值为 `0.10`，可在 Web、桌面端或 CLI 的 `--group-stickiness` 中调整。数值越大，越不容易因短时波动切换分组。每次结果都会告诉你为什么选它。

价格范围是硬约束，默认仅允许 `0.00x` 到 `0.15x`（含边界）的生效倍率参与路由。范围外的分组不会进入候选池，也不能通过手动分组切换；可在 Web、桌面端或 CLI 的 `--min-price`、`--max-price` 调整。

| 模式 | 价格权重 | 速度权重 | 适合谁 |
| --- | ---: | ---: | --- |
| `Economy` | 80% | 20% | 优先控制成本 |
| `Balanced` | 50% | 50% | 价格与响应速度同等权衡 |
| `Speed` | 20% | 80% | 优先响应速度 |

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

### Web 控制台

发布包的 `web` 目录提供与桌面端对应的浏览器界面，包括认证配置、策略、黑名单、Key 选择、模拟、立即/手动路由、自动路由和主题切换。

本机启动：

```bash
export AIHUB_WEB_PASSWORD='至少十二个字符的独立访问口令'
./web/aihub-router-web
```

默认只监听 `http://127.0.0.1:5080`。Linux 外网常驻部署使用仓库提供的 HTTPS systemd 安装器：

```bash
sudo ./scripts/install-web-systemd.sh artifacts/linux-x64/web/aihub-router-web
sudo systemctl status aihub-router-web.service
```

安装器会生成独立 Web 口令、凭据加密主密钥和自签名 HTTPS 证书，服务监听 `0.0.0.0:5443`。首次访问需要接受自签名证书，生产环境建议在 5443 端口前使用带正式证书的反向代理。服务器防火墙与云安全组也需要放行实际使用的端口。

Web 端不会向浏览器返回 AIHub 密码或 Token。外网模式必须使用 HTTPS；只有可信内网临时测试时才可同时设置 `AIHUB_WEB_URLS=http://0.0.0.0:5080` 和 `AIHUB_WEB_ALLOW_HTTP=1`。

## 你会得到什么

- **更少的手工判断**：价格、加权首 Token 延迟、置信度、最后样本时间和权限统一比较。
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

## 站点开启 Cloudflare 5 秒盾怎么办

当站点管理员开启 Cloudflare 5 秒盾（或人机验证）时，程序化请求会被拦截并返回“Just a moment”挑战页。AIHubRouter 会自动识别，并调用本机 Edge/Chrome 自动通过挑战（先无头模式，失败后弹出浏览器窗口；若出现“确认您是真人 / Verify you are human”请手动点击一次）。过盾成功后，登录与路由请求会自动携带浏览器获得的 Cookie 与 User-Agent，无需手动复制。如果自动过盾不可用（例如 Linux 服务器未安装浏览器），或验证需要人工介入：

1. 用浏览器打开站点首页，等待 5 秒盾自动通过（若出现“确认您是真人 / Verify you are human”，手动点击一次）。
2. 在浏览器开发者工具（F12）→ Network → 任意请求 → Request Headers 中复制整行 `Cookie`（需包含 `cf_clearance`）。
3. 桌面端：粘贴到“连接与认证”区域的 **Cookie** 字段后保存。
4. CLI：通过环境变量提供：`AIHUB_COOKIE='...' ./cli/aihub-router auth login --email <email> --password-stdin`（`route`/`watch` 也会自动携带该 Cookie）。

> 提示：`cf_clearance` 会过期。若再次遇到挑战提示，程序会自动再次过盾，也可手动用浏览器获取新 Cookie；还可以联系站长对 API 路径关闭挑战或加入 IP 白名单。

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

`watch` 会监视该服务 profile 中的 `settings.json` 与 `credentials.dat`。配置保存后无需重启服务，路由器会立即加载新配置并执行下一轮路由；文件暂时无效时会保留最后一次有效配置。命令行参数（例如 `--interval`）仍优先于配置文件。

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
src/AIHubRouter.Web/        ASP.NET Core Web 控制台与后台轮询
tests/                      无网络确定性测试
docs/                       跨平台与决策设计
```

想了解过滤条件、评分推导、状态机和安全边界，请阅读 [`docs/cross-platform-design.md`](docs/cross-platform-design.md)。
