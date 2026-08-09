# AIHubRouter 跨平台详细设计

## 1. 目标

AIHubRouter 迁移为 Windows、Linux、macOS 共用的 API-only 路由器，同时提供两个独立入口：

- `AIHubRouter.Cli`：面向无图形环境、systemd、容器和脚本。
- `AIHubRouter.Desktop`：基于 Avalonia 12 的桌面界面。

两个入口共用同一套路由、认证、缓存和状态管理。程序不得嵌入、自动化或自动启动浏览器。

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

1. 分组为启用状态，实时用量统计包含有效 TTFT 样本。
2. 最后样本时间不超过 `MaximumStatusAge`（默认 15 分钟），且未来偏差不超过 1 分钟。
3. 实时数据置信度不低于可配置的最低门槛，默认 `0.90`。
4. 统计平台与策略平台一致。
5. 账号拥有目标分组权限。
6. 倍率为有限且非负数。
7. 分组 ID 不在持久化黑名单中。

账号专属倍率优先于公开倍率。

### 4.2 价格与延迟权衡

每个路由周期读取每组最近 `100` 条请求的统计结果。新鲜度使用连续指数衰减，而不是把样本数量窗口当成离散权重：

```text
freshness = exp(-ln(2) * max(lastSampleAge, 0) / (MaximumStatusAge / 2))
volume = 1 - exp(-effectiveSampleCount / 20)
confidence = freshness * volume
```

当前接口返回的是聚合 TTFT 与最后样本时间；如果接口返回逐条样本，程序会按每条样本的时间计算连续权重。缺失、非法或低置信度延迟的候选不参与本轮竞争。为避免低置信度的低延迟获得虚假的速度优势，评分使用保守延迟：

```text
conservativeLatency = averageLatency * (1 + (1 - confidence))
```

默认最低置信度为 `0.90`，低于门槛的候选直接排除。可配置的置信度影响系数 `alpha` 将保守延迟扩展为：

```text
conservativeLatency = averageLatency * (1 + alpha * (1 - confidence))
```

程序从可信候选中选出最低倍率基准，并计算其他候选相对基准的权衡得分：

```text
pricePremiumRatio = (candidateRate - minimumRate) / minimumRate
speedupRatio = conservativeBaselineLatency / conservativeCandidateLatency - 1
weightedScore = latencyWeight * speedupRatio - priceWeight * pricePremiumRatio
```

最低倍率基准的得分为 0。其他候选得分大于 0 才进入推荐集合；得分相同时优先倍率更低、延迟更低和分组 ID 更小的候选。

预设策略：

| 模式 | `priceWeight` | `latencyWeight` | 决策倾向 |
|---|---:|---:|---|
| Economy | 0.90 | 0.10 | 价格优先，保留速度约束 |
| Balanced | 0.50 | 0.50 | 价格与首字速度同等权衡 |
| Speed | 0.10 | 0.90 | 速度优先，保留价格约束 |

最低倍率为 0 时，仅在零倍率候选中按延迟选择。全部候选都缺失延迟时回退到最低倍率，避免虚构速度收益。默认使用 `Balanced`。

### 4.3 供应商序列参考

路由服务按配置读取以下参考接口（缓存边界见本节后文）：

```text
GET /api/v1/public/providers/series?range=6h&timezone=Asia%2FShanghai
GET /api/v1/public/providers?timezone=Asia%2FShanghai
```

解析器只依赖 `group_id`、`probe` 和 `user_ttft`，并从 `/providers` 的 `cache_hit_rate` 读取供应商缓存命中率。`probe` 元组只读取前三项时间戳、成功标记和延迟毫秒，忽略未确认的尾部字段；`user_ttft` 按 `sample_count` 对 `avg_ttft_ms` 加权；命中率百分数字符串转换为 `0..1`，`样本不足` 等无效值不进入评分。

候选质量由可用分量平均得到：

```text
providerQuality = average(
  probeSuccessRate,
  inverseNormalizedProbeLatency,
  inverseNormalizedUserTtft,
  cacheHitRate when every comparable candidate has a valid value)

finalScore = weightedScore
  + providerSeriesWeight * (candidateQuality - baselineQuality)
```

探测成功率直接使用 `0..1`，但至少需要两次有效探测和有效成功探测延迟才生成候选质量分；每个候选还必须在最大状态年龄内。延迟分量只在全部已评分候选均有有效值、且至少两个候选的值不同时进行反向最小-最大归一化，缺少关键指标或已过期的候选不参与比较，避免缺失数据比真实的较差数据更有利。候选或最低倍率基准缺少质量分时不叠加，权重为 `0` 时结果与原评分一致。默认权重为 `0.20`，允许范围为 `0..1`。

