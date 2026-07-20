# AIHubRouter 跨平台详细设计

## 1. 目标

AIHubRouter 迁移为 Windows、Linux、macOS 共用的 API-only 路由器，同时提供两个独立入口：

- `AIHubRouter.Cli`：面向无图形环境、systemd、容器和脚本。
- `AIHubRouter.Desktop`：基于 Avalonia 12 的桌面界面。

两个入口共用同一套路由、认证、缓存和防抖状态机。程序不得嵌入、自动化或自动启动浏览器。

## 2. 非目标

- 不代理模型请求，不修改请求中的模型名称。
- 不修改 Codex 本地配置。
- 不实现网页抓取或浏览器登录。
- 不在缺少安全存储时回退为明文凭据文件。
- 第一阶段不启用实验性的 Linux 原生 Wayland 后端，默认使用 X11/XWayland。

## 3. 工程边界

```text
src/
  AIHubRouter.Core/       领域模型、路由算法、API 客户端、应用服务和存储接口
  AIHubRouter.Cli/        命令解析、stdin/stdout、watch 生命周期
  AIHubRouter.Desktop/    Avalonia 视图与 ViewModel
tests/
  AIHubRouter.Core.Tests/ 无网络确定性测试
```

`Core` 保持 `net10.0`，不得引用 Avalonia、WinForms 或平台专用桌面程序集。CLI 与桌面端分别发布，因此 CLI 不会加载 Skia 或 UI 原生库。

## 4. 路由决策

### 4.1 硬过滤

候选必须同时满足：

1. 供应商和分组均为启用状态。
2. 当前监测为可用。
3. 供应商平台与策略平台一致。
4. 账号拥有目标分组权限。
5. 监测时间不超过 `MaximumStatusAge`，且未来偏差不超过 1 分钟。
6. 6 小时可用率不低于 `MinimumSuccessRate6h`。
7. 倍率为有限且非负数。

账号专属倍率优先于公开倍率。

### 4.2 价格窗口与速度优选

先计算全部有效候选中的最低倍率 `minimumRate`。候选满足下式时进入价格窗口：

```text
effectiveRate <= minimumRate * (1 + MaximumPricePremiumPercent / 100)
```

当最低倍率为 0 时，仅保留倍率为 0 的候选。在窗口内依次按以下字段排序：

1. 首 Token 延迟更低；缺失或非法延迟排在最后。
2. 6 小时可用率更高。
3. 有效倍率更低。
4. 分组 ID 更小，保证结果确定。

预设策略：

| 模式 | 最大价格溢价 |
|---|---:|
| Economy | 5% |
| Balanced | 15% |
| Speed | 30% |

默认使用 `Balanced`。

### 4.3 切换防抖

路由状态持久化以下非敏感字段：

- 当前候选分组。
- 待确认候选分组与连续命中次数。
- 最后一次切换时间。

发生以下任一情况时允许立即切换：

- 当前分组已失效或不在有效候选集合中。
- 新分组倍率至少改善 `MinimumPriceImprovementPercent`。

其余速度优化切换必须同时满足：

- 新分组在价格窗口内。
- 首 Token 延迟至少改善 `MinimumLatencyImprovementPercent`。
- 新候选连续出现 `RequiredConfirmations` 次。
- 距离上次切换达到 `MinimumDwellTime`。

默认值为连续 2 次、驻留 5 分钟、延迟改善 15%。

### 4.4 可解释结果

算法返回 `RouteDecision`，包含目标分组、倍率、延迟、相对最低价的溢价、相对当前分组的改善、是否切换以及稳定原因码。CLI JSON 与 GUI 必须直接展示这些字段。

## 5. 应用服务

核心接口：

```csharp
public interface IAIHubApiClient;
public interface ICredentialStore;
public interface IAppSettingsStore;
public interface IRouteStateStore;
public interface IRouteSelector;
```

`RoutingService` 负责一个完整周期：

