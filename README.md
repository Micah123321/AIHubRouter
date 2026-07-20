# AIHubRouter

AIHubRouter 是一个 API-only 的跨平台 AIHub Key 分组路由器。它综合账号可用分组、专属倍率、首 Token 延迟和 6 小时可用率，自动选择价格与速度更均衡的分组，并通过 AIHub API 更新选中的 Key。

程序不代理模型请求、不修改 Codex 配置，也不会启动、嵌入或自动化浏览器。

## 功能

- 支持 Windows、Linux 和 macOS，提供 x64 与 ARM64 自包含包。
- 同时提供 Avalonia 桌面端和不加载图形组件的轻量 CLI。
- 支持 Economy、Balanced、Speed 三种价格窗口策略。
- 支持单次路由、只读模拟、定时自动路由和 JSON 输出。
- 使用连续确认和最短驻留时间，避免延迟波动导致频繁切换。
- 对明显降价或当前分组失效立即切换。
- 默认仅允许 HTTPS，不依赖浏览器登录。
- 凭据与普通设置分开存储，拒绝明文持久化。

## 下载选择

每个压缩包都包含 `desktop`、`cli` 和本 README：

| 系统 | x64 | ARM64 |
|---|---|---|
| Windows | `AIHubRouter-win-x64.zip` | `AIHubRouter-win-arm64.zip` |
| Linux | `AIHubRouter-linux-x64.zip` | `AIHubRouter-linux-arm64.zip` |
| macOS | `AIHubRouter-osx-x64.zip` | `AIHubRouter-osx-arm64.zip` |

这些包已经包含 .NET 运行时，目标机器不需要另外安装 .NET 10。不要混用不同平台目录中的可执行文件和原生库。

## 快速开始

### Windows

解压对应架构的 ZIP：

- 桌面端：运行 `desktop\AIHubRouter.exe`。
- CLI：在 PowerShell 中运行 `cli\aihub-router.exe --help`。

未签名构建可能触发 SmartScreen 提示。正式分发前应使用可信代码签名证书签名。

### Linux

```bash
chmod +x desktop/AIHubRouter cli/aihub-router
./desktop/AIHubRouter
./cli/aihub-router --help
```

桌面端需要 X11 或 XWayland。`libSkiaSharp.so` 和 `libHarfBuzzSharp.so` 必须保留在桌面程序旁边。

### macOS

```bash
chmod +x desktop/AIHubRouter cli/aihub-router
./desktop/AIHubRouter
./cli/aihub-router --help
```

当前发布物没有 Apple 签名和 notarization，正式对外分发前需要完成签名、公证，并进一步封装为 `.app`。

## 桌面端使用

1. 输入 AIHub 站点、邮箱和密码，或输入已有 Token。
2. 选择 Economy、Balanced 或 Speed。
3. 设置最低 6 小时可用率和自动检查间隔。
4. 点击“刷新”读取供应商状态、账号分组和 Key。
5. 勾选需要路由的 Key。
6. 使用“模拟”预览结果，确认后使用“立即路由”或开启“自动路由”。

“模拟”只计算决策，不发送更新 Key 分组的请求。界面不会打开浏览器。

## CLI 使用

```text
aihub-router auth login --email <email> --password-stdin [--persist]
aihub-router auth import-token --stdin [--persist]
aihub-router route --once [--dry-run] [--json]
aihub-router watch [--interval <seconds>] [--dry-run] [--json]
aihub-router status [--json]
aihub-router config show
aihub-router config set [options]
```

先模拟，再执行一次真实路由：

```bash
aihub-router route --once --dry-run --json
aihub-router route --once --json
```

持续自动检查：

```bash
aihub-router watch --interval 60 --json
```

修改策略：

```bash
aihub-router config set --mode balanced --minimum-success 90 --interval 60
aihub-router config set --selected-keys 101,102
```

密码和 Token 不接受普通命令行参数。认证可通过 stdin、安全凭据文件或以下环境变量提供：

```text
AIHUB_EMAIL
AIHUB_PASSWORD
AIHUB_TOKEN
AIHUB_REFRESH_TOKEN
AIHUB_COOKIE
AIHUB_USER_AGENT
AIHUB_ROUTER_MASTER_KEY
```

长时间运行时优先使用安全凭据存储，不要把敏感值写入脚本、服务文件、镜像层或命令历史。`watch` 响应 `Ctrl+C`、`SIGINT` 和 `SIGTERM`，同一配置目录通过排他锁阻止多个实例同时修改 Key。

