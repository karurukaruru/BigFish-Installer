# BigFish Installer

![BigFish Installer](assets/github-header.png)

**DeepSeek 大肥鱼，一键部署。** Windows 10 22H2 / Windows 11（x64），中文界面。

## ⬇️ 下载

### [点击进入 Releases 下载两个版本之一](https://github.com/karurukaruru/BigFish-Installer/releases)

| 选择 | 文件名 | 下载源 |
| --- | --- | --- |
| 🇨🇳 中国版（推荐国内用户） | **`BigFish-CN.exe`** | 国内镜像优先 |
| 🌐 国际版 | **`BigFish-Global.exe`** | 官方源优先 |

**直接在最新 Preview 的 Assets 中点击对应的 EXE 下载。** 不要下载 GitHub 自动生成的 *Source code.zip*，那是源代码，不是安装程序。

## 🚀 三步使用

1. 下载后**双击运行 EXE**，在窗口中点击「一键安装」。
2. 程序自动配置所需的 Node、DeepSeek Harness 和 QQ/微信消息插件（需要联网，不用自行安装开发环境）。
3. 打开 DSH 页面，填写**你自己的模型 API Key**；如需 QQ/微信，再完成平台要求的授权或扫码。

运行管理器、启动/停止服务、修复安装，都在图形界面完成。**两个 EXE 只需选一个**，不是需要同时安装的两个文件。

> **v0.1.1 工程预览**：Windows CI 已通过基础安装、插件加载、HTTP/RPC 检查和重复安装测试；**尚未完成全新用户电脑、真实 QQ/微信扫码和模型调用的人工验收**。当前 EXE 未签名，Windows 可能提示 SmartScreen；不要为此关闭系统安全保护。

BigFish 是 **独立第三方工具**，并非 DeepSeek、腾讯或 Bilibili 官方产品。软件安装完成不等于已获得平台权限，也不包含模型 API 额度。

---

遇到问题：[故障排查](docs/TROUBLESHOOTING.md) · [开发与构建](docs/VALIDATION-0.1.1.md) · [Windows 验收](docs/WINDOWS-ACCEPTANCE.md) · [English](README.en.md)

代码采用 MIT 许可；图片素材有单独授权限制，见 [assets/README.md](assets/README.md)。
