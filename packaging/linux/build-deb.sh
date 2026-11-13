#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname "$0")/../.." && pwd)
source=${1:-"$root/artifacts/linux-x64"}
output=${2:-"$root/artifacts/DockCommander-linux-x64.deb"}
version=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -n 1)
if [ -z "$version" ]; then
    echo "Version was not found in Directory.Build.props." >&2
    exit 1
fi
if [ ! -e "$source/DC" ]; then
    echo "DC was not found in $source. Publish the linux-x64 build first." >&2
    exit 1
fi

stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/usr/lib/dockcommander" "$stage/usr/bin" "$stage/DEBIAN"
cp -a "$source"/. "$stage/usr/lib/dockcommander/"
chmod 755 "$stage/usr/lib/dockcommander/DC"
cat > "$stage/usr/bin/dc" << 'EOF'
#!/bin/sh
if ! dotnet --list-runtimes 2>/dev/null | grep -E -q '^Microsoft\.NETCore\.App 10\.'; then
    echo "DockCommander needs the .NET 10 runtime. Install the package dotnet-runtime-10.0." >&2
    exit 1
fi
exec /usr/lib/dockcommander/DC "$@"
EOF
chmod 755 "$stage/usr/bin/dc"
cat > "$stage/DEBIAN/control" << EOF
Package: dockcommander
Version: ${version}
Section: utils
Priority: optional
Architecture: amd64
Depends: dotnet-runtime-10.0, libx11-6, libice6, libsm6, libfontconfig1, libglib2.0-bin, xdg-utils
Maintainer: Denis Marchkov <77468371+marchkov@users.noreply.github.com>
Description: Two-pane file manager
 Keyboard-first file manager for Windows and Debian/Ubuntu.
 The .NET 10 runtime is required and is not bundled.
EOF
mkdir -p "$(dirname "$output")"
dpkg-deb --root-owner-group --build "$stage" "$output"
echo "Wrote $output"
