#!/bin/sh
set -eu

home_dir=${DC_INSTALL_HOME:-$HOME}
dest="$home_dir/.local/share/dockcommander"
manifest="$dest/install.manifest"
command="$home_dir/.local/bin/dc"
desktop="$home_dir/.local/share/applications/dockcommander.desktop"
icon="$home_dir/.local/share/icons/hicolor/256x256/apps/dockcommander.png"

if [ ! -f "$manifest" ] || ! head -n 1 "$manifest" | grep -qx 'DockCommander'; then
    echo "No DockCommander install manifest was found. Nothing was removed: $dest" >&2
    exit 1
fi

while IFS= read -r rel; do
    [ -n "$rel" ] || continue
    case "$rel" in
        /*|*..*)
            echo "The manifest contains an unsafe path. Nothing further was removed: $rel" >&2
            exit 1
            ;;
    esac
    rm -f "$dest/$rel"
done << EOF
$(sed -n '1!p' "$manifest")
EOF

find "$dest" -depth -type d -empty -delete 2>/dev/null || true
if [ -d "$dest" ] && [ -z "$(find "$dest" -mindepth 1 -print -quit)" ]; then
    rmdir "$dest"
fi
if [ -f "$command" ] && grep -q "$dest/DC" "$command"; then
    rm -f "$command"
fi
if [ -f "$desktop" ] && grep -q "$command" "$desktop"; then
    rm -f "$desktop" "$icon"
fi
echo "Removed the DockCommander files recorded in $dest"