`/providers/series` 成功快照按 `range + timezone` 缓存在进程内，默认 TTL 为 300 秒，可配置范围为 30 到 3600 秒。这个 TTL 只控制序列响应刷新，不代表供应商的 `cache_hit_rate`。`/providers` 的命中率数据在账号参考数据刷新时获取，并随账号缓存边界复用；不从序列 `probe` 元组尾字段猜测命中率。普通周期命中有效缓存时不发送对应请求；强制刷新会访问接口，失败时可继续使用仍新鲜的序列缓存并标记 warning。任一参考接口数据过期、网络失败或格式错误时明确报告降级并沿用可用的基础评分；原始响应不写入磁盘。

同一 `/providers` 响应中的 `model_health` 是独立于 `cache_hit_rate` 的健康信号。有效分组即使命中率字段无效也会保留健康状态；重复分组的相同模型按 `failed` 优先合并。主路由沿用全部可用候选，Luna 路由在独立 Key 集合上先排除 `model_health.luna == "failed"` 的分组，再调用完整评分管线重新生成基准、候选和排序。健康信号缺失或请求失败时只跳过 Luna 写入，主路由继续执行。

持久化设置新增 `LunaSelectedKeyIds`，路由状态新增 `LunaCurrentGroupId`。主路由和 Luna 路由不能共享 Key；两条 lane 分别完成决策和 PUT 后，协调器统一合并更新后的 Key 缓存并保存双状态，周期结果通过 `LunaRoute` 暴露 Luna 目标、过滤数量、健康状态和逐 Key 结果。

### 4.4 权重稳定机制

首 Token 延迟会随网络和服务负载波动。算法通过置信度修正、保守延迟和最小得分优势抑制频繁切换：

- 不设置固定延迟上限。
- 不设置切换冷却或最短驻留时间。
- 不要求候选连续出现多次。
- 新候选必须比当前有效分组高出“分组粘性”（内部字段 `MinimumScoreAdvantageToSwitch`），默认值为 `0.10`，可由用户配置。
- 只有生效倍率位于价格范围内的分组才会进入候选池；默认范围为 `0.00x` 到 `0.15x`（含边界），这是不可被加权评分或手动切换绕过的硬约束。
- 只持久化当前候选分组。

候选相对最低倍率的必要速度收益为：

```text
speedupRatio > pricePremiumRatio * priceWeight / latencyWeight
```

Balanced 中右侧系数为 1，候选每增加 10% 倍率溢价，至少需要额外约 10% 的速度收益才能抵消价格损失。当前分组仍有效且双方得分可计算时，只有新候选的得分优势大于配置的分组粘性（默认 `0.10`）才立即切换。初始路由、当前分组失效或无法比较得分时不应用该门槛。

这个门槛使用加权得分单位，而不是百分比。Balanced 中，价格相同的两个分组需要约 20% 的速度收益才能超过默认切换门槛；速度相同时，价格改善需要超过约 20%。算法不设置固定或软延迟边界，极端延迟仍通过同一加权公式参与决策。

### 4.5 可解释结果

算法返回 `RouteDecision`，包含目标分组、倍率、延迟、相对最低价的溢价、相对当前分组的改善、是否切换以及原因码。CLI JSON 与 GUI 必须直接展示这些字段。JSONL 审计日志额外记录每个候选的价格溢价、速度收益、加权得分和是否进入推荐集合。

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
2. 从 `aihub.top` 获取每组最近 100 条真实用量的统计结果。
3. 读取或刷新供应商序列成功快照。
4. 读取供应商缓存命中率，并读取带 TTL 的分组、倍率和 Key 缓存。
5. 计算候选与综合评分决策。
6. `dry-run` 时只返回决策。
7. 仅对不在目标分组的已选 Key 发送 `PUT`。
8. 保存路由状态并返回逐 Key 结果及两类供应商参考数据加载状态。

资源策略：

