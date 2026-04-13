#!/usr/bin/env bash
# Regenerates the CLI's Kiota-based API client from the committed
# OpenAPI contract. The output lives under
# src/Cvoya.Spring.Cli/Generated/ and is checked in so contributors
# don't need Kiota installed for routine builds — only when changing
# the API surface.
#
# Requires the Kiota dotnet tool. Install once with:
#   dotnet tool install -g Microsoft.OpenApi.Kiota
#
# CI verifies the generated tree is in sync via the openapi-drift job
# (see #175 / #178).

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

KIOTA="${KIOTA_BIN:-kiota}"
if ! command -v "$KIOTA" >/dev/null 2>&1; then
  if [ -x "$HOME/.dotnet/tools/kiota" ]; then
    KIOTA="$HOME/.dotnet/tools/kiota"
  else
    echo "kiota not found on PATH or in ~/.dotnet/tools/. Install with:" >&2
    echo "  dotnet tool install -g Microsoft.OpenApi.Kiota" >&2
    exit 1
  fi
fi

"$KIOTA" generate \
  --openapi "$ROOT/src/Cvoya.Spring.Host.Api/openapi.json" \
  --language CSharp \
  --output "$ROOT/src/Cvoya.Spring.Cli/Generated" \
  --class-name SpringApiKiotaClient \
  --namespace-name Cvoya.Spring.Cli.Generated \
  --clean-output \
  --log-level Warning

echo "Regenerated CLI Kiota client at src/Cvoya.Spring.Cli/Generated/"
