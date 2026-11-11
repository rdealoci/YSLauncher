#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"

export PATH="$HOME/.cargo/bin:$PATH"

APP_NAME="YSLauncher"
BUNDLE_DIR="$PROJECT_DIR/bin/$APP_NAME.app"
PUBLISH_X64="$PROJECT_DIR/bin/publish-osx-x64"
PUBLISH_ARM64="$PROJECT_DIR/bin/publish-osx-arm64"
PUBLISH_UNIVERSAL="$PROJECT_DIR/bin/publish-universal"
OUTPUT_TARGZ="$PROJECT_DIR/bin/${APP_NAME}-macOS-Universal.tar.gz"

echo "YSLauncher: Linux -> macOS Universal 2 Build"

echo "Compiling osx-x64 (Intel)..."
rm -rf "$PUBLISH_X64"
dotnet publish "$PROJECT_DIR/YSLauncher.csproj" \
    -c Release \
    -r osx-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -o "$PUBLISH_X64" >/dev/null

echo "Compiling osx-arm64 (Apple Silicon)..."
rm -rf "$PUBLISH_ARM64"
dotnet publish "$PROJECT_DIR/YSLauncher.csproj" \
    -c Release \
    -r osx-arm64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -o "$PUBLISH_ARM64" >/dev/null

echo "Merging binaries with llvm-lipo into Universal 2..."
rm -rf "$PUBLISH_UNIVERSAL"
mkdir -p "$PUBLISH_UNIVERSAL"

llvm-lipo -create "$PUBLISH_X64/$APP_NAME" "$PUBLISH_ARM64/$APP_NAME" -output "$PUBLISH_UNIVERSAL/$APP_NAME"