- 每个进程复用一个 `SocketsHttpHandler` 和一个 `HttpClient`。
- 分组、用户倍率和 Key 默认缓存 5 分钟。
- 供应商序列成功快照默认缓存 5 分钟，统计范围和时区变化时不复用旧键；该缓存不等于供应商缓存命中率。
- `/providers` 的 `cache_hit_rate` 随账号参考数据刷新边界读取，缺失或失败时仅跳过该质量分量。
- 真实用量统计及其最后样本时间每个路由周期刷新。
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
- Linux/macOS GUI 与无头模式：当前统一使用 `AIHUB_ROUTER_MASTER_KEY` 保存 AES-GCM 加密凭据；Secret Service/Keychain 适配器属于后续平台增强，不是当前实现。
- 无头模式：优先从环境变量或 stdin 读取；没有外部主密钥时不保存非空凭据，也不回退到明文。
- `PersistentAppSettings.PersistCredentials` 对新配置和缺少该字段的旧配置默认为 `true`；旧配置中明确写出的 `false` 继续保留，避免升级时改变用户的安全选择。
- 邮箱、密码、Bearer/Refresh Token、Cookie 和 User-Agent 只写入加密的 `credentials.dat`；普通设置写入 `settings.json`，两个文件不会混存敏感字段。
- 保存先完成序列化和加密，再使用进程内锁和同目录 `persistence.lock` 跨进程排他锁提交两个文件；临时/备份路径只接受当前 profile 生成的固定文件名模式。事务记录用于进程异常退出恢复，加密或替换失败时恢复旧文件，不留下明文或半提交状态。这不承诺所有操作系统上的断电级目录元数据原子性。
- 没有安全存储或主密钥时，带非空凭据的保存会明确失败；普通设置和空凭据仍可保存，绝不回退到明文。
- 如果保护器暂时不可用但目录中已有 `credentials.dat`，加载状态会标记凭据不可用；普通设置保存不会把该文件当成空凭据删除，恢复主密钥后仍可尝试解密。用户明确关闭 `PersistCredentials` 时才会清理该文件。
- Web/桌面/CLI 会分别展示或输出“已有认证待解密”状态；CLI `config show` 保持 `hasStoredCredentials=true` 并额外输出 `credentialsUnavailable=true`，避免把不可读密文误报为没有认证。
- Web/CLI 的环境变量只覆盖本次运行时值，不回写 `settings.json` 或 `credentials.dat`；环境变量提供的 Token 刷新结果也只保留在进程内。

供应商参考设置包括 `ProviderSeriesWeight`、`ProviderSeriesCacheSeconds`、`ProviderSeriesRange` 和 `ProviderSeriesTimezone`。默认值分别为 `0.20`、`300`、`6h` 和 `Asia/Shanghai`。其中 `ProviderSeriesCacheSeconds` 只控制序列响应缓存；供应商 `cache_hit_rate` 来自 `/providers`，没有单独的伪缓存命中率配置。

主密钥只从 `AIHUB_ROUTER_MASTER_KEY` 或 stdin 获取，不写入参数、日志或配置文件。

配置目录：

- Windows：`%LocalAppData%/AIHubRouter`
- Linux：`$XDG_CONFIG_HOME/AIHubRouter`，回退 `~/.config/AIHubRouter`
- macOS：`~/Library/Application Support/AIHubRouter`
- Docker：`/app/data/AIHubRouter`（需要同时保留数据卷和 `AIHUB_ROUTER_MASTER_KEY`）

## 8. CLI 合约

```text
aihub-router auth login --email <email> --password-stdin [--persist|--no-persist]
aihub-router auth import-token --stdin [--persist|--no-persist]
aihub-router route --once [--dry-run] [--json]
aihub-router watch [--interval <seconds>] [--json]
aihub-router status [--json]
aihub-router config show|set
```

认证命令默认按 `PersistCredentials` 安全保存登录会话或 Token；`--persist` 是强制保存的兼容别名，`--no-persist` 只对本次命令禁用保存，不清理已有文件。通过 `config set`、Web 或 Desktop 将 `PersistCredentials` 设为 `false` 时才会删除 `credentials.dat`。敏感值不得作为普通命令行参数。CLI 使用稳定退出码：

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
- 轮询间隔和 Key 选择。
- 可勾选不参与候选评估的黑名单分组。
- 跟随系统、浅色和深色主题选择，主题偏好持久化。
- 供应商倍率、首字延迟、状态和推荐结果。
- 单次路由、dry-run、自动路由开关。
- 认证默认以加密形式持久化；关闭 `PersistCredentials` 设置会清理本地凭据文件，不会写入明文；CLI 的 `--no-persist` 不删除已有文件。

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

- 三种倍率/首字速度权重策略。
- 供应商序列请求参数、稳定字段解析、样本加权和异常元组容错。
- `/providers` 的 `cache_hit_rate` 百分比解析、重复分组聚合、`样本不足` 排除和接口失败降级。
- 序列权重为 0 的旧评分兼容，以及正权重对综合评分的影响。
- 序列缓存命中、过期、强制刷新和失败安全降级。
- Balanced 对普通速度差保持低倍率、对数量级速度差选择更快分组。
- 缺失延迟、零倍率、同价和异常数值。
- 加权推荐首次出现即执行、价格改善和当前路由失效。
- JSONL 日志格式、权限、轮转和敏感信息扫描。
- 业务 401 单次刷新。
- 跨域重定向拒绝和 Cookie 不泄漏。
- dry-run 不发送 `PUT`。
- watch 取消与 profile 排他锁。
- 配置文件不包含凭据明文。
