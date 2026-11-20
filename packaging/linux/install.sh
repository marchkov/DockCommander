#!/bin/sh
set -eu

here=$(CDPATH= cd -- "$(dirname "$0")" && pwd)
manifest_name=install.manifest

if [ "${1:-}" = "--self-test" ]; then
    probe=$(mktemp -d)
    trap 'rm -rf "$probe"' EXIT
    mkdir -p "$probe/src" "$probe/home/.local/share/dockcommander"
    printf 'first\n' > "$probe/src/DC"
    printf 'old\n' > "$probe/src/old.dll"
    cp "$here/../../icon.png" "$probe/src/icon.png"
    printf 'keep\n' > "$probe/home/.local/share/dockcommander/keep.txt"
    DC_INSTALL_SOURCE="$probe/src" DC_INSTALL_HOME="$probe/home" DC_SKIP_RUNTIME_CHECK=1 sh "$0"
    grep -q 'first' "$probe/home/.local/share/dockcommander/DC"
    grep -q 'keep' "$probe/home/.local/share/dockcommander/keep.txt"
    test -f "$probe/home/.local/share/applications/dockcommander.desktop"
    test -f "$probe/home/.local/share/icons/hicolor/256x256/apps/dockcommander.png"
    printf 'second\n' > "$probe/src/DC"
    rm -f "$probe/src/old.dll"
    DC_INSTALL_SOURCE="$probe/src" DC_INSTALL_HOME="$probe/home" DC_SKIP_RUNTIME_CHECK=1 sh "$0"
    grep -q 'second' "$probe/home/.local/share/dockcommander/DC"
    test ! -e "$probe/home/.local/share/dockcommander/old.dll"
    grep -q 'keep' "$probe/home/.local/share/dockcommander/keep.txt"
    DC_INSTALL_HOME="$probe/home" sh "$here/uninstall.sh"
    test ! -e "$probe/home/.local/share/dockcommander/DC"
    grep -q 'keep' "$probe/home/.local/share/dockcommander/keep.txt"
    test ! -e "$probe/home/.local/share/applications/dockcommander.desktop"
    test ! -e "$probe/home/.local/bin/dc"
    exit 0
fi

home_dir=${DC_INSTALL_HOME:-$HOME}
if [ -n "${DC_INSTALL_SOURCE:-}" ]; then
    source=$DC_INSTALL_SOURCE
elif [ -e "$here/DC" ]; then
    source=$here
else
    source=$(CDPATH= cd -- "$here/../../artifacts/linux-x64" && pwd)
fi
if [ ! -e "$source/DC" ]; then
    echo "DC was not found. Run this script from the extracted linux-x64 folder." >&2
    exit 1
fi
if [ "${DC_SKIP_RUNTIME_CHECK:-}" != 1 ]; then
    check=$here/check-dotnet.sh
    if [ ! -f "$check" ]; then
        check=$here/../check-dotnet.sh
    fi
    sh "$check"
fi

dest="$home_dir/.local/share/dockcommander"
desktop_dir="$home_dir/.local/share/applications"
icon_dir="$home_dir/.local/share/icons/hicolor/256x256/apps"
command="$home_dir/.local/bin/dc"
manifest="$dest/$manifest_name"
previous=""
if [ -f "$manifest" ]; then
    previous=$(sed -n '1!p' "$manifest")
fi
mkdir -p "$dest" "$home_dir/.local/bin" "$desktop_dir" "$icon_dir"
cp -a "$source"/. "$dest"/
rm -f "$dest/$manifest_name"
chmod +x "$dest/DC"
find "$source" -type f | while IFS= read -r file; do
    rel=${file#"$source"/}
    case "$rel" in
        "$manifest_name") continue ;;
    esac
    printf '%s\n' "$rel"
done > "$dest/files.new"
{
    printf '%s\n' DockCommander
    cat "$dest/files.new"
    printf '%s\n' "$manifest_name"
} > "$manifest"
rm -f "$dest/files.new"
if [ -n "$previous" ]; then
    printf '%s\n' "$previous" | while IFS= read -r rel; do
        [ -n "$rel" ] || continue
        if ! grep -F -x -q "$rel" "$manifest"; then
            case "$rel" in
                /*|*..*) continue ;;
            esac
            rm -f "$dest/$rel"
        fi
    done
fi
icon_source=$source/icon.png
if [ ! -f "$icon_source" ] && [ -f "$here/../../icon.png" ]; then
    icon_source=$here/../../icon.png
fi
if [ -f "$icon_source" ]; then
    cp "$icon_source" "$icon_dir/dockcommander.png"
fi
template=$here/dockcommander.desktop
if [ ! -f "$template" ]; then
    template=$dest/dockcommander.desktop
fi
if [ ! -f "$template" ]; then
    echo "dockcommander.desktop was not found next to the installer." >&2
    exit 1
fi
sed "s|@EXEC@|$command|" "$template" > "$desktop_dir/dockcommander.desktop"
cat > "$command" << EOF
#!/bin/sh
if ! dotnet --list-runtimes 2>/dev/null | grep -E -q '^Microsoft\\.NETCore\\.App 10\\.'; then
    echo "DockCommander needs the shared framework Microsoft.NETCore.App 10 x64. Install the package dotnet-runtime-10.0." >&2
    exit 1
fi
exec "$dest/DC" "\$@"
EOF
chmod +x "$command"
echo "Installed to $dest"
echo "Command: $command"
echo "Application menu: DockCommander"
