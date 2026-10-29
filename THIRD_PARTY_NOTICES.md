# Third-party notices

BigFish Installer is independently developed deployment software. It does not claim authorship of DeepSeek Harness or its plugins, and implies no endorsement from upstream maintainers.

| Component | Upstream / owner | License and distribution |
| --- | --- | --- |
| DeepSeek Harness | https://github.com/deepseek-ai/deepseek-harness — Copyright 2026 DeepSeek | MIT. Downloaded during deployment; package LICENSE retained. Copy in `third-party/DSH-LICENSE.txt`. |
| dsh-im | https://github.com/xmanrui/dsh-im — Copyright 2026 xmanrui | MIT. Package notices retained; copy in `third-party`. |
| dsh-bili-scroll | https://github.com/c1overzz/dsh-bili-scroll — Copyright 2026 c1overzz | MIT. Its NOTICE and vendored bilibili-mcp MIT attribution are retained. |
| Node.js | https://nodejs.org — Node.js contributors | MIT with third-party notices in the official ZIP's LICENSE. Downloaded unchanged; not included in BigFish EXE. |
| pnpm | https://github.com/pnpm/pnpm | MIT; downloaded as pinned npm dependency. |
| uv | https://github.com/astral-sh/uv | MIT OR Apache-2.0; optional official PyPI wheel. Its complete wheel is retained in downloads and license texts preserved under runtime/uv notices. |
| CPython | https://www.python.org | Python Software Foundation License; optional uv-managed runtime retains its distribution notices. |
| .NET / WPF | https://github.com/dotnet/runtime and https://github.com/dotnet/wpf | MIT plus runtime third-party notices. Self-contained application bundles these components. Copies in `third-party/DOTNET-*` and `third-party/WPF-LICENSE.txt`. |
| xUnit / Microsoft.NET.Test.Sdk | Official NuGet packages | Build/test dependencies, not shipped in end-user executable. See package license metadata. |
| Inno Setup | https://jrsoftware.org/isinfo.php | Separate installer build tool under its own license; generated installers may be distributed. No compiler is bundled here. |

The MIT license at the repository root applies to BigFish's original code only. Downloaded dependency trees retain their package license files. Review the npm lockfile and upstream third-party notices before distributing an offline dependency bundle; this project currently downloads dependencies from their audited sources during installation.

Artwork references and their AI-generated derivative are distributed only under their separate upstream terms in assets/. Full original license texts accompany bundled runtimes or are retained in downloaded dependency packages.

## 文档插画和 Inno 简体中文

assets/ 是 GitHub 文档插画与参考图，单独采用 CC BY-NC-SA 4.0 及上游署名义务；并非 MIT 代码的一部分，详见 assets/README.md 和 assets/licenses/。

installer/Languages/ChineseSimplified.isl 来自 jrsoftware/issrc 官方仓库，保留原文件声明及同目录 INNO-LICENSE.txt。该语言文件仅用于生成中文 Inno 安装器。
