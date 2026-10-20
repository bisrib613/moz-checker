#!/usr/bin/env bash
set -euo pipefail
cd /workspace/moz-checker
export DOTNET_CLI_HOME=/workspace/.cache/dotnet
export NUGET_PACKAGES=/workspace/.cache/nuget
export XDG_CACHE_HOME=/workspace/.cache
export XDG_DATA_HOME=/workspace/.cache/xdg
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export PATH="/workspace/.dotnet:$PATH"
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES" "$XDG_CACHE_HOME" "$XDG_DATA_HOME"
if ! command -v dotnet >/dev/null || ! dotnet --list-sdks | rg -q '^10\.'; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/moz-checker-dotnet-install.sh
  bash /tmp/moz-checker-dotnet-install.sh --channel 10.0 --install-dir /workspace/.dotnet
fi
# This isolated cloud cache has no legacy NuGet data to migrate. Mark that empty
# migration explicitly; native user-scoped mutex files cannot live in read-only HOME.
mkdir -p "$XDG_DATA_HOME/NuGet/Migrations"
touch "$XDG_DATA_HOME/NuGet/Migrations/1"
dotnet restore dotnet/MozChecker.Tests --locked-mode
dotnet test dotnet/MozChecker.Tests --no-restore --configuration Release --disable-build-servers
dotnet restore dotnet/MozChecker.Desktop --locked-mode
dotnet build dotnet/MozChecker.Desktop --no-restore --configuration Release --disable-build-servers
