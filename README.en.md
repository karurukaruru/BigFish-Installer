# BigFish Installer

A Chinese-first, per-user Windows 10 22H2 / Windows 11 x64 deployment manager for DeepSeek Harness and dsh-im. This is an independent project, not an official DeepSeek/Tencent product. See [Chinese README](README.md).

Version 0.1.1 is an engineering preview. Windows self-contained EXEs can be cross-built; actual clean Windows desktop, Python, QR binding and paid model calls require manual acceptance. Never interpret the Linux test evidence as proof of Windows account integration.

- `release/BigFish-CN.exe`: China mirrors preferred (npmmirror, optional Tsinghua PyPI).
- `release/BigFish-Global.exe`: official sources.
- Both use Chinese UI and preserve existing DSH locale/configuration. Advanced options can change the download region.
- Mirrors retain the official pinned Node/uv hash and npm lock integrity. Integrity failures stop installation.

Run the EXE, click 一键安装, then configure your API key and authorized QQ/WeChat connections in local DSH settings. No developer runtime is needed by the end user. Closing BigFish stops its own DSH process.

Developer build: `pwsh -File scripts/build.ps1 -Setup -Region all`. Windows CI includes both channels and real installation/repeat checks. Inno Setup wrappers are built only on Windows. See docs/WINDOWS-ACCEPTANCE.md and docs/VALIDATION-0.1.1.md.

Code: MIT. Artwork: separate upstream attribution/non-commercial/share-alike terms; see assets/README.md. No public GitHub Release has been created by this handoff.
