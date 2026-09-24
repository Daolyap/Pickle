#!/usr/bin/env bash
# Rebuild the graphify knowledge graph of the code (local tree-sitter parsing, no API key needed) and refresh
# docs/graph-report.md. Needs graphify:  uv tool install graphifyy   (or: pipx install graphifyy)
#   scripts/graph.sh            then e.g.  graphify query "how does the prompt get rendered?"
set -euo pipefail
cd "$(dirname "$0")/.."
if ! command -v graphify >/dev/null 2>&1; then
  echo "graphify is not installed: uv tool install graphifyy (or pipx install graphifyy)" >&2
  exit 1
fi
graphify extract . --code-only
graphify cluster-only . --no-label
cp graphify-out/GRAPH_REPORT.md docs/graph-report.md
echo "graph: graphify-out/graph.json + graph.html (git-ignored) · report: docs/graph-report.md · community names: graphify label ."
