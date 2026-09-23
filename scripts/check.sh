#!/usr/bin/env bash
# The single "am I done?" command: restore → build (warnings are errors) → format check → tests.
#
#   scripts/check.sh            full check
#   scripts/check.sh --quick    skip the format check
#   scripts/check.sh --e2e      also run the real-terminal end-to-end tests (needs python3 + pyte)
#   scripts/check.sh --fix      run `dotnet format` to fix formatting instead of verifying it
#   scripts/check.sh --filter X only run test methods whose Namespace.Class.Method contains X
set -euo pipefail
cd "$(dirname "$0")/.."

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
if ! command -v dotnet >/dev/null 2>&1 && [ -x "$HOME/.dotnet/dotnet" ]; then
  export PATH="$HOME/.dotnet:$PATH"
fi
if [ -z "${DOTNET_ROOT:-}" ] && [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
fi

quick=0 e2e=0 fix=0 filter=""
while [ $# -gt 0 ]; do
  case "$1" in
    --quick) quick=1 ;;
    --e2e) e2e=1 ;;
    --fix) fix=1 ;;
    --filter) filter="$2"; shift ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
  shift
done

step() { printf '\n\033[1;32m==> %s\033[0m\n' "$*"; }

step "restore"
dotnet restore Pickle.slnx --verbosity quiet

step "build (warnings are errors)"
dotnet build Pickle.slnx --no-restore --verbosity quiet -clp:ErrorsOnly

if [ $fix -eq 1 ]; then
  step "format (fixing)"
  dotnet format Pickle.slnx --no-restore
elif [ $quick -eq 0 ]; then
  step "format (verify)"
  dotnet format Pickle.slnx --no-restore --verify-no-changes || {
    echo "Formatting issues found. Run: scripts/check.sh --fix" >&2
    exit 1
  }
fi

step "tests"
if [ -n "$filter" ]; then
  # xunit.v3 on Microsoft.Testing.Platform: wildcard match on Namespace.Class.Method.
  dotnet test --solution Pickle.slnx --no-build -- --filter-method "*${filter}*"
else
  dotnet test --solution Pickle.slnx --no-build
fi

if [ $e2e -eq 1 ]; then
  step "end-to-end (pty)"
  python3 -c "import pyte" 2>/dev/null || pip install --quiet pyte
  python3 tests/Pickle.E2E/run_e2e.py
fi

step "all checks passed"
