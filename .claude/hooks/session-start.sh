#!/bin/bash
# SessionStart hook for Claude Code on the web: installs the .NET 10 SDK, the pyte module used by the pty
# end-to-end tests, and restores NuGet packages so `scripts/check.sh` works immediately.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

cd "${CLAUDE_PROJECT_DIR:-$(dirname "$0")/../..}"

DOTNET_DIR="$HOME/.dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

if ! "$DOTNET_DIR/dotnet" --list-sdks 2>/dev/null | grep -q '^10\.'; then
  echo "Installing .NET 10 SDK into $DOTNET_DIR..."
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_DIR" >/dev/null
fi

export PATH="$DOTNET_DIR:$PATH" DOTNET_ROOT="$DOTNET_DIR"
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  {
    echo "export PATH=\"$DOTNET_DIR:\$PATH\""
    echo "export DOTNET_ROOT=\"$DOTNET_DIR\""
    echo "export DOTNET_CLI_TELEMETRY_OPTOUT=1"
    echo "export DOTNET_NOLOGO=1"
  } >> "$CLAUDE_ENV_FILE"
fi

python3 -c "import pyte" 2>/dev/null || pip install --quiet pyte 2>/dev/null || true

dotnet restore Pickle.slnx --verbosity quiet
echo "Pickle dev environment ready: $(dotnet --version)"
