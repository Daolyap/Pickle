#!/usr/bin/env bash
# Rebuild the graphify knowledge graph of the code (local tree-sitter parsing, no API cost) and refresh
# docs/graph-report.md. Needs graphify:  uv tool install graphifyy   (or: pipx install graphifyy)
#   scripts/graph.sh            then e.g.  graphify query "how does the prompt get rendered?"
#   scripts/graph.sh --label    also name communities with an LLM. This costs tokens: graphify uses an API key
#                               from the environment, or else the `claude` CLI if it is on PATH.
set -euo pipefail
cd "$(dirname "$0")/.."
if ! command -v graphify >/dev/null 2>&1; then
  echo "graphify is not installed: uv tool install graphifyy (or pipx install graphifyy)" >&2
  exit 1
fi
graphify extract . --code-only --no-cluster
if [ "${1:-}" = "--label" ]; then
  graphify cluster-only .
else
  # Without --no-label graphify silently falls back to the `claude` CLI to name communities.
  graphify cluster-only . --no-label
fi
cp graphify-out/GRAPH_REPORT.md docs/graph-report.md
echo "graph: graphify-out/graph.json + graph.html (git-ignored) · report: docs/graph-report.md"
