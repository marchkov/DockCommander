#!/bin/sh
set -eu

here=$(CDPATH= cd -- "$(dirname "$0")" && pwd)
if [ -e "$here/DC" ]; then
    source=$here
    check=$here/check-dotnet.sh
else
    source=$(CDPATH= cd -- "$here/../../artifacts/linux-x64" && pwd)
    check=$here/../check-dotnet.sh
fi
if [ ! -e "$source/DC" ]; then
    echo "DC was not found. Run this script from the extracted linux-x64 folder." >&2
    exit 1
fi
if [ ! -f "$check" ]; then
    echo "check-dotnet.sh was not found next to the installer." >&2
    exit 1
fi

sh "$check"

dest="${HOME}/.local/share/dockcommander"
mkdir -p "$dest" "${HOME}/.local/bin"
cp -a "$source"/. "$dest"/
chmod +x "$dest/DC"
cat > "${HOME}/.local/bin/dc" << EOF
#!/bin/sh
if ! dotnet --list-runtimes 2>/dev/null | grep -E -q '^Microsoft\\.NETCore\\.App 10\\.'; then
    echo "DockCommander needs the .NET 10 runtime. Install the package dotnet-runtime-10.0." >&2
    exit 1
fi
exec "$dest/DC" "\$@"
EOF
chmod +x "${HOME}/.local/bin/dc"
echo "Installed to $dest"
echo "Command: ${HOME}/.local/bin/dc"
echo "If that directory is not on PATH, add it or run $dest/DC"
