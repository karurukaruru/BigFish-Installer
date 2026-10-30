# BigFish Installer

[中文版](README.md)

**Deploy DeepSeek Harness on Windows without command-line setup.** Windows 10 22H2 / Windows 11 x64. The China edition is Simplified Chinese and prefers China mirrors. The Global edition uses official sources and can switch between Simplified Chinese and English (United States).

## Download

**[Go to Releases](https://github.com/karurukaruru/BigFish-Installer/releases)** and choose **one** EXE in the latest release's **Assets**:

| Edition | Download | Default sources |
| --- | --- | --- |
| China | **`BigFish-CN.exe`** | China mirrors first |
| Global | **`BigFish-Global.exe`** | Official sources first |

Double-click the EXE, select **一键安装** (one-click install), and let it download the dependencies. No separate .NET or Node.js installation is required. An internet connection is necessary. To actually chat, configure **your own model API key**. QQ and WeChat also require the respective platform permissions and authorization.

**Version 0.1.2:** Windows CI has exercised basic DSH/IM installation, HTTP/RPC readiness and repeated installation, but clean end-user Windows machines, real account QR pairing and paid model calls still require manual validation. The current EXEs are unsigned; do not disable security protections to run them.

Independent third-party project, not an official DeepSeek, Tencent or Bilibili product.

[Help](docs/TROUBLESHOOTING.md) · [Validation details](docs/VALIDATION-0.1.2.md) · [Windows acceptance](docs/WINDOWS-ACCEPTANCE.md)

Code: MIT. Art assets carry separate licensing terms; see [assets/README.md](assets/README.md).
