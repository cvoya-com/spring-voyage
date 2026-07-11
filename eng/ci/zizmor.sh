#!/usr/bin/env bash

set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."

if ! command -v uvx >/dev/null 2>&1; then
  echo "ERROR: uvx is required to run the pinned zizmor version." >&2
  echo "Install uv from https://docs.astral.sh/uv/ and rerun this command." >&2
  exit 1
fi

exec uvx zizmor==1.26.1 --persona regular .github/workflows
