#!/bin/bash
set -euo pipefail

config_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$config_root"

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
        /opt/homebrew/opt/dotnet@8/bin/dotnet \
        /usr/local/opt/dotnet@8/bin/dotnet \
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
    printf '%s\n' 'Luban requires .NET 8. Install it or set DOTNET_EXECUTABLE to the absolute dotnet path.' >&2
    exit 127
fi

# Both targets use the same Excel source. Overrides keep verification outside the repository.
code_output="${LUBAN_OUTPUT_CODE_DIR:-$config_root/../Assets/Scripts/Hotfix/TableConfig/Generated}"
data_output="${LUBAN_OUTPUT_DATA_DIR:-$config_root/../Assets/Config/Excel/Bytes}"

server_code_output="${LUBAN_SERVER_OUTPUT_CODE_DIR:-$config_root/../../DotNet/Game.Config/Generated}"
server_data_output="${LUBAN_SERVER_OUTPUT_DATA_DIR:-$config_root/../../DotNet/Game.Config/Bytes}"

"$dotnet_executable" "$config_root/Luban/Luban.dll" \
    -t client \
    -c cs-bin \
    -d bin \
    --strict \
    --conf "$config_root/luban.conf" \
    -x "outputCodeDir=$code_output" \
    -x "outputDataDir=$data_output"

exec "$dotnet_executable" "$config_root/Luban/Luban.dll" \
    -t server \
    -c cs-bin \
    -d bin \
    --strict \
    --conf "$config_root/luban.conf" \
    -x "outputCodeDir=$server_code_output" \
    -x "outputDataDir=$server_data_output"
