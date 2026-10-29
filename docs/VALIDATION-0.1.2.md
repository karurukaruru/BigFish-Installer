# 0.1.2 修复与验证记录

日期：2026-10-29。本版针对用户报告的“运行有问题”及安装/配置入口缺失进行修复。

## 已确认的代码缺陷

- 进度条 Value 默认是 TwoWay，旧版绑定 ProgressPercent 的 private setter，可能在真实 DataContext 解析时抛出 InvalidOperationException。旧窗口自测没有 DataContext，遗漏了此路径。现在明确 OneWay，自测绑定真实 MainViewModel 并逐页加载，要求零绑定错误。
- 原窗口关闭逻辑在关闭回调内可能同步再次 Close；现在延迟回到 Dispatcher 执行，并保存未处理错误的脱敏诊断。
- 旧中文模板使用 zh-CN，而内置语言 ID 是 zh；在英文 Windows 上可能回退英文。新模板为 zh，仅自动迁移旧安装器原样模板并先备份，其他用户设置保留。
- 仅创建文件目录没有在 DSH 中注册工作区；现在通过官方 workspace/create 注册，保留目录内容。
- GUI 原“配置模型”只是指引。现在提供 PasswordBox，通过官方 authenticated credentials API 保存并回读 configured 状态；不是自行写凭据 YAML。输入后清空，不调用付费模型。

## 当前本地验证

- .NET 10 Solution Release 编译通过，0 警告/0 错误。
- 68 个单元/集成用例通过，0 失败/0 跳过；涵盖真实 TCP HTTP 认证、密钥写入确认、外部凭据保护、自定义引用保留、工作区、取消、响应关联和拒绝泄露密钥。
- Windows 实测证据以本次 GitHub CI 的 wpf-self-test.json 和 windows-engine.json 为准，不能用历史 0.1.1 结果代替。
- 人工扫码、真实模型请求、余额与干净 Windows VM 仍待用户验收。

## 安装器的边界

GUI 负责安装、填写密钥和打开绑定入口；聊天、模型高级参数与扫码使用 DSH / dsh-im 现有页面。没有新增聊天后端或自研 QQ/微信协议。