1. 确保 session 可用。
2. 获取监测数据。
3. 读取带 TTL 的分组、倍率和 Key 缓存。
4. 计算候选与防抖决策。
5. `dry-run` 时只返回决策。
6. 仅对不在目标分组的已选 Key 发送 `PUT`。
7. 保存路由状态并返回逐 Key 结果。

资源策略：

- 每个进程复用一个 `SocketsHttpHandler` 和一个 `HttpClient`。
- 分组、用户倍率和 Key 默认缓存 5 分钟。
- 供应商监测每个路由周期刷新。
- `watch` 使用 `PeriodicTimer`，不忙轮询。
- 同一 profile 使用跨平台排他文件锁，阻止 GUI 与 CLI 同时写入。

## 6. HTTP 与认证安全

- 默认只允许 HTTPS；仅显式开发选项允许 loopback HTTP。
- 禁止自动重定向，由客户端验证同源 HTTPS 跳转后再决定是否重试。
- Cookie 使用 `CookieContainer` 限定域，不直接设置可跨域转发的 Cookie 请求头。
- 邮箱密码调用 `/api/v1/auth/login`。
- session 刷新调用 `/api/v1/auth/refresh`。
- 业务接口 401 最多续期并重试一次。
- 2FA 或验证码返回立即失败，不等待浏览器。
- 错误信息不得回显服务端响应中的 Token、Cookie 或密码。

## 7. 凭据与配置

普通设置与凭据分离：

- Windows：保留当前用户范围 DPAPI。
- Linux/macOS GUI：后续平台适配器使用 Secret Service/Keychain。
- 无头模式：优先从环境变量或 stdin 读取；可使用外部提供的主密钥保存 AES-GCM 加密凭据。
- 没有安全存储或主密钥时禁用持久化，不写明文。

主密钥只从 `AIHUB_ROUTER_MASTER_KEY` 或 stdin 获取，不写入参数、日志或配置文件。

配置目录：

- Windows：`%LocalAppData%/AIHubRouter`
- Linux：`$XDG_CONFIG_HOME/AIHubRouter`，回退 `~/.config/AIHubRouter`
- macOS：`~/Library/Application Support/AIHubRouter`

## 8. CLI 合约

```text
aihub-router auth login --email <email> --password-stdin
aihub-router auth import-token --stdin
aihub-router route --once [--dry-run] [--json]
aihub-router watch [--interval <seconds>] [--json]
aihub-router status [--json]
aihub-router config show|set
```

敏感值不得作为普通命令行参数。CLI 使用稳定退出码：

- `0`：成功或无需切换。
- `2`：参数错误。
- `3`：缺少凭据。
- `4`：认证失败或需要交互认证。
- `5`：网络/API 错误。
- `6`：没有可用路由。
- `7`：另一个实例持有 profile 锁。

CLI 监听 `Ctrl+C`、`SIGINT` 和 `SIGTERM`，取消当前请求并正常退出。

## 9. 桌面端

Avalonia UI 只负责编辑配置、调用应用服务和显示结果。主界面包含：

- 站点、邮箱、密码和 Token 输入。
- Economy/Balanced/Speed 分段策略。
- 可用率、轮询间隔和 Key 选择。
- 供应商倍率、首字延迟、状态和推荐结果。
- 单次路由、dry-run、自动路由开关。

不提供“打开登录页”，只允许复制站点地址或认证说明。

## 10. 发布

发布矩阵：

```text
win-x64, win-arm64
linux-x64, linux-arm64
osx-x64, osx-arm64
```

CLI 和 Desktop 分别生成自包含包。第一阶段不启用 Native AOT，先确保序列化、XAML 和平台存储行为一致。macOS 正式分发需要签名与 notarization。

## 11. 验证

必须覆盖：

- 三种价格窗口策略。
- 缺失延迟、零倍率、同价和异常数值。
- 连续确认、驻留时间、价格立即改善和当前路由失效。
- 业务 401 单次刷新。
- 跨域重定向拒绝和 Cookie 不泄漏。
- dry-run 不发送 `PUT`。
- watch 取消与 profile 排他锁。
- 配置文件不包含凭据明文。
