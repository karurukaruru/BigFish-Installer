# 上游核查与版本策略

核查日期：2026-10-28。区分仓库主分支、npm 发布版本、作者声明兼容性、本项目实际测试四类证据。

| 组件 | 固定版本 | 核查依据 | 本项目验证边界 |
| --- | --- | --- | --- |
| Node.js | 24.22.1 x64 LTS | nodejs.org 官方下载与 SHASUMS256 | 当前 Linux 运行时可执行；Windows ZIP 已固定 SHA-256，待 Windows 执行 |
| DSH | 0.2.1-rc.2 | npm 发布包、官方 CLI 源码及 README；主分支为 0.2.2-alpha.2 | 真实 Linux 安装、CLI、启动认证、HTTP |
| pnpm | 11.28.5 | 官方主仓 packageManager；npm 包完整性 | 真实 profile 插件安装 |
| dsh-im | 5.7.0 | npm 发布包声明 DSH 0.2.1-rc.2 compatible、profile web | 真实加载与 QQ/微信只读状态；未绑定账号 |
| dsh-bili-scroll | 2.3.0 | npm latest 当时为 2.3.0，repository 指向 c1overzz；主分支已为 2.3.1 | 真实插件 HTTP status；Windows Python/扫码/任务待验 |
| uv（可选） | 0.13.6 | PyPI 官方 Windows wheel 与 SHA-256 | 尚未在 Windows 执行 |

本表是**核对后的候选版本组合**，不是“全平台已验收”的承诺。DSH 仍处于 developer preview，发布号本身包含 rc；没有将预览版伪称稳定正式版。

## 官方来源

- DSH：https://github.com/deepseek-ai/deepseek-harness
- CLI 文档：https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/cli-help.md
- Web 启动及认证：https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/bundle/web-app/README.md
- dsh-im：https://github.com/xmanrui/dsh-im
- dsh-im 元数据：https://github.com/xmanrui/dsh-im/blob/main/package.json
- 实际 HTTP RPC 参考：发布包 `scripts/verify-interface-language.mjs`（303 Cookie 交换、`client-request` envelope）
- 用户给定 B站仓库：https://github.com/lll-77777777/dsh-bili-scroll （该分支 package.json 仍为 0.1.7）
- 当前 npm 声明维护仓库：https://github.com/c1overzz/dsh-bili-scroll
- Node 校验表：https://nodejs.org/dist/v24.22.1/SHASUMS256.txt
- uv 官方发布元数据：https://pypi.org/pypi/uv/0.13.6/json
- 自包含发布：https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview

已实际核对 npm registry 精确版本元数据和 tarball SHA-512；直接包的精确名称、版本、repository 和完整性写入 `compatibility/versions.json`。Node Windows x64 ZIP 的固定 SHA-256 为：

```
d2046d28c2c3dcd211ac5145c0bf30e2047ef9dfb306693c8c6e2eccd8b95fa5
```

## 命令与隔离策略

BigFish 不使用 PowerShell 字符串执行 npm.cmd。它实际运行 `node.exe <npm-cli.js> ...` 与 `node.exe <dsh/lib/bin.js> ...`，通过 `ProcessStartInfo.ArgumentList` 传参，避免 Windows `.cmd` 外壳的额外解析层。

运行时使用 `npm ci --prefix <generation> --omit=dev`，由随应用嵌入的 `runtime-package.json` 和 `runtime-package-lock.json` 固定全部 DSH/pnpm 依赖。每代安装在独立目录，执行版本与 help 检查后才原子写入活动代指针。

若已存在精确版本的系统 DSH，会保存只读的绝对入口引用、仅准备私有 pnpm，避免重装整套 DSH。所有配置依然在独立 DSH_HOME；每次启动复验外部 CLI 版本。外部代码被移除或升级时提示修复，不尝试恢复/降级用户的全局安装。

插件采用上游支持的：

```
dsh plugin --profile web add -w --save-exact @xmanrui/dsh-im@5.7.0
dsh plugin --profile web add -w --save-exact dsh-bili-scroll@2.3.0
dsh --profile web --patch <privacy.patch.yml> --no-open --host 127.0.0.1 --port <3080或0>
```

不调用 `allow-version --accept-risk` 绕过兼容性检查。插件安装由上游 pnpm profile 事务实现；安装前额外备份 package.json、lockfile、workspace YAML 和配置 patch。pnpm 会按 profile 锁文件复用依赖；新增插件的传递依赖及 Python requirements 仍由审核包声明的范围解析，尚未提供跨平台统一离线锁包镜像。

`DSH_HOME`、`DSH_AGENTS_HOME`、npm userconfig/globalconfig/cache 均指向私有位置；只给子进程临时 PATH。不覆盖系统 PATH、不改全局 npm、不读取用户 npm Token。

管理服务仅绑定 127.0.0.1。3080 占用时，仅复用当前管理器拥有且已经认证的实例；其他实例无法证明目录/版本/认证归属时保留其运行，使用 `--port 0` 让操作系统分配端口。没有通过端口开放来伪造兼容性判断。

## 更新规则

“检查更新”只展示 registry 最新版本与审核版本对比，不自动升级。维护者更新版本时必须核查包来源、许可证、Node 要求、CLI 参数、启动认证、插件 RPC 与依赖锁，并运行 Windows 基础安装与重复安装验收，再做人工扫码/权限测试。未完成的组合标为预览，不能对公众宣称已验证。

## 0.1.1 地区下载策略

默认中国版使用 npmmirror Node/npm 与清华 PyPI；国际版使用官方源。运行时 npm 锁文件仅重写已审核 registry 的 resolved 前缀，保留 integrity；网络/同步失败最多回退一次官方源。哈希或发布元数据不匹配不回退。两个 EXE 的程序和插件固定版本相同，默认 UI 都是中文，用户已有 locale 设置不覆盖。可选 uv 管理的 CPython 下载仍由 uv 官方上游机制处理。
