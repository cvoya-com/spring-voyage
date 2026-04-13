#!/usr/bin/env bash
# Regenerates the CLI's Kiota-based API client from the committed
# OpenAPI contract. The output lives under src/Cvoya.Spring.Cli/Generated/
# and is gitignored — `dotnet build` re-emits it via the
# GenerateKiotaClient MSBuild target. This script is provided for manual
# regeneration outside of a build (e.g. when inspecting the diff after
# an OpenAPI change).
#
# Kiota is declared as a local dotnet tool in .config/dotnet-tools.json
# so contributors only need `dotnet tool restore` (run automatically by
# the MSBuild target).

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

dotnet tool restore
dotnet tool run kiota generate \
  --openapi "$ROOT/src/Cvoya.Spring.Host.Api/openapi.json" \
  --language CSharp \
  --output "$ROOT/src/Cvoya.Spring.Cli/Generated" \
  --class-name SpringApiKiotaClient \
  --namespace-name Cvoya.Spring.Cli.Generated \
  --clean-output \
  --log-level Warning

echo "Regenerated CLI Kiota client at src/Cvoya.Spring.Cli/Generated/"