### CLI 退出码

| 退出码 | 含义 |
|---:|---|
| 0 | 成功或当前已经最优 |
| 2 | 参数错误 |
| 3 | 缺少凭据 |
| 4 | 认证失败或需要交互认证 |
| 5 | 网络、API 或 Key 更新失败 |
| 6 | 没有可用路由或本地配置不可用 |
| 7 | 另一个实例持有当前 profile 锁 |

## 路由策略

程序首先排除以下候选：未启用、监测不可用、状态过期、平台不匹配、账号无权限、倍率非法或 6 小时可用率低于阈值。

随后以有效候选中的最低倍率建立价格窗口：

| 模式 | 允许高于最低倍率 |
|---|---:|
| Economy | 5% |
| Balanced | 15% |
| Speed | 30% |

在价格窗口内依次选择首 Token 延迟更低、可用率更高、倍率更低的候选。延迟缺失不会被当作速度改善。倍率为零时，价格窗口只保留免费候选。

仅由速度优势触发的切换默认要求：

- 首 Token 延迟至少改善 15%。
- 同一候选连续出现 2 次。
- 距离上次切换至少 5 分钟。

如果当前分组失效，或新分组倍率至少改善 5%，程序会立即切换。每次决策都会输出原因、目标分组、价格溢价、延迟改善比例和逐 Key 更新结果。

## 认证与本地存储

- 所有远程请求默认只允许 HTTPS；只有显式开发选项允许 loopback HTTP。
- 自动重定向关闭，Cookie 使用按域隔离的 `CookieContainer`。
- Windows 使用当前用户范围 DPAPI 加密凭据。
- Linux/macOS 设置 `AIHUB_ROUTER_MASTER_KEY` 后，使用 AES-256-GCM 加密凭据。
- 未提供可用安全存储时，程序拒绝把凭据保存成明文。
- 设置、加密凭据和非敏感路由防抖状态分别保存，并使用原子写入。

配置目录：

| 系统 | 目录 |
|---|---|
| Windows | `%LocalAppData%\AIHubRouter` |
| Linux | `$XDG_CONFIG_HOME/AIHubRouter` 或 `~/.config/AIHubRouter` |
| macOS | `~/Library/Application Support/AIHubRouter` |

## 资源占用

- CLI 不引用或加载 Avalonia、Skia 等桌面组件。
- 每个进程复用 HTTP 客户端和连接池。
- 分组、专属倍率和 Key 使用短期缓存，监测数据按周期刷新。
- `watch` 使用 `PeriodicTimer`，不进行忙轮询。
- 仅在目标分组发生变化时调用 Key 更新接口。

## 从源码构建

需要 .NET 10 SDK。NuGet 默认优先使用华为云镜像，官方源只作为缺失包兜底。

```bash
dotnet restore AIHubRouter.slnx \
  --configfile NuGet.Config \
  --source https://mirrors.huaweicloud.com/repository/nuget/v3/index.json \
  -p:NuGetAudit=false -m:1
dotnet build AIHubRouter.slnx -c Release --no-restore -m:1
dotnet run --project tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj \
  -c Release --no-restore
```

本环境中的 Ubuntu .NET 10 SDK 并行构建解决方案时存在项目引用竞态，因此命令显式使用 `-m:1`。

## 发布与打包

生成全部六个平台发布目录：

```bash
./scripts/publish.sh
```

构建、测试、敏感信息扫描并分别生成六个 ZIP：

```bash
./scripts/package-release.sh
```

只生成指定目标：

```bash
./scripts/package-release.sh linux-x64 win-x64
```

产物位于 `artifacts/packages/`，`SHA256SUMS.txt` 用于校验下载完整性。打包脚本会扫描 JWT、Bearer Token、API Key、Cookie、私钥、非示例邮箱和本机用户路径；任何命中都会中止发布。

## 工程结构

```text
src/AIHubRouter.Core/       路由算法、API 客户端、认证、缓存和存储
src/AIHubRouter.Cli/        无图形命令行入口
src/AIHubRouter.Desktop/    Avalonia 桌面端
tests/                      无网络确定性测试
docs/                       详细设计
scripts/                    构建、发布、扫描和桌面冒烟工具
```

详细架构和决策状态机参见 `docs/cross-platform-design.md`。
