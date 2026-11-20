#!/bin/sh
set -eu

has_runtime() {
    printf '%s\n' "$1" | grep -E '^Microsoft\.NETCore\.App 10\.' >/dev/null 2>&1
}

elf_machine() {
    if [ ! -f "$1" ]; then
        return 1
    fi
    # Bytes 0-3 are the ELF magic. e_machine is the little-endian value at offset 18.
    set -- $(od -An -t u1 -N 20 "$1")
    if [ "$#" -lt 20 ] || [ "$1" != 127 ] || [ "$2" != 69 ] || [ "$3" != 76 ] || [ "$4" != 70 ]; then
        return 1
    fi
    shift 18
    echo "$1"
}

if [ "${1:-}" = "--self-test" ]; then
    if has_runtime "Microsoft.NETCore.App 8.0.21 [/usr/share/dotnet/shared/Microsoft.NETCore.App]"; then
        exit 1
    fi
    if ! has_runtime "Microsoft.NETCore.App 10.0.1 [/usr/share/dotnet/shared/Microsoft.NETCore.App]"; then
        exit 1
    fi
    probe=$(mktemp -d)
    trap 'rm -rf "$probe"' EXIT
    # ELF64, little endian, e_machine 62 (x86_64) at offset 18.
    printf '\177ELF\002\001\001\000\000\000\000\000\000\000\000\000\002\000\076\000' > "$probe/x64"
    # e_machine 3 is x86.
    printf '\177ELF\002\001\001\000\000\000\000\000\000\000\000\000\002\000\003\000' > "$probe/x86"
    printf 'not an elf' > "$probe/script"
    x64_machine=$(elf_machine "$probe/x64" || true)
    x86_machine=$(elf_machine "$probe/x86" || true)
    if [ "$x64_machine" != 62 ] || [ "$x86_machine" = 62 ]; then
        echo "ELF probe failed: x64=$x64_machine x86=$x86_machine" >&2
        od -An -t u1 -N 20 "$probe/x64" >&2 || true
        exit 1
    fi
    if elf_machine "$probe/script" >/dev/null 2>&1; then
        exit 1
    fi
    exit 0
fi

if ! uname -m | grep -qx 'x86_64'; then
    echo "DockCommander needs a 64-bit x86 system and Microsoft.NETCore.App 10." >&2
    exit 1
fi

dotnet_bin=$(command -v dotnet 2>/dev/null || true)
if [ -n "$dotnet_bin" ]; then
    machine=$(elf_machine "$dotnet_bin" || true)
    if [ -n "$machine" ] && [ "$machine" != 62 ]; then
        echo "DockCommander needs the x64 shared framework Microsoft.NETCore.App 10. The dotnet host is a different architecture." >&2
        exit 1
    fi
fi

listing=$(dotnet --list-runtimes 2>/dev/null || true)
if has_runtime "$listing"; then
    exit 0
fi

echo "DockCommander needs the shared framework Microsoft.NETCore.App 10 x64." >&2
echo "On Debian/Ubuntu, install the package dotnet-runtime-10.0." >&2
echo "https://learn.microsoft.com/dotnet/core/install/linux" >&2
exit 1
