#!/usr/bin/env bash
# Publish self-contained single-file binaries (ReadyToRun) into artifacts/publish/<rid>/.
#   scripts/publish.sh [rid...]      default: win-x64 win-arm64 linux-x64 osx-arm64
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then export PATH="$HOME/.dotnet:$PATH"; fi

rids=("$@")
[ ${#rids[@]} -eq 0 ] && rids=(win-x64 win-arm64 linux-x64 osx-arm64)
version="${PICKLE_VERSION:-}"

for rid in "${rids[@]}"; do
  out="artifacts/publish/$rid"
  echo "==> $rid → $out"
  args=(publish src/Pickle/Pickle.csproj -c Release -r "$rid" -o "$out"
        -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false)
  [ -n "$version" ] && args+=(-p:Version="$version")
  dotnet "${args[@]}"
  rm -f "$out"/*.pdb
done
