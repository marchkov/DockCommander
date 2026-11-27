#!/bin/sh
# Package the already checked-out application source as an experimental
# self-contained Apple Silicon app. This script does not change that source.
set -eu

source_dir=${1:?application source directory}
output=${2:?output dmg path}

root=$(CDPATH= cd -- "$source_dir" && pwd)
case "$output" in
  /*) ;;
  *) output=$(pwd)/$output ;;
esac
mkdir -p "$(dirname "$output")"

version=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -n 1)
if [ -z "$version" ]; then
  echo "Version was not found in Directory.Build.props." >&2
  exit 1
fi
bundle_version=${version%%-*}
source_sha="unknown"
if [ -d "$root/.git" ]; then
  source_sha=$(git -C "$root" rev-parse HEAD)
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
publish="$work/publish"
app="$work/DockCommander.app"
stage="$work/dmg"

echo "Publishing osx-arm64 $version from $source_sha"
dotnet publish "$root/src/DockCommander.App/DockCommander.App.csproj" \
  -c Release -r osx-arm64 --self-contained true \
  -p:PublishTrimmed=false -p:PublishAot=false \
  -o "$publish" --nologo

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish"/. "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/DC"
if [ ! -x "$app/Contents/MacOS/DC" ]; then
  echo "The native executable was not produced." >&2
  exit 1
fi

iconset="$work/DockCommander.iconset"
mkdir -p "$iconset"
icon="$root/icon.png"
sips -z 16 16 "$icon" --out "$iconset/icon_16x16.png" >/dev/null
sips -z 32 32 "$icon" --out "$iconset/icon_16x16@2x.png" >/dev/null
sips -z 32 32 "$icon" --out "$iconset/icon_32x32.png" >/dev/null
sips -z 64 64 "$icon" --out "$iconset/icon_32x32@2x.png" >/dev/null
sips -z 128 128 "$icon" --out "$iconset/icon_128x128.png" >/dev/null
sips -z 256 256 "$icon" --out "$iconset/icon_128x128@2x.png" >/dev/null
sips -z 256 256 "$icon" --out "$iconset/icon_256x256.png" >/dev/null
sips -z 512 512 "$icon" --out "$iconset/icon_256x256@2x.png" >/dev/null
sips -z 512 512 "$icon" --out "$iconset/icon_512x512.png" >/dev/null
sips -z 1024 1024 "$icon" --out "$iconset/icon_512x512@2x.png" >/dev/null
iconutil -c icns "$iconset" -o "$app/Contents/Resources/DockCommander.icns"

cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en</string>
  <key>CFBundleDisplayName</key>
  <string>DockCommander</string>
  <key>CFBundleExecutable</key>
  <string>DC</string>
  <key>CFBundleIconFile</key>
  <string>DockCommander</string>
  <key>CFBundleIdentifier</key>
  <string>cz.marchkov.dockcommander</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>DockCommander</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${version}</string>
  <key>CFBundleVersion</key>
  <string>${bundle_version}</string>
  <key>LSMinimumSystemVersion</key>
  <string>13.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
EOF
printf 'APPL????' > "$app/Contents/PkgInfo"

echo "Checking Mach-O architecture"
find "$app/Contents" -type f > "$work/files.txt"
while IFS= read -r file; do
  description=$(file -b "$file")
  case "$description" in
    *Mach-O*)
      # Skia ships universal binaries. Keep the arm64 slice so the bundle is Apple Silicon only.
      case "$description" in
        *x86_64*)
          case "$description" in
            *arm64*)
              lipo "$file" -thin arm64 -output "$file.arm64" || exit 1
              mv "$file.arm64" "$file"
              description=$(file -b "$file")
              ;;
          esac
          ;;
      esac
      case "$description" in
        *arm64*) ;;
        *) echo "Not an arm64 Mach-O file: $file ($description)" >&2; exit 1 ;;
      esac
      case "$description" in
        *x86_64*) echo "Unexpected Intel slice: $file ($description)" >&2; exit 1 ;;
      esac
      # The main executable is signed with the bundle. Signing it alone makes codesign
      # treat the surrounding managed libraries as unsigned nested code.
      if [ "$file" != "$app/Contents/MacOS/DC" ]; then
        codesign --force --sign - "$file" || exit 1
      fi
      ;;
  esac
done < "$work/files.txt"
codesign --force --sign - "$app"
codesign --verify --deep --strict --verbose=2 "$app"

cat > "$work/EXPERIMENTAL.txt" <<EOF
DockCommander ${version} experimental macOS ARM64 build

Source commit: ${source_sha}
Architecture: osx-arm64, self-contained, not trimmed, not Native AOT
Bundle identifier: cz.marchkov.dockcommander
Signature: ad-hoc. This build is not signed with a Developer ID and is not notarized.

This build is experimental Apple Silicon only. It is not a stable macOS release
and it is not an Intel build.

Gatekeeper will warn on first launch. Do not turn Gatekeeper off and do not clear
the quarantine flag for the whole disk. Use System Settings, Privacy & Security,
Open Anyway. You can also right-click DockCommander.app, choose Open, then Open again.

F8 moves local items to the Trash. A Trash failure does not delete them permanently.
Shift+F8 deletes permanently after confirmation. Remote connections still delete
permanently, after the same confirmation.

Saved connection secrets are encrypted with a key file restricted to the current
user (mode 0600, directory mode 0700). This build does not use the macOS Keychain.

File name case follows the volume. /tmp, /var and /etc are accepted only when they
point at /private/tmp, /private/var and /private/etc. Other symbolic links are refused.
There is no Finder context menu. Dragging files to or from Finder was not part of
the automated checks.
EOF
mkdir -p "$stage"
cp -R "$app" "$stage/"
cp "$work/EXPERIMENTAL.txt" "$stage/EXPERIMENTAL.txt"
ln -s /Applications "$stage/Applications"

rm -f "$output"
hdiutil create -volname "DockCommander" -srcfolder "$stage" -ov -format UDZO "$output" >/dev/null
mount_point=$(hdiutil attach -nobrowse -readonly "$output" | sed -n 's/.*\(\/Volumes\/.*\)$/\1/p' | head -n 1)
if [ -z "$mount_point" ] || [ ! -d "$mount_point/DockCommander.app" ] || [ ! -L "$mount_point/Applications" ]; then
  echo "The disk image did not mount with the application and the Applications shortcut." >&2
  hdiutil detach "$mount_point" >/dev/null 2>&1 || true
  exit 1
fi
hdiutil detach "$mount_point" >/dev/null

shasum -a 256 "$output" > "$output.sha256"
cat "$output.sha256"
cp "$work/EXPERIMENTAL.txt" "$(dirname "$output")/DockCommander-macos-arm64-EXPERIMENTAL.txt"
checksum=$(awk '{print $1}' "$output.sha256")
printf '\nSHA-256: %s\n' "$checksum" >> "$(dirname "$output")/DockCommander-macos-arm64-EXPERIMENTAL.txt"

smoke_home="$work/smoke-home"
mkdir -p "$smoke_home"
smoke_log="$(dirname "$output")/startup-smoke.log"
: > "$smoke_log"
HOME="$smoke_home" "$app/Contents/MacOS/DC" >>"$smoke_log" 2>&1 &
smoke_pid=$!
sleep 5
if kill -0 "$smoke_pid" 2>/dev/null; then
  echo "startup: process was still running after 5 seconds; interactive UI was not inspected" | tee -a "$smoke_log"
  kill "$smoke_pid" 2>/dev/null || true
  wait "$smoke_pid" 2>/dev/null || true
else
  wait "$smoke_pid"
  echo "startup: process exited with status $?" | tee -a "$smoke_log"
fi

echo "Packaged $output"
ls -lh "$output"
