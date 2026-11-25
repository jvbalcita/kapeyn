#!/bin/bash
# Builds dist/Kapeyn.app (menu bar app) from src/main.swift.
# Needs the Swift compiler from the free Xcode Command Line Tools:  xcode-select --install
set -euo pipefail
cd "$(dirname "$0")"

if ! command -v swiftc >/dev/null 2>&1; then
    echo "The Swift compiler isn't installed yet."
    echo "Run:  xcode-select --install   (free, from Apple), then run this script again."
    exit 1
fi

APP="dist/Kapeyn.app"
OBJ="obj"
SRC=(src/*.swift)
FLAGS=(-O -swift-version 5 -module-name Kapeyn)

rm -rf "$APP" "$OBJ"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$OBJ"

echo "Compiling..."
if swiftc "${FLAGS[@]}" -target arm64-apple-macos12 -o "$OBJ/Kapeyn-arm64" "${SRC[@]}" 2>"$OBJ/arm64.log" \
   && swiftc "${FLAGS[@]}" -target x86_64-apple-macos12 -o "$OBJ/Kapeyn-x86_64" "${SRC[@]}" 2>"$OBJ/x86_64.log"; then
    lipo -create -output "$OBJ/Kapeyn" "$OBJ/Kapeyn-arm64" "$OBJ/Kapeyn-x86_64"
    echo "Built a universal app (Apple silicon + Intel)."
else
    echo "Universal build not available here; building for this Mac only."
    cat "$OBJ/arm64.log" "$OBJ/x86_64.log" 2>/dev/null | head -40 || true
    swiftc "${FLAGS[@]}" -o "$OBJ/Kapeyn" "${SRC[@]}"
fi
cp "$OBJ/Kapeyn" "$APP/Contents/MacOS/Kapeyn"

cp ../help/help.md "$APP/Contents/Resources/help.md" # the in-app guide, shared with Windows
cp ../LICENSE "$APP/Contents/Resources/LICENSE"

echo "Making icon..."
"$OBJ/Kapeyn" --export-iconset "$OBJ/Kapeyn.iconset"
iconutil -c icns "$OBJ/Kapeyn.iconset" -o "$APP/Contents/Resources/Kapeyn.icns"

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>               <string>Kapeyn</string>
    <key>CFBundleDisplayName</key>        <string>Kapeyn</string>
    <key>CFBundleIdentifier</key>         <string>com.kapeyn.Kapeyn</string>
    <key>CFBundleExecutable</key>         <string>Kapeyn</string>
    <key>CFBundleIconFile</key>           <string>Kapeyn</string>
    <key>CFBundlePackageType</key>        <string>APPL</string>
    <key>CFBundleShortVersionString</key> <string>2.4.0</string>
    <key>CFBundleVersion</key>            <string>7</string>
    <key>NSAppleEventsUsageDescription</key>
    <string>Kapeyn uses System Events to shut down your Mac when work hours end, only if you turned that on.</string>
    <key>LSMinimumSystemVersion</key>     <string>12.0</string>
    <key>LSUIElement</key>                <true/>
    <key>NSAppSleepDisabled</key>         <true/>
    <key>NSHighResolutionCapable</key>    <true/>
</dict>
</plist>
PLIST

# Ad-hoc signature (required to run on Apple silicon; no Apple account needed).
codesign --force --sign - "$APP" >/dev/null 2>&1 || true

echo "Built $APP"
