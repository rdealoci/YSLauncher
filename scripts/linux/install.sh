#!/usr/bin/env bash
set -e

BASE_URL="${1:-https://github.com/rdealoci/YSLauncher/releases/latest/download}"
OS="$(uname -s)"

if [ "$OS" = "Linux" ]; then
    echo "YSLauncher: Instalador para Linux"
    
    INSTALL_DIR="$HOME/.local/share/YSLauncher"
    BIN_DIR="$HOME/.local/bin"
    APPS_DIR="$HOME/.local/share/applications"
    ICONS_DIR="$HOME/.local/share/icons/hicolor/256x256/apps"
    
    mkdir -p "$INSTALL_DIR" "$BIN_DIR" "$APPS_DIR" "$ICONS_DIR"
    
    TMP_TAR="/tmp/YSLauncher-Linux.tar.gz"
    echo "Baixando YSLauncher..."
    curl -# -fL "${BASE_URL%/}/YSLauncher-Linux.tar.gz" -o "$TMP_TAR"
    
    echo "Extraindo arquivos..."
    tar -xzf "$TMP_TAR" -C "$INSTALL_DIR"
    rm -f "$TMP_TAR"
    chmod +x "$INSTALL_DIR/YSLauncher" 2>/dev/null || true
    
    if [ ! -f "$INSTALL_DIR/logo.png" ]; then
        curl -fsSL "https://raw.githubusercontent.com/rdealoci/YSLauncher/main/Assets/logo.png" -o "$INSTALL_DIR/logo.png" 2>/dev/null || true
    fi
    if [ -f "$INSTALL_DIR/logo.png" ]; then
        cp -f "$INSTALL_DIR/logo.png" "$ICONS_DIR/yslauncher.png" 2>/dev/null || true
    fi

    echo "Vinculando comandos globais 'yslauncher' e 'YSLauncher'..."
    ln -sf "$INSTALL_DIR/YSLauncher" "$BIN_DIR/yslauncher"
    ln -sf "$INSTALL_DIR/YSLauncher" "$BIN_DIR/YSLauncher"
    
    if [[ ":$PATH:" != *":$HOME/.local/bin:"* ]]; then
        for rc in "$HOME/.bashrc" "$HOME/.zshrc" "$HOME/.profile"; do
            if [ -f "$rc" ] && ! grep -q '\.local/bin' "$rc"; then
                echo 'export PATH="$HOME/.local/bin:$PATH"' >> "$rc"
            fi
        done
        export PATH="$HOME/.local/bin:$PATH"
    fi

    echo "Criando atalho no menu de aplicativos..."
    cat << EOF > "$APPS_DIR/yslauncher.desktop"
[Desktop Entry]
Name=YSLauncher
Comment=Launcher para Yandere Simulator
Exec=$INSTALL_DIR/YSLauncher
Icon=$INSTALL_DIR/logo.png
Terminal=false
Type=Application
Categories=Game;
EOF
    chmod +x "$APPS_DIR/yslauncher.desktop" 2>/dev/null || true
    update-desktop-database "$APPS_DIR" 2>/dev/null || true

    echo "YSLauncher instalado com sucesso."
    echo "Voce pode inicia-lo digitando 'yslauncher' no terminal"
    echo "ou procurando por 'YSLauncher' no seu menu de aplicativos."
    
    nohup "$INSTALL_DIR/YSLauncher" >/dev/null 2>&1 &
    exit 0

elif [ "$OS" = "Darwin" ]; then
    echo "Baixando YSLauncher para macOS..."
    TARGET_DIR="/Applications"
    if [ ! -w "$TARGET_DIR" ]; then
        TARGET_DIR="$HOME/Applications"
        mkdir -p "$TARGET_DIR"
    fi
    TMP_DMG="/tmp/YSLauncher.dmg"
    curl -# -fL "${BASE_URL%/}/YSLauncher.dmg" -o "$TMP_DMG"
    
    MOUNT_DIR="/tmp/YSLauncher-Mount"
    mkdir -p "$MOUNT_DIR"
    hdiutil detach "$MOUNT_DIR" -quiet 2>/dev/null || true
    hdiutil attach "$TMP_DMG" -nobrowse -quiet -mountpoint "$MOUNT_DIR"
    rm -rf "$TARGET_DIR/YSLauncher.app"
    cp -R "$MOUNT_DIR/YSLauncher.app" "$TARGET_DIR/"
    hdiutil detach "$MOUNT_DIR" -quiet 2>/dev/null || true
    rm -rf "$MOUNT_DIR" "$TMP_DMG"
    
    chmod -R +x "$TARGET_DIR/YSLauncher.app/Contents/MacOS" 2>/dev/null || true
    xattr -cr "$TARGET_DIR/YSLauncher.app" 2>/dev/null || true
    codesign --force --deep --sign - "$TARGET_DIR/YSLauncher.app" 2>/dev/null || true
    
    echo "YSLauncher instalado com sucesso em $TARGET_DIR/YSLauncher.app"
    open "$TARGET_DIR/YSLauncher.app"
    exit 0
else
    echo "Sistema operacional nao suportado pelo script: $OS"
    exit 1
fi
