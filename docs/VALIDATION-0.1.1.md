# 0.1.1 验证记录

记录日期：2026-10-29（新加坡时间）。执行平台 Linux/Ubuntu x64；.NET SDK 10.0.403，Node 24.22.1。未提供 Windows 桌面环境。

## 本次实际验证

| 项目 | 结果 | 证据 |
| --- | --- | --- |
| 单元与集成测试 | 55 通过、0 失败、0 跳过 | evidence/tests-0.1.1-linux.trx |
| Solution Release 编译 | 通过，0 警告、0 错误 | Core/CLI/WPF/Tests 实际编译 |
| 中国/国际 EXE 发布 | 两版均成功交叉发布 | evidence/windows-build-0.1.1.json |
| 自包含配置 | includedFrameworks，包含 .NETCore/WPF 10.0.14 | evidence/published-0.1.1-runtimeconfig.json |
| PE 架构 | 两版实际检查 PE32+ / x64 | 构建 JSON 中字节数与 SHA-256 |
| Actions YAML、项目 XML/XAML、兼容 JSON | 解析通过，WPF XAML 编译通过 | 源码与实际编译 |

新增自动化覆盖中国/国际源决策、镜像锁文件保留 integrity、镜像网络回退、篡改内容拒绝回退、元数据同步失败回退、发布哈希不符停止、首次中文不覆盖已有语言、工作区保护、停止输出异常仍释放锁。

这些测试包含模拟安装和本机 HTTP 夹具，也包含真实 Node 子进程。它们不能证明 Windows 桌面启动或扫码成功。历史版本的真实 DSH 验证另见 VALIDATION.md 与旧 evidence；本次真实上游安装结果以新增 acceptance-0.1.1-*.json 是否存在及内容为准。

## 待完成或未验证

- 干净 Windows 10/11 标准用户双击、无任何 .NET/Node 环境的最终安装验收。
- Windows WPF 布局、进程树/Job、Portable Node 实际运行、目录权限和恢复。
- Windows 可选 uv/CPython 安装、B站扫码与任务。
- DeepSeek API Key/余额与真实模型调用，QQ/微信授权与真实聊天。
- Inno Setup 双渠道引导安装包、签名、远程 GitHub Actions/Release。

当前交付为工程预览；不能声称用户最高验收标准已全部达成。按 WINDOWS-ACCEPTANCE.md 在真实机器执行并记录证据，才可提升发布结论。

## 本次真实 DSH 双下载源部署（Linux）

中国源与国际源分别从空白应用目录执行真实安装、启动、停止、重新创建 controller 和重复安装，均退出码 0。使用实际 Node/npm、DSH、pnpm 和 dsh-im 包，没有用模拟安装替代。

| 检查 | 中国源 | 国际源 |
| --- | --- | --- |
| DSH CLI + 本机 HTTP 认证 | 通过 | 通过 |
| dsh-im 实际 RPC | 通过 | 通过 |
| 当前界面语言 | zh | zh |
| 重复安装复用活动代 | 通过 | 通过 |
| 配置哨兵保留 | 通过 | 通过 |
| 停止进程 | 通过 | 通过 |
| QQ/微信真实绑定 | 未测试，0 个绑定 | 未测试，0 个绑定 |

原始报告：evidence/acceptance-0.1.1-cn-linux.json 与 evidence/acceptance-0.1.1-global-linux.json。国际源运行时使用了机器共享的 pnpm 内容缓存；两次应用安装目录均为空白，不等于在两台全新 Windows 上测试。
