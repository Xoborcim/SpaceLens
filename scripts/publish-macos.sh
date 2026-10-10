#!/usr/bin/env bash
# Builds SpaceLens.app for macOS: a self-contained .NET app (no runtime to install) in an application bundle.
#
#   scripts/publish-macos.sh [arm64|x64] [version]
#
# Apple silicon is arm64 (the default); Intel Macs are x64. The bundle lands in publish/macos-<arch>/SpaceLens.app.
# It can be built on any OS. On a Mac it is also signed ad hoc, which Apple silicon requires before it runs
# anything; elsewhere, sign it on a Mac afterwards (codesign --force --deep --sign - SpaceLens.app).
# It is not notarized: the first time, open it with right-click > Open (or run
# xattr -dr com.apple.quarantine SpaceLens.app) to get past Gatekeeper.
set -euo pipefail

arch="${1:-arm64}"
version="${2:-1.0.0}"
case "$arch" in
  arm64|x64) ;;
  *) echo "usage: $0 [arm64|x64] [version]" >&2; exit 2 ;;
esac

root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/publish/macos-$arch"
staging="$out/staging"
app="$out/SpaceLens.app"

rm -rf "$out"
dotnet publish "$root/SpaceLens.Avalonia/SpaceLens.Avalonia.csproj" \
  -c Release -r "osx-$arch" --self-contained true \
  -p:UseAppHost=true -p:Version="$version" \
  -o "$staging"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$staging/." "$app/Contents/MacOS/"
cp "$root/SpaceLens.Avalonia/Assets/SpaceLens.icns" "$app/Contents/Resources/SpaceLens.icns"
chmod +x "$app/Contents/MacOS/SpaceLens"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>SpaceLens</string>
  <key>CFBundleDisplayName</key><string>SpaceLens</string>
  <key>CFBundleIdentifier</key><string>io.github.xoborcim.spacelens</string>
  <key>CFBundleExecutable</key><string>SpaceLens</string>
  <key>CFBundleIconFile</key><string>SpaceLens.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>SpaceLens</string>
</dict>
</plist>
PLIST

rm -rf "$staging"

if command -v codesign >/dev/null 2>&1; then
  codesign --force --deep --sign - "$app"
  echo "Signed ad hoc."
else
  echo "Not on a Mac: sign it there before running (codesign --force --deep --sign - SpaceLens.app)."
fi

echo "Built $app"
