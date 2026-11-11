#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"

export PATH="$HOME/.dotnet:$PATH"

APP_NAME="YSLauncher"
BUNDLE_DIR="$PROJECT_DIR/bin/$APP_NAME.app"
PUBLISH_X64="$PROJECT_DIR/bin/publish-osx-x64"
PUBLISH_ARM64="$PROJECT_DIR/bin/publish-osx-arm64"
PUBLISH_UNIVERSAL="$PROJECT_DIR/bin/publish-universal"

echo "YSLauncher: macOS App Bundle Build"

echo "Compiling osx-x64..."
rm -rf "$PUBLISH_X64"
dotnet publish "$PROJECT_DIR/YSLauncher.csproj" \
    -c Release \
    -r osx-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -o "$PUBLISH_X64"

echo "Compiling osx-arm64..."
rm -rf "$PUBLISH_ARM64"
dotnet publish "$PROJECT_DIR/YSLauncher.csproj" \
    -c Release \
    -r osx-arm64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -o "$PUBLISH_ARM64"

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

codesign --force --deep -s - "$BUNDLE_DIR"
echo "Build complete: $BUNDLE_DIR"
