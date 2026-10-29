# BigFish Installer

![大肥鱼安装器](assets/github-header.png)

**DeepSeek 大肥鱼，一键部署。** 面向 Windows 10 22H2 / Windows 11 x64 的本地部署器，使用 C#、WPF、.NET 10。安装器自行携带 .NET Runtime，最终用户无需安装 SDK、Node.js、npm、pnpm 或 Python，也无需输入命令。

BigFish 是 DeepSeek Harness 的第三方部署与管理工具，不是新的聊天机器人后端，也不是 DeepSeek、腾讯或 Bilibili 官方产品。DSH 由 DeepSeek 开发，消息插件由各上游作者维护。

> 当前为 **0.1.1 工程预览**。已在 Linux 上交叉构建 Windows 自包含程序，并进行了真实 DSH 安装、HTTP 认证、插件状态及重复安装验证。**尚未通过真实 Windows 桌面及扫码验收，不能宣称干净 Windows 一键安装已经完成最终验收。** 精确结果见 [验证记录](docs/VALIDATION-0.1.1.md)。

## 下载与使用

本交付没有创建远程 GitHub 仓库或线上 Release。维护者将仓库推送 GitHub 后，标签 `v*` 工作流会在 Windows 上构建、测试并生成 **草稿预发布**。人工验收后再公开发布。

当前交付的 `release/BigFish-CN.exe`（中国版）和 `release/BigFish-Global.exe`（国际版）都是可直接双击的 Windows x64 自包含图形程序。它们已交叉编译，但尚未在 Windows 上运行验收。真正的 Inno 安装包 `BigFishSetup-CN.exe` / `BigFishSetup-Global.exe` 需在 Windows CI 成功后生成；目前没有公开 Release 下载链接。

| 版本 | 适用地区 | 默认下载源 |
| --- | --- | --- |
| BigFish-CN.exe | 中国用户，默认推荐 | Node/npm 优先 npmmirror；可选 Python 包优先清华 PyPI |
| BigFish-Global.exe | 其他地区 | Node、npm、PyPI 官方源 |

两版默认都是中文。首次创建 DSH 设置时选择中文；已有设置不覆盖。在「高级选项」可更改下载源并点击应用。中国镜像同步失败或网络不可用时有界回退官方源；完整性校验失败立即停止，不能通过换源跳过校验。Node、uv 与 npm 固定依赖仍使用官方审核哈希。可选 Python 托管运行时可能仍需 GitHub 官方下载连接。

1. 从对应地区的完整交接 ZIP 解压 `release`，中国用户双击 `BigFish-CN.exe`，其他地区双击 `BigFish-Global.exe`；无需预装 .NET。
2. 保留「DeepSeek Harness」「QQ / 微信消息接入插件」，点击「一键安装」。Bilibili 默认不选。
3. 等待真实检查与安装阶段完成。CLI、HTTP 认证和 IM RPC 通过后浏览器打开本机 DSH。
4. 在 DSH 的设置中配置模型 API Key，再按 IM机器人 菜单扫码授权。

普通用户可以完全通过 EXE 操作。尚无已发布仓库，因而不提供无法执行的下载一行命令；开发者拿到源码后可用一行构建：`pwsh -File scripts/build.ps1 -Setup -Region all`。

## 配置模型 API Key

点击「配置 AI 模型」，在 DSH Web UI 中进入 **设置 → 模型**，选择 DeepSeek 并输入 API Key。密钥保存在 DSH 的本机凭据存储中；BigFish 不索取、不存储、不记录密钥，也不会自动调用付费模型。

API Key 无效、余额不足、服务商限流分别需要在 DSH 或服务商账户中处理。安装成功不代表模型调用成功。新建对话，手动发送一条短消息，确认模型正常回复。

## 接入 QQ / 微信

- **QQ**：设置 → IM机器人 → QQ。使用插件当前支持的官方机器人创建/扫码流程，或者填写已有机器人凭据。平台授权、沙箱/发布状态和群聊权限由腾讯决定。
- **微信**：设置 → IM机器人 → 微信。生成二维码并由用户扫码。若账号未获得相关功能权限，按平台提示处理；二维码过期需要重新生成。
- 回到 BigFish 点击「测试连接」读取实际状态，只显示配置数量和连接数量。再到 QQ 或微信手动发送测试消息，观察往返结果。

BigFish 不实现逆向协议，不接收普通 QQ/微信账号密码，不假装扫码成功或已获群聊权限。绑定按钮打开当前进程的认证 Web 入口并显示精确菜单指引；未使用未经上游保证的设置深链接。

## 启动、停止、修复

