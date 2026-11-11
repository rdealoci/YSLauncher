#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"

export PATH="$HOME/.dotnet:$PATH"

APP_NAME="YSLauncher"
DMG_NAME="YSLauncher-macOS-Universal.dmg"
BUNDLE_DIR="$PROJECT_DIR/bin/$APP_NAME.app"
PUBLISH_X64="$PROJECT_DIR/bin/publish-osx-x64"
PUBLISH_ARM64="$PROJECT_DIR/bin/publish-osx-arm64"
PUBLISH_UNIVERSAL="$PROJECT_DIR/bin/publish-universal"
DMG_STAGING="$PROJECT_DIR/bin/dmg_staging"
OUTPUT_DMG="$PROJECT_DIR/bin/$DMG_NAME"

echo "YSLauncher: macOS Universal 2 DMG Generator"

if [ ! -f "$PROJECT_DIR/Assets/icon.icns" ]; then
    echo "Generating icon.icns..."
    ICONSET_DIR="/tmp/yslauncher_icon.iconset"
    mkdir -p "$ICONSET_DIR"
    sips -z 16 16     "$PROJECT_DIR/Assets/logo.png" --out "$ICONSET_DIR/icon_16x16.png" >/dev/null
    sips -z 32 32     "$PROJECT_DIR/Assets/logo.png" --out "$ICONSET_DIR/icon_16x16@2x.png" >/dev/null
    sips -z 32 32     "$PROJECT_DIR/Assets/logo.png" --out "$ICONSET_DIR/icon_32x32.png" >/dev/null
    sips -z 64 64     "$PROJECT_DIR/Assets/logo.png" --out "$ICONSET_DIR/icon_32x32@2x.png" >/dev/null
    sips -z 128 128   "$PROJECT_DIR/Assets/logo.png" --out "$ICONSET_DIR/icon_128x128.png" >/dev/null
    sips -z 256 256   "$PROJECT_DIR/Assets/logo.png" --out "$ICONSET_DIR/icon_128x128@2x.png" >/dev/null
    sips -z 256 256   "$PROJECT_DIR/Assets/logo.png" --out "$ICONSET_DIR/icon_256x256.png" >/dev/null
    sips -z 512 512   "$PROJECT_DIR/Assets/logo.png" --out "$ICONSET_DIR/icon_256x256@2x.png" >/dev/null
    sips -z 512 512   "$PROJECT_DIR/Assets/logo.png" --out "$ICONSET_DIR/icon_512x512.png" >/dev/null
    sips -z 1024 1024 "$PROJECT_DIR/Assets/logo.png" --out "$ICONSET_DIR/icon_512x512@2x.png" >/dev/null
    iconutil -c icns "$ICONSET_DIR" -o "$PROJECT_DIR/Assets/icon.icns"
    rm -rf "$ICONSET_DIR"
fi

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

echo "Merging into Universal 2 binary..."
rm -rf "$PUBLISH_UNIVERSAL"
mkdir -p "$PUBLISH_UNIVERSAL"

lipo -create "$PUBLISH_X64/$APP_NAME" "$PUBLISH_ARM64/$APP_NAME" -output "$PUBLISH_UNIVERSAL/$APP_NAME"

