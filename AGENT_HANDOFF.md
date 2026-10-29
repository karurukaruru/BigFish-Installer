# BigFish Installer 0.1.1 — Agent 交接

交接日期：2026-10-29（新加坡时间）。默认语言：中文。

## 当前结论

这是可构建的 Windows x64 工程预览，不能称为已完成干净 Windows 一键安装最终验收。源代码、测试、WPF、自包含发布与 Windows CI 已实现。没有真实扫码成功或模型调用成功的证据，不允许把模拟测试写成真实登录通过。

## 接手入口

打开 `source/BigFish.sln`。`source/README.md` 为中文用户说明，`source/docs/WINDOWS-ACCEPTANCE.md` 为验收表，`source/docs/VALIDATION-0.1.1.md` 为本次实际验证记录。根目录 `release/` 为这次构建产物，详见其 SHA256SUMS.txt。

- `BigFish-CN.exe`：中国源优先；Node/npm 为 npmmirror，可选 Python 包为清华 PyPI。
- `BigFish-Global.exe`：国际官方源。
- 两版 UI 默认中文，首次 DSH 设置中文；已有配置保留。高级选项可切换源。
- 代码 MIT；`assets/` 角色参考及派生头图为 CC BY-NC-SA 4.0 与上游保留声明，不能全部按 MIT 分发。署名见 assets/README.md。

## 固定组合与关键限制

Node 24.22.1；DSH 0.2.1-rc.2；pnpm 11.28.5；dsh-im 5.7.0；可选 dsh-bili-scroll 2.3.0（维护仓库 c1overzz/dsh-bili-scroll）；uv 0.13.6。全部直接包来源与完整性在 compatibility/versions.json，DSH/pnpm 传递依赖锁在 runtime-package-lock.json。

镜像只改变下载位置，保留官方审核哈希与 npm integrity。网络错误有限回退官方；完整性错误停止。部分可选 CPython 下载仍需 uv 官方 GitHub 下载链路。插件传递依赖及 Python requirements 范围尚非完整离线锁包方案。

不修改系统 PATH；执行 node.exe + JS 入口，ArgumentList 传参；隔离 DSH_HOME 和 Web profile；配置先备份；已验证组件复用；关闭 BigFish 停止自有进程。3080 占用保留现有程序，由操作系统分配其他本机端口。

## GitHub 与上传状态

GitHub 账户已连接；当前工具可管理已有仓库，但没有创建新仓库能力。`karurukaruru/BigFish-Installer` 查询返回 404，未向其他项目写入，未推送、未创建线上 Release，也未运行远程 Actions。用户授权的替代交付为 Google Drive ZIP + 本文。

后续有 GitHub 创建能力时，可建立 BigFish-Installer 仓库并推送 source；若包中含 git bundle，可 clone bundle 恢复。不要把当前未完成的 Windows 验收写进 Release 为已通过。

## 构建与正式发布

Windows 开发机安装 .NET 10 SDK、Node 24（自动化子进程测试用）、Inno Setup 6。最终用户不需要这些 SDK。

```powershell
pwsh -File scripts/build.ps1 -Setup -Region all
pwsh -File scripts/windows-acceptance.ps1 -Region cn
pwsh -File scripts/windows-acceptance.ps1 -Region global
```

Windows 成功构建后 Setup 位于 artifacts/release/BigFishSetup-CN.exe 与 BigFishSetup-Global.exe。本交接只交付实际已构建的直接运行 EXE，Linux 上未构建 Inno Setup 引导包。标签 v0.1.1 触发 Windows 草稿预发布工作流；先完成人工验收再公开。

## 必须继续验证

1. 全新 Windows 11 标准用户，无 Node/npm/Python/Docker/.NET，双击中国版和国际版分别安装；另测 Windows 10 22H2。
2. WPF 实际布局、中文/空格路径、无终端窗口、Job 对象清理、取消、重启恢复、配置和历史会话不丢失。
3. 旧 Node、PATH 缺失、网络中断、镜像未同步、3080 冲突和修复流程。
4. 自己输入模型 API Key，验证有效/无效/余额不足；不要上传凭据或聊天记录。
5. QQ 官方机器人扫码或凭据、微信权限与扫码、真实消息往返、重启后保持；只能报告实际获准的能力。
6. 可选 B站 Windows uv/Python 导入、扫码、手动任务；评论/回复/私信默认关闭。
7. Inno 双渠道安装和卸载、Runtime 全缺失 VM、Authenticode 签名（当前未签名）、远程 CI/Release。

没有完成的功能：旧工作区凭据自动迁移、HTTP Range 字节续传、离线全依赖包、自动扫码、系统级后台服务。当前实现为状态检查点恢复与校验缓存，失败下载安全重新下载。

## 本次确认通过的验证

55 项自动化测试通过（0 失败/0 跳过）；Solution Release 构建 0 警告/0 错误；中国版与国际版自包含 Windows x64 EXE 均交叉发布成功。当前代码在 Linux 上分别真实安装两种源的 DSH/IM，HTTP 认证、RPC、中文语言、重复安装、配置保留与停止均通过，证据位于 source/docs/evidence/acceptance-0.1.1-*.json。社交绑定均为 0，未扫码，未调用模型。Windows 端结论依旧待验收。

## Drive 分包说明

上传入口单文件限制为 100 MiB，因此交付为 `BigFish-0.1.1-CN-full.zip` 与 `BigFish-0.1.1-Global-full.zip` 两份标准 ZIP。每份均含完整 source/、Git bundle、交接说明和许可证；release/ 仅包含该地区对应的 EXE。两份源码相同，选择一份即可开发或构建两个渠道。普通用户选择自己地区的一份解压即可。
