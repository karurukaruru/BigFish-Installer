# 工程说明

核心库 `net10.0` 不引用 WPF，GUI `net10.0-windows` 仅管理交互与绑定，CLI 复用同一安装引擎。异步命令禁止重入，可取消操作在后端执行；跨线程日志/进度通过 Dispatcher/Progress 回到 UI。

| 模块 | 职责 |
| --- | --- |
| SystemDetector / DependencyResolver | 实际执行版本探测、PATH/常见位置、权限/目录与依赖决策 |
| NodeInstaller / VerifiedDownloader | 官方私有运行时、固定哈希、有界重试、临时解压后切换 |
| DshInstaller / PackageVerifier | npm 元数据核验、完整运行时 lockfile、CLI 执行、只读复用系统 DSH |
| PluginManager | 固定插件、profile 配置备份、上游安装与动态 import 检测 |
| ServiceManager / HealthChecker | 私有服务生命周期、Job Object、loopback 端口、303 Cookie 认证、插件 RPC |
| ConfigurationManager / InstallationStateStore | 不覆盖已有配置、原子检查点、版本化 state |
| LogManager / ErrorClassifier | 完整行脱敏、诊断与用户可执行的恢复提示 |
| OptionalBiliInstaller | 可选 uv/Python/依赖和 HTTP 就绪，不运行内容任务 |
| UpdateManager | 只读版本比较，不安装未审核组合 |
| InstallationEngine | 编排阶段和检查点，可注入 IInstallOperations 模拟故障 |

## 持久数据

```
%LOCALAPPDATA%\BigFish
  state.json
  releases\<generation>\node_modules
  runtime\node-v24.22.1-win-x64
  dsh-home\profiles\web
  dsh-home\integrations
  downloads
  backups
  logs
```

活动代以 state 指针提交，不把大目录原地覆盖。外部精确版本 DSH 可保存为本代的只读入口引用。取消会停止进程；失败代保留诊断，重试新建。没有自动删除旧代或回滚用户历史数据。

## 已知工程限制

- 下载中断后重新下载，而非 HTTP Range 字节续传；完整校验缓存和阶段状态支持重复执行恢复。
- .NET/npm 组件单代安装具备原子状态切换；上游 profile 插件安装不是覆盖全部文件系统的 ACID 事务。配置清单预备份，上游 pnpm 事务失败后由复检恢复。
- 不接管外部正在运行的 DSH，不复制其历史配置/密钥；只复用精确匹配的程序文件。
- B站 Python 依赖遵循上游 requirements 范围，尚无跨平台完整冻结镜像；实验性状态直到 Windows 实测。
- GUI 关闭即停服务，无托盘常驻、Windows Service、自启动或云端后台。
- 发布流程产出草稿预发布，未做代码签名；正式发布必须由维护者完成验收和签名策略。