for file in "$PUBLISH_X64"/*; do
    fname="$(basename "$file")"
    arm_file="$PUBLISH_ARM64/$fname"
    target="$PUBLISH_UNIVERSAL/$fname"
    
    if [[ "$fname" == "$APP_NAME" ]]; then
        continue
    elif [[ "$fname" == *.dylib ]]; then
        if [ -f "$arm_file" ]; then
            llvm-lipo -create "$file" "$arm_file" -output "$target" 2>/dev/null || cp "$file" "$target"
        else
            cp "$file" "$target"
        fi
    elif [ -d "$file" ]; then
        cp -R "$file" "$target"
    fi
done

echo "Verifying architecture:"
llvm-lipo -info "$PUBLISH_UNIVERSAL/$APP_NAME"

echo "Assembling $APP_NAME.app and Info.plist..."
rm -rf "$BUNDLE_DIR"
mkdir -p "$BUNDLE_DIR/Contents/MacOS"
mkdir -p "$BUNDLE_DIR/Contents/Resources"

cp -R "$PUBLISH_UNIVERSAL/"* "$BUNDLE_DIR/Contents/MacOS/"
rm -f "$BUNDLE_DIR/Contents/MacOS/"*.pdb
rm -f "$BUNDLE_DIR/Contents/MacOS/libmscordbi.dylib"
rm -f "$BUNDLE_DIR/Contents/MacOS/libmscordaccore.dylib"
rm -f "$BUNDLE_DIR/Contents/MacOS/libclrgcexp.dylib"
rm -f "$BUNDLE_DIR/Contents/MacOS/Avalonia.Win32"*
rm -f "$BUNDLE_DIR/Contents/MacOS/Avalonia.X11"*
rm -rf "$BUNDLE_DIR/Contents/MacOS/Assets"

if [ -f "$PROJECT_DIR/Assets/icon_official.icns" ]; then
    cp "$PROJECT_DIR/Assets/icon_official.icns" "$BUNDLE_DIR/Contents/Resources/icon.icns"
elif [ -f "$PROJECT_DIR/Assets/icon.icns" ]; then
    cp "$PROJECT_DIR/Assets/icon.icns" "$BUNDLE_DIR/Contents/Resources/icon.icns"
fi

if [ -f "$PROJECT_DIR/Assets/MacRuntime.tar.xz" ]; then
    cp "$PROJECT_DIR/Assets/MacRuntime.tar.xz" "$BUNDLE_DIR/Contents/Resources/MacRuntime.tar.xz"
fi

if [ -f "$PROJECT_DIR/Platforms/Mac/launch_detached" ]; then
    cp "$PROJECT_DIR/Platforms/Mac/launch_detached" "$BUNDLE_DIR/Contents/MacOS/launch_detached"
    chmod +x "$BUNDLE_DIR/Contents/MacOS/launch_detached"
fi

if [ -f "$PROJECT_DIR/Platforms/Mac/libnomicrophone.dylib" ]; then
    cp "$PROJECT_DIR/Platforms/Mac/libnomicrophone.dylib" "$BUNDLE_DIR/Contents/MacOS/libnomicrophone.dylib"
fi

cat << 'PLIST_EOF' > "$BUNDLE_DIR/Contents/Info.plist"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleExecutable</key>
    <string>YSLauncher</string>
    <key>CFBundleIconFile</key>
    <string>icon.icns</string>
    <key>CFBundleIdentifier</key>
    <string>com.yanderesimulator.launcher</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>YSLauncher</string>
    <key>CFBundleDisplayName</key>
    <string>YSLauncher</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0.0</string>
    <key>CFBundleVersion</key>
    <string>1.0.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.15</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSSupportsAutomaticGraphicsSwitching</key>
    <true/>
</dict>
</plist>
PLIST_EOF

echo -n "APPL????" > "$BUNDLE_DIR/Contents/PkgInfo"
chmod +x "$BUNDLE_DIR/Contents/MacOS/YSLauncher"

echo "Signing .app bundle (ad-hoc)..."
if command -v rcodesign >/dev/null 2>&1; then
    rcodesign sign "$BUNDLE_DIR"
fi

echo "Packaging TAR.GZ archive..."
tar -czf "$OUTPUT_TARGZ" -C "$PROJECT_DIR/bin" "$APP_NAME.app"

OUTPUT_DMG="$PROJECT_DIR/bin/${APP_NAME}.dmg"
if command -v mkfs.hfsplus >/dev/null 2>&1 && command -v dmg >/dev/null 2>&1; then
    echo "Generating DMG disk image (HFS+ UDZO)..."
    RAW_IMG="/tmp/${APP_NAME}_build.img"
    MNT_DIR="/tmp/${APP_NAME}_build_mnt"
    rm -f "$RAW_IMG" "$OUTPUT_DMG"
    mkdir -p "$MNT_DIR"

    dd if=/dev/zero of="$RAW_IMG" bs=1M count=250 status=none
    mkfs.hfsplus -v "$APP_NAME" "$RAW_IMG" >/dev/null
    sudo mount -t hfsplus -o loop "$RAW_IMG" "$MNT_DIR"
    sudo cp -a "$BUNDLE_DIR" "$MNT_DIR/"
    sudo ln -s /Applications "$MNT_DIR/Applications"
    sudo mkdir -p "$MNT_DIR/.background"
    if [ -f "$PROJECT_DIR/Assets/dmg_background.png" ]; then
        sudo cp "$PROJECT_DIR/Assets/dmg_background.png" "$MNT_DIR/.background/background.png"
        sudo cp "$PROJECT_DIR/Assets/dmg_background.png" "$MNT_DIR/.background.png"
    fi

    sudo chmod -R 755 "$MNT_DIR/$APP_NAME.app"
    sudo chmod +x "$MNT_DIR/$APP_NAME.app/Contents/MacOS/$APP_NAME"
    if [ -f "$MNT_DIR/$APP_NAME.app/Contents/MacOS/launch_detached" ]; then
        sudo chmod +x "$MNT_DIR/$APP_NAME.app/Contents/MacOS/launch_detached"
    fi
    sudo chmod +x "$MNT_DIR/$APP_NAME.app/Contents/MacOS/"*.dylib 2>/dev/null || true

    if command -v rcodesign >/dev/null 2>&1; then
        sudo rcodesign sign "$MNT_DIR/$APP_NAME.app" >/dev/null 2>&1 || true
    fi

    if [ -f "$PROJECT_DIR/scripts/mac/make_dmg_dsstore.py" ]; then
        sudo python3 "$PROJECT_DIR/scripts/mac/make_dmg_dsstore.py" "$MNT_DIR" "$APP_NAME" >/dev/null 2>&1 || true
    fi

    sync
    sudo umount "$MNT_DIR"
    rmdir "$MNT_DIR"

    dmg dmg "$RAW_IMG" "$OUTPUT_DMG" >/dev/null 2>&1
    rm -f "$RAW_IMG"
    chmod 644 "$OUTPUT_DMG" 2>/dev/null || true
fi

mkdir -p "$PROJECT_DIR/dist"
cp -f "$OUTPUT_TARGZ" "$PROJECT_DIR/dist/"
if [ -f "$OUTPUT_DMG" ]; then
    cp -f "$OUTPUT_DMG" "$PROJECT_DIR/dist/"
fi
rm -rf "$PROJECT_DIR/dist/$APP_NAME.app"
cp -R "$BUNDLE_DIR" "$PROJECT_DIR/dist/"

if [ -d "$PROJECT_DIR/site" ]; then
    cp -f "$OUTPUT_TARGZ" "$PROJECT_DIR/site/"
    if [ -f "$OUTPUT_DMG" ]; then
        cp -f "$OUTPUT_DMG" "$PROJECT_DIR/site/"
    fi
fi

echo ""
echo "Build complete."
echo "Bundle .app: $BUNDLE_DIR"
if [ -f "$OUTPUT_DMG" ]; then
    echo "DMG package: $OUTPUT_DMG"
fi
echo "TAR package: $OUTPUT_TARGZ"