for file in "$PUBLISH_X64"/*; do
    fname="$(basename "$file")"
    arm_file="$PUBLISH_ARM64/$fname"
    target="$PUBLISH_UNIVERSAL/$fname"
    
    if [[ "$fname" == "$APP_NAME" ]]; then
        continue
    elif [[ "$fname" == *.dylib ]]; then
        if [ -f "$arm_file" ]; then
            lipo -create "$file" "$arm_file" -output "$target" 2>/dev/null || cp "$file" "$target"
        else
            cp "$file" "$target"
        fi
    elif [ -d "$file" ]; then
        cp -R "$file" "$target"
    fi
done

echo "Verifying architecture:"
lipo -info "$PUBLISH_UNIVERSAL/$APP_NAME"

echo "Assembling $APP_NAME.app..."
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
else
    cp "$PROJECT_DIR/Assets/icon.icns" "$BUNDLE_DIR/Contents/Resources/icon.icns"
fi

if [ -f "$PROJECT_DIR/Assets/MacRuntime.tar.xz" ]; then
    cp "$PROJECT_DIR/Assets/MacRuntime.tar.xz" "$BUNDLE_DIR/Contents/Resources/MacRuntime.tar.xz"
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

if [ -f "$PROJECT_DIR/Platforms/Mac/launch_detached.c" ]; then
    clang -arch x86_64 -arch arm64 "$PROJECT_DIR/Platforms/Mac/launch_detached.c" -o "$BUNDLE_DIR/Contents/MacOS/launch_detached"
    chmod +x "$BUNDLE_DIR/Contents/MacOS/launch_detached"
fi

if [ -f "$PROJECT_DIR/Platforms/Mac/libnomicrophone.c" ]; then
    clang -dynamiclib -arch x86_64 -arch arm64 "$PROJECT_DIR/Platforms/Mac/libnomicrophone.c" -o "$BUNDLE_DIR/Contents/MacOS/libnomicrophone.dylib" -framework IOKit -framework CoreAudio -framework CoreFoundation -F/System/Library/PrivateFrameworks -framework TCC
    codesign -f -s - "$BUNDLE_DIR/Contents/MacOS/libnomicrophone.dylib"
fi

echo "Signing .app bundle (ad-hoc)..."
codesign --force --deep -s - "$BUNDLE_DIR"

echo "Generating DMG disk image..."
TEMP_DMG="/tmp/temp_${APP_NAME}.dmg"
rm -f "$TEMP_DMG" "$OUTPUT_DMG"

if [ -d "/Volumes/$APP_NAME" ]; then
    hdiutil detach "/Volumes/$APP_NAME" -force 2>/dev/null || true
    sleep 1
fi

hdiutil create -size 450m -fs HFS+ -volname "$APP_NAME" "$TEMP_DMG" >/dev/null

DEV_NAME=$(hdiutil attach -readwrite -noverify -noautoopen "$TEMP_DMG" | grep '^/dev/' | head -n 1 | awk '{print $1}')
MOUNT_DIR="/Volumes/$APP_NAME"

sleep 1

cp -R "$BUNDLE_DIR" "$MOUNT_DIR/"
ln -s /Applications "$MOUNT_DIR/Applications"

mkdir -p "$MOUNT_DIR/.background"
cp "$PROJECT_DIR/Assets/dmg_background.png" "$MOUNT_DIR/.background/background.png"
chflags hidden "$MOUNT_DIR/.background" 2>/dev/null || true
SetFile -a V "$MOUNT_DIR/.background" 2>/dev/null || true

echo "Configuring Finder window layout..."
osascript << APPLESCRIPT || true
tell application "Finder"
    tell disk "$APP_NAME"
        open
        set current view of container window to icon view
        set toolbar visible of container window to false
        set statusbar visible of container window to false
        set the bounds of container window to {140, 120, 1000, 632}
        set theViewOptions to the icon view options of container window
        set arrangement of theViewOptions to not arranged
        set icon size of theViewOptions to 128
        set background picture of theViewOptions to file ".background:background.png"
        set position of item "$APP_NAME.app" of container window to {172, 285}
        set position of item "Applications" of container window to {686, 285}
        close
        open
        update without registering applications
        delay 2
        close
    end tell
end tell
APPLESCRIPT

sync
sleep 2

hdiutil detach "$DEV_NAME" -force >/dev/null || hdiutil detach "$MOUNT_DIR" -force >/dev/null
sleep 1

echo "Compressing final DMG..."
hdiutil convert "$TEMP_DMG" -format ULMO -o "$OUTPUT_DMG" >/dev/null
rm -f "$TEMP_DMG"

echo "Build complete: $OUTPUT_DMG"
ls -lh "$OUTPUT_DMG"

mkdir -p "$PROJECT_DIR/dist"
cp -f "$OUTPUT_DMG" "$PROJECT_DIR/dist/"

if [ -d "$PROJECT_DIR/site" ]; then
    cp "$OUTPUT_DMG" "$PROJECT_DIR/site/"
fi
