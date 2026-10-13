#!/bin/bash
set -euo pipefail

config_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$config_root/../../DotNet"

# Unity launched from Finder/Hub may not inherit the terminal's PATH.
dotnet_executable="${DOTNET_EXECUTABLE:-}"
if [[ -z "$dotnet_executable" ]]; then
    dotnet_executable="$(command -v dotnet || true)"
fi
if [[ -z "$dotnet_executable" ]]; then
    for candidate in \
        "${DOTNET_ROOT:-}/dotnet" \
        /usr/local/share/dotnet/dotnet \
        /opt/homebrew/bin/dotnet \
        /usr/local/bin/dotnet \
        /usr/share/dotnet/dotnet \
        /usr/lib/dotnet/dotnet \
        "${HOME:-}/.dotnet/dotnet"; do
        if [[ -x "$candidate" ]]; then
            dotnet_executable="$candidate"
            break
        fi
    done
fi
if [[ -z "$dotnet_executable" || ! -x "$dotnet_executable" ]]; then
    printf '%s\n' 'ConfigGenerator requires .NET SDK 10.0.401 and .NET 8 runtime. Install them or set DOTNET_EXECUTABLE to the absolute dotnet path.' >&2
    exit 127
fi

# Export the selected host so ConfigGenerator uses it for the .NET 8 Luban child.
export DOTNET_EXECUTABLE="$dotnet_executable"
exec "$dotnet_executable" run --project ConfigGenerator --no-launch-profile -- generate --target all