管理页支持启动、停止、重启、打开工作区、打开 Web、检查更新、查看版本及日志。**关闭 BigFish 会停止它拥有的服务**，下次打开可点击启动；不创建开机自启任务或后台系统服务。

「修复安装」重新执行幂等检测：健康组件复用，损坏运行环境使用新的独立目录重建，插件修复前备份配置清单。不会清空会话、删除工作区或覆盖系统 Node.js。中断后重新点击一键安装即可重试；已完成并验证的文件会保留，未完整下载的文件重新下载。

默认数据目录 `%LOCALAPPDATA%\BigFish`，工作区 `%USERPROFILE%\ai-fish`。已有 `%USERPROFILE%\.dsh` 会被发现并保留。精确匹配审核版本且可执行的系统 DSH 会**只读复用程序代码**，同时使用独立 Web profile 和私有 pnpm；不接管其现有服务、不自动迁移旧凭据。兼容系统 Node/npm 可以复用。若外部 DSH 后续被升级或删除，启动时会提示修复并创建隔离版本。

## Bilibili 可选功能

勾选后安装固定版本 `dsh-bili-scroll`，单独准备 Python 环境，并验证插件状态。首次配置关闭定时执行、评论、回复和私信。本地转写模型默认不下载。

完成后在 **设置 → 刷B站** 扫码，手动点击「现在跑一轮」。依赖安装与实际 B站任务仍需 Windows 验收；可选组件失败会保留基础安装并尝试恢复 DSH。首次默认设置不覆盖老用户已明确修改的设置。

## 构建与测试（开发者）

需要 .NET 10 SDK；运行测试中的真实子进程夹具需要 Node.js 24。推荐 Windows 上安装 Inno Setup 6，打开 `BigFish.sln`，或执行：

```powershell
dotnet test tests/BigFish.Tests -c Release
dotnet build BigFish.sln -c Release
dotnet publish src/BigFish.App -c Release -r win-x64 --self-contained true -p:BigFishDownloadRegion=cn -o artifacts/channels/cn
./scripts/build.ps1 -Setup -Region all
./scripts/windows-acceptance.ps1
```

输出：`artifacts/release/BigFish-CN.exe`、`BigFish-Global.exe`；Inno Setup 成功后另外输出同目录下的 `BigFishSetup-CN.exe`、`BigFishSetup-Global.exe`。Linux 可编译引擎、运行测试并交叉发布 WPF；不能运行 WPF 或代替 Windows 安装验收。

`BigFish.Cli` 是开发/CI 验收入口，与 GUI 共用安装引擎；普通用户无需使用。`smoke` 命令会真实安装和启动两次，读取状态并检查配置哨兵文件；不扫码、不发送社交消息、不调用模型。

```powershell
dotnet run --project src/BigFish.Cli -- smoke --root C:\Temp\BigFish-Test --workspace C:\Temp\ai-fish-test --private-node --report C:\Temp\bigfish-report.json
```

## 项目结构

| 路径 | 内容 |
| --- | --- |
| `src/BigFish.Core` | 检测、依赖决策、下载校验、隔离安装、认证就绪、状态/配置保护 |
| `src/BigFish.App` | WPF 界面及可取消的异步命令 |
| `src/BigFish.Cli` | 与 GUI 同引擎的真实验收程序 |
| `tests/BigFish.Tests` | 单元、模拟安装、真实本机 HTTP 与子进程测试 |
| `compatibility` | 固定版本、npm 完整依赖锁文件 |
| `installer` | Inno Setup 每用户安装工程 |
| `.github/workflows` | 双平台测试、Windows 真实基础安装、可选 B站及草稿发布 |

## 边界与文档

不能自动化的部分包括模型密钥申请/充值、平台权限审批、扫码授权、验证码、真实聊天与 B站内容效果。当前不支持 ARM64 原生版、Windows 10 旧构建、VPS、Docker、旧全局环境自动迁移、后台常驻及自动升级到未知版本。

- [上游核查与兼容策略](docs/COMPATIBILITY.md)
- [Windows 人工验收](docs/WINDOWS-ACCEPTANCE.md)
- [故障排查](docs/TROUBLESHOOTING.md)
- [安全说明](SECURITY.md)
- [验证记录](docs/VALIDATION-0.1.1.md)
- [英文简介](README.en.md)
- [第三方声明](THIRD_PARTY_NOTICES.md)

BigFish 自身使用 MIT 许可证；第三方代码保留各自许可证及版权。

美术素材另有非商业授权约束，见 [assets/README.md](assets/README.md)；MIT 只适用于项目代码。
