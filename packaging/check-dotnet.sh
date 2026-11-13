#!/bin/sh
set -eu

has_runtime() {
    printf '%s\n' "$1" | grep -E '^Microsoft\.NETCore\.App 10\.' >/dev/null 2>&1
}

if [ "${1:-}" = "--self-test" ]; then
    if has_runtime "Microsoft.NETCore.App 8.0.21 [/usr/share/dotnet/shared/Microsoft.NETCore.App]"; then
        exit 1
    fi
    if has_runtime "Microsoft.WindowsDesktop.App 10.0.1 [/usr/share/dotnet/shared/Microsoft.WindowsDesktop.App]
Microsoft.NETCore.App 10.0.1 [/usr/share/dotnet/shared/Microsoft.NETCore.App]"; then
        exit 0
    fi
    exit 1
fi

if ! uname -m | grep -qx 'x86_64'; then
    echo "DockCommander needs a 64-bit x86 system and the .NET 10 runtime." >&2
    exit 1
fi

listing="$(dotnet --list-runtimes 2>/dev/null || true)"
if has_runtime "$listing"; then
    exit 0
fi

echo "DockCommander needs the .NET 10 runtime (package dotnet-runtime-10.0)." >&2
echo "Install instructions: https://learn.microsoft.com/dotnet/core/install/linux" >&2
exit 1
