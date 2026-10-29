> 本文件为 **0.1.0 的历史验证记录**。0.1.1 的测试、构建及未验收项请查看 [VALIDATION-0.1.1.md](VALIDATION-0.1.1.md)。历史 EXE 哈希不适用于 0.1.1。

# 验证记录

日期：2026-10-28。执行环境：Ubuntu 24.04.3 LTS x64；.NET SDK 10.0.403 / Runtime 10.0.14；Node 24.22.1。没有 Windows 桌面、Windows 虚拟机或真实社交账号登录环境。

## 已实际执行并通过

| 检查 | 结果与证据 |
| --- | --- |
| .NET 自动化测试 | **46 passed / 0 failed / 0 skipped**；[TRX](evidence/tests-linux.trx) |
| Visual Studio Solution 构建 | Core、CLI、WPF、Tests 均编译；Release 构建退出码 0 |
| Windows 自包含发布 | `dotnet publish ... -r win-x64 --self-contained true` 成功；实际生成 `artifacts/portable/BigFish.exe` |
| EXE 类型/包含运行时 | PE32+ GUI x86-64；[runtimeconfig](evidence/published-runtimeconfig.json) 使用 `includedFrameworks`，包含 .NETCore 与 WindowsDesktop 10.0.14 |
| 干净应用目录的真实部署 | 经 C# 安装引擎执行 npm ci、DSH CLI、IM 安装、认证 HTTP/RPC、停止、重建 controller、重复安装；[记录](evidence/acceptance-fresh-linux.json) |
| 已有 DSH 代码只读复用 | 精确版本外部 CLI 引用、私有 pnpm、独立 profile、真实启动与重复安装通过；[记录](evidence/acceptance-reuse-linux.json) |
| 配置保护 | 实际重复安装中测试哨兵文件保持，活动代未重复创建；备份/不覆盖配置另有自动化测试 |
| QQ/微信只读状态 | 实际插件接口返回成功；两者均 0 个绑定、0 个连接，不宣称扫码成功 |
| B站插件 | 真实 npm 2.3.0 包安装、动态导入、HTTP status 200；[记录](evidence/acceptance-bili-linux.json) |
| B站安全初始配置 | 实际 status 验证评论/回复/私信/定时/转写关闭；Python 未安装，未执行任务 |
| 官方 Windows 依赖文件 | 实际下载 Node ZIP 37,748,064 字节及 uv wheel 15,334,118 字节，固定 SHA-256 匹配；未执行其中的 Windows 程序 |
| 工作流/源文件语法 | GitHub Actions YAML、项目 XML、XAML、JSON 清单均解析；WPF XAML 编译成功 |

最终 EXE 的大小、SHA-256 与架构见 [windows-build.json](evidence/windows-build.json)。

## 自动化测试包含什么

- 单元：Node 版本解析与兼容、依赖决策、中文/空格路径与路径越界、原子配置写入、状态恢复、配置备份、不覆盖 B站既有设置、错误分类、日志脱敏、可信源和 ZIP 路径保护、ArgumentList、并发锁、外部 DSH 入口。
- 模拟集成：空白/完整/部分依赖、下载失败与重试、重复安装、启动超时、取消、可选失败保留基础服务、真实阶段计数。
- 本机集成：真实 TCP HTTP 夹具进行 303/Cookie 认证与 RPC，拒绝无关服务器；真实 Node 子进程取消、无输出启动超时及进程/锁释放。

模拟 HTTP 与模拟安装通过不代表真实扫码或真实模型请求成功；上表另外列出了真实上游安装和接口验证。

## 未执行或尚未完成

| 项目 | 当前状态 |
| --- | --- |
| 干净 Windows 10/11 双击最终验收 | **未执行**；需按人工表验证，不能宣称已经达标 |
| Windows 上 WPF 界面、屏幕布局、后台窗口、Job Object | **未运行**；代码已交叉编译，Windows CI 自测已配置 |
| Windows Portable Node 自动下载/解压/运行 | 下载文件及哈希已验；Windows 运行分支未实际执行 |
| Windows .NET Runtime 全缺失环境 | self-contained 配置与包已验证；全新 Windows VM 验收待做 |
| Windows B站 Python 全链路 | 源码和可选工作流已实现；未实际完成 uv/Python 安装 |
| DeepSeek API Key/余额/模型请求 | 未提供密钥，未调用；由用户配置和测试 |
| QQ/微信扫码、权限和真实聊天 | 未登录任何账号；必须人工参与 |
| B站扫码和手动视频筛选 | 未登录，未运行外部任务，未评论或私信 |
| Inno Setup `BigFishSetup.exe` | 安装器工程与 Windows 打包流程已写；当前 Linux 环境未构建该引导安装包 |
| GitHub Actions / Release | 工作流已写但未在远程执行；没有远程仓库或公开 Release |
| Authenticode 签名 | 未配置证书，当前 EXE 未签名 |
| 旧工作区/凭据自动迁移 | 未实现；已有数据保留，用户手动决定迁移 |
| HTTP Range 字节续传 | 未实现；使用验证缓存、阶段恢复及安全重新下载 |

验收入口：[Windows 人工验收](WINDOWS-ACCEPTANCE.md)。在实际执行并取得证据之前，以上“未验证”项不得改写为通过。

## 开发中发现并修复

真实重跑发现 DSH 新版 CLI 需要显式 `--profile web` 与隐私 patch 参数组合；已修正并通过真实部署重跑。另修复了 .NET 编译中的跨 await Span 使用、测试读取 HTTP body、下载超时错误分类、外部启动失败锁释放、可选步骤计数和日志分块脱敏问题。最终测试与产物基于修复后的源码。
