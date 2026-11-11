# YSLauncher 

A fast, lightweight cross-platform launcher and version manager for Yandere Simulator on Windows, Linux, and macOS.

Built with C# and [Avalonia UI](https://avaloniaui.net/), targeting .NET 10.

---

## Features

- **Cross-Platform**:
  - **Windows (x64)**: Native portable single-file executable.
  - **Linux (x64)**: Integrated Wine/Proton runner management with Wayland and X11 support.
  - **macOS (Universal 2)**: Native Intel and Apple Silicon bundle with Wine-Crossover and Apple Metal DirectX acceleration (GPTK / D3DMetal).
- **Version Management & Archive**:
  - Browse and download over 500+ historical builds directly from Archive.org (2014–2026).
  - Fast local build detection via Unity scene data (`level0`).
  - Organize and switch between multiple local versions seamlessly.
- **Save & Mod Protection**:
  - Automatic backup and restoration of Custom Mode data across game updates.
  - Non-destructive PoseMod integration and save preservation.
- **Native Notifications**:
  - Native OS notifications (Windows WinRT Toast, Linux notify-send, macOS Notification Center) for official updates and blog posts.
- **Multilingual**:
  - Built-in localized interface supporting 14 languages.

---

## Download & Installation

### Linux & macOS (Quick Install)
Install or update automatically with a single command:
```bash
curl -fsSL https://raw.githubusercontent.com/rdealoci/YSLauncher/main/scripts/install.sh | bash
```
- **Linux**: Installs to `~/.local/share/YSLauncher`, links `yslauncher` to `~/.local/bin`, and adds a desktop menu entry.
- **macOS**: Installs `YSLauncher.app` to `/Applications`, strips quarantine attributes, and signs the application.

### Standalone Downloads
Binaries are also available on the [Releases](https://github.com/rdealoci/YSLauncher/releases) page:
- **Windows (x64)**: `YSLauncher.exe` (Portable single-file, no installer required).
- **macOS (Universal 2)**: `YSLauncher.dmg` (Intel & Apple Silicon).
- **Linux (x64)**: `YSLauncher-linux` / `YSLauncher-Linux.tar.gz`.

---

## Building from Source

### Prerequisites
- [.NET 10.0 SDK](https://dotnet.microsoft.com/)
- Platform build dependencies:
  - Linux: standard build tools (`gcc`, `clang`, `tar`)
  - macOS: Xcode Command Line Tools (`clang`, `codesign`)

### Build Single Binary
```bash
dotnet build YSLauncher.csproj -c Release
```

### Build Distribution Packages
To compile self-contained single-file packages for all supported platforms:
```bash
./scripts/build_all.sh
```
Or build specifically for your target platform:
```bash
./scripts/build_all.sh --win
./scripts/build_all.sh --linux
./scripts/build_all.sh --mac
```

---

## Disclaimer

YSLauncher is an independent, open-source fan-made project. It is not affiliated with, endorsed by, or connected to YandereDev or the official development of Yandere Simulator.
