#!/usr/bin/env bash
# Fail on high/critical dependency, secret, and configuration findings using
# the same scanner set and repository exclusions as .github/workflows/trivy.yml.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT"

command -v trivy >/dev/null 2>&1 || {
  echo "trivy is required (https://trivy.dev/latest/getting-started/installation/)" >&2
  exit 1
}

trivy fs \
  --scanners vuln,secret,misconfig \
  --severity HIGH,CRITICAL \
  --exit-code 1 \
  --ignore-unfixed \
  --skip-dirs v0.1 \
  --skip-dirs '**/node_modules' \
  --skip-dirs .git \
  --skip-dirs artifacts \
  --skip-dirs '**/bin' \
  --skip-dirs '**/obj' \
  --skip-dirs '**/.next' \
  --skip-dirs '**/coverage' \
  --skip-dirs '**/TestResults' \
  .
