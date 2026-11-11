# YSLauncher 

A fast, lightweight cross-platform launcher for Yandere Simulator on Windows, Linux, and macOS.

Built with C# and [Avalonia UI](https://avaloniaui.net/), targeting .NET 10.

---

## Features

- **Cross-Platform**:
  - **Windows (x64)**: Native portable single-file executable.
  - **Linux (x64)**: Integrated Wine/Proton runner management with Wayland and X11 support.
  - **macOS (Universal 2)**: Native Intel and Apple Silicon bundle with Wine-Crossover and Apple Metal DirectX acceleration (GPTK / D3DMetal).
- **Version Management & Archive**:
  - Browse and download over 500+ historical builds directly from Archive.org (2014–2026).
  - Organize and switch between multiple local versions seamlessly.
- **Save & Mod Protection**:
  - Automatic backup and restoration of Custom Mode and Posemod data across game updates.
- **Notifies you when there is a new update** 
- **Multilingual**
- *Support up to 12 languages*

---

## Download & Installation

### Linux & macOS (Quick Install - Recommended)
Install or update automatically with a single command:
```bash
curl -fsSL https://raw.githubusercontent.com/rdealoci/YSLauncher/main/scripts/install.sh | bash
```
- **Linux**: Installs to `~/.local/share/YSLauncher`, links `yslauncher` to `~/.local/bin`, and adds a desktop menu entry.
- **macOS**: Installs `YSLauncher.app` to `/Applications`, strips quarantine attributes, and signs the application.

### Standalone Downloads
Binaries are also available on the [Releases](https://github.com/rdealoci/YSLauncher/releases) page:
- **Windows (x64)**: `YSLauncher.exe` (Portable single-file, no installer required).
- **Linux (x64)**: `YSLauncher-linux` / `YSLauncher-Linux.tar.gz`.

- **In macOS**: `YSLauncher.dmg` (Intel & Apple Silicon [M Chips] ).

(⚠️WARNING⚠️)
After moving YSLauncher.app to Applications;
1. Press Command + Space;
2. Type "terminal" and press enter;
3. Paste the command below and press enter:

xattr -dr com.apple.quarantine "/Applications/YSLauncher.app"

And enjoy the game!🌟
---

## Disclaimer

YSLauncher is an independent, open-source fan-made project. It is not connected to the official development of Yandere Simulator.
